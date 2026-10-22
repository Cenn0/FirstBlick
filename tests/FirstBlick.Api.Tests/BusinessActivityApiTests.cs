using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TaskStatus = FirstBlick.Core.Business.TaskStatus;

namespace FirstBlick.Api.Tests;

public sealed class BusinessActivityApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private const string Path = "/api/v1/reports/business-activity?from=2026-10-01&to=2026-10-31";

    [PostgresApiFact]
    public async Task Supported_Windows_business_zones_are_normalized_for_Postgres_and_browser_reports()
    {
        var seed = await Seed();
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var tenant = await db.Tenants.SingleAsync(x => x.Id == seed.User.TenantId);
            tenant.TimeZone = "W. Europe Standard Time"; await db.SaveChangesAsync();
        });
        var response = await (await fixture.Login(seed.User)).GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = AppJson.Read<ActivityReport>(await response.Content.ReadAsStringAsync());
        Assert.Equal("Europe/Berlin", report.TimeZone);
        Assert.Equal(new ActivityCounts(1, 0, 1), report.Current.Totals);
    }

    [PostgresApiFact]
    public async Task Report_uses_business_timezone_equal_periods_explicit_project_filter_and_current_due_dates()
    {
        var seed = await Seed(); var foreign = await Seed(); var client = await fixture.Login(seed.User);
        var response = await client.GetAsync(Path); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore); Assert.True(response.Headers.CacheControl?.Private);
        var report = AppJson.Read<ActivityReport>(await response.Content.ReadAsStringAsync());
        Assert.Equal(seed.User.TenantId, report.TenantId); Assert.Equal("Europe/Berlin", report.TimeZone);
        Assert.Equal(new ActivityCounts(1, 0, 1), report.Current.Totals); Assert.Equal(new ActivityCounts(0, 1, 0), report.Previous.Totals);
        Assert.Equal(new DateOnly(2026, 10, 1), report.Sources.Single(x => x.Kind == "projects").CreatedOn);
        Assert.Equal("Done", report.Sources.Single(x => x.Kind == "tasks").CurrentStatus);
        Assert.Equal(2, report.Sources.Count); Assert.DoesNotContain(report.Sources, x => x.ProjectId == foreign.Project.Id);
        var selected = await client.GetAsync(Path + "&projectId=" + seed.Project.Id);
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        Assert.NotEqual(report.Digest, AppJson.Read<ActivityReport>(await selected.Content.ReadAsStringAsync()).Digest);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Path + "&projectId=" + foreign.Project.Id)).StatusCode);
    }

    [PostgresApiFact]
    public async Task Exports_bind_filters_revision_bytes_and_tenant_and_reject_stale_or_missing_review()
    {
        var seed = await Seed(); var client = await fixture.Login(seed.User);
        var report = AppJson.Read<ActivityReport>(await (await client.GetAsync(Path)).Content.ReadAsStringAsync());
        var export = Path.Replace("business-activity?", "business-activity/export?") + "&expectedDigest=" + report.Digest;
        foreach (var format in new[] { "csv", "json" })
        {
            var response = await client.GetAsync(export + "&format=" + format); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), response.Headers.GetValues("X-Content-Sha256").Single());
            Assert.Equal(report.Digest, response.Headers.GetValues("X-Report-Digest").Single());
            Assert.Equal(seed.User.TenantId.ToString(), response.Headers.GetValues("X-Report-Tenant").Single());
            if (format == "csv") Assert.Contains("\"'=SUM(1;2)\"", await response.Content.ReadAsStringAsync());
            else Assert.Equal(report.Digest, AppJson.Read<ActivityReport>(await response.Content.ReadAsStringAsync()).Digest);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path.Replace("business-activity?", "business-activity/export?") + "&format=csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(export + "&format=json&includeArchived=true")).StatusCode);
        await fixture.InScope(async services =>
        {
            services.GetRequiredService<TenantScope>().TenantId = seed.User.TenantId;
            services.GetRequiredService<TenantScope>().UserId = seed.User.UserId;
            var store = services.GetRequiredService<EntityStore>(); var task = await store.Get<TaskItem>(seed.Task.Id); task.DueDate = new(2026, 10, 16);
            await store.Save(task, "test.update", false); await services.GetRequiredService<FirstBlickDbContext>().SaveChangesAsync();
        });
        var stale = await client.GetAsync(export + "&format=json"); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("activity_report_changed", await stale.Content.ReadAsStringAsync());
    }

    [PostgresApiFact]
    public async Task Archived_sources_are_opt_in_and_no_matching_sources_produce_zero_not_unknown_totals()
    {
        var seed = await Seed(); var client = await fixture.Login(seed.User);
        await fixture.InScope(async services =>
        {
            var scope = services.GetRequiredService<TenantScope>(); scope.TenantId = seed.User.TenantId; scope.UserId = seed.User.UserId;
            var store = services.GetRequiredService<EntityStore>(); var task = await store.Get<TaskItem>(seed.Task.Id); task.Archived = true;
            await store.Save(task, "test.archive", false); await services.GetRequiredService<FirstBlickDbContext>().SaveChangesAsync();
        });
        var report = AppJson.Read<ActivityReport>(await (await client.GetAsync(Path)).Content.ReadAsStringAsync());
        Assert.Equal(0, report.Current.Totals.TasksDue); Assert.Single(report.Sources);
        var archived = AppJson.Read<ActivityReport>(await (await client.GetAsync(Path + "&includeArchived=true")).Content.ReadAsStringAsync());
        Assert.Equal(1, archived.Current.Totals.TasksDue); Assert.Contains(archived.Sources, x => x.Archived);
        var empty = AppJson.Read<ActivityReport>(await (await client.GetAsync(Path.Replace("2026", "2022"))).Content.ReadAsStringAsync());
        Assert.Empty(empty.Sources); Assert.Equal(new ActivityCounts(0, 0, 0), empty.Current.Totals);
    }

    [PostgresApiFact]
    public async Task Reports_reject_personal_roles_unknown_filters_duplicates_and_invalid_dates()
    {
        var seed = await Seed();
        foreach (var role in new[] { BusinessRole.Worker, BusinessRole.Trainer, BusinessRole.Partner })
            Assert.Equal(HttpStatusCode.Forbidden, (await (await fixture.Login(await fixture.CreateUser(seed.User.TenantId, role))).GetAsync(Path)).StatusCode);
        foreach (var role in new[] { BusinessRole.Office, BusinessRole.Accounting, BusinessRole.SiteManager })
            Assert.Equal(HttpStatusCode.OK, (await (await fixture.Login(await fixture.CreateUser(seed.User.TenantId, role))).GetAsync(Path)).StatusCode);
        var client = await fixture.Login(seed.User);
        foreach (var query in new[] { "&tenantId=" + Guid.NewGuid(), "&from=2026-10-01", "&includeArchived=yes", "&projectId=not-an-id" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path + query)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path.Replace("2026-10-01", "2025-01-01"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path.Replace("2026-10-31", "2026-02-30"))).StatusCode);
    }

    [PostgresApiFact]
    public async Task Dataset_budget_fails_closed_instead_of_returning_partial_statistics()
    {
        var seed = await Seed();
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            for (var i = 0; i < 5000; i++)
            {
                var task = new TaskItem { TenantId = seed.User.TenantId, ProjectId = seed.Project.Id, Title = "Budget task", CreatedAt = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero) };
                db.Entities.Add(new StoredEntity { TenantId = task.TenantId, Id = task.Id, Kind = "tasks", Revision = 1,
                    CreatedAt = task.CreatedAt, UpdatedAt = task.UpdatedAt, Data = AppJson.Serialize(task), SearchText = "budget" });
            }
            await db.SaveChangesAsync();
        });
        var response = await (await fixture.Login(seed.User)).GetAsync(Path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("activity_budget", await response.Content.ReadAsStringAsync());
    }

    [PostgresApiFact]
    public async Task Role_is_rechecked_after_an_observed_tenant_lock_wait()
    {
        var seed = await Seed(); var client = await fixture.Login(seed.User);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@tenant,0))", connection, transaction))
        { command.Parameters.AddWithValue("tenant", seed.User.TenantId.ToString()); await command.ExecuteNonQueryAsync(); }
        var pending = client.GetAsync(Path);
        await using var observer = new NpgsqlConnection(fixture.ConnectionString); await observer.OpenAsync();
        var timeout = Stopwatch.StartNew(); var waited = false;
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            await using var query = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory')", observer);
            if (await query.ExecuteScalarAsync() is true) { waited = true; break; }
            await Task.Delay(20);
        }
        Assert.True(waited, "Report did not reach the advisory-lock wait.");
        await using (var command = new NpgsqlCommand("UPDATE \"Memberships\" SET \"Role\"='Worker' WHERE \"TenantId\"=@tenant AND \"UserId\"=@user", connection, transaction))
        { command.Parameters.AddWithValue("tenant", seed.User.TenantId); command.Parameters.AddWithValue("user", seed.User.UserId); await command.ExecuteNonQueryAsync(); }
        await transaction.CommitAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
    }

    private async Task<(ApiTestUser User, Project Project, TaskItem Task)> Seed()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        await fixture.InScope(async services => { var db = services.GetRequiredService<FirstBlickDbContext>(); var value = await db.Tenants.SingleAsync(x => x.Id == tenant.Id); value.TimeZone = "Europe/Berlin"; await db.SaveChangesAsync(); });
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Report customer" }, user.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id, Name = "House", Address = "Test address" }, user.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id, BuildingId = building.Id, Name = "=SUM(1;2)",
            CreatedAt = new(2026, 9, 30, 22, 30, 0, TimeSpan.Zero), Status = ProjectStatus.Active }, user.UserId);
        var task = await fixture.Save(new TaskItem { TenantId = tenant.Id, ProjectId = project.Id, Title = "Retained finished task", Status = TaskStatus.Done,
            CreatedAt = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero), DueDate = new(2026, 10, 15) }, user.UserId);
        return (user, project, task);
    }
}
