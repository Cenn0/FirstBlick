using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class PlanningWeekApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private async Task<(BusinessTenant Tenant, ApiTestUser Director, HttpClient Client,
        Employee Assigned, Employee Other, Project Project)> Scenario()
    {
        var tenant = await fixture.CreateTenant();
        var director = await fixture.CreateUser(tenant.Id);
        var client = await fixture.Login(director);
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Calendar owner" }, director.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id,
            CustomerId = customer.Id, Name = "Calendar building", Address = "Synthetic address 1" }, director.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id,
            CustomerId = customer.Id, BuildingId = building.Id, Name = "Rafter renovation" }, director.UserId);
        var assigned = await fixture.Save(new Employee { TenantId = tenant.Id,
            Name = "Synthetic roofer A" }, director.UserId);
        var other = await fixture.Save(new Employee { TenantId = tenant.Id,
            Name = "Synthetic roofer B" }, director.UserId);
        return (tenant, director, client, assigned, other, project);
    }

    [PostgresApiFact]
    public async Task Week_projection_shows_only_overlapping_live_events_with_tenant_names_and_business_dates()
    {
        var data = await Scenario();
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var tenant = await db.Tenants.SingleAsync(x => x.Id == data.Tenant.Id);
            tenant.TimeZone = "Europe/Berlin";
            await db.SaveChangesAsync();
        });
        var crossing = await fixture.Save(new ScheduleAssignment
        {
            TenantId = data.Tenant.Id, ProjectId = data.Project.Id, EmployeeId = data.Assigned.Id,
            Title = "Night roof repair", Start = new(2026, 10, 24, 21, 30, 0, TimeSpan.Zero),
            End = new(2026, 10, 25, 2, 30, 0, TimeSpan.Zero)
        }, data.Director.UserId);
        _ = await fixture.Save(new ScheduleAssignment
        {
            TenantId = data.Tenant.Id, ProjectId = data.Project.Id, EmployeeId = data.Other.Id,
            Title = "Future after calendar", Start = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero),
            End = new(2026, 11, 7, 16, 0, 0, TimeSpan.Zero)
        }, data.Director.UserId);
        _ = await fixture.Save(new ScheduleAssignment
        {
            TenantId = data.Tenant.Id, ProjectId = data.Project.Id, EmployeeId = data.Other.Id,
            Title = "Cancelled yesterday", Start = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero),
            End = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero), Cancelled = true
        }, data.Director.UserId);

        using var response = await data.Client.GetAsync("/api/v1/planning/week?weekStart=2026-10-19");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("Europe/Berlin", root.GetProperty("timeZone").GetString());
        Assert.Equal(7, root.GetProperty("days").GetArrayLength());
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        var one = Assert.Single(root.GetProperty("assignments").EnumerateArray());
        Assert.Equal(crossing.Id.ToString(), one.GetProperty("id").GetString());
        Assert.Equal("Night roof repair", one.GetProperty("title").GetString());
        Assert.Equal("Synthetic roofer A", one.GetProperty("employeeName").GetString());
        Assert.Equal("Rafter renovation", one.GetProperty("projectName").GetString());
        Assert.Equal(new[] { "2026-10-24", "2026-10-25" },
            one.GetProperty("days").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(TimeSpan.FromHours(25),
            root.GetProperty("days")[6].GetProperty("endUtc").GetDateTimeOffset() -
            root.GetProperty("days")[6].GetProperty("startUtc").GetDateTimeOffset());
        await Error(data.Client.GetAsync("/api/v1/planning/week?weekStart=2026-10-20"),
            HttpStatusCode.BadRequest, "invalid_planning_week");
    }

    [PostgresApiFact]
    public async Task Worker_sees_only_their_assigned_week_without_cross_tenant_or_unrelated_titles()
    {
        var data = await Scenario();
        var workerUser = await fixture.CreateUser(data.Tenant.Id, BusinessRole.Worker);
        data.Assigned.UserId = workerUser.UserId;
        await fixture.InScope(async services =>
        {
            services.GetRequiredService<TenantScope>().TenantId = data.Tenant.Id;
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var stored = await db.Entities.SingleAsync(x => x.TenantId == data.Tenant.Id
                && x.Kind == "employees" && x.Id == data.Assigned.Id);
            var entity = AppJson.Read<Employee>(stored.Data);
            entity.UserId = workerUser.UserId;
            stored.Data = AppJson.Serialize(entity);
            await db.SaveChangesAsync();
        });
        var own = await fixture.Save(new ScheduleAssignment
        {
            TenantId = data.Tenant.Id, ProjectId = data.Project.Id, EmployeeId = data.Assigned.Id,
            Title = "Only visible assignment", Start = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero),
            End = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero)
        }, data.Director.UserId);
        _ = await fixture.Save(new ScheduleAssignment
        {
            TenantId = data.Tenant.Id, ProjectId = data.Project.Id, EmployeeId = data.Other.Id,
            Title = "Confidential team assignment", Start = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero),
            End = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero)
        }, data.Director.UserId);
        var worker = await fixture.Login(workerUser);
        using var result = await worker.GetAsync("/api/v1/planning/week?weekStart=2026-10-19");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var payload = await result.Content.ReadAsStringAsync();
        using var parsed = JsonDocument.Parse(payload);
        var visible = Assert.Single(parsed.RootElement.GetProperty("assignments").EnumerateArray());
        Assert.Equal(own.Id.ToString(), visible.GetProperty("id").GetString());
        Assert.DoesNotContain("Confidential team assignment", payload, StringComparison.Ordinal);

        var outsider = await fixture.CreateTenant();
        var stranger = await fixture.Login(await fixture.CreateUser(outsider.Id));
        using var different = await stranger.GetAsync("/api/v1/planning/week?weekStart=2026-10-19");
        Assert.Equal(HttpStatusCode.OK, different.StatusCode);
        using var otherJson = JsonDocument.Parse(await different.Content.ReadAsStringAsync());
        Assert.Equal(0, otherJson.RootElement.GetProperty("count").GetInt32());
    }

    [PostgresApiFact]
    public async Task Resource_filters_preserve_equipment_identity_and_do_not_expand_visible_scope()
    {
        var data = await Scenario();
        var equipment = await fixture.Save(new Equipment { TenantId = data.Tenant.Id,
            Name = "Synthetic crane", InventoryNumber = "CAL-01", Kind = EquipmentKind.Machine }, data.Director.UserId);
        var selected = await fixture.Save(new ScheduleAssignment {
            TenantId = data.Tenant.Id, ProjectId = data.Project.Id, EmployeeId = data.Assigned.Id,
            EquipmentIds = [equipment.Id], Title = "Filtered roof lift",
            Start = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero), End = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero)
        }, data.Director.UserId);
        _ = await fixture.Save(new ScheduleAssignment {
            TenantId = data.Tenant.Id, ProjectId = data.Project.Id, EmployeeId = data.Other.Id,
            Title = "Other task", Start = selected.Start, End = selected.End
        }, data.Director.UserId);
        using var response = await data.Client.GetAsync($"/api/v1/planning/week?weekStart=2026-10-19&equipmentId={equipment.Id}&employeeId={data.Assigned.Id}&projectId={data.Project.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = Assert.Single(json.RootElement.GetProperty("assignments").EnumerateArray());
        Assert.Equal(selected.Id, result.GetProperty("id").GetGuid());
        var resource = Assert.Single(result.GetProperty("equipment").EnumerateArray());
        Assert.Equal(equipment.Id, resource.GetProperty("id").GetGuid());
        Assert.Equal(equipment.Name, resource.GetProperty("name").GetString());
        Assert.Equal(data.Assigned.Id, json.RootElement.GetProperty("filters").GetProperty("employeeId").GetGuid());
        using var unknown = await data.Client.GetAsync($"/api/v1/planning/week?weekStart=2026-10-19&employeeId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        using var empty = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        Assert.Equal(0, empty.RootElement.GetProperty("count").GetInt32());
    }

    [PostgresApiFact]
    public async Task Overfull_week_is_rejected_before_partial_results_and_can_be_narrowed()
    {
        var data = await Scenario();
        // Synthetic bulk projection fixture: avoid thousands of unrelated command/audit writes.
        await fixture.InScope(async services => {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            for (var i = 0; i <= PlanningWeek.MaximumAssignments; i++)
            {
                var entry = new ScheduleAssignment { TenantId = data.Tenant.Id,
                    EmployeeId = i == 0 ? data.Assigned.Id : data.Other.Id, ProjectId = data.Project.Id,
                    Title = $"Synthetic projection row {i}", Start = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero),
                    End = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero) };
                db.Entities.Add(new StoredEntity { TenantId = data.Tenant.Id, Kind = "schedule-assignments",
                    Id = entry.Id, Revision = entry.Revision, Data = AppJson.Serialize(entry),
                    CreatedAt = entry.CreatedAt, UpdatedAt = entry.UpdatedAt });
            }
            await db.SaveChangesAsync();
        });
        await Error(data.Client.GetAsync("/api/v1/planning/week?weekStart=2026-10-19"),
            HttpStatusCode.BadRequest, "planning_week_budget");
        using var narrowed = await data.Client.GetAsync($"/api/v1/planning/week?weekStart=2026-10-19&employeeId={data.Assigned.Id}");
        Assert.Equal(HttpStatusCode.OK, narrowed.StatusCode);
        using var json = JsonDocument.Parse(await narrowed.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("count").GetInt32());
    }

    [PostgresApiFact]
    public async Task Role_controls_projection_and_disabled_module_blocks_writes_but_retains_historical_read()
    {
        var data = await Scenario();
        var accountant = await fixture.Login(await fixture.CreateUser(data.Tenant.Id, BusinessRole.Accounting));
        await Error(accountant.GetAsync("/api/v1/planning/week?weekStart=2026-10-19"), HttpStatusCode.Forbidden, "forbidden");
        var disabled = await fixture.CreateTenant(modules: ["core"]);
        var disabledClient = await fixture.Login(await fixture.CreateUser(disabled.Id));
        using var historical = await disabledClient.GetAsync("/api/v1/planning/week?weekStart=2026-10-19");
        Assert.Equal(HttpStatusCode.OK, historical.StatusCode);
        await Error(Send(disabledClient, HttpMethod.Post, "/api/v1/schedule-assignments", new { }),
            HttpStatusCode.Forbidden, "module_disabled");
    }

    [PostgresApiFact]
    public async Task Weekly_edit_uses_existing_revision_and_conflict_guards_and_can_cancel_unavailable_resources()
    {
        var data = await Scenario();
        var equipment = await fixture.Save(new Equipment { TenantId = data.Tenant.Id,
            Name = "Synthetic lift", InventoryNumber = "CAL-02" }, data.Director.UserId);
        var body = new ScheduleAssignment { ProjectId = data.Project.Id, EmployeeId = data.Assigned.Id,
            EquipmentIds = [equipment.Id], Title = "Weekly work", Start = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero),
            End = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero) };
        const string path = "/api/v1/schedule-assignments";
        using var create = await Send(data.Client, HttpMethod.Post, path, body);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var saved = AppJson.Read<ScheduleAssignment>(await create.Content.ReadAsStringAsync());
        body.EmployeeId = data.Other.Id;
        await Error(Send(data.Client, HttpMethod.Post, path, body), HttpStatusCode.BadRequest, "planning_conflict");
        saved.Title = "Reviewed weekly work";
        using var update = await Send(data.Client, HttpMethod.Put, $"{path}/{saved.Id}", saved, saved.Revision);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var revised = AppJson.Read<ScheduleAssignment>(await update.Content.ReadAsStringAsync());
        await Error(Send(data.Client, HttpMethod.Put, $"{path}/{saved.Id}", saved, saved.Revision),
            HttpStatusCode.Conflict, "revision_conflict");
        var worker = await fixture.Login(await fixture.CreateUser(data.Tenant.Id, BusinessRole.Worker));
        await Error(Send(worker, HttpMethod.Put, $"{path}/{revised.Id}", revised, revised.Revision),
            HttpStatusCode.Forbidden, "forbidden");
        await fixture.InScope(async services => {
            services.GetRequiredService<TenantScope>().TenantId = data.Tenant.Id;
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var resource = await db.Entities.SingleAsync(x => x.Kind == "equipment" && x.Id == equipment.Id);
            var value = AppJson.Read<Equipment>(resource.Data); value.Available = false;
            resource.Data = AppJson.Serialize(value);
            await db.SaveChangesAsync();
        });
        revised.Cancelled = true;
        using var cancel = await Send(data.Client, HttpMethod.Put, $"{path}/{revised.Id}", revised, revised.Revision);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        using var week = await data.Client.GetAsync("/api/v1/planning/week?weekStart=2026-10-19");
        using var json = JsonDocument.Parse(await week.Content.ReadAsStringAsync());
        Assert.Equal(0, json.RootElement.GetProperty("count").GetInt32());
    }

    private static async Task Error(Task<HttpResponseMessage> pending, HttpStatusCode status, string code)
    {
        using var response = await pending;
        Assert.Equal(status, response.StatusCode);
        Assert.Contains(code, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
