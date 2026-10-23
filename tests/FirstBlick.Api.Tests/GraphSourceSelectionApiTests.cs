using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FirstBlick.Api.Tests;

public sealed class GraphSourceSelectionApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    [PostgresApiFact]
    public async Task Time_validation_ignores_unrelated_history_and_still_rejects_own_overlap_without_a_receipt()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id, BusinessRole.Worker);
        var project = await Project(user); var employee = await fixture.Save(new Employee { TenantId = tenant.Id, UserId = user.UserId, Name = "Field worker" }, user.UserId);
        await fixture.Save(new ScheduleAssignment { TenantId = tenant.Id, ProjectId = project.Id, EmployeeId = employee.Id, Title = "Field work", Start = Start, End = Start.AddHours(8) }, user.UserId);
        await Seed(tenant.Id, "time-entries");
        var client = await fixture.Login(user);
        var body = new TimeEntry { TenantId = tenant.Id, ProjectId = project.Id, EmployeeId = employee.Id, Activity = "Synthetic work", Start = Start, End = Start.AddHours(1) };
        await Ok(PostgresApiFixture.Send(client, HttpMethod.Post, "/api/v1/time-entries", body));
        var key = Guid.NewGuid().ToString("N");
        await Error(PostgresApiFixture.Send(client, HttpMethod.Post, "/api/v1/time-entries", body, key: key), "time_conflict");
        await NoReceipt(tenant.Id, key);
        await Ok(PostgresApiFixture.Send(client, HttpMethod.Post, "/api/v1/time-entries", new TimeEntry
        { TenantId = tenant.Id, ProjectId = project.Id, EmployeeId = employee.Id, Activity = "Adjacent work", Start = Start.AddHours(1), End = Start.AddHours(2) }));
    }

    [PostgresApiFact]
    public async Task Project_change_ignores_unrelated_quotes_but_preserves_its_own_customer_commitments()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id); var project = await Project(user);
        await fixture.Save(new Quote { TenantId = tenant.Id, ProjectId = project.Id, CustomerId = project.CustomerId, Title = "Retained draft" }, user.UserId);
        foreach (var kind in new[] { "quotes", "roof-revisions", "measurements", "technical-results", "schedule-assignments" }) await Seed(tenant.Id, kind);
        var client = await fixture.Login(user); project.Name = "Renamed project";
        var updated = await Ok(PostgresApiFixture.Send(client, HttpMethod.Put, $"/api/v1/projects/{project.Id}", project, project.Revision));
        project = AppJson.Read<Project>(updated.GetRawText());
        var other = await Project(user); project.CustomerId = other.CustomerId; project.BuildingId = other.BuildingId;
        var key = Guid.NewGuid().ToString("N");
        await Error(PostgresApiFixture.Send(client, HttpMethod.Put, $"/api/v1/projects/{project.Id}", project, project.Revision, key), "customer_mismatch");
        await NoReceipt(tenant.Id, key);
    }

    [PostgresApiFact]
    public async Task Reservations_use_only_the_material_history_and_do_not_overbook_after_either_creation_path()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id); var project = await Project(user);
        var material = await fixture.Save(new Material { TenantId = tenant.Id, Name = "Synthetic material" }, user.UserId);
        await fixture.Save(new StockMovement { TenantId = tenant.Id, MaterialId = material.Id, Kind = StockMovementKind.Adjustment,
            Location = "Synthetic warehouse", Quantity = 10m, UnitCost = 1m, OccurredAt = Start, Reason = "Synthetic opening balance" }, user.UserId);
        await Seed(tenant.Id, "stock-movements"); await Seed(tenant.Id, "material-reservations");
        var client = await fixture.Login(user);
        await Ok(PostgresApiFixture.Send(client, HttpMethod.Post, $"/api/v1/materials/{material.Id}/reserve",
            new MaterialReserveRequest(project.Id, 6m, "Synthetic warehouse"), material.Revision));
        await Ok(PostgresApiFixture.Send(client, HttpMethod.Post, "/api/v1/material-reservations", new MaterialReservation
        { TenantId = tenant.Id, MaterialId = material.Id, ProjectId = project.Id, Location = "Synthetic warehouse", Quantity = 4m }));
        var key = Guid.NewGuid().ToString("N");
        await Error(PostgresApiFixture.Send(client, HttpMethod.Post, $"/api/v1/materials/{material.Id}/reserve",
            new MaterialReserveRequest(project.Id, 1m, "Synthetic warehouse"), material.Revision, key), "insufficient_stock");
        await NoReceipt(tenant.Id, key);
    }

    [PostgresApiFact]
    public async Task Acceptance_checks_all_own_project_defects_without_reading_unrelated_projects()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id); var project = await Project(user);
        var defect = await fixture.Save(new Defect { TenantId = tenant.Id, ProjectId = project.Id, Title = "Open defect", Description = "Synthetic unresolved defect" }, user.UserId);
        await Seed(tenant.Id, "defects"); var client = await fixture.Login(user);
        var body = new Acceptance { TenantId = tenant.Id, ProjectId = project.Id, Title = "Synthetic handover", CustomerRepresentative = "Synthetic customer",
            ContractorRepresentative = "Synthetic contractor", OccurredAt = Start };
        await Error(PostgresApiFixture.Send(client, HttpMethod.Post, "/api/v1/acceptances", body), "unacknowledged_defects");
        body.WithReservations = true; body.DefectIds = [defect.Id]; body.Notes = "Retained reservation";
        await Ok(PostgresApiFixture.Send(client, HttpMethod.Post, "/api/v1/acceptances", body));
    }

    [PostgresApiFact]
    public async Task Planning_retains_cross_employee_equipment_conflicts_and_can_release_a_cancelled_slot()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id); var project = await Project(user);
        var own = await fixture.Save(new Employee { TenantId = tenant.Id, Name = "First worker" }, user.UserId);
        var other = await fixture.Save(new Employee { TenantId = tenant.Id, Name = "Second worker" }, user.UserId);
        var equipment = await fixture.Save(new Equipment { TenantId = tenant.Id, Name = "Shared equipment", InventoryNumber = "SYNTH-TOOL-1" }, user.UserId);
        await Seed(tenant.Id, "schedule-assignments"); await Seed(tenant.Id, "absences");
        var client = await fixture.Login(user);
        var body = new ScheduleAssignment { TenantId = tenant.Id, ProjectId = project.Id, EmployeeId = own.Id, EquipmentIds = [equipment.Id], Title = "First slot", Start = Start, End = Start.AddHours(1) };
        var saved = AppJson.Read<ScheduleAssignment>((await Ok(PostgresApiFixture.Send(client, HttpMethod.Post, "/api/v1/schedule-assignments", body))).GetRawText());
        body.EmployeeId = other.Id;
        await Error(PostgresApiFixture.Send(client, HttpMethod.Post, "/api/v1/schedule-assignments", body), "planning_conflict");
        saved.Cancelled = true;
        await Ok(PostgresApiFixture.Send(client, HttpMethod.Put, $"/api/v1/schedule-assignments/{saved.Id}", saved, saved.Revision));
        await Ok(PostgresApiFixture.Send(client, HttpMethod.Post, "/api/v1/schedule-assignments", body));
    }

    private async Task<Project> Project(ApiTestUser user)
    {
        var customer = await fixture.Save(new Customer { TenantId = user.TenantId, Name = "Synthetic graph customer" }, user.UserId);
        var building = await fixture.Save(new Building { TenantId = user.TenantId, CustomerId = customer.Id, Name = "Synthetic building", Address = "Synthetic address" }, user.UserId);
        return await fixture.Save(new Project { TenantId = user.TenantId, CustomerId = customer.Id, BuildingId = building.Id, Name = "Synthetic graph project" }, user.UserId);
    }
    private static async Task<JsonElement> Ok(Task<HttpResponseMessage> task)
    {
        using var response = await task; var text = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text); return json.RootElement.Clone();
    }
    private static async Task Error(Task<HttpResponseMessage> task, string code)
    {
        using var response = await task; var text = await response.Content.ReadAsStringAsync(); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(text); Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }
    private Task NoReceipt(Guid tenant, string key) => fixture.InScope(async services => Assert.False(await services.GetRequiredService<FirstBlickDbContext>()
        .Receipts.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenant && x.Key == key)));
    private Task Seed(Guid tenantId, string kind) => fixture.InScope(async services =>
    {
        var db = services.GetRequiredService<FirstBlickDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Entities" ("TenantId","Kind","Id","Revision","Data","SearchText","Archived","CreatedAt","UpdatedAt")
            SELECT {tenantId},{kind},gen_random_uuid(),1,jsonb_build_object(),'',false,now(),now() FROM generate_series(1,10001)
            """);
    });
}
