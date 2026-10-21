using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FirstBlick.Api.Tests;

public sealed class OperationsApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Snapshot_counts_only_the_current_business_and_distinguishes_delayed_work_from_scheduled_work()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        var foreign = await fixture.CreateTenant(); var other = await fixture.CreateUser(foreign.Id);
        var now = DateTimeOffset.UtcNow;
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            OutboxJob Job(Guid tenantId, Guid actorId, JobState state, DateTimeOffset available, DateTimeOffset? lease = null)
                => new() { TenantId = tenantId, ActorId = actorId, State = state, AvailableAt = available, LeaseUntil = lease,
                    Type = "synthetic-private-type", Payload = "{\"recipient\":\"private@example.test\"}", LastError = "private-job-error", ProviderReference = "private-provider-reference" };
            db.Jobs.AddRange(Job(tenant.Id, user.UserId, JobState.Pending, now.AddMinutes(-6)),
                Job(tenant.Id, user.UserId, JobState.Pending, now.AddHours(1)),
                Job(tenant.Id, user.UserId, JobState.Leased, now.AddMinutes(-2)),
                Job(tenant.Id, user.UserId, JobState.Leased, now, now.AddMinutes(10)),
                Job(tenant.Id, user.UserId, JobState.Failed, now), Job(tenant.Id, user.UserId, JobState.Uncertain, now),
                Job(tenant.Id, user.UserId, JobState.Completed, now));
            for (var i = 0; i < 4; i++) db.Jobs.Add(Job(foreign.Id, other.UserId, JobState.Failed, now));
            DocumentObject Document(Guid tenantId, string state, DateTimeOffset created) => new()
                { TenantId = tenantId, UploadedBy = user.UserId, Name = "Private invoice.pdf", ContentType = "application/pdf", Size = 123,
                    Sha256 = new string('a', 64), ObjectKey = "private-object-" + Guid.NewGuid(), VersionId = "private-version", State = state, CreatedAt = created };
            db.Documents.AddRange(Document(tenant.Id, "Ready", now), Document(tenant.Id, "Staged", now.AddMinutes(-20)),
                Document(tenant.Id, "Staged", now), Document(tenant.Id, "Archived", now), Document(foreign.Id, "Staged", now.AddDays(-1)));
            await db.SaveChangesAsync();
        });
        using var response = await (await fixture.Login(user)).GetAsync("/api/v1/operations/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore); Assert.True(response.Headers.CacheControl?.Private);
        var text = await response.Content.ReadAsStringAsync(); var result = JsonDocument.Parse(text).RootElement;
        Assert.Equal("firstblick.operations/1.0.0", result.GetProperty("schema").GetString());
        var queue = result.GetProperty("queue");
        Assert.Equal(2, queue.GetProperty("pending").GetInt64()); Assert.Equal(1, queue.GetProperty("due").GetInt64());
        Assert.Equal(2, queue.GetProperty("leased").GetInt64()); Assert.Equal(1, queue.GetProperty("expiredLeases").GetInt64());
        Assert.Equal(1, queue.GetProperty("failed").GetInt64()); Assert.Equal(1, queue.GetProperty("uncertain").GetInt64());
        Assert.InRange(queue.GetProperty("oldestDueAgeSeconds").GetDouble(), 360, 480);
        var documents = result.GetProperty("documents");
        Assert.Equal(1, documents.GetProperty("ready").GetInt64()); Assert.Equal(2, documents.GetProperty("staged").GetInt64()); Assert.Equal(1, documents.GetProperty("stalled").GetInt64());
        Assert.Equal(new[] { "expired_leases", "external_outcome_uncertain", "failed_jobs", "queue_delay", "stalled_uploads" },
            result.GetProperty("signals").EnumerateArray().Select(x => x.GetProperty("code").GetString()).Order());
        Assert.DoesNotContain("private", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(user.UserId.ToString(), text); Assert.DoesNotContain(foreign.Id.ToString(), text);
    }

    [PostgresApiFact]
    public async Task Snapshot_rejects_other_roles_unrelated_businesses_and_client_scope_selectors()
    {
        var tenant = await fixture.CreateTenant();
        using var anonymous = fixture.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/operations/snapshot")).StatusCode);
        foreach (var role in Enum.GetValues<BusinessRole>().Where(x => x != BusinessRole.ManagingDirector))
        {
            var client = await fixture.Login(await fixture.CreateUser(tenant.Id, role));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/operations/snapshot")).StatusCode);
        }
        var director = await fixture.Login(await fixture.CreateUser(tenant.Id));
        var bad = await director.GetAsync("/api/v1/operations/snapshot?tenantId=" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        director.DefaultRequestHeaders.Remove("X-Tenant-Id"); director.DefaultRequestHeaders.Add("X-Tenant-Id", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await director.GetAsync("/api/v1/operations/snapshot")).StatusCode);
    }

    [PostgresApiFact]
    public async Task Snapshot_returns_explicit_empty_counts_without_claiming_worker_or_storage_reachability()
    {
        var tenant = await fixture.CreateTenant(); var client = await fixture.Login(await fixture.CreateUser(tenant.Id));
        var response = await client.GetAsync("/api/v1/operations/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.All(result.GetProperty("queue").EnumerateObject(), x => Assert.Equal(0, x.Value.GetDouble()));
        Assert.All(result.GetProperty("documents").EnumerateObject(), x => Assert.Equal(0, x.Value.GetInt64()));
        Assert.Empty(result.GetProperty("signals").EnumerateArray());
        Assert.False(result.TryGetProperty("healthy", out _)); Assert.False(result.TryGetProperty("workerOnline", out _));
    }

    [PostgresApiFact]
    public async Task Snapshot_rechecks_director_role_after_an_observed_database_lock_wait()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(user);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@tenant, 0))", connection, transaction))
        { command.Parameters.AddWithValue("tenant", tenant.Id.ToString()); await command.ExecuteNonQueryAsync(); }
        var response = client.GetAsync("/api/v1/operations/snapshot");
        await WaitForAdvisoryWait(fixture.ConnectionString);
        await using (var command = new NpgsqlCommand("UPDATE \"Memberships\" SET \"Role\"='Office', \"Revision\"=\"Revision\"+1 WHERE \"TenantId\"=@tenant AND \"UserId\"=@user", connection, transaction))
        { command.Parameters.AddWithValue("tenant", tenant.Id); command.Parameters.AddWithValue("user", user.UserId); await command.ExecuteNonQueryAsync(); }
        await transaction.CommitAsync();
        var denied = await response.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("oldestDueAgeSeconds", await denied.Content.ReadAsStringAsync());
    }

    private static async Task WaitForAdvisoryWait(string connectionString)
    {
        await using var observer = new NpgsqlConnection(connectionString); await observer.OpenAsync();
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            await using var query = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory')", observer);
            if (await query.ExecuteScalarAsync() is true) return;
            await Task.Delay(20);
        }
        Assert.Fail("The snapshot request never reached the database advisory-lock wait.");
    }
}
