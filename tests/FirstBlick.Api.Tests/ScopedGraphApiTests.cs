using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class ScopedGraphApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    [PostgresApiFact]
    public async Task Project_updates_ignore_unrelated_histories_but_keep_their_linked_assignment_window()
    {
        var g = await Setup();
        await fixture.Save(new ScheduleAssignment { TenantId = g.User.TenantId, EmployeeId = g.Employee.Id, ProjectId = g.Project.Id,
            Title = "Retained project window", Start = Start, End = Start.AddHours(4) }, g.User.UserId);
        foreach (var kind in new[] { "quotes", "roof-revisions", "measurements", "technical-results", "schedule-assignments" })
            await Unrelated(g.User.TenantId, kind);
        g.Project.Name = "Updated despite unrelated history";
        var changed = await Ok<Project>(Send(g.Client, HttpMethod.Put, $"/api/v1/projects/{g.Project.Id}", g.Project, g.Project.Revision));
        changed.PlannedEnd = DateOnly.FromDateTime(Start.UtcDateTime).AddDays(-1);
        await Error(Send(g.Client, HttpMethod.Put, $"/api/v1/projects/{changed.Id}", changed, changed.Revision), "planning_conflict");
        var retained = await fixture.Read<Project>(g.User.TenantId, changed.Id);
        Assert.Equal(changed.Revision, retained.Revision); Assert.Null(retained.PlannedEnd);
    }

    [PostgresApiFact]
    public async Task Dispatch_unions_employee_and_equipment_conflicts_and_absence_keeps_the_reverse_check()
    {
        var g = await Setup();
        var colleague = await fixture.Save(new Employee { TenantId = g.User.TenantId, Name = "Synthetic colleague" }, g.User.UserId);
        var tool = await fixture.Save(new Equipment { TenantId = g.User.TenantId, Name = "Synthetic shared tool", InventoryNumber = "SYN-TOOL-1" }, g.User.UserId);
        await fixture.Save(new ScheduleAssignment { TenantId = g.User.TenantId, EmployeeId = g.Employee.Id, ProjectId = g.Project.Id,
            Title = "Existing shared resource", EquipmentIds = [tool.Id], Start = Start, End = Start.AddHours(4) }, g.User.UserId);
        await Unrelated(g.User.TenantId, "schedule-assignments"); await Unrelated(g.User.TenantId, "absences");
        var input = new ScheduleAssignment { ProjectId = g.Project.Id, EmployeeId = colleague.Id, EquipmentIds = [tool.Id],
            Title = "Shared equipment overlap", Start = Start.AddHours(1), End = Start.AddHours(2) };
        await Error(Send(g.Client, HttpMethod.Post, "/api/v1/schedule-assignments", input), "planning_conflict");
        input.Start = Start.AddHours(4); input.End = Start.AddHours(5);
        await Ok<ScheduleAssignment>(Send(g.Client, HttpMethod.Post, "/api/v1/schedule-assignments", input));
        await Error(Send(g.Client, HttpMethod.Post, "/api/v1/absences", new Absence { EmployeeId = g.Employee.Id,
            Kind = AbsenceKind.Vacation, Status = AbsenceStatus.Approved, Start = Start, End = Start.AddHours(1) }), "planning_conflict");
    }

    [PostgresApiFact]
    public async Task Time_overlap_uses_the_employees_history_even_when_other_histories_exceed_the_source_budget()
    {
        var g = await Setup(); await Unrelated(g.User.TenantId, "time-entries");
        var input = new TimeEntry { ProjectId = g.Project.Id, EmployeeId = g.Employee.Id, Start = Start, End = Start.AddHours(1), Activity = "Synthetic repair work" };
        await Ok<TimeEntry>(Send(g.Client, HttpMethod.Post, "/api/v1/time-entries", input));
        input.Start = Start.AddMinutes(30); input.End = Start.AddHours(2);
        await Error(Send(g.Client, HttpMethod.Post, "/api/v1/time-entries", input), "time_conflict");
    }

    [PostgresApiFact]
    public async Task Acceptance_requires_all_linked_open_defects_without_reading_unrelated_defects()
    {
        var g = await Setup();
        var defect = await fixture.Save(new Defect { TenantId = g.User.TenantId, ProjectId = g.Project.Id,
            Title = "Synthetic retained defect", Description = "Synthetic pending correction" }, g.User.UserId);
        await Unrelated(g.User.TenantId, "defects");
        var input = new Acceptance { ProjectId = g.Project.Id, Title = "Synthetic customer acceptance", OccurredAt = Start,
            CustomerRepresentative = "Synthetic customer", ContractorRepresentative = "Synthetic contractor" };
        await Error(Send(g.Client, HttpMethod.Post, "/api/v1/acceptances", input), "unacknowledged_defects");
        input.WithReservations = true; input.DefectIds = [defect.Id]; input.Notes = "Retained open defect acknowledged.";
        var result = await Ok<Acceptance>(Send(g.Client, HttpMethod.Post, "/api/v1/acceptances", input));
        Assert.Equal([defect.Id], result.DefectIds);
    }

    private async Task<Graph> Setup()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Scoped synthetic customer" }, user.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id, Name = "Scoped synthetic building", Address = "Synthetic address" }, user.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id, BuildingId = building.Id, Name = "Scoped synthetic project" }, user.UserId);
        var employee = await fixture.Save(new Employee { TenantId = tenant.Id, Name = "Scoped synthetic employee" }, user.UserId);
        return new(user, await fixture.Login(user), project, employee);
    }

    private Task Unrelated(Guid tenant, string kind) => fixture.InScope(async services =>
    {
        var db = services.GetRequiredService<FirstBlickDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Entities" ("TenantId","Kind","Id","Revision","Data","SearchText","Archived","CreatedAt","UpdatedAt")
            SELECT {tenant},{kind},gen_random_uuid(),1,jsonb_build_object(),'',false,now(),now()
            FROM generate_series(1,{EntitySources.RecordLimit + 1})
            """);
    });

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text); return AppJson.Read<T>(text);
    }
    private static async Task Error(Task<HttpResponseMessage> pending, string code)
    {
        using var response = await pending; Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }
    private sealed record Graph(ApiTestUser User, HttpClient Client, Project Project, Employee Employee);
}
