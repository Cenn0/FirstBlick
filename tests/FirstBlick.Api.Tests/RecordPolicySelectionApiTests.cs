using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FirstBlick.Api.Tests;

public sealed class RecordPolicySelectionApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    [PostgresApiFact]
    public async Task Worker_reads_only_own_work_with_oversized_unrelated_sets_and_revokes_on_the_next_request()
    {
        var tenant = await fixture.CreateTenant(); var worker = await fixture.CreateUser(tenant.Id, BusinessRole.Worker);
        var own = await fixture.Save(new Employee { TenantId = tenant.Id, UserId = worker.UserId, Name = "Own worker" }, worker.UserId);
        var project = await Project(worker); var hidden = await Project(worker);
        var assignment = await fixture.Save(new ScheduleAssignment { TenantId = tenant.Id, EmployeeId = own.Id, ProjectId = project.Id,
            Title = "Own assignment", Start = Start, End = Start.AddHours(1) }, worker.UserId);
        var assignedTask = await fixture.Save(new TaskItem { TenantId = tenant.Id, ProjectId = project.Id, Title = "Marker project work" }, worker.UserId);
        var personalTask = await fixture.Save(new TaskItem { TenantId = tenant.Id, ProjectId = hidden.Id, AssignedEmployeeId = own.Id, Title = "Marker personal work" }, worker.UserId);
        await fixture.Save(new TaskItem { TenantId = tenant.Id, ProjectId = hidden.Id, Title = "Marker hidden work" }, worker.UserId);
        foreach (var kind in new[] { "employees", "schedule-assignments", "projects", "tasks" }) await Seed(tenant.Id, kind, 10_001, "{}");
        var client = await fixture.Login(worker);
        Assert.Equal([own.Id], await Ids(client, "employees"));
        Assert.Equal([project.Id], await Ids(client, "projects"));
        Assert.Equal(new[] { assignedTask.Id, personalTask.Id }.Order(), (await Ids(client, "tasks")).Order());
        Assert.Equal([project.BuildingId], await Ids(client, "buildings"));
        using (var response = await client.GetAsync($"/api/v1/projects/{hidden.Id}")) Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using (var response = await client.GetAsync("/api/v1/search?q=Marker&kind=tasks"))
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal(2, json.RootElement.GetProperty("total").GetInt32());
        }
        await Change(worker, assignment, x => x.Cancelled = true);
        Assert.Empty(await Ids(client, "projects")); Assert.Equal([personalTask.Id], await Ids(client, "tasks"));
        using (var response = await client.GetAsync($"/api/v1/projects/{project.Id}")) Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await Change(worker, own, x => x.Active = false);
        Assert.Empty(await Ids(client, "tasks")); Assert.Empty(await Ids(client, "employees"));
    }

    [PostgresApiFact]
    public async Task Trainer_sees_only_own_and_directly_mentored_staff_and_participating_training_records()
    {
        var tenant = await fixture.CreateTenant(); var trainer = await fixture.CreateUser(tenant.Id, BusinessRole.Trainer);
        var own = await fixture.Save(new Employee { TenantId = tenant.Id, UserId = trainer.UserId, Name = "Own trainer" }, trainer.UserId);
        var apprentice = await fixture.Save(new Employee { TenantId = tenant.Id, Name = "Direct apprentice", IsApprentice = true, MentorEmployeeId = own.Id }, trainer.UserId);
        var other = await fixture.Save(new Employee { TenantId = tenant.Id, Name = "Other mentor" }, trainer.UserId);
        var stranger = await fixture.Save(new Employee { TenantId = tenant.Id, Name = "Other apprentice", IsApprentice = true, MentorEmployeeId = other.Id }, trainer.UserId);
        var record = await fixture.Save(new TrainingRecord { TenantId = tenant.Id, ApprenticeEmployeeId = apprentice.Id, MentorEmployeeId = own.Id, WeekStart = new(2026,10,19) }, trainer.UserId);
        await fixture.Save(new TrainingRecord { TenantId = tenant.Id, ApprenticeEmployeeId = stranger.Id, MentorEmployeeId = other.Id, WeekStart = new(2026,10,19) }, trainer.UserId);
        foreach (var kind in new[] { "employees", "training-records" }) await Seed(tenant.Id, kind, 10_001, "{}");
        var foreign = await fixture.CreateTenant();
        await fixture.Save(new Employee { TenantId = foreign.Id, UserId = trainer.UserId, Name = "Foreign same account" }, trainer.UserId);
        var client = await fixture.Login(trainer);
        Assert.Equal(new[] { own.Id, apprentice.Id }.Order(), (await Ids(client, "employees")).Order());
        Assert.Equal([record.Id], await Ids(client, "training-records"));
        await Change(trainer, apprentice, x => x.MentorEmployeeId = other.Id);
        Assert.Equal([own.Id], await Ids(client, "employees"));
        // Historical reports retain their participants, independent of a later staff reassignment.
        Assert.Equal([record.Id], await Ids(client, "training-records"));
    }

    [PostgresApiFact]
    public async Task Combined_record_budget_rejects_the_next_source_before_deserializing_it_and_resets_per_scope()
    {
        var tenant = await fixture.CreateTenant();
        await Seed(tenant.Id, "customers", 6_000, AppJson.Serialize(new Customer { TenantId = tenant.Id, Name = "Bounded source" }));
        await Seed(tenant.Id, "employees", 6_000, "{}");
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(6_000, (await EntitySources.Read<Customer>(db, tenant.Id)).Count);
            var error = await Assert.ThrowsAsync<DomainException>(() => EntitySources.Read<Employee>(db, tenant.Id));
            Assert.Equal("source_request_budget", error.Code);
            Assert.Equal(4_000, db.SourceBudget.RemainingRecords);
        });
        await fixture.InScope(async services => Assert.Equal(6_000,
            (await EntitySources.Read<Customer>(services.GetRequiredService<FirstBlickDbContext>(), tenant.Id)).Count));
    }

    [PostgresApiFact]
    public async Task Combined_byte_budget_is_checked_before_invalid_second_source_bodies_are_returned()
    {
        var tenant = await fixture.CreateTenant();
        await Seed(tenant.Id, "customers", 4, AppJson.Serialize(new Customer { TenantId = tenant.Id, Name = "Byte budget", Notes = new string('x', 1_600_000) }));
        await Seed(tenant.Id, "employees", 4, JsonSerializer.Serialize(new { unexpected = new string('x', 1_600_000) }));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(4, (await EntitySources.Read<Customer>(db, tenant.Id)).Count);
            Assert.Equal("source_request_budget", (await Assert.ThrowsAsync<DomainException>(() => EntitySources.Read<Employee>(db, tenant.Id))).Code);
        });
    }

    [PostgresApiFact]
    public async Task Selected_metadata_corruption_and_oversized_visible_sets_fail_closed()
    {
        var tenant = await fixture.CreateTenant(); var worker = await fixture.CreateUser(tenant.Id, BusinessRole.Worker);
        var own = await fixture.Save(new Employee { TenantId = tenant.Id, UserId = worker.UserId, Name = "Own worker" }, worker.UserId);
        var client = await fixture.Login(worker);
        await Seed(tenant.Id, "time-entries", 10_001, JsonSerializer.Serialize(new { employeeId = own.Id }));
        using (var response = await client.GetAsync("/api/v1/time-entries"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal("source_budget", problem.RootElement.GetProperty("code").GetString());
        }
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var body = AppJson.Serialize(new Employee { Id = own.Id, TenantId = Guid.NewGuid(), UserId = worker.UserId, Name = "Wrong tenant canary" });
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Entities\" SET \"Data\"={body}::jsonb WHERE \"TenantId\"={tenant.Id} AND \"Kind\"='employees' AND \"Id\"={own.Id}");
        });
        using var corrupt = await client.GetAsync("/api/v1/projects");
        Assert.Equal(HttpStatusCode.BadRequest, corrupt.StatusCode);
        using var error = JsonDocument.Parse(await corrupt.Content.ReadAsStringAsync()); Assert.Equal("source_metadata", error.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void Source_budget_includes_exact_limits_and_refuses_overflow_without_changing_remaining_work()
    {
        var budget = new EntitySourceBudget(); budget.Consume(10_000, 12 * 1024 * 1024);
        Assert.Equal(0, budget.RemainingRecords); Assert.Equal(0, budget.RemainingBytes); budget.Consume(0, 0);
        Assert.Equal("source_request_budget", Assert.Throws<DomainException>(() => budget.Consume(1, 0)).Code);
        Assert.Equal("source_request_budget", Assert.Throws<DomainException>(() => budget.Consume(0, 1)).Code);
        Assert.Equal(0, budget.RemainingRecords); Assert.Equal(0, budget.RemainingBytes);
    }

    private async Task<Project> Project(ApiTestUser user)
    {
        var customer = await fixture.Save(new Customer { TenantId = user.TenantId, Name = "Policy customer" }, user.UserId);
        var building = await fixture.Save(new Building { TenantId = user.TenantId, CustomerId = customer.Id, Name = "Policy building", Address = "Synthetic address" }, user.UserId);
        return await fixture.Save(new Project { TenantId = user.TenantId, CustomerId = customer.Id, BuildingId = building.Id, Name = "Policy project" }, user.UserId);
    }
    private Task Change<T>(ApiTestUser user, T entity, Action<T> change) where T : TenantEntity => fixture.InScope(async services =>
    {
        var scope = services.GetRequiredService<TenantScope>(); scope.TenantId = user.TenantId; scope.UserId = user.UserId;
        change(entity); await services.GetRequiredService<EntityStore>().Save(entity, "test.change", false);
        await services.GetRequiredService<FirstBlickDbContext>().SaveChangesAsync();
    });
    private static async Task<Guid[]> Ids(HttpClient client, string kind)
    {
        using var response = await client.GetAsync("/api/v1/" + kind); var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body); using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();
    }
    private Task Seed(Guid tenantId, string kind, int count, string body) => fixture.InScope(async services =>
    {
        var db = services.GetRequiredService<FirstBlickDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Entities" ("TenantId","Kind","Id","Revision","Data","SearchText","Archived","CreatedAt","UpdatedAt")
            SELECT {tenantId},{kind},id,1,jsonb_set({body}::jsonb,ARRAY['id'],to_jsonb(id::text)), '',false,now(),now()
            FROM (SELECT gen_random_uuid() AS id FROM generate_series(1,{count})) s
            """);
    });
}
