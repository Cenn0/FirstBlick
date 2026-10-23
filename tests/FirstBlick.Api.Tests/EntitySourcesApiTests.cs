using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FirstBlick.Api.Tests;

public sealed class EntitySourcesApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Exact_record_limit_is_complete_and_an_extra_record_fails_before_deserialization()
    {
        var tenant = await fixture.CreateTenant();
        await Seed(tenant.Id, EntitySources.RecordLimit, AppJson.Serialize(new Customer { TenantId = tenant.Id, Name = "Synthetic limit customer" }));
        var rows = await Read<Customer>(tenant.Id);
        Assert.Equal(EntitySources.RecordLimit, rows.Count);
        Assert.Equal(rows.Count, rows.Select(x => x.Id).Distinct().Count());
        await Seed(tenant.Id, 1, "{}");
        Assert.Equal("source_budget", (await Assert.ThrowsAsync<DomainException>(() => Read<Customer>(tenant.Id))).Code);
    }

    [PostgresApiFact]
    public async Task Byte_budget_rejects_a_small_record_count_before_reading_invalid_bodies()
    {
        var tenant = await fixture.CreateTenant();
        // Each JSONB value is below the database's 2 MiB limit, the set exceeds 12 MiB.
        // Unexpected members would fail deserialization if the payload were incorrectly materialized.
        await Seed(tenant.Id, 7, JsonSerializer.Serialize(new { unexpected = new string('x', 1_900_000) }));
        Assert.Equal("source_budget", (await Assert.ThrowsAsync<DomainException>(() => Read<Customer>(tenant.Id))).Code);
    }

    [PostgresApiFact]
    public async Task Foreign_and_archived_sources_do_not_consume_the_active_tenant_budget()
    {
        var own = await fixture.CreateTenant(); var other = await fixture.CreateTenant();
        await Seed(other.Id, EntitySources.RecordLimit + 1, "{}");
        await Seed(own.Id, EntitySources.RecordLimit + 1, "{}", archived: true);
        await Seed(own.Id, 1, AppJson.Serialize(new Customer { TenantId = own.Id, Name = "Visible synthetic customer" }));
        Assert.Equal("Visible synthetic customer", Assert.Single(await Read<Customer>(own.Id)).Name);
    }

    [PostgresApiFact]
    public async Task Body_metadata_disagreement_never_enters_a_policy_source_set()
    {
        var tenant = await fixture.CreateTenant();
        await Seed(tenant.Id, 1, AppJson.Serialize(new Customer { TenantId = Guid.NewGuid(), Name = "Wrong tenant canary" }));
        Assert.Equal("source_metadata", (await Assert.ThrowsAsync<DomainException>(() => Read<Customer>(tenant.Id))).Code);
    }

    [PostgresApiFact]
    public async Task Worker_without_an_own_staff_record_ignores_unrelated_oversized_dependencies()
    {
        var tenant = await fixture.CreateTenant();
        var worker = await fixture.CreateUser(tenant.Id, BusinessRole.Worker);
        await Seed(tenant.Id, EntitySources.RecordLimit + 1, "{}", kind: "employees");
        var client = await fixture.Login(worker);
        using var response = await client.GetAsync("/api/v1/projects");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, result.RootElement.GetProperty("total").GetInt32());
        Assert.Empty(result.RootElement.GetProperty("items").EnumerateArray());
    }

    [PostgresApiFact]
    public async Task Import_matches_only_requested_account_keys_even_when_the_rest_of_the_bank_history_is_oversized()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        await Seed(tenant.Id, EntitySources.RecordLimit + 1, "{}", kind: "bank-transactions");
        var client = await fixture.Login(user);
        var input = new BankImportRequest("synthetic-account", [new("external-1", new(2026, 10, 23), 12.34m)]);
        using var first = await PostgresApiFixture.Send(client, HttpMethod.Post, "/api/v1/bank-transactions/import", input);
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        using var second = await PostgresApiFixture.Send(client, HttpMethod.Post, "/api/v1/bank-transactions/import", input);
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
        using var reused = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(0, reused.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(1, reused.RootElement.GetProperty("reused").GetInt32());
        using var conflict = await PostgresApiFixture.Send(client, HttpMethod.Post, "/api/v1/bank-transactions/import",
            input with { Transactions = [input.Transactions[0] with { Amount = 99m }] });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    private Task<List<T>> Read<T>(Guid tenantId) where T : TenantEntity => fixture.InScope(async services =>
    {
        services.GetRequiredService<TenantScope>().TenantId = tenantId;
        return await services.GetRequiredService<EntityStore>().All<T>();
    });

    [PostgresApiFact]
    public async Task Related_sources_filter_before_budgeting_union_without_duplicates_and_check_retained_links()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Related customer" }, user.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id, Name = "Related building", Address = "Synthetic address" }, user.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id, BuildingId = building.Id, Name = "Related project" }, user.UserId);
        await Seed(tenant.Id, EntitySources.RecordLimit + 1, "{}", kind: "projects");
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var rows = await EntitySources.Read<Project>(db, tenant.Id, targets:
                [BusinessRegistry.Reference<Customer>(customer.Id), BusinessRegistry.Reference<Building>(building.Id)]);
            Assert.Equal(project.Id, Assert.Single(rows).Id);
            var empty = await EntitySources.Read<Project>(db, tenant.Id, targets: []);
            Assert.Empty(empty);
            project.CustomerId = Guid.NewGuid();
            var body = AppJson.Serialize(project);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Entities\" SET \"Data\"={body}::jsonb WHERE \"TenantId\"={tenant.Id} AND \"Kind\"='projects' AND \"Id\"={project.Id}");
            var error = await Assert.ThrowsAsync<DomainException>(() => EntitySources.Read<Project>(db, tenant.Id,
                targets: [BusinessRegistry.Reference<Customer>(customer.Id)]));
            Assert.Equal("source_reference", error.Code);
        });
    }

    private Task Seed(Guid tenantId, int count, string body, bool archived = false, string kind = "customers") =>
        fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Entities" ("TenantId","Kind","Id","Revision","Data","SearchText","Archived","CreatedAt","UpdatedAt")
                SELECT {tenantId},{kind},id,1,jsonb_set({body}::jsonb,ARRAY['id'],to_jsonb(id::text)),
                       '',{archived},now(),now()
                FROM (SELECT gen_random_uuid() AS id FROM generate_series(1,{count})) s
                """);
        });
}
