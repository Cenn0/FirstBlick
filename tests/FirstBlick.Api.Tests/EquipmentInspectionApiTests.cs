using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class EquipmentInspectionApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private sealed record Change(Equipment Equipment, EquipmentInspection Inspection, long AffectedAssignmentCount);
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [PostgresApiFact]
    public async Task Planned_failure_documented_repair_and_followup_preserve_one_audited_history()
    {
        var (owner, client, equipment) = await Setup();
        var planned = await Plan(client, equipment); var inspection = planned.Inspection; equipment = planned.Equipment;
        Assert.Equal(inspection.Id, equipment.PendingInspectionId); Assert.Equal(2, equipment.Revision);
        Assert.Equal(EquipmentInspectionWorkflow.Snapshot(equipment), inspection.EquipmentSnapshot);
        var key = Guid.NewGuid().ToString("N"); var revision = equipment.Revision;
        var failedBody = Completion(inspection, false);
        var failed = await Ok<Change>(Send(client, HttpMethod.Post, Path(equipment, "inspection-complete"), failedBody, revision, key));
        Assert.False(failed.Equipment.Available); Assert.True(failed.Equipment.InspectionBlocked);
        Assert.Equal(Today, failed.Equipment.NextInspectionOn); Assert.Null(failed.Equipment.PendingInspectionId);
        Assert.Equal(inspection.Id, failed.Equipment.LastInspectionId);
        Assert.Equal(owner.UserId, failed.Inspection.Completion!.RecordedBy);
        var replay = await Send(client, HttpMethod.Post, Path(equipment, "inspection-complete"), failedBody, revision, key);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(failed.Equipment.Revision, (await Ok<Change>(Task.FromResult(replay))).Equipment.Revision);
        equipment = failed.Equipment; equipment.Available = true;
        await Error(Send(client, HttpMethod.Put, $"/api/v1/equipment/{equipment.Id}", equipment, equipment.Revision), "inspection_blocked");
        equipment.Available = false; equipment.InspectionBlocked = false; equipment.LastInspectionId = null; equipment.LastInspectionOn = null;
        await Error(Send(client, HttpMethod.Put, $"/api/v1/equipment/{equipment.Id}", equipment, equipment.Revision), "inspection_evidence_immutable");
        equipment = await fixture.Read<Equipment>(owner.TenantId, equipment.Id);
        var repair = await Plan(client, equipment, EquipmentInspectionKind.Repair);
        var repaired = await Ok<Change>(Send(client, HttpMethod.Post, Path(equipment, "inspection-complete"), Completion(repair.Inspection, true), repair.Equipment.Revision));
        Assert.True(repaired.Equipment.Available); Assert.False(repaired.Equipment.InspectionBlocked);
        Assert.Equal(Today.AddMonths(6), repaired.Equipment.NextInspectionOn);
        Assert.Equal(repair.Inspection.Id, repaired.Equipment.LastInspectionId);
        var original = await Ok<EquipmentInspection>(client.GetAsync($"/api/v1/equipment-inspections/{inspection.Id}"));
        Assert.Equal(EquipmentInspectionResult.Failed, original.Completion!.Result); original.Validate();
        var history = await Ok<JsonElement>(client.GetAsync($"/api/v1/equipment/{equipment.Id}/inspection-history"));
        Assert.Equal(2, history.GetProperty("total").GetInt32()); Assert.Equal(JsonValueKind.Null, history.GetProperty("pendingInspection").ValueKind);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(2, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == owner.TenantId && x.Action == "equipment.inspection_completed"));
            Assert.Equal(2, await db.Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == owner.TenantId && x.Kind == "equipment-inspections"));
        });
    }

    [PostgresApiFact]
    public async Task Failed_inspection_is_recorded_despite_existing_assignments_and_dispatch_blocks_further_use()
    {
        var (owner, client, equipment) = await Setup(new DateOnly(2030, 3, 31));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(x => x.Id == owner.TenantId);
            tenant.TimeZone = "Europe/Berlin"; await db.SaveChangesAsync();
        });
        var (employee, project) = await AssignmentSources(client);
        // 22:00 UTC is local midnight after the DST spring-forward day, and the end is exclusive.
        var end = new DateTimeOffset(2030, 3, 31, 22, 0, 0, TimeSpan.Zero);
        var assignment = await Create<ScheduleAssignment>(client, "schedule-assignments", new()
        {
            Title = "Geplanter Geräteeinsatz", EmployeeId = employee.Id, ProjectId = project.Id,
            EquipmentIds = [equipment.Id], Start = end.AddHours(-1), End = end
        });
        var late = new ScheduleAssignment { Title = "Zu später Einsatz", EmployeeId = employee.Id, ProjectId = project.Id,
            EquipmentIds = [equipment.Id], Start = end, End = end.AddHours(1) };
        await Error(Send(client, HttpMethod.Post, "/api/v1/schedule-assignments", late), "equipment_inspection_due");
        var planned = await Plan(client, equipment);
        var failed = await Ok<Change>(Send(client, HttpMethod.Post, Path(equipment, "inspection-complete"), Completion(planned.Inspection, false), planned.Equipment.Revision));
        Assert.Equal(1, failed.AffectedAssignmentCount);
        var retained = await fixture.Read<ScheduleAssignment>(owner.TenantId, assignment.Id);
        Assert.False(retained.Cancelled); Assert.Equal(assignment.Revision, retained.Revision);
        late.Start = end.AddHours(-3); late.End = end.AddHours(-2);
        await Error(Send(client, HttpMethod.Post, "/api/v1/schedule-assignments", late), "equipment_unavailable");
        assignment.Title = "Unzulässige Umbuchung";
        await Error(Send(client, HttpMethod.Put, $"/api/v1/schedule-assignments/{assignment.Id}", assignment, assignment.Revision), "equipment_unavailable");
        assignment.Cancelled = true;
        await Ok<ScheduleAssignment>(Send(client, HttpMethod.Put, $"/api/v1/schedule-assignments/{assignment.Id}", assignment, assignment.Revision));
        var history = await Ok<JsonElement>(client.GetAsync($"/api/v1/equipment/{equipment.Id}/inspection-history"));
        Assert.Equal(0, history.GetProperty("affectedAssignmentCount").GetInt64());
        Assert.Equal("Europe/Berlin", history.GetProperty("timeZone").GetString());
    }

    [PostgresApiFact]
    public async Task Assignment_warning_retains_tick_precision_at_the_exclusive_midnight_boundary()
    {
        var due = new DateOnly(2030, 3, 31);
        var (_, client, equipment) = await Setup(due.AddDays(1));
        var (employee, project) = await AssignmentSources(client);
        var midnight = new DateTimeOffset(2030, 4, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var (start, end) in new[] { (midnight.AddHours(-1), midnight.AddTicks(-1)), (midnight.AddTicks(-1), midnight), (midnight, midnight.AddTicks(1)) })
            await Create<ScheduleAssignment>(client, "schedule-assignments", new()
            {
                Title = "Exakte Tagesgrenze", EmployeeId = employee.Id, ProjectId = project.Id,
                EquipmentIds = [equipment.Id], Start = start, End = end
            });
        var planned = await Plan(client, equipment);
        var complete = Completion(planned.Inspection, true) with { NextInspectionOn = due };
        var result = await Ok<Change>(Send(client, HttpMethod.Post, Path(equipment, "inspection-complete"), complete, planned.Equipment.Revision));
        Assert.Equal(1, result.AffectedAssignmentCount);
        var history = await Ok<JsonElement>(client.GetAsync($"/api/v1/equipment/{equipment.Id}/inspection-history"));
        Assert.Equal(1, history.GetProperty("affectedAssignmentCount").GetInt64());
    }

    [PostgresApiFact]
    public async Task Cancellation_is_sealed_does_not_renew_dates_and_requires_current_dual_revisions()
    {
        var (owner, client, equipment) = await Setup(); var planned = await Plan(client, equipment);
        var body = new { inspectionId = planned.Inspection.Id, inspectionRevision = planned.Inspection.Revision, reason = "Prüfperson ist verhindert" };
        await Error(Send(client, HttpMethod.Post, Path(equipment, "inspection-cancel"), body, equipment.Revision), "revision_conflict", HttpStatusCode.Conflict);
        await Error(Send(client, HttpMethod.Post, Path(equipment, "inspection-cancel"), body with { inspectionRevision = 99 }, planned.Equipment.Revision), "revision_conflict", HttpStatusCode.Conflict);
        await Error(Send(client, HttpMethod.Post, Path(equipment, "inspection-cancel"), body with { reason = " " }, planned.Equipment.Revision), "required");
        var cancelled = await Ok<Change>(Send(client, HttpMethod.Post, Path(equipment, "inspection-cancel"), body, planned.Equipment.Revision));
        Assert.Equal(EquipmentInspectionStatus.Cancelled, cancelled.Inspection.Status); Assert.Null(cancelled.Equipment.PendingInspectionId);
        Assert.Equal(Today, cancelled.Equipment.NextInspectionOn); Assert.Null(cancelled.Equipment.LastInspectionId);
        var replacement = await Plan(client, cancelled.Equipment); Assert.NotEqual(planned.Inspection.Id, replacement.Inspection.Id);
        await Error(Send(client, HttpMethod.Post, Path(equipment, "inspection-cancel"), body with { inspectionRevision = cancelled.Inspection.Revision }, replacement.Equipment.Revision), "inspection_sealed");
        Assert.Equal(replacement.Inspection.Id, (await fixture.Read<Equipment>(owner.TenantId, equipment.Id)).PendingInspectionId);
    }

    [PostgresApiFact]
    public async Task Concurrent_plans_commit_exactly_one_pending_record_and_generic_paths_cannot_fabricate_it()
    {
        var (owner, client, equipment) = await Setup(); var body = PlanBody();
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Send(client, HttpMethod.Post, Path(equipment, "inspection-plan"), body, equipment.Revision)));
        Assert.Single(responses, x => x.IsSuccessStatusCode);
        var failure = Assert.Single(responses, x => !x.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, failure.StatusCode);
        var planned = await Ok<Change>(Task.FromResult(responses.Single(x => x.IsSuccessStatusCode)));
        await Error(Send(client, HttpMethod.Post, Path(equipment, "inspection-plan"), body, planned.Equipment.Revision), "inspection_already_planned");
        var direct = await Send(client, HttpMethod.Put, $"/api/v1/equipment-inspections/{planned.Inspection.Id}", planned.Inspection, planned.Inspection.Revision);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, direct.StatusCode);
        var fabricated = new Equipment { Name = "Fake", InventoryNumber = "FAKE-1", LastInspectionId = planned.Inspection.Id, LastInspectionOn = Today };
        await Error(Send(client, HttpMethod.Post, "/api/v1/equipment", fabricated), "command_required");
        planned.Equipment.InventoryNumber = "Substitution";
        await Error(Send(client, HttpMethod.Put, $"/api/v1/equipment/{equipment.Id}", planned.Equipment, planned.Equipment.Revision), "inspection_equipment_changed");
        await fixture.InScope(async services => Assert.Equal(1, await services.GetRequiredService<FirstBlickDbContext>().Entities.IgnoreQueryFilters()
            .CountAsync(x => x.TenantId == owner.TenantId && x.Kind == "equipment-inspections")));
    }

    [PostgresApiFact]
    public async Task Pending_inspection_allows_location_update_without_body_id_but_keeps_equipment_identity_frozen()
    {
        var (owner, client, equipment) = await Setup(); var planned = await Plan(client, equipment); var current = planned.Equipment;
        var body = new
        {
            current.Name, current.InventoryNumber, current.Kind, current.Registration, Location = "Außenlager",
            current.NextInspectionOn, current.Available, current.PendingInspectionId,
            current.LastInspectionId, current.LastInspectionOn, current.InspectionBlocked
        };
        var updated = await Ok<Equipment>(Send(client, HttpMethod.Put, $"/api/v1/equipment/{equipment.Id}", body, current.Revision));
        Assert.Equal(equipment.Id, updated.Id); Assert.Equal("Außenlager", updated.Location);
        Assert.Equal(current.Revision + 1, updated.Revision); Assert.Equal(planned.Inspection.Id, updated.PendingInspectionId);
        Assert.Equal(planned.Inspection.EquipmentSnapshot, EquipmentInspectionWorkflow.Snapshot(updated));

        await Error(Send(client, HttpMethod.Put, $"/api/v1/equipment/{equipment.Id}", body with { Name = "Anderes Gerät" }, updated.Revision),
            "inspection_equipment_changed");
        var retained = await fixture.Read<Equipment>(owner.TenantId, equipment.Id);
        Assert.Equal(updated.Revision, retained.Revision); Assert.Equal(current.Name, retained.Name); Assert.Equal("Außenlager", retained.Location);
        var inspection = await fixture.Read<EquipmentInspection>(owner.TenantId, planned.Inspection.Id);
        Assert.Equal(EquipmentInspectionStatus.Scheduled, inspection.Status); Assert.Equal(planned.Inspection.Revision, inspection.Revision);
        Assert.Equal(planned.Inspection.ScopeDigest, inspection.ScopeDigest);
    }

    [PostgresApiFact]
    public async Task Current_rights_and_purchasing_module_are_checked_before_command_replay()
    {
        var (owner, client, equipment) = await Setup(); var key = Guid.NewGuid().ToString("N"); var body = PlanBody();
        await Ok<Change>(Send(client, HttpMethod.Post, Path(equipment, "inspection-plan"), body, equipment.Revision, key));
        var accounting = await fixture.CreateUser(owner.TenantId, BusinessRole.Accounting); var accountingClient = await fixture.Login(accounting);
        await Ok<JsonElement>(accountingClient.GetAsync($"/api/v1/equipment/{equipment.Id}/inspection-history"));
        await Error(Send(accountingClient, HttpMethod.Post, Path(equipment, "inspection-plan"), body, equipment.Revision), "forbidden", HttpStatusCode.Forbidden);
        var worker = await fixture.CreateUser(owner.TenantId, BusinessRole.Worker); var workerClient = await fixture.Login(worker);
        await Error(workerClient.GetAsync("/api/v1/equipment/inspection-worklist"), "forbidden", HttpStatusCode.Forbidden);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var membership = await db.Memberships.IgnoreQueryFilters().SingleAsync(x => x.TenantId == owner.TenantId && x.UserId == owner.UserId);
            membership.Role = BusinessRole.Worker; await db.SaveChangesAsync();
        });
        await Error(Send(client, HttpMethod.Post, Path(equipment, "inspection-plan"), body, equipment.Revision, key), "forbidden", HttpStatusCode.Forbidden);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var membership = await db.Memberships.IgnoreQueryFilters().SingleAsync(x => x.TenantId == owner.TenantId && x.UserId == owner.UserId);
            membership.Role = BusinessRole.ManagingDirector;
            var tenant = await db.Tenants.SingleAsync(x => x.Id == owner.TenantId); tenant.Modules = ["core"]; await db.SaveChangesAsync();
        });
        await Error(Send(client, HttpMethod.Post, Path(equipment, "inspection-plan"), body, equipment.Revision, key), "module_disabled", HttpStatusCode.Forbidden);
    }

    [PostgresApiFact]
    public async Task Cross_tenant_inspections_and_wrong_resource_results_are_rejected_without_writes()
    {
        var (owner, client, equipment) = await Setup(); var planned = await Plan(client, equipment);
        var (_, other, otherEquipment) = await Setup();
        await Error(other.GetAsync($"/api/v1/equipment/{equipment.Id}/inspection-history"), "not_found", HttpStatusCode.NotFound);
        await Error(other.GetAsync($"/api/v1/equipment-inspections/{planned.Inspection.Id}"), "not_found", HttpStatusCode.NotFound);
        await Error(Send(other, HttpMethod.Post, Path(otherEquipment, "inspection-complete"), Completion(planned.Inspection, true), otherEquipment.Revision), "not_found", HttpStatusCode.NotFound);
        var ownOther = await Create<Equipment>(client, "equipment", new() { Name = "Zweites Gerät", InventoryNumber = "G-2" });
        await Error(Send(client, HttpMethod.Post, Path(ownOther, "inspection-complete"), Completion(planned.Inspection, true), ownOther.Revision), "inspection_equipment_mismatch");
        var invalid = Completion(planned.Inspection, true) with { PerformedOn = Today.AddDays(5) };
        await Error(Send(client, HttpMethod.Post, Path(equipment, "inspection-complete"), invalid, planned.Equipment.Revision), "inspection_future_result");
        Assert.Equal(planned.Equipment.Revision, (await fixture.Read<Equipment>(owner.TenantId, equipment.Id)).Revision);
        Assert.Equal(EquipmentInspectionStatus.Scheduled, (await fixture.Read<EquipmentInspection>(owner.TenantId, planned.Inspection.Id)).Status);
    }

    [PostgresApiFact]
    public async Task Legacy_equipment_keeps_canonical_json_and_gets_explicit_normalized_workplace_fields()
    {
        var (_, client, equipment) = await Setup();
        var raw = await Ok<JsonElement>(client.GetAsync($"/api/v1/equipment/{equipment.Id}"));
        Assert.False(raw.TryGetProperty("lastInspectionId", out _)); Assert.False(raw.TryGetProperty("inspectionBlocked", out _));
        var history = await Ok<JsonElement>(client.GetAsync($"/api/v1/equipment/{equipment.Id}/inspection-history"));
        var normalized = history.GetProperty("equipment");
        Assert.Equal(JsonValueKind.Null, normalized.GetProperty("lastInspectionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, normalized.GetProperty("pendingInspectionId").ValueKind);
        Assert.False(normalized.GetProperty("inspectionBlocked").GetBoolean());
        var future = await Create<Equipment>(client, "equipment", new() { Name = "Zukunft", InventoryNumber = "G-FUT", NextInspectionOn = Today.AddYears(1) });
        var due = await Ok<JsonElement>(client.GetAsync("/api/v1/equipment/inspection-worklist?status=due"));
        var summary = Assert.Single(due.GetProperty("items").EnumerateArray());
        Assert.Equal(equipment.Id, summary.GetProperty("id").GetGuid()); Assert.Equal("Due", summary.GetProperty("dueStatus").GetString());
        await Plan(client, future);
        var planned = await Ok<JsonElement>(client.GetAsync("/api/v1/equipment/inspection-worklist?status=planned&search=zukunft"));
        Assert.Equal(future.Id, Assert.Single(planned.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        await Error(client.GetAsync("/api/v1/equipment/inspection-worklist?status=unknown"), "invalid_inspection_filter");
    }

    [PostgresApiFact]
    public async Task History_pagination_retains_pending_item_outside_the_current_page()
    {
        var (_, client, equipment) = await Setup();
        for (var index = 0; index < 21; index++)
        {
            var planned = await Plan(client, equipment);
            var cancelled = await Ok<Change>(Send(client, HttpMethod.Post, Path(equipment, "inspection-cancel"), new
            { inspectionId = planned.Inspection.Id, inspectionRevision = planned.Inspection.Revision, reason = $"Dokumentierte Terminänderung {index}" }, planned.Equipment.Revision));
            equipment = cancelled.Equipment;
        }
        var open = await Plan(client, equipment);
        var history = await Ok<JsonElement>(client.GetAsync($"/api/v1/equipment/{equipment.Id}/inspection-history?page=2"));
        Assert.Equal(22, history.GetProperty("total").GetInt32()); Assert.Equal(2, history.GetProperty("items").GetArrayLength());
        Assert.Equal(open.Inspection.Id, history.GetProperty("pendingInspection").GetProperty("id").GetGuid());
        Assert.DoesNotContain(history.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == open.Inspection.Id);
    }

    private async Task<(ApiTestUser Owner, HttpClient Client, Equipment Equipment)> Setup(DateOnly? next = null)
    {
        var tenant = await fixture.CreateTenant(); var owner = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(owner);
        var equipment = await Create<Equipment>(client, "equipment", new() { Name = "Materialaufzug", InventoryNumber = "G-17", Kind = EquipmentKind.Machine,
            NextInspectionOn = next ?? Today, Available = true });
        return (owner, client, equipment);
    }
    private static string Path(Equipment equipment, string command) => $"/api/v1/equipment/{equipment.Id}/{command}";
    private static EquipmentInspectionPlanRequest PlanBody(EquipmentInspectionKind kind = EquipmentInspectionKind.Inspection) =>
        new(kind, Today, "Betrieblich festgelegte Geräteprüfung", "Prüfumfang gemäß Geräteakte");
    private static Task<Change> Plan(HttpClient client, Equipment equipment, EquipmentInspectionKind kind = EquipmentInspectionKind.Inspection) =>
        Ok<Change>(Send(client, HttpMethod.Post, Path(equipment, "inspection-plan"), PlanBody(kind), equipment.Revision));
    private static EquipmentInspectionCompleteRequest Completion(EquipmentInspection inspection, bool passed) =>
        new(inspection.Id, inspection.Revision, Today, passed ? EquipmentInspectionResult.Passed : EquipmentInspectionResult.Failed,
            "Synthetische prüfende Person", "Prüfbericht TEST-17", passed ? "Funktion nach Reparatur geprüft" : "Sicherung beschädigt; außer Betrieb genommen",
            passed ? Today.AddMonths(6) : null, passed);
    private static async Task<(Employee Employee, Project Project)> AssignmentSources(HttpClient client)
    {
        var customer = await Create<Customer>(client, "customers", new() { Name = "Synthetischer Kunde" });
        var building = await Create<Building>(client, "buildings", new() { CustomerId = customer.Id, Name = "Werkstatt", Address = "Teststraße 1" });
        var project = await Create<Project>(client, "projects", new() { CustomerId = customer.Id, BuildingId = building.Id, Name = "Dachreparatur" });
        var employee = await Create<Employee>(client, "employees", new() { Name = "Synthetischer Mitarbeiter", Active = true });
        return (employee, project);
    }
    private static Task<T> Create<T>(HttpClient client, string kind, T entity) where T : TenantEntity =>
        Ok<T>(Send(client, HttpMethod.Post, $"/api/v1/{kind}", entity));
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> responseTask)
    {
        using var response = await responseTask; var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}"); return AppJson.Read<T>(text);
    }
    private static async Task Error(Task<HttpResponseMessage> task, string code, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        using var response = await task; var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode); using var json = JsonDocument.Parse(text); Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }
}
