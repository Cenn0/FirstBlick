using System.Net;
using System.Text.Json;
using FirstBlick.Api;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static FirstBlick.Api.Tests.PostgresApiFixture;
using TaskStatus = FirstBlick.Core.Business.TaskStatus;

namespace FirstBlick.Api.Tests;

public sealed class TaskDependencyApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Graph_read_rechecks_membership_after_observed_tenant_lock_wait()
    {
        var s = await Setup();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@tenant,0))", connection, transaction))
        {
            acquire.Parameters.AddWithValue("tenant", s.Owner.TenantId.ToString()); await acquire.ExecuteNonQueryAsync();
        }
        var response = s.Client.GetAsync($"/api/v1/projects/{s.Project.Id}/task-dependencies"); var waiting = false;
        for (var attempt = 0; attempt < 150 && !waiting; attempt++)
        {
            await using var inspect = new NpgsqlCommand("""
                SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND NOT granted
                  AND database=(SELECT oid FROM pg_database WHERE datname=current_database()))
                """, connection, transaction);
            waiting = (bool)(await inspect.ExecuteScalarAsync())!;
            if (!waiting) await Task.Delay(20);
        }
        Assert.True(waiting, "The request must reach the held tenant lock before membership revocation.");
        await using (var revoke = new NpgsqlCommand("UPDATE \"Memberships\" SET \"Active\"=false,\"Revision\"=\"Revision\"+1 WHERE \"TenantId\"=@tenant AND \"UserId\"=@user", connection, transaction))
        {
            revoke.Parameters.AddWithValue("tenant", s.Owner.TenantId); revoke.Parameters.AddWithValue("user", s.Owner.UserId);
            Assert.Equal(1, await revoke.ExecuteNonQueryAsync());
        }
        await transaction.CommitAsync(); await Error(response, "forbidden", HttpStatusCode.Forbidden);
    }

    [PostgresApiFact]
    public async Task Reviewed_dependencies_are_linked_replayed_and_survive_generic_edits()
    {
        var s = await Setup(); var key = Guid.NewGuid().ToString("N"); var body = new TaskDependencyRequest([s.First.Id]);
        var result = await Ok<TaskDependencyNode>(Send(s.Client, HttpMethod.Put, Path(s.Second), body, s.Second.Revision, key));
        Assert.Equal(2, result.Revision);
        using (var replay = await Send(s.Client, HttpMethod.Put, Path(s.Second), body, s.Second.Revision, key))
        {
            Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync());
            Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
            Assert.Equal(result.Id, AppJson.Read<TaskDependencyNode>(await replay.Content.ReadAsStringAsync()).Id);
        }
        await Error(Send(s.Client, HttpMethod.Put, Path(s.Second), body, s.Second.Revision), "revision_conflict", HttpStatusCode.Conflict);
        var generic = await Ok<TaskItem>(Send(s.Client, HttpMethod.Put, $"/api/v1/tasks/{s.Second.Id}", new { projectId = s.Project.Id, title = "Dachfläche schließen", status = "Open" }, result.Revision));
        Assert.Equal([s.First.Id], generic.PredecessorTaskIds);
        await Error(Send(s.Client, HttpMethod.Put, $"/api/v1/tasks/{generic.Id}", new { projectId = s.Project.Id, title = generic.Title, status = "Open", predecessorTaskIds = Array.Empty<Guid>() }, generic.Revision), "task_dependency_command_required");
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(1, await db.Links.IgnoreQueryFilters().CountAsync(x => x.TenantId == s.Owner.TenantId && x.SourceKind == "tasks" && x.SourceId == s.Second.Id && x.TargetKind == "tasks" && x.TargetId == s.First.Id));
            Assert.Equal(1, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == s.Owner.TenantId && x.EntityId == s.Second.Id && x.Action == "task.dependencies"));
        });
        var graph = await Ok<TaskDependencyGraph>(s.Client.GetAsync($"/api/v1/projects/{s.Project.Id}/task-dependencies"));
        Assert.Equal(s.Owner.TenantId, graph.TenantId); Assert.Equal(2, graph.Tasks.Count);
        Assert.Equal([s.First.Id], graph.Tasks.Single(x => x.Id == s.Second.Id).PredecessorTaskIds);
    }

    [PostgresApiFact]
    public async Task Generic_and_dedicated_status_changes_cannot_start_before_predecessors_or_reopen_them_afterwards()
    {
        var s = await Setup(); var dependent = await Set(s, s.Second, s.First.Id);
        await Error(Status(s.Client, dependent, TaskStatus.InProgress), "task_dependency_blocked");
        var current = await fixture.Read<TaskItem>(s.Owner.TenantId, s.Second.Id); current.Status = TaskStatus.Done;
        await Error(Send(s.Client, HttpMethod.Put, $"/api/v1/tasks/{current.Id}", current, current.Revision), "task_dependency_blocked");
        var first = await Ok<TaskDependencyNode>(Status(s.Client, Node(s.First), TaskStatus.Done));
        dependent = await Ok<TaskDependencyNode>(Status(s.Client, dependent, TaskStatus.InProgress));
        await Error(Status(s.Client, first, TaskStatus.Open), "task_dependency_blocked");
        dependent = await Ok<TaskDependencyNode>(Status(s.Client, dependent, TaskStatus.Done));
        var prior = await fixture.Read<TaskItem>(s.Owner.TenantId, s.First.Id); prior.Status = TaskStatus.Cancelled;
        await Error(Send(s.Client, HttpMethod.Put, $"/api/v1/tasks/{prior.Id}", prior, prior.Revision), "task_dependency_blocked");
        await Error(Send(s.Client, HttpMethod.Delete, $"/api/v1/tasks/{prior.Id}", revision: prior.Revision), "record_in_use");
        await Error(Send(s.Client, HttpMethod.Put, Path(s.Second), new TaskDependencyRequest([]), dependent.Revision), "task_dependency_locked");
    }

    [PostgresApiFact]
    public async Task Concurrent_opposite_edges_cannot_commit_a_cycle()
    {
        var s = await Setup();
        var responses = await Task.WhenAll(Send(s.Client, HttpMethod.Put, Path(s.First), new TaskDependencyRequest([s.Second.Id]), s.First.Revision),
            Send(s.Client, HttpMethod.Put, Path(s.Second), new TaskDependencyRequest([s.First.Id]), s.Second.Revision));
        Assert.Single(responses, x => x.IsSuccessStatusCode);
        var rejected = responses.Single(x => !x.IsSuccessStatusCode); Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("task_dependency_cycle", JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        var tasks = new[] { await fixture.Read<TaskItem>(s.Owner.TenantId, s.First.Id), await fixture.Read<TaskItem>(s.Owner.TenantId, s.Second.Id) };
        TaskDependencies.ValidateGraph(tasks); Assert.Equal(1, tasks.Sum(x => x.PredecessorTaskIds?.Count ?? 0));
        foreach (var response in responses) response.Dispose();
    }

    [PostgresApiFact]
    public async Task Foreign_missing_self_duplicate_and_archived_predecessors_are_rejected_atomically()
    {
        var s = await Setup(); var other = await Setup();
        foreach (var ids in new List<Guid>[] { [Guid.NewGuid()], [other.First.Id] })
            await Error(Send(s.Client, HttpMethod.Put, Path(s.Second), new TaskDependencyRequest(ids), s.Second.Revision), "task_dependency_reference");
        await Error(Send(s.Client, HttpMethod.Put, Path(s.Second), new TaskDependencyRequest([s.Second.Id]), s.Second.Revision), "task_dependency_invalid");
        await Error(Send(s.Client, HttpMethod.Put, Path(s.Second), new TaskDependencyRequest([s.First.Id, s.First.Id]), s.Second.Revision), "task_dependency_invalid");
        await Error(Send(s.Client, HttpMethod.Put, Path(s.Second), new TaskDependencyRequest(Enumerable.Range(0, 17).Select(_ => Guid.NewGuid()).ToList()), s.Second.Revision), "task_dependency_limit");
        await Ok<TaskItem>(Send(s.Client, HttpMethod.Delete, $"/api/v1/tasks/{s.First.Id}", revision: s.First.Revision));
        await Error(Send(s.Client, HttpMethod.Put, Path(s.Second), new TaskDependencyRequest([s.First.Id]), s.Second.Revision), "task_dependency_reference");
        Assert.Equal(1, (await fixture.Read<TaskItem>(s.Owner.TenantId, s.Second.Id)).Revision);
        await Error(s.Client.GetAsync($"/api/v1/projects/{other.Project.Id}/task-dependencies"), "not_found", HttpStatusCode.NotFound);
    }

    [PostgresApiFact]
    public async Task Project_move_cannot_hide_a_live_dependency_and_new_generic_tasks_cannot_fabricate_edges()
    {
        var s = await Setup(); await Set(s, s.Second, s.First.Id);
        var project = await Ok<Project>(Send(s.Client, HttpMethod.Post, "/api/v1/projects", new { customerId = s.Project.CustomerId, buildingId = s.Project.BuildingId, name = "Zweites Projekt" }));
        var first = await fixture.Read<TaskItem>(s.Owner.TenantId, s.First.Id); first.ProjectId = project.Id;
        await Error(Send(s.Client, HttpMethod.Put, $"/api/v1/tasks/{first.Id}", first, first.Revision), "task_dependency_project");
        await Error(Send(s.Client, HttpMethod.Post, "/api/v1/tasks", new { projectId = s.Project.Id, title = "Erfundene Abhängigkeit", predecessorTaskIds = new[] { s.First.Id } }), "task_dependency_command_required");
    }

    [PostgresApiFact]
    public async Task Linked_graph_capacity_serializes_new_tasks_and_rejects_moves_without_blocking_existing_work()
    {
        var s = await Setup(); var dependent = await Set(s, s.Second, s.First.Id);
        await SeedUnlinkedTasks(s, TaskDependencies.MaximumTasks - 3); // 999 persisted tasks, including the existing edge.
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Send(s.Client, HttpMethod.Post, "/api/v1/tasks",
            new { projectId = s.Project.Id, title = $"Letzter freier Platz {index}" })));
        Assert.Single(responses, response => response.IsSuccessStatusCode);
        var rejected = Assert.Single(responses, response => !response.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("task_graph_limit", JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        var added = AppJson.Read<TaskItem>(await responses.Single(response => response.IsSuccessStatusCode).Content.ReadAsStringAsync());
        foreach (var response in responses) response.Dispose();

        var otherProject = await OtherProject(s);
        var moving = await Ok<TaskItem>(Send(s.Client, HttpMethod.Post, "/api/v1/tasks", new { projectId = otherProject.Id, title = "Umzuplanende Aufgabe" }));
        moving.ProjectId = s.Project.Id;
        await Error(Send(s.Client, HttpMethod.Put, $"/api/v1/tasks/{moving.Id}", moving, moving.Revision), "task_graph_limit");
        var retained = await fixture.Read<TaskItem>(s.Owner.TenantId, moving.Id);
        Assert.Equal(otherProject.Id, retained.ProjectId); Assert.Equal(moving.Revision, retained.Revision);

        added.Title = "Bestehende freie Aufgabe bearbeitet";
        await Ok<TaskItem>(Send(s.Client, HttpMethod.Put, $"/api/v1/tasks/{added.Id}", added, added.Revision));
        await Ok<TaskDependencyNode>(Status(s.Client, Node(s.First), TaskStatus.Done));
        await Ok<TaskDependencyNode>(Status(s.Client, dependent, TaskStatus.InProgress));
        var graph = await Ok<TaskDependencyGraph>(s.Client.GetAsync($"/api/v1/projects/{s.Project.Id}/task-dependencies"));
        Assert.Equal(TaskDependencies.MaximumTasks, graph.Tasks.Count);
        Assert.Equal(TaskStatus.InProgress, graph.Tasks.Single(task => task.Id == s.Second.Id).Status);
    }

    [PostgresApiFact]
    public async Task Template_batch_rechecks_graph_capacity_after_preview_and_leaves_no_partial_application()
    {
        var s = await Setup(); await Set(s, s.Second, s.First.Id);
        await SeedUnlinkedTasks(s, TaskDependencies.MaximumTasks - 4); // Two remaining places at preview time.
        var template = await Template(s, 2); var preview = await TemplatePreview(s, template);
        await Ok<TaskItem>(Send(s.Client, HttpMethod.Post, "/api/v1/tasks", new { projectId = s.Project.Id, title = "Zwischenzeitlich angelegte Aufgabe" }));
        var key = Guid.NewGuid().ToString("N");
        await Error(ApplyTemplate(s, template, preview, key), "task_graph_limit");
        Assert.Equal(TaskDependencies.MaximumTasks - 1, await ProjectTaskCount(s));
        Assert.Equal(s.Project.Revision, (await fixture.Read<Project>(s.Owner.TenantId, s.Project.Id)).Revision);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.False(await db.Entities.IgnoreQueryFilters().AnyAsync(row => row.TenantId == s.Owner.TenantId && row.Kind == "project-template-applications"));
            Assert.False(await db.Receipts.IgnoreQueryFilters().AnyAsync(row => row.TenantId == s.Owner.TenantId && row.ActorId == s.Owner.UserId && row.Key == key));
            Assert.False(await db.Audit.IgnoreQueryFilters().AnyAsync(row => row.TenantId == s.Owner.TenantId && row.Action == "project.template_applied"));
        });

        var single = await Template(s, 1); var singlePreview = await TemplatePreview(s, single);
        var applied = await Ok<ProjectTemplateApplied>(ApplyTemplate(s, single, singlePreview));
        Assert.Single(applied.Tasks);
        var graph = await Ok<TaskDependencyGraph>(s.Client.GetAsync($"/api/v1/projects/{s.Project.Id}/task-dependencies"));
        Assert.Equal(TaskDependencies.MaximumTasks, graph.Tasks.Count);
    }

    [PostgresApiFact]
    public async Task Unlinked_legacy_projects_can_grow_move_edit_and_apply_templates_above_the_graph_limit()
    {
        var s = await Setup(); await SeedUnlinkedTasks(s, TaskDependencies.MaximumTasks - 1);
        s.First.Title = "Bestehende Aufgabe bleibt bearbeitbar";
        await Ok<TaskItem>(Send(s.Client, HttpMethod.Put, $"/api/v1/tasks/{s.First.Id}", s.First, s.First.Revision));
        await Ok<TaskItem>(Send(s.Client, HttpMethod.Post, "/api/v1/tasks", new { projectId = s.Project.Id, title = "Weitere unverknüpfte Aufgabe" }));
        var otherProject = await OtherProject(s);
        var moving = await Ok<TaskItem>(Send(s.Client, HttpMethod.Post, "/api/v1/tasks", new { projectId = otherProject.Id, title = "Unverknüpfter Projektwechsel" }));
        moving.ProjectId = s.Project.Id;
        await Ok<TaskItem>(Send(s.Client, HttpMethod.Put, $"/api/v1/tasks/{moving.Id}", moving, moving.Revision));
        var template = await Template(s, 2); var preview = await TemplatePreview(s, template);
        var applied = await Ok<ProjectTemplateApplied>(ApplyTemplate(s, template, preview));
        Assert.Equal(2, applied.Tasks.Count); Assert.Equal(TaskDependencies.MaximumTasks + 5, await ProjectTaskCount(s));
    }

    [PostgresApiFact]
    public async Task Readers_cannot_mutate_and_replay_checks_current_role_and_module()
    {
        var s = await Setup(); var key = Guid.NewGuid().ToString("N"); var body = new TaskDependencyRequest([s.First.Id]);
        await Ok<TaskDependencyNode>(Send(s.Client, HttpMethod.Put, Path(s.Second), body, s.Second.Revision, key));
        var accounting = await fixture.Login(await fixture.CreateUser(s.Owner.TenantId, BusinessRole.Accounting));
        await Ok<TaskDependencyGraph>(accounting.GetAsync($"/api/v1/projects/{s.Project.Id}/task-dependencies"));
        await Error(Send(accounting, HttpMethod.Put, Path(s.Second), body, 2), "forbidden", HttpStatusCode.Forbidden);
        var worker = await fixture.Login(await fixture.CreateUser(s.Owner.TenantId, BusinessRole.Worker));
        await Error(worker.GetAsync($"/api/v1/projects/{s.Project.Id}/task-dependencies"), "forbidden", HttpStatusCode.Forbidden);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var member = await db.Memberships.IgnoreQueryFilters().SingleAsync(x => x.TenantId == s.Owner.TenantId && x.UserId == s.Owner.UserId);
            member.Role = BusinessRole.Accounting; await db.SaveChangesAsync();
        });
        await Error(Send(s.Client, HttpMethod.Put, Path(s.Second), body, s.Second.Revision, key), "forbidden", HttpStatusCode.Forbidden);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var member = await db.Memberships.IgnoreQueryFilters().SingleAsync(x => x.TenantId == s.Owner.TenantId && x.UserId == s.Owner.UserId);
            member.Role = BusinessRole.ManagingDirector; var tenant = await db.Tenants.SingleAsync(x => x.Id == s.Owner.TenantId); tenant.Modules = []; await db.SaveChangesAsync();
        });
        await Error(Send(s.Client, HttpMethod.Put, Path(s.Second), body, s.Second.Revision, key), "module_disabled", HttpStatusCode.Forbidden);
        await Ok<TaskDependencyGraph>(s.Client.GetAsync($"/api/v1/projects/{s.Project.Id}/task-dependencies"));
    }

    private sealed record SetupData(ApiTestUser Owner, HttpClient Client, Project Project, TaskItem First, TaskItem Second);
    private async Task<SetupData> Setup()
    {
        var tenant = await fixture.CreateTenant(modules: ["core"]); var owner = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(owner);
        var customer = await Ok<Customer>(Send(client, HttpMethod.Post, "/api/v1/customers", new { name = "Testkunde" }));
        var building = await Ok<Building>(Send(client, HttpMethod.Post, "/api/v1/buildings", new { customerId = customer.Id, name = "Testdach", address = "Teststraße 1" }));
        var project = await Ok<Project>(Send(client, HttpMethod.Post, "/api/v1/projects", new { customerId = customer.Id, buildingId = building.Id, name = "Dachsanierung", status = "Planning" }));
        var first = await Ok<TaskItem>(Send(client, HttpMethod.Post, "/api/v1/tasks", new { projectId = project.Id, title = "Material prüfen" }));
        var second = await Ok<TaskItem>(Send(client, HttpMethod.Post, "/api/v1/tasks", new { projectId = project.Id, title = "Dach schließen" }));
        return new(owner, client, project, first, second);
    }
    private Task SeedUnlinkedTasks(SetupData s, int count) => fixture.InScope(async services =>
    {
        var db = services.GetRequiredService<FirstBlickDbContext>();
        for (var index = 0; index < count; index++)
        {
            var task = new TaskItem { TenantId = s.Owner.TenantId, ProjectId = s.Project.Id, Title = $"Vorhandene Aufgabe {index}" }; task.Validate();
            db.Entities.Add(new StoredEntity { TenantId = task.TenantId, Kind = "tasks", Id = task.Id, Revision = task.Revision,
                CreatedAt = task.CreatedAt, UpdatedAt = task.UpdatedAt, Data = AppJson.Serialize(task) });
            foreach (var reference in task.References())
                db.Links.Add(new EntityLink { TenantId = task.TenantId, SourceKind = "tasks", SourceId = task.Id,
                    TargetKind = reference.Kind, TargetId = reference.Id });
        }
        await db.SaveChangesAsync();
    });
    private Task<int> ProjectTaskCount(SetupData s) => fixture.InScope(async services =>
        await services.GetRequiredService<FirstBlickDbContext>().Links.IgnoreQueryFilters().CountAsync(link => link.TenantId == s.Owner.TenantId
            && link.SourceKind == "tasks" && link.TargetKind == "projects" && link.TargetId == s.Project.Id));
    private static Task<Project> OtherProject(SetupData s) => Ok<Project>(Send(s.Client, HttpMethod.Post, "/api/v1/projects",
        new { customerId = s.Project.CustomerId, buildingId = s.Project.BuildingId, name = "Weiteres Projekt" }));
    private static Task<ProjectTemplate> Template(SetupData s, int count) => Ok<ProjectTemplate>(Send(s.Client, HttpMethod.Post, "/api/v1/project-templates",
        new ProjectTemplateDetails($"Zusätzliche Aufgaben {count}", "Kapazitätsprüfung", s.Project.Type,
            Enumerable.Range(0, count).Select(index => new ProjectTemplateTask($"Vorlagenaufgabe {index}", "", index)).ToList())));
    private static Task<ProjectTemplatePreview> TemplatePreview(SetupData s, ProjectTemplate template) => Ok<ProjectTemplatePreview>(Send(s.Client,
        HttpMethod.Post, $"/api/v1/projects/{s.Project.Id}/template-preview", new ProjectTemplatePreviewRequest(template.Id, template.Revision, new DateOnly(2028, 2, 28)), s.Project.Revision));
    private static Task<HttpResponseMessage> ApplyTemplate(SetupData s, ProjectTemplate template, ProjectTemplatePreview preview, string? key = null) =>
        Send(s.Client, HttpMethod.Post, $"/api/v1/projects/{s.Project.Id}/template-apply",
            new ProjectTemplateApplyRequest(template.Id, template.Revision, preview.AnchorDate, preview.Digest), s.Project.Revision, key);
    private static string Path(TaskItem task) => $"/api/v1/tasks/{task.Id}/dependencies";
    private static TaskDependencyNode Node(TaskItem task) => new(task.Id, task.Revision, task.Title, task.Status, task.Archived, task.PredecessorTaskIds ?? []);
    private static Task<TaskDependencyNode> Set(SetupData s, TaskItem task, Guid predecessor) => Ok<TaskDependencyNode>(Send(s.Client, HttpMethod.Put, Path(task), new TaskDependencyRequest([predecessor]), task.Revision));
    private static Task<HttpResponseMessage> Status(HttpClient client, TaskDependencyNode task, TaskStatus status) => Send(client, HttpMethod.Post, $"/api/v1/tasks/{task.Id}/dependency-status", new TaskDependencyStatusRequest(status), task.Revision);
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, text); return AppJson.Read<T>(text);
    }
    private static async Task Error(Task<HttpResponseMessage> pending, string code, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync(); Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        Assert.Equal(code, JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
    }
}
