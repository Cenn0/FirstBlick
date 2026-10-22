using System.Data;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FirstBlick.Core.Business.TaskStatus;

namespace FirstBlick.Api;

public sealed record TaskDependencyRequest(List<Guid> PredecessorTaskIds);
public sealed record TaskDependencyStatusRequest(TaskStatus Status);
public sealed record TaskDependencyNode(Guid Id, long Revision, string Title, TaskStatus Status, bool Archived,
    IReadOnlyList<Guid> PredecessorTaskIds);
public sealed record TaskDependencyGraph(Guid TenantId, Guid ProjectId, string ProjectName, IReadOnlyList<TaskDependencyNode> Tasks);

public static class TaskDependencyEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{id:guid}/task-dependencies", async (Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await using var transaction = await c.Db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            // Reauthorize only after waiting for the same lock used by every business mutation.
            // ReadCommitted does not retain a pre-wait snapshot of a revoked membership.
            await c.Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({c.Scope.TenantId.ToString()}, 0))", ct);
            await c.Commands.Authorize("tasks", false, ct); await c.Commands.Authorize("projects", false, ct);
            EnsureWorkspaceRole(c.Scope);
            var project = await c.Store.Get<Project>(id, ct);
            await RecordPolicy.EnsureVisible("projects", id, c.Scope, c.Store, ct);
            var tasks = await ProjectTasks(c.Db, id, ct);
            TaskDependencies.ValidateGraph(tasks);
            var result = new TaskDependencyGraph(c.Scope.TenantId, id, project.Name,
                tasks.OrderBy(x => x.Title, StringComparer.Ordinal).ThenBy(x => x.Id).Select(Node).ToArray());
            await transaction.CommitAsync(ct); return Results.Ok(result);
        }).Produces<TaskDependencyGraph>().WithTags("tasks");

        api.MapPut("/tasks/{id:guid}/dependencies", (Guid id, TaskDependencyRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Commands.Execute(c.Http, "tasks", "task.dependencies", body, async ct =>
            {
                var task = await c.Current<TaskItem>(id, ct); await OpenProject(task.ProjectId, c, ct);
                TaskDependencies.Replace(task, body.PredecessorTaskIds);
                await ValidateMutation(task, c.Store, c.Db, c.Scope, ct);
                await c.Store.Save(task, "task.dependencies", false, ct); return Node(task);
            }, async ct => { EnsureWorkspaceRole(c.Scope); _ = await c.Current<TaskItem>(id, ct, false); }))
            .Produces<TaskDependencyNode>().WithTags("tasks");

        api.MapPost("/tasks/{id:guid}/dependency-status", (Guid id, TaskDependencyStatusRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Commands.Execute(c.Http, "tasks", "task.dependency_status", body, async ct =>
            {
                var task = await c.Current<TaskItem>(id, ct); await OpenProject(task.ProjectId, c, ct);
                Guard.Require(Enum.IsDefined(body.Status), "invalid_status", "Der Aufgabenstatus ist ungültig.");
                task.Status = body.Status;
                await ValidateMutation(task, c.Store, c.Db, c.Scope, ct);
                await c.Store.Save(task, "task.dependency_status", false, ct); return Node(task);
            }, async ct => { EnsureWorkspaceRole(c.Scope); _ = await c.Current<TaskItem>(id, ct, false); }))
            .Produces<TaskDependencyNode>().WithTags("tasks");
    }

    // Called from every generic task mutation as well as both dedicated commands, under the tenant write lock.
    public static async Task ValidateMutation(TaskItem task, EntityStore store, FirstBlickDbContext db, TenantScope scope, CancellationToken ct)
    {
        task.Validate();
        Guard.Require(task.TenantId == scope.TenantId, "forbidden", "Die Aufgabe gehört zu einem anderen Betrieb.");
        var incoming = await db.Links.AnyAsync(x => x.TargetKind == "tasks" && x.TargetId == task.Id && x.SourceKind == "tasks", ct);
        var previousRow = await db.Entities.AsNoTracking().SingleOrDefaultAsync(x => x.Kind == "tasks" && x.Id == task.Id, ct);
        var previous = previousRow is null ? null : AppJson.Read<TaskItem>(previousRow.Data);
        var involved = incoming || (task.PredecessorTaskIds?.Count ?? 0) > 0 || (previous?.PredecessorTaskIds?.Count ?? 0) > 0;
        if (involved)
            Guard.Require(previous is null || previous.ProjectId == task.ProjectId,
                "task_dependency_project", "Eine verknüpfte Aufgabe kann nicht in ein anderes Projekt verschoben werden.");
        if (previous is null || previous.ProjectId != task.ProjectId)
            await EnsureAdditionCapacity(db, task.ProjectId, 1, ct);
        if (!involved) return;
        _ = await store.Get<Project>(task.ProjectId, ct);
        var tasks = await ProjectTasks(db, task.ProjectId, ct);
        tasks.RemoveAll(x => x.Id == task.Id); tasks.Add(task);
        TaskDependencies.ValidateGraph(tasks);
    }

    // The caller holds the tenant write lock. Check the entire addition before saving any rows.
    // Unlinked legacy projects have no new size restriction, but an existing graph must stay usable.
    public static async Task EnsureAdditionCapacity(FirstBlickDbContext db, Guid projectId, int additionalTasks, CancellationToken ct)
    {
        var projectLinks = db.Links.Where(x => x.TargetKind == "projects" && x.TargetId == projectId && x.SourceKind == "tasks");
        var hasDependencies = await db.Links.AnyAsync(edge => edge.SourceKind == "tasks" && edge.TargetKind == "tasks"
            && projectLinks.Any(source => source.SourceId == edge.SourceId), ct);
        if (!hasDependencies) return;
        var existingTasks = await projectLinks.CountAsync(ct);
        Guard.Require(additionalTasks >= 0 && existingTasks <= TaskDependencies.MaximumTasks - additionalTasks,
            "task_graph_limit", "Die Abhängigkeitsprüfung unterstützt höchstens 1.000 Aufgaben je Projekt.");
    }

    private static async Task<List<TaskItem>> ProjectTasks(FirstBlickDbContext db, Guid projectId, CancellationToken ct)
    {
        var projectLinks = db.Links.Where(x => x.TargetKind == "projects" && x.TargetId == projectId && x.SourceKind == "tasks");
        var rows = await db.Entities.AsNoTracking().Where(x => x.Kind == "tasks" && projectLinks.Any(l => l.SourceId == x.Id))
            .OrderBy(x => x.Id).Take(TaskDependencies.MaximumTasks + 1).Select(x => x.Data).ToListAsync(ct);
        Guard.Require(rows.Count <= TaskDependencies.MaximumTasks, "task_graph_limit", "Die Abhängigkeitsprüfung unterstützt höchstens 1.000 Aufgaben je Projekt.");
        return rows.Select(AppJson.Read<TaskItem>).ToList();
    }

    private static TaskDependencyNode Node(TaskItem task) => new(task.Id, task.Revision, task.Title, task.Status, task.Archived,
        (task.PredecessorTaskIds ?? []).ToArray());
    private static void EnsureWorkspaceRole(TenantScope scope) => Guard.Require(scope.Role is BusinessRole.ManagingDirector or BusinessRole.Office or BusinessRole.SiteManager or BusinessRole.Accounting,
        "forbidden", "Die projektweite Abhängigkeitsübersicht ist für diesen Zugang nicht verfügbar.");
    private static async Task OpenProject(Guid projectId, BusinessCommandServices c, CancellationToken ct)
    {
        await c.Commands.Authorize("projects", false, ct);
        var project = await c.Current<Project>(projectId, ct, false);
        Guard.Require(project.Status is not ProjectStatus.Completed and not ProjectStatus.Cancelled,
            "project_closed", "Abgeschlossene oder abgesagte Projekte können hier nicht verändert werden.");
    }
}
