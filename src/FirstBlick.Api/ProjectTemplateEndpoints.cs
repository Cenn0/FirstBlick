using System.Data;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record ProjectTemplateDetails(string Name, string Description, ProjectType ProjectType, List<ProjectTemplateTask> Tasks);
public sealed record ProjectTemplateArchiveRequest(bool Archived);
public sealed record ProjectTemplatePreviewRequest(Guid TemplateId, long TemplateRevision, DateOnly? AnchorDate);
public sealed record ProjectTemplateApplyRequest(Guid TemplateId, long TemplateRevision, DateOnly? AnchorDate, string PreviewDigest);
public sealed record ProjectTemplateApplied(ProjectTemplateApplication Application, long ProjectRevision, IReadOnlyList<TaskItem> Tasks);
public sealed record ProjectTemplateSummary(Guid Id, Guid TenantId, long Revision, bool Archived, string Name, string Description,
    ProjectType ProjectType, int TaskCount);
public sealed record ProjectTemplateApplicationSummary(Guid Id, Guid TenantId, Guid TemplateId, Guid ProjectId, string TemplateName,
    long TemplateRevision, string ProjectName, DateOnly AnchorDate, ProjectTemplateAnchorKind AnchorKind, Guid AppliedBy,
    DateTimeOffset AppliedAt, int TaskCount, string Digest);

public static class ProjectTemplateEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/project-templates", Templates).WithTags("project-templates");
        api.MapGet("/project-templates/{id:guid}", async (Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await c.Commands.Authorize("project-templates", false, ct);
            var template = await c.Store.Get<ProjectTemplate>(id, ct); template.Validate();
            c.Http.Response.Headers.ETag = $"\"{template.Revision}\"";
            return Results.Ok(template);
        }).Produces<ProjectTemplate>().WithTags("project-templates");
        api.MapPost("/project-templates", (ProjectTemplateDetails body, [AsParameters] BusinessCommandServices c) =>
            c.Commands.Execute(c.Http, "project-templates", "project.template_create", body, async ct =>
            {
                var now = DateTimeOffset.UtcNow;
                var createdAt = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
                var template = new ProjectTemplate { TenantId = c.Scope.TenantId, Name = body.Name, Description = body.Description,
                    ProjectType = body.ProjectType, Tasks = body.Tasks, CreatedAt = createdAt, UpdatedAt = createdAt };
                await c.Store.Save(template, "project.template_created", true, ct); return template;
            })).Produces<ProjectTemplate>().WithTags("project-templates");
        api.MapPut("/project-templates/{id:guid}", (Guid id, ProjectTemplateDetails body, [AsParameters] BusinessCommandServices c) =>
            c.Commands.Execute(c.Http, "project-templates", "project.template_update", body, async ct =>
            {
                var template = await c.Current<ProjectTemplate>(id, ct);
                template.Name = body.Name; template.Description = body.Description; template.ProjectType = body.ProjectType; template.Tasks = body.Tasks;
                await c.Store.Save(template, "project.template_updated", false, ct); return template;
            }, async ct => { _ = await c.Current<ProjectTemplate>(id, ct, false); })).Produces<ProjectTemplate>().WithTags("project-templates");
        api.MapPost("/project-templates/{id:guid}/archive", (Guid id, ProjectTemplateArchiveRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Commands.Execute(c.Http, "project-templates", "project.template_availability", body, async ct =>
            {
                var template = await c.Store.Get<ProjectTemplate>(id, ct); CommandExecutor.Match(c.Http, template.Revision);
                Guard.Require(template.Archived != body.Archived, "project_template_archive_state", "The template already has the requested availability.");
                template.Archived = body.Archived;
                await c.Store.Save(template, body.Archived ? "project.template_archived" : "project.template_restored", false, ct); return template;
            })).Produces<ProjectTemplate>().WithTags("project-templates");

        api.MapPost("/projects/{id:guid}/template-preview", Preview).Produces<ProjectTemplatePreview>().WithTags("project-templates");
        api.MapPost("/projects/{id:guid}/template-apply", (Guid id, ProjectTemplateApplyRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Commands.Execute(c.Http, "projects", "project.template_apply", body, async ct =>
            {
                var project = await OpenProject(c, id, ct); CommandExecutor.Match(c.Http, project.Revision);
                var template = await c.Current<ProjectTemplate>(body.TemplateId, ct, false);
                RequireTemplateRevision(template, body.TemplateRevision);
                await NotApplied(c, id, template.Id, ct);
                var preview = ProjectTemplateWorkflow.Preview(template, project, body.AnchorDate);
                Guard.Require(preview.Digest == body.PreviewDigest, "project_template_preview_changed", "Reload and confirm a preview of the current template and project before applying it.");
                var (application, tasks) = ProjectTemplateWorkflow.Apply(preview, c.Scope.UserId, DateTimeOffset.UtcNow);
                await TaskDependencyEndpoints.EnsureAdditionCapacity(c.Db, project.Id, tasks.Count, ct);
                // Saving the evidence first makes its task provenance an ordinary tenant-qualified FK.
                // The enclosing CommandExecutor transaction commits all tasks, sources and receipt together.
                await c.Store.Save(application, "project.template_applied", true, ct);
                foreach (var task in tasks)
                {
                    ProjectTemplateWorkflow.EnsureTaskOrigin(task, application);
                    await c.Store.Save(task, "task.from_project_template", true, ct);
                }
                await c.Store.Save(project, "project.template_apply", false, ct);
                return new ProjectTemplateApplied(application, project.Revision, tasks);
            }, async ct =>
            {
                await c.Commands.Authorize("project-templates", false, ct); await c.Commands.Authorize("tasks", true, ct);
                _ = await OpenProject(c, id, ct); _ = await c.Current<ProjectTemplate>(body.TemplateId, ct, false);
            })).Produces<ProjectTemplateApplied>().WithTags("project-templates");

        api.MapGet("/projects/{id:guid}/template-applications", History).WithTags("project-templates");
        api.MapGet("/project-template-applications/{id:guid}", async (Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await c.Commands.Authorize("project-template-applications", false, ct); await c.Commands.Authorize("projects", false, ct);
            var application = await c.Store.Get<ProjectTemplateApplication>(id, ct); application.Validate();
            await RecordPolicy.EnsureVisible("projects", application.ProjectId, c.Scope, c.Store, ct);
            return Results.Ok(application);
        }).Produces<ProjectTemplateApplication>().WithTags("project-templates");
    }

    private static async Task<IResult> Templates([AsParameters] BusinessCommandServices c, CancellationToken ct,
        string search = "", int page = 1, bool includeArchived = false)
    {
        await c.Commands.Authorize("project-templates", false, ct); Guard.Text(search, "Search", 200, false);
        var term = search.Trim().ToLowerInvariant(); page = Math.Clamp(page, 1, 100_000); const int pageSize = 20;
        var query = c.Db.Entities.AsNoTracking().Where(x => x.Kind == "project-templates" && (includeArchived || !x.Archived));
        if (term.Length > 0) query = query.Where(x => x.SearchText.Contains(term));
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => x.Data).ToListAsync(ct);
        var items = rows.Select(row =>
        {
            var template = AppJson.Read<ProjectTemplate>(row); template.Validate();
            return new ProjectTemplateSummary(template.Id, template.TenantId, template.Revision, template.Archived,
                template.Name, template.Description, template.ProjectType, template.Tasks.Count);
        }).ToArray();
        return Results.Ok(new { items, total, page, pageSize });
    }

    private static async Task<IResult> Preview(Guid id, ProjectTemplatePreviewRequest body, [AsParameters] BusinessCommandServices c, CancellationToken ct)
    {
        await using var transaction = await c.Db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await c.Commands.Authorize("projects", true, ct); await c.Commands.Authorize("tasks", true, ct);
        await c.Commands.Authorize("project-templates", false, ct);
        var project = await OpenProject(c, id, ct); CommandExecutor.Match(c.Http, project.Revision);
        var template = await c.Current<ProjectTemplate>(body.TemplateId, ct, false);
        RequireTemplateRevision(template, body.TemplateRevision); await NotApplied(c, id, template.Id, ct);
        var preview = ProjectTemplateWorkflow.Preview(template, project, body.AnchorDate);
        await transaction.CommitAsync(ct); c.Http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(preview);
    }

    private static async Task<IResult> History(Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct, int page = 1)
    {
        await c.Commands.Authorize("project-template-applications", false, ct); await c.Commands.Authorize("projects", false, ct);
        await RecordPolicy.EnsureVisible("projects", id, c.Scope, c.Store, ct); _ = await c.Store.Get<Project>(id, ct);
        page = Math.Clamp(page, 1, 100_000); const int pageSize = 20;
        var links = c.Db.Links.Where(x => x.SourceKind == "project-template-applications" && x.TargetKind == "projects" && x.TargetId == id);
        var query = c.Db.Entities.AsNoTracking().Where(x => x.Kind == "project-template-applications" && links.Any(l => l.SourceId == x.Id));
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(x => x.Data).ToListAsync(ct);
        var items = rows.Select(row =>
        {
            var application = AppJson.Read<ProjectTemplateApplication>(row); application.Validate();
            Guard.Require(application.ProjectId == id, "project_template_evidence", "The application does not match its project link.");
            var preview = application.Preview!;
            return new ProjectTemplateApplicationSummary(application.Id, application.TenantId, application.TemplateId, id, preview.Template.Name,
                preview.Template.Revision, preview.Project.Name, preview.AnchorDate, preview.AnchorKind, application.AppliedBy,
                application.AppliedAt, application.Tasks.Count, application.Digest);
        }).ToArray();
        return Results.Ok(new { projectId = id, tenantId = c.Scope.TenantId, items, total, page, pageSize });
    }

    private static async Task<Project> OpenProject(BusinessCommandServices c, Guid id, CancellationToken ct)
    {
        var project = await c.Current<Project>(id, ct, false); project.Validate();
        Guard.Require(project.Status is not ProjectStatus.Completed and not ProjectStatus.Cancelled,
            "project_template_inactive", "Template application requires an open project.");
        return project;
    }

    private static void RequireTemplateRevision(ProjectTemplate template, long expected)
        => Guard.Require(expected > 0 && template.Revision == expected, "project_template_revision", "The template changed; review its current revision.");

    private static async Task NotApplied(BusinessCommandServices c, Guid projectId, Guid templateId, CancellationToken ct)
    {
        var key = $"project:{projectId:N}:template:{templateId:N}";
        Guard.Require(!await c.Db.Entities.AnyAsync(x => x.Kind == "project-template-applications" && x.UniqueKey == key, ct),
            "project_template_already_applied", "This template has already been applied to this project; review its existing tasks.");
    }
}
