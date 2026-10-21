using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class RecoveryAccessTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private const string ClaimedPassword = "Claimed-Synthetic-Password-2026!";

    [PostgresApiFact]
    public async Task A_pending_import_requires_a_fresh_invitation_and_creates_one_personal_credential_with_the_new_role()
    {
        var tenant = await fixture.CreateTenant(); var director = await fixture.CreateUser(tenant.Id);
        var imported = await Imported(tenant.Id); var token = await Invite(await fixture.Login(director), imported.Email);
        await Error(Send(await fixture.Login(director), HttpMethod.Put, $"/api/v1/members/{imported.UserId}", new { role = "ManagingDirector", active = true, revision = 1 }, 1),
            HttpStatusCode.BadRequest, "migration_activation_required");
        var anonymous = fixture.Client(); await RefreshCsrf(anonymous);
        await Error(Send(anonymous, HttpMethod.Post, "/api/v1/auth/login", new { imported.Email, password = ClaimedPassword }), HttpStatusCode.Unauthorized);
        await Ok(Send(anonymous, HttpMethod.Post, "/api/v1/auth/activate", new { imported.Email, token, password = ClaimedPassword, displayName = "Claimed personal account" }));
        var loggedIn = await fixture.Login(imported with { Password = ClaimedPassword });
        var me = await Json(await loggedIn.GetAsync("/api/v1/auth/me"));
        Assert.Equal(imported.UserId, me.GetProperty("user").GetProperty("id").GetGuid());
        Assert.Equal("Office", me.GetProperty("tenants")[0].GetProperty("role").GetString());
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var user = await db.Users.SingleAsync(x => x.Id == imported.UserId); Assert.True(user.EmailConfirmed); Assert.NotNull(user.PasswordHash);
            var marker = AppJson.Read<MigrationAccountMarker>((await db.Receipts.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenant.Id && x.Key == MigrationAccountMarker.Key(imported.UserId))).Response);
            Assert.Equal("Claimed", marker.State); Assert.Equal(BusinessRole.ManagingDirector, marker.SourceRole);
            Assert.Equal(1, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant.Id && x.Action == "migration.account_claimed"));
        });
        await Error(Send(anonymous, HttpMethod.Post, "/api/v1/auth/activate", new { imported.Email, token, password = ClaimedPassword, displayName = "Replay" }), HttpStatusCode.BadRequest, "invalid_invitation");
    }

    [PostgresApiFact]
    public async Task Missing_or_mismatched_import_evidence_cannot_make_an_existing_identity_claimable()
    {
        foreach (var fault in new[] { "missing", "email", "digest", "claimed" })
        {
            var tenant = await fixture.CreateTenant(); var director = await fixture.CreateUser(tenant.Id); var imported = await Imported(tenant.Id);
            var token = await Invite(await fixture.Login(director), imported.Email);
            await fixture.InScope(async services =>
            {
                var db = services.GetRequiredService<FirstBlickDbContext>();
                var receipt = await db.Receipts.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenant.Id && x.Key == MigrationAccountMarker.Key(imported.UserId));
                var marker = AppJson.Read<MigrationAccountMarker>(receipt.Response);
                if (fault == "missing") db.Receipts.Remove(receipt);
                if (fault == "email") receipt.Response = AppJson.Serialize(marker with { Email = "another@example.test" });
                if (fault == "digest") receipt.RequestDigest = "unverified";
                if (fault == "claimed") receipt.Response = AppJson.Serialize(marker with { State = "Claimed" });
                await db.SaveChangesAsync();
            });
            var anonymous = fixture.Client(); await RefreshCsrf(anonymous);
            await Error(Send(anonymous, HttpMethod.Post, "/api/v1/auth/activate", new { imported.Email, token, password = ClaimedPassword, displayName = "No takeover" }),
                HttpStatusCode.BadRequest, fault is "email" or "digest" ? "invalid_migration_claim" : "existing_account_login_required");
            await fixture.InScope(async services => Assert.Null((await services.GetRequiredService<FirstBlickDbContext>().Users.SingleAsync(x => x.Id == imported.UserId)).PasswordHash));
        }
    }

    [PostgresApiFact]
    public async Task A_person_with_an_active_account_elsewhere_must_login_and_cannot_reset_credentials_through_migration()
    {
        var original = await fixture.CreateTenant(); var target = await fixture.CreateTenant();
        var existing = await fixture.CreateUser(original.Id, BusinessRole.Worker); var director = await fixture.CreateUser(target.Id);
        await PendingMembership(target.Id, existing.UserId, existing.Email);
        var token = await Invite(await fixture.Login(director), existing.Email); var anonymous = fixture.Client(); await RefreshCsrf(anonymous);
        await Error(Send(anonymous, HttpMethod.Post, "/api/v1/auth/activate", new { existing.Email, token, password = ClaimedPassword, displayName = "No takeover" }), HttpStatusCode.BadRequest, "existing_account_login_required");
        var owner = await fixture.Login(existing);
        await Error(Send(owner, HttpMethod.Post, "/api/v1/auth/activate", new { existing.Email, token, password = ClaimedPassword, displayName = "Owner" }), HttpStatusCode.BadRequest, "invitation_not_recovery");
        await Ok(Send(owner, HttpMethod.Post, "/api/v1/auth/activate", new { existing.Email, token, displayName = "Owner" }));
        _ = await fixture.Login(existing);
        await fixture.InScope(async services => Assert.Equal(2, await services.GetRequiredService<FirstBlickDbContext>().Memberships.CountAsync(x => x.UserId == existing.UserId && x.Active)));
    }

    [PostgresApiFact]
    public async Task Concurrent_first_claims_across_businesses_create_exactly_one_credential_then_require_owner_login()
    {
        var a = await fixture.CreateTenant(); var b = await fixture.CreateTenant(); var imported = await Imported(a.Id);
        await PendingMembership(b.Id, imported.UserId, imported.Email);
        var tokenA = await Invite(await fixture.Login(await fixture.CreateUser(a.Id)), imported.Email);
        var tokenB = await Invite(await fixture.Login(await fixture.CreateUser(b.Id)), imported.Email);
        var alice = fixture.Client(); var bob = fixture.Client(); await RefreshCsrf(alice); await RefreshCsrf(bob);
        var tokens = new[] { tokenA, tokenB }; var tenants = new[] { a.Id, b.Id };
        var responses = await Task.WhenAll(Send(alice, HttpMethod.Post, "/api/v1/auth/activate", new { imported.Email, token = tokenA, password = ClaimedPassword, displayName = "Person" }),
            Send(bob, HttpMethod.Post, "/api/v1/auth/activate", new { imported.Email, token = tokenB, password = ClaimedPassword, displayName = "Person" }));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        var rejected = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal("existing_account_login_required", (await Json(rejected)).GetProperty("code").GetString());
        var winner = Array.FindIndex(responses, x => x.StatusCode == HttpStatusCode.OK);
        var owner = await fixture.Login(imported with { TenantId = tenants[winner], Password = ClaimedPassword });
        await Ok(Send(owner, HttpMethod.Post, "/api/v1/auth/activate", new { imported.Email, token = tokens[1 - winner], displayName = "Person" }));
        await fixture.InScope(async services => Assert.Equal(2, await services.GetRequiredService<FirstBlickDbContext>().Memberships.CountAsync(x => x.UserId == imported.UserId && x.Active)));
    }

    [PostgresApiFact]
    public async Task A_recovery_hold_blocks_reads_commands_receipt_replay_activation_and_recovery_until_evidenced_release()
    {
        var tenant = await fixture.CreateTenant(); var director = await fixture.CreateUser(tenant.Id); var admin = await fixture.Login(director);
        var imported = await Imported(tenant.Id); var token = await Invite(admin, imported.Email);
        var key = Guid.NewGuid().ToString("N"); var body = new { name = "Before restore hold" };
        await Ok(Send(admin, HttpMethod.Post, "/api/v1/customers", body, key: key));
        await Hold(tenant.Id, AppJson.Serialize(Marker()));
        await Error(admin.GetAsync("/api/v1/customers"), (HttpStatusCode)423, "recovery_hold");
        await Error(Send(admin, HttpMethod.Post, "/api/v1/customers", body, key: key), (HttpStatusCode)423, "recovery_hold");
        var me = await Json(await admin.GetAsync("/api/v1/auth/me")); Assert.True(me.GetProperty("tenants")[0].GetProperty("recoveryHold").GetBoolean());
        Assert.Empty(me.GetProperty("tenants")[0].GetProperty("capabilities").EnumerateArray());
        var anonymous = fixture.Client(); await RefreshCsrf(anonymous);
        await Error(Send(anonymous, HttpMethod.Post, "/api/v1/auth/activate", new { imported.Email, token, password = ClaimedPassword, displayName = "Held" }), (HttpStatusCode)423, "recovery_hold");
        await Ok(Send(anonymous, HttpMethod.Post, "/api/v1/auth/recover", new { director.Email }));
        await fixture.InScope(async services => Assert.Equal(0, await services.GetRequiredService<FirstBlickDbContext>().Jobs.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant.Id && x.Type == "identity.recovery")));
        await Hold(tenant.Id, AppJson.Serialize(Marker() with { State = "Released", ResolvedAt = DateTimeOffset.UtcNow, EvidenceDigest = new string('a', 64) }));
        await Ok(admin.GetAsync("/api/v1/customers"));
        var replay = await Send(admin, HttpMethod.Post, "/api/v1/customers", body, key: key); Assert.True(replay.IsSuccessStatusCode); Assert.True(replay.Headers.Contains("Idempotency-Replayed"));
        await Ok(Send(anonymous, HttpMethod.Post, "/api/v1/auth/activate", new { imported.Email, token, password = ClaimedPassword, displayName = "Released" }));
    }

    [PostgresApiFact]
    public async Task Malformed_unknown_or_unevidenced_recovery_markers_fail_closed()
    {
        var tenant = await fixture.CreateTenant(); var admin = await fixture.Login(await fixture.CreateUser(tenant.Id));
        foreach (var marker in new[] { "null", "{\"state\":\"Released\"}", AppJson.Serialize(Marker() with { State = "Unknown" }),
            AppJson.Serialize(Marker() with { State = "Released", ResolvedAt = DateTimeOffset.UtcNow, EvidenceDigest = "true" }),
            AppJson.Serialize(Marker() with { State = "Released", ResolvedAt = DateTimeOffset.UtcNow.AddDays(-1), EvidenceDigest = new string('a', 64) }) })
        {
            await Hold(tenant.Id, marker);
            await Error(admin.GetAsync("/api/v1/customers"), (HttpStatusCode)423, "recovery_hold");
        }
    }

    private async Task<ApiTestUser> Imported(Guid tenantId)
    {
        var user = await fixture.InScope(async services =>
        {
            var id = Guid.NewGuid(); var email = $"imported-{id:N}@example.test";
            var result = await services.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(new ApplicationUser { Id = id, Email = email, UserName = email, EmailConfirmed = false, DisplayName = "Imported personal account" });
            Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(x => x.Description)));
            return new ApiTestUser(tenantId, id, email, "");
        });
        await PendingMembership(tenantId, user.UserId, user.Email); return user;
    }
    private Task PendingMembership(Guid tenantId, Guid userId, string email) => fixture.InScope(async services =>
    {
        var db = services.GetRequiredService<FirstBlickDbContext>();
        db.Memberships.Add(new Membership { TenantId = tenantId, UserId = userId, Active = false, Role = BusinessRole.ManagingDirector });
        db.Receipts.Add(new CommandReceipt { TenantId = tenantId, ActorId = Guid.Empty, Key = MigrationAccountMarker.Key(userId), RequestDigest = new string('b', 64),
            Response = AppJson.Serialize(new MigrationAccountMarker(Guid.NewGuid(), "synthetic-source-identity", userId, email, "Pending", BusinessRole.ManagingDirector, true)) });
        await db.SaveChangesAsync();
    });
    private Task Hold(Guid tenantId, string response) => fixture.InScope(async services =>
    {
        var db = services.GetRequiredService<FirstBlickDbContext>();
        var marker = await db.Receipts.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ActorId == Guid.Empty && x.Key == OperationsRecoveryHold.Key);
        if (marker is null) { marker = new CommandReceipt { TenantId = tenantId, ActorId = Guid.Empty, Key = OperationsRecoveryHold.Key, RequestDigest = new string('c', 64) }; db.Receipts.Add(marker); }
        marker.Response = response; await db.SaveChangesAsync();
    });
    private static OperationsRecoveryHold Marker() => new(Guid.NewGuid(), "Held", DateTimeOffset.UtcNow.AddHours(-1), "synthetic-test-release", "Synthetic recovery reconciliation", null, null);
    private static async Task<string> Invite(HttpClient admin, string email)
    {
        var response = await Send(admin, HttpMethod.Post, "/api/v1/members/invitations", new { email, role = "Office" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("token").GetString()!;
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    private static async Task Ok(Task<HttpResponseMessage> pending) { var response = await pending; Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); }
    private static async Task Error(Task<HttpResponseMessage> pending, HttpStatusCode status, string? code = null)
    {
        var response = await pending; var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {content}");
        if (code is not null) Assert.Equal(code, JsonDocument.Parse(content).RootElement.GetProperty("code").GetString());
    }
}
