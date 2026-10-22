using System.Net;
using System.Text.Json;
using FirstBlick.Api;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class ProjectTemplateApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Templates_are_manageable_searchable_archivable_and_recoverable_without_losing_identity()
    {
        var s = await Setup();
        var first = await Json(s.Client.GetAsync("/api/v1/project-templates?search=Sanierung"));
        Assert.Equal(1, first.GetProperty("total").GetInt32());
        Assert.Equal(3, first.GetProperty("items")[0].GetProperty("taskCount").GetInt32());
        Assert.False(first.GetProperty("items")[0].TryGetProperty("tasks", out _));
        var details = Details() with { Name = "Vorbereitung Dachsanierung", Tasks = [new("Prüfen", "Dokumentieren", -7), new("Ausführen", "", 0)] };
        var updated = await Ok<ProjectTemplate>(Send(s.Client, HttpMethod.Put, TemplatePath(s.Template), details, s.Template.Revision));
        Assert.Equal(s.Template.Id, updated.Id); Assert.Equal(s.Template.Revision + 1, updated.Revision); Assert.Equal(-7, updated.Tasks[0].OffsetDays);
        Assert.Equal(s.Template.CreatedAt, updated.CreatedAt);
        await Error(Send(s.Client, HttpMethod.Put, TemplatePath(s.Template), details, s.Template.Revision), "revision_conflict", HttpStatusCode.Conflict);
        var archived = await Ok<ProjectTemplate>(Send(s.Client, HttpMethod.Post, TemplatePath(s.Template) + "/archive", new ProjectTemplateArchiveRequest(true), updated.Revision));
        Assert.True(archived.Archived);
        Assert.Equal(0, (await Json(s.Client.GetAsync("/api/v1/project-templates"))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(s.Client.GetAsync("/api/v1/project-templates?includeArchived=true"))).GetProperty("total").GetInt32());
        await Error(Send(s.Client, HttpMethod.Put, TemplatePath(s.Template), details, archived.Revision), "not_found", HttpStatusCode.NotFound);
        var restored = await Ok<ProjectTemplate>(Send(s.Client, HttpMethod.Post, TemplatePath(s.Template) + "/archive", new ProjectTemplateArchiveRequest(false), archived.Revision));
        Assert.False(restored.Archived); Assert.Equal(archived.Revision + 1, restored.Revision);
    }

    [PostgresApiFact]
    public async Task Preview_has_no_mutations_and_apply_commits_tasks_original_links_audit_and_one_receipt_atomically()
    {
        var s = await Setup(); var before = await Count(s.Owner.TenantId, "tasks");
        var receiptsBefore = await ReceiptCount(s.Owner.TenantId);
        var preview = await Preview(s); preview.Validate();
        Assert.Equal(before, await Count(s.Owner.TenantId, "tasks")); Assert.Equal(receiptsBefore, await ReceiptCount(s.Owner.TenantId));
        Assert.Equal(s.Template.Revision, preview.Template.Revision); Assert.Equal(s.Project.Revision, preview.Project.Revision);
        var result = await Apply(s, preview); result.Application.Validate();
        Assert.Equal(s.Project.Revision + 1, result.ProjectRevision); Assert.Equal(3, result.Tasks.Count);
        Assert.Equal(1, await Count(s.Owner.TenantId, "project-template-applications")); Assert.Equal(before + 3, await Count(s.Owner.TenantId, "tasks"));
        Assert.Equal(receiptsBefore + 1, await ReceiptCount(s.Owner.TenantId));
        foreach (var task in result.Tasks)
        {
            ProjectTemplateWorkflow.EnsureTaskOrigin(task, result.Application); Assert.Null(task.AssignedEmployeeId);
            var stored = await fixture.Read<TaskItem>(s.Owner.TenantId, task.Id); Assert.Equal(task.TemplateOrigin, stored.TemplateOrigin);
        }
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(3, await db.Links.IgnoreQueryFilters().CountAsync(x => x.TenantId == s.Owner.TenantId
                && x.SourceKind == "tasks" && x.TargetKind == "project-template-applications" && x.TargetId == result.Application.Id));
            Assert.Equal(1, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == s.Owner.TenantId && x.EntityId == result.Application.Id));
            Assert.Equal($"project:{s.Project.Id:N}:template:{s.Template.Id:N}", (await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.TenantId == s.Owner.TenantId && x.Id == result.Application.Id)).UniqueKey);
        });
    }

    [PostgresApiFact]
    public async Task Idempotent_replay_returns_original_and_new_keys_cannot_apply_the_same_template_again()
    {
        var s = await Setup(); var preview = await Preview(s); var key = Guid.NewGuid().ToString("N");
        var body = ApplyBody(s.Template, preview); var result = await Apply(s, preview, key);
        using (var replay = await Send(s.Client, HttpMethod.Post, ApplyPath(s.Project), body, s.Project.Revision, key))
        {
            Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync());
            Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
            Assert.Equal(result.Application.Id, AppJson.Read<ProjectTemplateApplied>(await replay.Content.ReadAsStringAsync()).Application.Id);
        }
        await Error(Send(s.Client, HttpMethod.Post, ApplyPath(s.Project), body with { AnchorDate = preview.AnchorDate.AddDays(1) }, s.Project.Revision, key),
            "idempotency_conflict", HttpStatusCode.Conflict);
        await Error(Send(s.Client, HttpMethod.Post, ApplyPath(s.Project), body, result.ProjectRevision), "project_template_already_applied", HttpStatusCode.Conflict);
        var updated = await Ok<ProjectTemplate>(Send(s.Client, HttpMethod.Put, TemplatePath(s.Template), Details() with { Name = "Neue Version" }, s.Template.Revision));
        await Error(Send(s.Client, HttpMethod.Post, ApplyPath(s.Project), body with { TemplateRevision = updated.Revision }, result.ProjectRevision),
            "project_template_already_applied", HttpStatusCode.Conflict);
        Assert.Equal(3, await Count(s.Owner.TenantId, "tasks"));
    }

    [PostgresApiFact]
    public async Task Source_revision_and_digest_changes_invalidate_the_preview_without_partial_work()
    {
        var s = await Setup(); var preview = await Preview(s);
        await Error(Send(s.Client, HttpMethod.Post, ApplyPath(s.Project), ApplyBody(s.Template, preview)), "revision_required", HttpStatusCode.PreconditionRequired);
        await Error(Send(s.Client, HttpMethod.Post, ApplyPath(s.Project), ApplyBody(s.Template, preview) with { PreviewDigest = new string('0', 64) }, s.Project.Revision),
            "project_template_preview_changed", HttpStatusCode.Conflict);
        var changed = await Ok<ProjectTemplate>(Send(s.Client, HttpMethod.Put, TemplatePath(s.Template), Details() with { Name = "Geändert" }, s.Template.Revision));
        await Error(Send(s.Client, HttpMethod.Post, ApplyPath(s.Project), ApplyBody(s.Template, preview), s.Project.Revision), "project_template_revision", HttpStatusCode.Conflict);
        await Error(Send(s.Client, HttpMethod.Post, ApplyPath(s.Project), ApplyBody(changed, preview), s.Project.Revision), "project_template_preview_changed", HttpStatusCode.Conflict);
        s.Project.Name = "Neuer Projektname";
        var project = await Ok<Project>(Send(s.Client, HttpMethod.Put, $"/api/v1/projects/{s.Project.Id}", s.Project, s.Project.Revision));
        await Error(Send(s.Client, HttpMethod.Post, ApplyPath(s.Project), ApplyBody(changed, preview), s.Project.Revision), "revision_conflict", HttpStatusCode.Conflict);
        await Error(Send(s.Client, HttpMethod.Post, ApplyPath(s.Project), ApplyBody(changed, preview), project.Revision), "project_template_preview_changed", HttpStatusCode.Conflict);
        Assert.Equal(0, await Count(s.Owner.TenantId, "tasks")); Assert.Equal(0, await Count(s.Owner.TenantId, "project-template-applications"));
    }

    [PostgresApiFact]
    public async Task Concurrent_applications_create_exactly_one_batch()
    {
        var s = await Setup(); var preview = await Preview(s); var body = ApplyBody(s.Template, preview);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Send(s.Client, HttpMethod.Post, ApplyPath(s.Project), body, s.Project.Revision)));
        Assert.Single(responses, x => x.IsSuccessStatusCode); Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in responses) response.Dispose();
        Assert.Equal(1, await Count(s.Owner.TenantId, "project-template-applications")); Assert.Equal(3, await Count(s.Owner.TenantId, "tasks"));
    }

    [PostgresApiFact]
    public async Task Templates_can_be_reused_on_another_project_and_explicit_anchor_requires_a_new_confirmation()
    {
        var s = await Setup(); await Apply(s, await Preview(s)); var other = await ProjectFor(s.Client, s.Customer, s.Building);
        other.PlannedStart = null; other = await Ok<Project>(Send(s.Client, HttpMethod.Put, $"/api/v1/projects/{other.Id}", other, other.Revision));
        var request = new ProjectTemplatePreviewRequest(s.Template.Id, s.Template.Revision, null);
        await Error(Send(s.Client, HttpMethod.Post, PreviewPath(other), request, other.Revision), "project_template_anchor");
        var anchor = new DateOnly(2030, 12, 31);
        var preview = await Ok<ProjectTemplatePreview>(Send(s.Client, HttpMethod.Post, PreviewPath(other), request with { AnchorDate = anchor }, other.Revision));
        Assert.Equal(ProjectTemplateAnchorKind.Explicit, preview.AnchorKind); Assert.Equal(new DateOnly(2031, 1, 3), preview.Tasks[2].DueDate);
        await Error(Send(s.Client, HttpMethod.Post, ApplyPath(other), ApplyBody(s.Template, preview) with { AnchorDate = anchor.AddDays(1) }, other.Revision),
            "project_template_preview_changed", HttpStatusCode.Conflict);
        var result = await Ok<ProjectTemplateApplied>(Send(s.Client, HttpMethod.Post, ApplyPath(other), ApplyBody(s.Template, preview), other.Revision));
        Assert.Equal(other.Id, result.Application.ProjectId); Assert.Equal(2, await Count(s.Owner.TenantId, "project-template-applications"));
    }

    [PostgresApiFact]
    public async Task Historical_application_survives_template_edits_archive_and_editing_or_archiving_generated_tasks()
    {
        var s = await Setup(); var result = await Apply(s, await Preview(s)); var original = await Stored(s.Owner.TenantId, result.Application.Id);
        var changed = await Ok<ProjectTemplate>(Send(s.Client, HttpMethod.Put, TemplatePath(s.Template), Details() with { Name = "Andere zukünftige Aufgaben", Tasks = [new("Neu", "", 14)] }, s.Template.Revision));
        await Ok<ProjectTemplate>(Send(s.Client, HttpMethod.Post, TemplatePath(s.Template) + "/archive", new ProjectTemplateArchiveRequest(true), changed.Revision));
        var task = result.Tasks[0]; task.Title = "Für das Objekt angepasst"; task.DueDate = new(2028, 3, 10); task.Status = FirstBlick.Core.Business.TaskStatus.Done;
        var edited = await Ok<TaskItem>(Send(s.Client, HttpMethod.Put, $"/api/v1/tasks/{task.Id}", task, task.Revision));
        await Ok<TaskItem>(Send(s.Client, HttpMethod.Delete, $"/api/v1/tasks/{edited.Id}", revision: edited.Revision));
        var history = await Json(s.Client.GetAsync($"/api/v1/projects/{s.Project.Id}/template-applications"));
        Assert.Equal(1, history.GetProperty("total").GetInt32()); Assert.Equal("Sanierung vorbereiten", history.GetProperty("items")[0].GetProperty("templateName").GetString());
        var retained = await Ok<ProjectTemplateApplication>(s.Client.GetAsync($"/api/v1/project-template-applications/{result.Application.Id}"));
        Assert.Equal(3, retained.Preview!.Tasks.Count); Assert.Equal("Material prüfen", retained.Preview.Tasks[0].Title);
        Assert.Equal(original, await Stored(s.Owner.TenantId, retained.Id));
        ProjectTemplateWorkflow.EnsureTaskOrigin(await fixture.Read<TaskItem>(s.Owner.TenantId, task.Id), retained);
    }

    [PostgresApiFact]
    public async Task Generic_mutation_cannot_fabricate_or_remove_task_provenance_or_move_it_to_another_project()
    {
        var s = await Setup(); var result = await Apply(s, await Preview(s)); var task = result.Tasks[0]; var origin = task.TemplateOrigin;
        await Error(Send(s.Client, HttpMethod.Post, "/api/v1/tasks", task), "command_required");
        task.TemplateOrigin = null;
        await Error(Send(s.Client, HttpMethod.Put, $"/api/v1/tasks/{task.Id}", task, task.Revision), "project_template_origin");
        task.TemplateOrigin = origin; var other = await ProjectFor(s.Client, s.Customer, s.Building); task.ProjectId = other.Id;
        await Error(Send(s.Client, HttpMethod.Put, $"/api/v1/tasks/{task.Id}", task, task.Revision), "project_template_origin");
        using var forged = await Send(s.Client, HttpMethod.Post, "/api/v1/project-template-applications", result.Application);
        Assert.Contains(forged.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        using var rewritten = await Send(s.Client, HttpMethod.Put, $"/api/v1/project-template-applications/{result.Application.Id}", result.Application, 1);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, rewritten.StatusCode);
        Assert.Equal(s.Project.Id, (await fixture.Read<TaskItem>(s.Owner.TenantId, task.Id)).ProjectId);
    }

    [PostgresApiFact]
    public async Task Invalid_type_overflow_closed_and_archived_sources_leave_no_partial_application()
    {
        var s = await Setup(); var request = new ProjectTemplatePreviewRequest(s.Template.Id, s.Template.Revision, null);
        s.Project.Type = ProjectType.Repair;
        var differentType = await Ok<Project>(Send(s.Client, HttpMethod.Put, $"/api/v1/projects/{s.Project.Id}", s.Project, s.Project.Revision));
        await Error(Send(s.Client, HttpMethod.Post, PreviewPath(s.Project), request, differentType.Revision), "project_template_type");
        differentType.Type = ProjectType.Renovation;
        var restored = await Ok<Project>(Send(s.Client, HttpMethod.Put, $"/api/v1/projects/{s.Project.Id}", differentType, differentType.Revision));
        await Error(Send(s.Client, HttpMethod.Post, PreviewPath(restored), request with { AnchorDate = new(9999, 12, 31) }, restored.Revision), "project_template_date");
        restored.Status = ProjectStatus.Completed;
        var completed = await Ok<Project>(Send(s.Client, HttpMethod.Put, $"/api/v1/projects/{s.Project.Id}", restored, restored.Revision));
        await Error(Send(s.Client, HttpMethod.Post, PreviewPath(completed), request, completed.Revision), "project_template_inactive");
        var other = await ProjectFor(s.Client, s.Customer, s.Building);
        var template = await Ok<ProjectTemplate>(Send(s.Client, HttpMethod.Post, TemplatePath(s.Template) + "/archive", new ProjectTemplateArchiveRequest(true), s.Template.Revision));
        await Error(Send(s.Client, HttpMethod.Post, PreviewPath(other), request with { TemplateRevision = template.Revision }, other.Revision), "not_found", HttpStatusCode.NotFound);
        Assert.Equal(0, await Count(s.Owner.TenantId, "tasks")); Assert.Equal(0, await Count(s.Owner.TenantId, "project-template-applications"));
    }

    [PostgresApiFact]
    public async Task Tenant_boundaries_cover_templates_projects_applications_and_history()
    {
        var s = await Setup(); var other = await Setup(); var result = await Apply(s, await Preview(s));
        await Error(other.Client.GetAsync(TemplatePath(s.Template)), "not_found", HttpStatusCode.NotFound);
        await Error(other.Client.GetAsync($"/api/v1/project-template-applications/{result.Application.Id}"), "not_found", HttpStatusCode.NotFound);
        await Error(other.Client.GetAsync($"/api/v1/projects/{s.Project.Id}/template-applications"), "not_found", HttpStatusCode.NotFound);
        await Error(Send(other.Client, HttpMethod.Post, PreviewPath(other.Project), new ProjectTemplatePreviewRequest(s.Template.Id, s.Template.Revision, null), other.Project.Revision), "not_found", HttpStatusCode.NotFound);
        await Error(Send(s.Client, HttpMethod.Post, PreviewPath(other.Project), new ProjectTemplatePreviewRequest(s.Template.Id, s.Template.Revision, null), other.Project.Revision), "not_found", HttpStatusCode.NotFound);
        Assert.Equal(0, await Count(other.Owner.TenantId, "tasks"));
    }

    [PostgresApiFact]
    public async Task Current_permissions_are_enforced_before_replay_and_readers_have_history_only()
    {
        var s = await Setup(); var preview = await Preview(s); var key = Guid.NewGuid().ToString("N"); var result = await Apply(s, preview, key);
        var reader = await fixture.Login(await fixture.CreateUser(s.Owner.TenantId, BusinessRole.Accounting));
        await Ok<ProjectTemplate>(reader.GetAsync(TemplatePath(s.Template)));
        await Ok<ProjectTemplateApplication>(reader.GetAsync($"/api/v1/project-template-applications/{result.Application.Id}"));
        await Error(Send(reader, HttpMethod.Post, "/api/v1/project-templates", Details()), "forbidden", HttpStatusCode.Forbidden);
        await Error(Send(reader, HttpMethod.Post, PreviewPath(s.Project), new ProjectTemplatePreviewRequest(s.Template.Id, s.Template.Revision, null), result.ProjectRevision), "forbidden", HttpStatusCode.Forbidden);
        var worker = await fixture.Login(await fixture.CreateUser(s.Owner.TenantId, BusinessRole.Worker));
        await Error(worker.GetAsync("/api/v1/project-templates"), "forbidden", HttpStatusCode.Forbidden);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var member = await db.Memberships.IgnoreQueryFilters().SingleAsync(x => x.TenantId == s.Owner.TenantId && x.UserId == s.Owner.UserId);
            member.Role = BusinessRole.Accounting; await db.SaveChangesAsync();
        });
        await Error(Send(s.Client, HttpMethod.Post, ApplyPath(s.Project), ApplyBody(s.Template, preview), s.Project.Revision, key), "forbidden", HttpStatusCode.Forbidden);
        Assert.Equal(3, await Count(s.Owner.TenantId, "tasks"));
    }

    [PostgresApiFact]
    public async Task Disabled_core_blocks_new_actions_and_replay_while_retaining_readable_originals()
    {
        var s = await Setup(); var preview = await Preview(s); var key = Guid.NewGuid().ToString("N"); var result = await Apply(s, preview, key);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(x => x.Id == s.Owner.TenantId);
            tenant.Modules = []; await db.SaveChangesAsync();
        });
        await Error(Send(s.Client, HttpMethod.Post, ApplyPath(s.Project), ApplyBody(s.Template, preview), s.Project.Revision, key), "module_disabled", HttpStatusCode.Forbidden);
        await Error(Send(s.Client, HttpMethod.Post, "/api/v1/project-templates", Details()), "module_disabled", HttpStatusCode.Forbidden);
        await Ok<ProjectTemplateApplication>(s.Client.GetAsync($"/api/v1/project-template-applications/{result.Application.Id}"));
    }

    [PostgresApiFact]
    public async Task List_pagination_is_bounded_and_invalid_template_shapes_are_not_persisted()
    {
        var s = await Setup();
        await Error(Send(s.Client, HttpMethod.Post, "/api/v1/project-templates", Details() with { Tasks = [] }), "invalid_collection");
        await Error(Send(s.Client, HttpMethod.Post, "/api/v1/project-templates", Details() with { Tasks = [new("Termin", "", 3651)] }), "project_template_offset");
        for (var i = 0; i < 20; i++) await Ok<ProjectTemplate>(Send(s.Client, HttpMethod.Post, "/api/v1/project-templates", Details() with { Name = $"Vorlage {i:00}" }));
        var first = await Json(s.Client.GetAsync("/api/v1/project-templates?page=1")); var second = await Json(s.Client.GetAsync("/api/v1/project-templates?page=2"));
        Assert.Equal(21, first.GetProperty("total").GetInt32()); Assert.Equal(20, first.GetProperty("items").GetArrayLength());
        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.Equal(21, first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(x => x.GetProperty("id").GetGuid()).Distinct().Count());
    }

    private sealed record SetupData(ApiTestUser Owner, HttpClient Client, Customer Customer, Building Building, Project Project, ProjectTemplate Template);
    private async Task<SetupData> Setup()
    {
        var tenant = await fixture.CreateTenant(modules: ["core"]); var owner = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(owner);
        var customer = await Ok<Customer>(Send(client, HttpMethod.Post, "/api/v1/customers", new { name = "Synthetischer Kunde" }));
        var building = await Ok<Building>(Send(client, HttpMethod.Post, "/api/v1/buildings", new { customerId = customer.Id, name = "Dach A", address = "Teststraße 1" }));
        var project = await ProjectFor(client, customer, building);
        var template = await Ok<ProjectTemplate>(Send(client, HttpMethod.Post, "/api/v1/project-templates", Details()));
        return new(owner, client, customer, building, project, template);
    }
    private static ProjectTemplateDetails Details() => new("Sanierung vorbereiten", "Betriebliche Arbeitsliste", ProjectType.Renovation,
        [new("Material prüfen", "Bestellung kontrollieren", -2), new("Dacharbeiten", "Nach Auftrag", 0), new("Endkontrolle", "Fotos dokumentieren", 3)]);
    private static Task<Project> ProjectFor(HttpClient client, Customer customer, Building building) => Ok<Project>(Send(client, HttpMethod.Post, "/api/v1/projects",
        new { customerId = customer.Id, buildingId = building.Id, name = "Dachsanierung", type = "Renovation", status = "Planning", plannedStart = "2028-02-28" }));
    private static Task<ProjectTemplatePreview> Preview(SetupData s) => Ok<ProjectTemplatePreview>(Send(s.Client, HttpMethod.Post, PreviewPath(s.Project),
        new ProjectTemplatePreviewRequest(s.Template.Id, s.Template.Revision, null), s.Project.Revision));
    private static Task<ProjectTemplateApplied> Apply(SetupData s, ProjectTemplatePreview preview, string? key = null) =>
        Ok<ProjectTemplateApplied>(Send(s.Client, HttpMethod.Post, ApplyPath(s.Project), ApplyBody(s.Template, preview), s.Project.Revision, key));
    private static ProjectTemplateApplyRequest ApplyBody(ProjectTemplate template, ProjectTemplatePreview preview) =>
        new(template.Id, template.Revision, preview.AnchorKind == ProjectTemplateAnchorKind.Explicit ? preview.AnchorDate : null, preview.Digest);
    private static string TemplatePath(ProjectTemplate template) => $"/api/v1/project-templates/{template.Id}";
    private static string PreviewPath(Project project) => $"/api/v1/projects/{project.Id}/template-preview";
    private static string ApplyPath(Project project) => $"/api/v1/projects/{project.Id}/template-apply";
    private Task<int> Count(Guid tenant, string kind) => fixture.InScope(async services => await services.GetRequiredService<FirstBlickDbContext>().Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant && x.Kind == kind));
    private Task<int> ReceiptCount(Guid tenant) => fixture.InScope(async services => await services.GetRequiredService<FirstBlickDbContext>().Receipts.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant));
    private Task<string> Stored(Guid tenant, Guid id) => fixture.InScope(async services => await services.GetRequiredService<FirstBlickDbContext>().Entities.IgnoreQueryFilters().Where(x => x.TenantId == tenant && x.Id == id).Select(x => x.Data).SingleAsync());
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {text}"); return AppJson.Read<T>(text);
    }
    private static async Task<JsonElement> Json(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
    private static async Task Error(Task<HttpResponseMessage> pending, string code, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        Assert.Equal(code, JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
    }
}
