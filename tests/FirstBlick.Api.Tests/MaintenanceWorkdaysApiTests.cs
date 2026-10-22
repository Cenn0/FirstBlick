using System.Net;
using System.Text;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class MaintenanceWorkdaysApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static MaintenanceWorkdayPolicy Weekdays(params DateOnly[] excluded) => new(
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], excluded);

    [PostgresApiFact]
    public async Task Reviewed_execution_day_creates_project_keeps_original_calendar_due_and_advances_original_month_end()
    {
        var (user, client, contract) = await Setup(); contract = await Configure(client, contract, Weekdays(new DateOnly(2027, 2, 1)));
        var preview = await Preview(client, contract);
        Assert.Equal(new DateOnly(2027, 1, 31), preview.DueDate); Assert.Equal(new DateOnly(2027, 2, 2), preview.ScheduledDate);
        Assert.Equal(user.TenantId, preview.TenantId); Assert.Equal(contract.Revision, preview.ContractRevision); Assert.Null(preview.ExistingVisitId);
        await Error(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/schedule",
            new { expectedDueDate = contract.NextDueDate }, contract.Revision), "maintenance_workday_preview");
        await Error(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/schedule",
            new { expectedDueDate = contract.NextDueDate, reviewedPreviewDigest = new string('0', 64) }, contract.Revision), "maintenance_workday_preview");
        var key = Guid.NewGuid().ToString("N");
        var body = new { expectedDueDate = contract.NextDueDate, reviewedPreviewDigest = preview.Digest };
        var visit = await Ok<MaintenanceVisit>(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/schedule", body, contract.Revision, key));
        using var replay = await Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/schedule", body, contract.Revision, key);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        var project = await fixture.Read<Project>(user.TenantId, visit.ProjectId);
        Assert.Equal(preview.ScheduledDate, project.PlannedStart); Assert.Equal(project.PlannedStart, project.PlannedEnd);
        Assert.Equal(preview.ScheduledDate, visit.ExecutionPlan!.ScheduledDate); Assert.Equal(preview.DueDate, visit.DueDate);
        var stored = await fixture.Read<MaintenanceVisit>(user.TenantId, visit.Id); stored.Validate(); Assert.Equal(visit.ScopeDigest, stored.ScopeDigest);
        using var calendar = await client.GetAsync($"/api/v1/maintenance-visits/{visit.Id}/calendar?revision={visit.Revision}");
        Assert.Equal(HttpStatusCode.OK, calendar.StatusCode);
        Assert.Contains("DTSTART;VALUE=DATE:20270131", Encoding.UTF8.GetString(await calendar.Content.ReadAsByteArrayAsync()));
        foreach (var check in visit.Checklist.ToArray()) visit = await Ok<MaintenanceVisit>(Send(client, HttpMethod.Post,
            $"/api/v1/maintenance-visits/{visit.Id}/check", new { checkId = check.Id, state = "Done", note = "Geprüft" }, visit.Revision));
        visit = await Ok<MaintenanceVisit>(Send(client, HttpMethod.Post, $"/api/v1/maintenance-visits/{visit.Id}/complete", new { notes = "Erledigt" }, visit.Revision));
        Assert.Equal(new DateOnly(2027, 2, 28), visit.NextDueDate);
        contract = await fixture.Read<MaintenanceContract>(user.TenantId, contract.Id);
        var next = await Preview(client, contract); Assert.Equal(new DateOnly(2027, 3, 1), next.ScheduledDate);
    }

    [PostgresApiFact]
    public async Task Existing_occurrence_retains_its_original_policy_when_contract_is_changed_or_disabled()
    {
        var (_, client, contract) = await Setup(); contract = await Configure(client, contract, Weekdays(new DateOnly(2027, 2, 1)));
        var preview = await Preview(client, contract); var visit = await Plan(client, contract, preview);
        contract = await Configure(client, contract, new([DayOfWeek.Friday], []));
        var existing = await Preview(client, contract);
        Assert.Equal(visit.Id, existing.ExistingVisitId); Assert.Equal(new DateOnly(2027, 2, 2), existing.ScheduledDate);
        Assert.Equal(visit.ExecutionPlan!.Workdays.ExcludedDates, existing.Workdays!.ExcludedDates);
        Assert.Equal(visit.Id, (await Plan(client, contract, existing)).Id);
        contract = await Configure(client, contract, null); Assert.Null(contract.Workdays);
        existing = await Preview(client, contract); Assert.Equal(visit.Id, existing.ExistingVisitId); Assert.Equal(new DateOnly(2027, 2, 2), existing.ScheduledDate);
        await Error(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/schedule",
            new { expectedDueDate = contract.NextDueDate }, contract.Revision), "maintenance_workday_preview");
        Assert.Equal(visit.Id, (await Plan(client, contract, existing)).Id);
    }

    [PostgresApiFact]
    public async Task Legacy_configuration_and_generic_edits_preserve_workday_policy_and_cannot_replace_it()
    {
        var (_, client, contract) = await Setup(); contract = await Configure(client, contract, Weekdays());
        contract = await Ok<MaintenanceContract>(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/configure",
            new { active = true, automaticPlanning = false, planningLeadDays = 21, checklistTemplate = contract.ChecklistTemplate }, contract.Revision));
        Assert.NotNull(contract.Workdays); Assert.Equal(21, contract.PlanningLeadDays);
        var preserved = ContentDigest.Of(contract.Workdays); contract.Workdays = null; contract.Scope = "Bearbeiteter Umfang";
        contract = await Ok<MaintenanceContract>(Send(client, HttpMethod.Put, $"/api/v1/maintenance-contracts/{contract.Id}", contract, contract.Revision));
        Assert.Equal(preserved, ContentDigest.Of(contract.Workdays));
        contract.Workdays = new([DayOfWeek.Sunday], []);
        await Error(Send(client, HttpMethod.Put, $"/api/v1/maintenance-contracts/{contract.Id}", contract, contract.Revision), "maintenance_workdays_command_required");
    }

    [PostgresApiFact]
    public async Task Invalid_policy_and_no_day_before_contract_end_leave_no_planned_project()
    {
        var (user, client, contract) = await Setup();
        await Error(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/configure", new
        { active = true, automaticPlanning = false, planningLeadDays = 14, checklistTemplate = contract.ChecklistTemplate,
            updateWorkdays = true, workdays = new MaintenanceWorkdayPolicy([], []) }, contract.Revision), "maintenance_workday_policy");
        Assert.Equal(contract.Revision, (await fixture.Read<MaintenanceContract>(user.TenantId, contract.Id)).Revision);
        contract.EndDate = contract.NextDueDate;
        contract = await Ok<MaintenanceContract>(Send(client, HttpMethod.Put, $"/api/v1/maintenance-contracts/{contract.Id}", contract, contract.Revision));
        contract = await Configure(client, contract, Weekdays());
        await Error(client.GetAsync($"/api/v1/maintenance-contracts/{contract.Id}/planning-preview?revision={contract.Revision}"), "maintenance_workday_window");
        await Error(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/schedule", new
        { expectedDueDate = contract.NextDueDate, reviewedPreviewDigest = new string('a', 64) }, contract.Revision), "maintenance_workday_window");
        await fixture.InScope(async services => Assert.Equal(0, await services.GetRequiredService<FirstBlickDbContext>().Entities.IgnoreQueryFilters()
            .CountAsync(x => x.TenantId == user.TenantId && (x.Kind == "projects" || x.Kind == "maintenance-visits"))));
    }

    [PostgresApiFact]
    public async Task Preview_and_confirmation_are_bound_to_tenant_current_revision_and_access()
    {
        var (user, client, contract) = await Setup(); contract = await Configure(client, contract, Weekdays());
        var oldPreview = await Preview(client, contract);
        var path = $"/api/v1/maintenance-contracts/{contract.Id}/planning-preview?revision={contract.Revision}";
        var (_, other, _) = await Setup(); await Error(other.GetAsync(path), "not_found", HttpStatusCode.NotFound);
        var worker = await fixture.CreateUser(user.TenantId, BusinessRole.Worker); var workerClient = await fixture.Login(worker);
        await Error(workerClient.GetAsync(path), "forbidden", HttpStatusCode.Forbidden);
        contract = await Configure(client, contract, Weekdays(new DateOnly(2027, 2, 1)));
        await Error(client.GetAsync(path), "revision_conflict", HttpStatusCode.Conflict);
        await Error(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/schedule", new
        { expectedDueDate = contract.NextDueDate, reviewedPreviewDigest = oldPreview.Digest }, contract.Revision), "maintenance_workday_preview");
        var preview = await Preview(client, contract);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(x => x.Id == user.TenantId);
            tenant.Modules = ["core"]; await db.SaveChangesAsync();
        });
        await Error(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/schedule", new
        { expectedDueDate = contract.NextDueDate, reviewedPreviewDigest = preview.Digest }, contract.Revision), "module_disabled", HttpStatusCode.Forbidden);
    }

    [PostgresApiFact]
    public async Task Automatic_planning_uses_business_date_across_dst_and_keeps_one_shifted_occurrence()
    {
        var (user, client, contract) = await Setup(); contract.StartDate = contract.NextDueDate = new(2026, 10, 25);
        contract = await Ok<MaintenanceContract>(Send(client, HttpMethod.Put, $"/api/v1/maintenance-contracts/{contract.Id}", contract, contract.Revision));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(x => x.Id == user.TenantId);
            tenant.TimeZone = "Europe/Berlin"; await db.SaveChangesAsync();
        });
        contract = await Configure(client, contract, Weekdays(new DateOnly(2026, 10, 26)), automatic: true, leadDays: 0);
        Assert.Equal(0, await RunScheduler(new(2026, 10, 24, 21, 59, 0, TimeSpan.Zero)));
        Assert.Equal(1, await RunScheduler(new(2026, 10, 24, 22, 0, 0, TimeSpan.Zero)));
        Assert.Equal(0, await RunScheduler(new(2026, 10, 25, 1, 30, 0, TimeSpan.Zero)));
        var preview = await Preview(client, contract); Assert.NotNull(preview.ExistingVisitId); Assert.Equal(new DateOnly(2026, 10, 27), preview.ScheduledDate);
        var visit = await fixture.Read<MaintenanceVisit>(user.TenantId, preview.ExistingVisitId.Value);
        Assert.Equal(new DateOnly(2026, 10, 25), visit.DueDate);
        Assert.Equal(preview.ScheduledDate, (await fixture.Read<Project>(user.TenantId, visit.ProjectId)).PlannedStart);
    }

    private Task<int> RunScheduler(DateTimeOffset now) => fixture.InScope(services => services.GetRequiredService<MaintenancePlanner>().ScheduleDue(now));
    private static Task<MaintenancePlanningPreview> Preview(HttpClient client, MaintenanceContract contract) => Ok<MaintenancePlanningPreview>(
        client.GetAsync($"/api/v1/maintenance-contracts/{contract.Id}/planning-preview?revision={contract.Revision}"));
    private static Task<MaintenanceVisit> Plan(HttpClient client, MaintenanceContract contract, MaintenancePlanningPreview preview) => Ok<MaintenanceVisit>(Send(client,
        HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/schedule", new { expectedDueDate = contract.NextDueDate, reviewedPreviewDigest = preview.Digest }, contract.Revision));
    private static Task<MaintenanceContract> Configure(HttpClient client, MaintenanceContract contract, MaintenanceWorkdayPolicy? policy, bool automatic = false, int leadDays = 14)
        => Ok<MaintenanceContract>(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/configure", new
        { active = true, automaticPlanning = automatic, planningLeadDays = leadDays, checklistTemplate = contract.ChecklistTemplate, updateWorkdays = true, workdays = policy }, contract.Revision));
    private async Task<(ApiTestUser User, HttpClient Client, MaintenanceContract Contract)> Setup()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(user);
        var customer = await Ok<Customer>(Send(client, HttpMethod.Post, "/api/v1/customers", new Customer { Name = "Workday test customer" }));
        var building = await Ok<Building>(Send(client, HttpMethod.Post, "/api/v1/buildings", new Building { CustomerId = customer.Id, Name = "Test roof", Address = "Teststraße 1" }));
        var contract = await Ok<MaintenanceContract>(Send(client, HttpMethod.Post, "/api/v1/maintenance-contracts", new MaintenanceContract
        { CustomerId = customer.Id, BuildingId = building.Id, Title = "Wartung mit Arbeitstagen", Scope = "Dach prüfen", StartDate = new(2027, 1, 31),
            NextDueDate = new(2027, 1, 31), IntervalMonths = 1, ChecklistTemplate = [new("Abläufe prüfen")] }));
        return (user, client, contract);
    }
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> task)
    {
        using var response = await task; var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {json}"); return AppJson.Read<T>(json);
    }
    private static async Task Error(Task<HttpResponseMessage> task, string code, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        using var response = await task; var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {json}");
        using var document = JsonDocument.Parse(json); Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
}
