using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class MaintenanceWorkflowApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Calendar_download_is_an_integrity_bound_read_of_the_opened_persisted_revision()
    {
        var (user, client, contract) = await Setup(); var visit = await Plan(client, contract);
        var path = $"/api/v1/maintenance-visits/{visit.Id}/calendar?revision={visit.Revision}";
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains(visit.Id.ToString("N"), response.Content.Headers.ContentDisposition.FileNameStar);
        Assert.True(response.Headers.CacheControl!.NoStore); Assert.True(response.Headers.CacheControl.Private);
        Assert.Equal($"\"{visit.Revision}\"", response.Headers.ETag!.Tag);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), response.Headers.GetValues("X-Content-SHA256").Single());
        Assert.Equal(MaintenanceCalendar.Export(await fixture.Read<MaintenanceVisit>(user.TenantId, visit.Id)), bytes);
        using var repeated = await client.GetAsync(path); Assert.Equal(bytes, await repeated.Content.ReadAsByteArrayAsync());
        var text = Encoding.UTF8.GetString(bytes).Replace("\r\n ", "", StringComparison.Ordinal);
        Assert.Contains("UID:" + MaintenanceCalendar.Uid(visit), text);
        Assert.Contains("DTSTART;VALUE=DATE:20270131", text); Assert.Contains("DTEND;VALUE=DATE:20270201", text);
        Assert.DoesNotContain(contract.Scope, text); Assert.DoesNotContain(visit.Checklist[0].Title, text);
        var stored = await fixture.Read<MaintenanceVisit>(user.TenantId, visit.Id); Assert.Equal(visit.Revision, stored.Revision);
        await Error(client.GetAsync($"/api/v1/maintenance-visits/{visit.Id}/calendar?revision=999"), "revision_conflict", HttpStatusCode.Conflict);
    }

    [PostgresApiFact]
    public async Task Calendar_export_respects_tenant_assignment_revocation_and_disabled_module_read_policy()
    {
        var (owner, client, contract) = await Setup(); var visit = await Plan(client, contract);
        var path = $"/api/v1/maintenance-visits/{visit.Id}/calendar?revision={visit.Revision}";
        var (_, other, _) = await Setup(); await Error(other.GetAsync(path), "not_found", HttpStatusCode.NotFound);
        using var anonymous = fixture.Client(); using var denied = await anonymous.GetAsync(path); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var worker = await fixture.CreateUser(owner.TenantId, BusinessRole.Worker); var workerClient = await fixture.Login(worker);
        await Error(workerClient.GetAsync(path), "not_found", HttpStatusCode.NotFound);
        var employee = await Create<Employee>(client, "employees", new() { Name = "Calendar worker", UserId = worker.UserId, Active = true });
        var assignment = await Create<ScheduleAssignment>(client, "schedule-assignments", new()
        { Title = "Calendar maintenance", EmployeeId = employee.Id, ProjectId = visit.ProjectId,
          Start = new(2027, 1, 31, 8, 0, 0, TimeSpan.Zero), End = new(2027, 1, 31, 10, 0, 0, TimeSpan.Zero) });
        using var assigned = await workerClient.GetAsync(path); Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        assignment.Cancelled = true; await Ok<ScheduleAssignment>(Send(client, HttpMethod.Put, $"/api/v1/schedule-assignments/{assignment.Id}", assignment, assignment.Revision));
        await Error(workerClient.GetAsync(path), "not_found", HttpStatusCode.NotFound);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(x => x.Id == owner.TenantId);
            tenant.Modules = ["core"]; await db.SaveChangesAsync();
        });
        using var disabled = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var membership = await db.Memberships.SingleAsync(x => x.TenantId == owner.TenantId && x.UserId == owner.UserId);
            membership.Active = false; await db.SaveChangesAsync();
        });
        await Error(client.GetAsync(path), "forbidden", HttpStatusCode.Forbidden);
    }

    [PostgresApiFact]
    public async Task Completed_calendar_keeps_uid_and_due_date_but_old_download_revision_is_rejected()
    {
        var (_, client, contract) = await Setup(); var visit = await Plan(client, contract); var uid = MaintenanceCalendar.Uid(visit); var oldRevision = visit.Revision;
        foreach (var check in visit.Checklist.ToArray()) visit = await Record(client, visit, check.Id);
        visit = await Ok<MaintenanceVisit>(Send(client, HttpMethod.Post, $"/api/v1/maintenance-visits/{visit.Id}/complete", new { notes = "Private final inspection report" }, visit.Revision));
        await Error(client.GetAsync($"/api/v1/maintenance-visits/{visit.Id}/calendar?revision={oldRevision}"), "revision_conflict", HttpStatusCode.Conflict);
        using var response = await client.GetAsync($"/api/v1/maintenance-visits/{visit.Id}/calendar?revision={visit.Revision}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var text = (await response.Content.ReadAsStringAsync()).Replace("\r\n ", "", StringComparison.Ordinal);
        Assert.Contains("UID:" + uid, text); Assert.Contains("SEQUENCE:" + (visit.Revision - 1), text);
        Assert.Contains("SUMMARY:Wartung abgeschlossen: ", text); Assert.Contains("X-FIRSTBLICK-MAINTENANCE-STATUS:Completed", text);
        Assert.DoesNotContain("Private final inspection report", text); Assert.DoesNotContain("\r\nSTATUS:COMPLETED", text);
    }

    [PostgresApiFact]
    public async Task Legacy_raw_contract_without_new_fields_can_be_explicitly_configured_and_planned()
    {
        var (user, client, contract) = await Setup();
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var row = await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.TenantId == user.TenantId && x.Kind == "maintenance-contracts" && x.Id == contract.Id);
            var legacy = JsonNode.Parse(row.Data)!.AsObject();
            legacy.Remove("checklistTemplate"); legacy.Remove("automaticPlanning"); legacy.Remove("planningLeadDays");
            row.Data = legacy.ToJsonString(); await db.SaveChangesAsync();
        });
        var raw = await Ok<JsonElement>(client.GetAsync($"/api/v1/maintenance-contracts/{contract.Id}"));
        Assert.False(raw.TryGetProperty("checklistTemplate", out _)); Assert.False(raw.TryGetProperty("automaticPlanning", out _)); Assert.False(raw.TryGetProperty("planningLeadDays", out _));
        Assert.Equal(0, await RunScheduler(new(2027, 1, 31, 8, 0, 0, TimeSpan.Zero)));
        contract = await Ok<MaintenanceContract>(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/configure", new
        {
            active = true, automaticPlanning = false, planningLeadDays = 14,
            checklistTemplate = new[] { new MaintenanceCheckTemplate("Bestandsvertrag prüfen", "Vom Betrieb ausdrücklich festgelegt") }
        }, contract.Revision));
        Assert.False(contract.AutomaticPlanning); Assert.Equal(14, contract.PlanningLeadDays);
        var visit = await Plan(client, contract);
        Assert.Equal("Bestandsvertrag prüfen", Assert.Single(visit.Checklist).Title);
        Assert.Equal("Vom Betrieb ausdrücklich festgelegt", visit.Checklist[0].Instructions);
        Assert.Equal(contract.Id, visit.ContractId);
    }

    [PostgresApiFact]
    public async Task Plan_check_complete_and_next_occurrence_form_one_persistent_audited_chain()
    {
        var (user, client, contract) = await Setup();
        var key = Guid.NewGuid().ToString("N"); var body = new { expectedDueDate = contract.NextDueDate };
        var visit = await Ok<MaintenanceVisit>(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/schedule", body, contract.Revision, key));
        var replay = await Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/schedule", body, contract.Revision, key);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(visit.Id, (await Ok<MaintenanceVisit>(Task.FromResult(replay))).Id);
        var repeated = await Ok<MaintenanceVisit>(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/schedule", body, contract.Revision));
        Assert.Equal(visit.Id, repeated.Id);
        await Error(Send(client, HttpMethod.Post, $"/api/v1/maintenance-visits/{visit.Id}/complete", new { notes = "Noch offen" }, visit.Revision), "maintenance_incomplete");
        Assert.Equal(0, (await fixture.Read<MaintenanceContract>(user.TenantId, contract.Id)).CompletedOccurrences);
        foreach (var check in visit.Checklist.ToArray()) visit = await Record(client, visit, check.Id);
        var completionKey = Guid.NewGuid().ToString("N"); var revision = visit.Revision;
        visit = await Ok<MaintenanceVisit>(Send(client, HttpMethod.Post, $"/api/v1/maintenance-visits/{visit.Id}/complete", new { notes = "Abläufe und Anschlüsse geprüft." }, revision, completionKey));
        Assert.Equal(MaintenanceVisitStatus.Completed, visit.Status); Assert.Equal(new DateOnly(2027, 2, 28), visit.NextDueDate);
        Assert.Equal(ProjectStatus.Completed, (await fixture.Read<Project>(user.TenantId, visit.ProjectId)).Status);
        Assert.Equal(1, (await fixture.Read<MaintenanceContract>(user.TenantId, contract.Id)).CompletedOccurrences);
        await Ok<MaintenanceVisit>(Send(client, HttpMethod.Post, $"/api/v1/maintenance-visits/{visit.Id}/complete", new { notes = "Abläufe und Anschlüsse geprüft." }, revision, completionKey));
        await Error(Send(client, HttpMethod.Post, $"/api/v1/maintenance-visits/{visit.Id}/check", new { checkId = visit.Checklist[0].Id, state = "Pending", note = "" }, visit.Revision), "maintenance_sealed");
        contract = await fixture.Read<MaintenanceContract>(user.TenantId, contract.Id);
        var next = await Ok<MaintenanceVisit>(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/schedule", new { expectedDueDate = contract.NextDueDate }, contract.Revision));
        Assert.NotEqual(visit.Id, next.Id); Assert.NotEqual(visit.ProjectId, next.ProjectId); Assert.All(next.Checklist, x => Assert.Equal(MaintenanceCheckState.Pending, x.State));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(2, await db.Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == user.TenantId && x.Kind == "maintenance-visits"));
            Assert.Equal(2, await db.Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == user.TenantId && x.Kind == "projects"));
            Assert.Equal(1, await db.Receipts.IgnoreQueryFilters().CountAsync(x => x.TenantId == user.TenantId && x.ActorId == Guid.Empty && x.Key.StartsWith("maintenance-completion:")));
            Assert.Equal(1, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == user.TenantId && x.Action == "maintenance.visit_complete"));
        });
    }

    [PostgresApiFact]
    public async Task Concurrent_planners_create_one_project_and_snapshot_and_retry_returns_it()
    {
        var (user, client, contract) = await Setup();
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Send(client, HttpMethod.Post,
            $"/api/v1/maintenance-contracts/{contract.Id}/schedule", new { expectedDueDate = contract.NextDueDate }, contract.Revision)));
        var visits = await Task.WhenAll(responses.Select(x => Ok<MaintenanceVisit>(Task.FromResult(x))));
        Assert.Equal(visits[0].Id, visits[1].Id);
        await fixture.InScope(async services => Assert.Equal(1, await services.GetRequiredService<FirstBlickDbContext>().Entities.IgnoreQueryFilters()
            .CountAsync(x => x.TenantId == user.TenantId && x.Kind == "projects")));
    }

    [PostgresApiFact]
    public async Task Checklist_scope_and_recurrence_cannot_be_rewritten_or_bypassed_by_legacy_completion()
    {
        var (user, client, contract) = await Setup(); var visit = await Plan(client, contract);
        contract.Scope = "Nur künftige Wartungen"; contract.ChecklistTemplate = [new("Neue Prüfung")];
        contract = await Ok<MaintenanceContract>(Send(client, HttpMethod.Put, $"/api/v1/maintenance-contracts/{contract.Id}", contract, contract.Revision));
        Assert.Equal(2, (await fixture.Read<MaintenanceVisit>(user.TenantId, visit.Id)).Checklist.Count);
        contract.IntervalMonths = 2;
        await Error(Send(client, HttpMethod.Put, $"/api/v1/maintenance-contracts/{contract.Id}", contract, contract.Revision), "maintenance_recurrence_in_use");
        var project = await fixture.Read<Project>(user.TenantId, visit.ProjectId); project.Status = ProjectStatus.Completed;
        await Ok<Project>(Send(client, HttpMethod.Put, $"/api/v1/projects/{project.Id}", project, project.Revision));
        await Error(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/complete", new { expectedDueDate = visit.DueDate, projectId = project.Id }, contract.Revision), "maintenance_visit_required");
        var direct = await Send(client, HttpMethod.Put, $"/api/v1/maintenance-visits/{visit.Id}", visit, visit.Revision);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, direct.StatusCode);
    }

    [PostgresApiFact]
    public async Task Workers_can_record_only_assigned_visits_and_cannot_complete_contracts_or_replay_after_revocation()
    {
        var (owner, client, contract) = await Setup(); var visit = await Plan(client, contract);
        var worker = await fixture.CreateUser(owner.TenantId, BusinessRole.Worker); var workerClient = await fixture.Login(worker);
        var employee = await Create<Employee>(client, "employees", new() { Name = "Mitarbeiter", UserId = worker.UserId, Active = true });
        await Error(workerClient.GetAsync($"/api/v1/maintenance-visits/{visit.Id}"), "not_found", HttpStatusCode.NotFound);
        var assignment = await Create<ScheduleAssignment>(client, "schedule-assignments", new() { Title = "Wartung durchführen", EmployeeId = employee.Id, ProjectId = visit.ProjectId,
            Start = new(2027, 1, 31, 8, 0, 0, TimeSpan.Zero), End = new(2027, 1, 31, 10, 0, 0, TimeSpan.Zero) });
        await Ok<MaintenanceVisit>(workerClient.GetAsync($"/api/v1/maintenance-visits/{visit.Id}"));
        var key = Guid.NewGuid().ToString("N"); var revision = visit.Revision;
        var body = new { checkId = visit.Checklist[0].Id, state = "Done", note = "Erledigt" };
        visit = await Ok<MaintenanceVisit>(Send(workerClient, HttpMethod.Post, $"/api/v1/maintenance-visits/{visit.Id}/check", body, revision, key));
        Assert.Equal(worker.UserId, visit.Checklist[0].RecordedBy);
        await Error(Send(workerClient, HttpMethod.Post, $"/api/v1/maintenance-visits/{visit.Id}/complete", new { notes = "" }, visit.Revision), "forbidden", HttpStatusCode.Forbidden);
        assignment.Cancelled = true;
        await Ok<ScheduleAssignment>(Send(client, HttpMethod.Put, $"/api/v1/schedule-assignments/{assignment.Id}", assignment, assignment.Revision));
        await Error(Send(workerClient, HttpMethod.Post, $"/api/v1/maintenance-visits/{visit.Id}/check", body, revision, key), "not_found", HttpStatusCode.NotFound);
        var page = await Ok<JsonElement>(workerClient.GetAsync("/api/v1/maintenance-visits")); Assert.Equal(0, page.GetProperty("total").GetInt32());
    }

    [PostgresApiFact]
    public async Task Stale_revisions_cross_tenant_access_and_disabled_modules_are_rejected()
    {
        var (user, client, contract) = await Setup(); var visit = await Plan(client, contract);
        await Error(Send(client, HttpMethod.Post, $"/api/v1/maintenance-visits/{visit.Id}/check", new { checkId = visit.Checklist[0].Id, state = "Done", note = "" }, 99), "revision_conflict", HttpStatusCode.Conflict);
        var (_, other, _) = await Setup();
        await Error(other.GetAsync($"/api/v1/maintenance-visits/{visit.Id}"), "not_found", HttpStatusCode.NotFound);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(x => x.Id == user.TenantId);
            tenant.Modules = ["core"]; await db.SaveChangesAsync();
        });
        await Ok<MaintenanceVisit>(client.GetAsync($"/api/v1/maintenance-visits/{visit.Id}"));
        await Error(Send(client, HttpMethod.Post, $"/api/v1/maintenance-visits/{visit.Id}/check", new { checkId = visit.Checklist[0].Id, state = "Done", note = "" }, visit.Revision), "module_disabled", HttpStatusCode.Forbidden);
    }

    [PostgresApiFact]
    public async Task Automatic_planning_uses_business_date_lead_time_and_is_safe_with_two_workers()
    {
        var (user, client, contract) = await Setup();
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(x => x.Id == user.TenantId); tenant.TimeZone = "Europe/Berlin"; await db.SaveChangesAsync();
        });
        await Ok<MaintenanceContract>(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/configure", new
        { active = true, automaticPlanning = true, planningLeadDays = 1, checklistTemplate = contract.ChecklistTemplate }, contract.Revision));
        Assert.Equal(0, await RunScheduler(new(2027, 1, 29, 22, 59, 0, TimeSpan.Zero)));
        var results = await Task.WhenAll(RunScheduler(new(2027, 1, 29, 23, 0, 0, TimeSpan.Zero)), RunScheduler(new(2027, 1, 29, 23, 0, 0, TimeSpan.Zero)));
        Assert.Equal(1, results.Sum()); Assert.Equal(0, await RunScheduler(new(2027, 2, 10, 8, 0, 0, TimeSpan.Zero)));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(1, await db.Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == user.TenantId && x.Kind == "projects"));
            Assert.Equal(1, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == user.TenantId && x.ActorId == Guid.Empty && x.Action == "maintenance.visit_planned"));
        });
    }

    [PostgresApiFact]
    public async Task Automatic_planning_requires_opt_in_enabled_module_and_skips_an_unavailable_source()
    {
        var (user, client, contract) = await Setup();
        Assert.Equal(0, await RunScheduler(new(2027, 1, 31, 8, 0, 0, TimeSpan.Zero)));
        contract = await Ok<MaintenanceContract>(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/configure", new
        { active = true, automaticPlanning = true, planningLeadDays = 14, checklistTemplate = contract.ChecklistTemplate }, contract.Revision));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(x => x.Id == user.TenantId); tenant.Modules = ["core"]; await db.SaveChangesAsync();
        });
        Assert.Equal(0, await RunScheduler(new(2027, 1, 31, 8, 0, 0, TimeSpan.Zero)));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(x => x.Id == user.TenantId); tenant.Modules = AccessRules.AllModules;
            var building = await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.TenantId == user.TenantId && x.Kind == "buildings" && x.Id == contract.BuildingId);
            var value = AppJson.Read<Building>(building.Data); value.Archived = true; building.Data = AppJson.Serialize(value); building.Archived = true; await db.SaveChangesAsync();
        });
        Assert.Equal(0, await RunScheduler(new(2027, 1, 31, 8, 0, 0, TimeSpan.Zero)));
    }

    [PostgresApiFact]
    public async Task Recovery_hold_blocks_automatic_project_creation_until_explicitly_released()
    {
        var (user, client, contract) = await Setup();
        await Ok<MaintenanceContract>(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/configure", new
        { active = true, automaticPlanning = true, planningLeadDays = 14, checklistTemplate = contract.ChecklistTemplate }, contract.Revision));
        var now = new DateTimeOffset(2027, 1, 31, 8, 0, 0, TimeSpan.Zero);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var hold = new OperationsRecoveryHold(Guid.NewGuid(), "Held", now, "test-release", "Synthetic restoration", null, null);
            db.Receipts.Add(new CommandReceipt { TenantId = user.TenantId, ActorId = Guid.Empty, Key = OperationsRecoveryHold.Key, Response = AppJson.Serialize(hold), RequestDigest = ContentDigest.Of(hold) });
            await db.SaveChangesAsync();
        });
        Assert.Equal(0, await RunScheduler(now));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(0, await db.Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == user.TenantId && x.Kind == "projects"));
            var marker = await db.Receipts.IgnoreQueryFilters().SingleAsync(x => x.TenantId == user.TenantId && x.Key == OperationsRecoveryHold.Key);
            var hold = AppJson.Read<OperationsRecoveryHold>(marker.Response) with { State = "Released", ResolvedAt = now, EvidenceDigest = new string('a', 64) };
            marker.Response = AppJson.Serialize(hold); marker.RequestDigest = ContentDigest.Of(hold); await db.SaveChangesAsync();
        });
        Assert.Equal(1, await RunScheduler(now));
    }

    [PostgresApiFact]
    public async Task Planned_project_cannot_be_borrowed_by_a_different_contract_to_skip_its_checklist()
    {
        var (user, client, contract) = await Setup(); var visit = await Plan(client, contract);
        var second = await Create<MaintenanceContract>(client, "maintenance-contracts", new()
        {
            CustomerId = contract.CustomerId, BuildingId = contract.BuildingId, Title = "Separater Vertrag", Scope = "Separater Umfang",
            StartDate = contract.StartDate, NextDueDate = contract.StartDate
        });
        var project = await fixture.Read<Project>(user.TenantId, visit.ProjectId); project.Status = ProjectStatus.Completed;
        await Ok<Project>(Send(client, HttpMethod.Put, $"/api/v1/projects/{project.Id}", project, project.Revision));
        await Error(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{second.Id}/complete", new { expectedDueDate = second.NextDueDate, projectId = project.Id }, second.Revision), "maintenance_visit_required");
        Assert.Equal(0, (await fixture.Read<MaintenanceContract>(user.TenantId, second.Id)).CompletedOccurrences);
    }

    private Task<int> RunScheduler(DateTimeOffset now) => fixture.InScope(services => services.GetRequiredService<MaintenancePlanner>().ScheduleDue(now));
    private static Task<MaintenanceVisit> Plan(HttpClient client, MaintenanceContract contract) => Ok<MaintenanceVisit>(Send(client, HttpMethod.Post,
        $"/api/v1/maintenance-contracts/{contract.Id}/schedule", new { expectedDueDate = contract.NextDueDate }, contract.Revision));
    private static Task<MaintenanceVisit> Record(HttpClient client, MaintenanceVisit visit, Guid checkId) => Ok<MaintenanceVisit>(Send(client, HttpMethod.Post,
        $"/api/v1/maintenance-visits/{visit.Id}/check", new { checkId, state = "Done", note = "Geprüft" }, visit.Revision));
    private static Task<T> Create<T>(HttpClient client, string kind, T body) => Ok<T>(Send(client, HttpMethod.Post, "/api/v1/" + kind, body));
    private async Task<(ApiTestUser User, HttpClient Client, MaintenanceContract Contract)> Setup()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(user);
        var customer = await Create<Customer>(client, "customers", new() { Name = "Synthetic maintenance customer" });
        var building = await Create<Building>(client, "buildings", new() { CustomerId = customer.Id, Name = "Synthetic roof", Address = "Teststraße 1" });
        var contract = await Create<MaintenanceContract>(client, "maintenance-contracts", new() { CustomerId = customer.Id, BuildingId = building.Id,
            Title = "Monatliche Dachkontrolle", Scope = "Abläufe und Anschlüsse kontrollieren", StartDate = new(2027, 1, 31), NextDueDate = new(2027, 1, 31), IntervalMonths = 1,
            ChecklistTemplate = [new("Abläufe prüfen"), new("Anschlüsse prüfen")] });
        return (user, client, contract);
    }
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> responseTask)
    {
        using var response = await responseTask; var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {json}"); return AppJson.Read<T>(json);
    }
    private static async Task Error(Task<HttpResponseMessage> responseTask, string code, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        using var response = await responseTask; var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {json}");
        using var doc = JsonDocument.Parse(json); Assert.Equal(code, doc.RootElement.GetProperty("code").GetString());
    }
}
