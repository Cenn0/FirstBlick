using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class SecurityApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Roles_are_enforced_by_the_server_including_direct_resource_urls()
    {
        var tenant = await fixture.CreateTenant();
        var expectations = new[]
        {
            (BusinessRole.ManagingDirector, true, true, true, true),
            (BusinessRole.Office, true, true, true, false),
            (BusinessRole.Accounting, true, false, false, false),
            (BusinessRole.SiteManager, true, false, true, false),
            (BusinessRole.Worker, false, false, true, true),
            (BusinessRole.Trainer, false, false, true, true),
            (BusinessRole.Partner, false, false, false, false)
        };
        foreach (var (role, customers, writeCustomer, employees, training) in expectations)
        {
            var client = await fixture.Login(await fixture.CreateUser(tenant.Id, role));
            Assert.Equal(customers ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/customers")).StatusCode);
            Assert.Equal(employees ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/employees")).StatusCode);
            Assert.Equal(training ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/training-records")).StatusCode);
            Assert.Equal(writeCustomer ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
                (await Send(client, HttpMethod.Post, "/api/v1/customers", new { name = $"Synthetic {role}" })).StatusCode);
            Assert.Equal(role == BusinessRole.ManagingDirector ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/members")).StatusCode);
        }
    }

    [PostgresApiFact]
    public async Task Tenant_switches_and_cross_tenant_references_never_expose_or_link_foreign_data()
    {
        var a = await fixture.CreateTenant(); var b = await fixture.CreateTenant();
        var alice = await fixture.CreateUser(a.Id); var bob = await fixture.CreateUser(b.Id);
        var client = await fixture.Login(alice);
        var foreign = await fixture.Save(new Customer { TenantId = b.Id, Name = "Foreign confidential synthetic customer" }, bob.UserId);
        await Error(client.GetAsync($"/api/v1/customers/{foreign.Id}"), HttpStatusCode.NotFound, "not_found");
        var list = await Json(await client.GetAsync("/api/v1/customers")); Assert.Equal(0, list.GetProperty("total").GetInt32());
        await Error(Send(client, HttpMethod.Post, "/api/v1/buildings", new { customerId = foreign.Id, name = "Invalid foreign link", address = "Synthetic Street" }), HttpStatusCode.BadRequest, "invalid_reference");
        client.DefaultRequestHeaders.Remove("X-Tenant-Id"); client.DefaultRequestHeaders.Add("X-Tenant-Id", b.Id.ToString());
        await Error(client.GetAsync("/api/v1/customers"), HttpStatusCode.Forbidden, "forbidden");
    }

    [PostgresApiFact]
    public async Task Database_foreign_keys_are_tenant_qualified_even_when_application_checks_are_bypassed()
    {
        var a = await fixture.CreateTenant(); var b = await fixture.CreateTenant();
        var alice = await fixture.CreateUser(a.Id); var bob = await fixture.CreateUser(b.Id);
        var local = await fixture.Save(new Customer { TenantId = a.Id, Name = "Local synthetic" }, alice.UserId);
        var foreign = await fixture.Save(new Customer { TenantId = b.Id, Name = "Foreign synthetic" }, bob.UserId);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            db.Links.Add(new EntityLink { TenantId = a.Id, SourceKind = "customers", SourceId = local.Id, TargetKind = "customers", TargetId = foreign.Id });
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal("23503", Assert.IsType<PostgresException>(exception.InnerException).SqlState);
        });
    }

    [PostgresApiFact]
    public async Task Concurrent_identical_minimal_create_requests_replay_once_despite_server_generated_fields()
    {
        var tenant = await fixture.CreateTenant(); var person = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(person);
        var key = Guid.NewGuid().ToString("N"); var body = new { name = "Exactly one synthetic customer" };
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Send(client, HttpMethod.Post, "/api/v1/customers", body, key: key)));
        foreach (var response in responses) Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var ids = await Task.WhenAll(responses.Select(async r => (await Json(r)).GetProperty("id").GetGuid())); Assert.Single(ids.Distinct());
        Assert.Equal(4, responses.Count(r => r.Headers.Contains("Idempotency-Replayed")));
        await fixture.InScope(async services =>
        {
            services.GetRequiredService<TenantScope>().TenantId = tenant.Id; var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(1, await db.Entities.CountAsync()); Assert.Equal(1, await db.Audit.CountAsync());
            Assert.Equal(1, await db.Jobs.CountAsync()); Assert.Equal(1, await db.Receipts.CountAsync());
        });
        await Error(Send(client, HttpMethod.Post, "/api/v1/customers", new { name = "Different content" }, key: key), HttpStatusCode.Conflict, "idempotency_conflict");
    }

    [PostgresApiFact]
    public async Task Concurrent_edits_require_the_current_revision_and_only_one_wins()
    {
        var tenant = await fixture.CreateTenant(); var person = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(person);
        var customer = await Ok<Customer>(Send(client, HttpMethod.Post, "/api/v1/customers", new { name = "Original" }));
        await Error(Send(client, HttpMethod.Put, $"/api/v1/customers/{customer.Id}", new { name = "No revision" }), (HttpStatusCode)428, "revision_required");
        var responses = await Task.WhenAll(Send(client, HttpMethod.Put, $"/api/v1/customers/{customer.Id}", new { name = "First" }, 1),
            Send(client, HttpMethod.Put, $"/api/v1/customers/{customer.Id}", new { name = "Second" }, 1));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        var stored = await fixture.Read<Customer>(tenant.Id, customer.Id); Assert.Equal(2, stored.Revision); Assert.Contains(stored.Name, new[] { "First", "Second" });
    }

    [PostgresApiFact]
    public async Task Revoked_membership_blocks_reads_writes_and_receipt_replay_on_the_next_request()
    {
        var tenant = await fixture.CreateTenant(); var person = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(person);
        var key = Guid.NewGuid().ToString("N"); var body = new { name = "Before revocation" };
        _ = await Ok<Customer>(Send(client, HttpMethod.Post, "/api/v1/customers", body, key: key));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var membership = await db.Memberships.SingleAsync(x => x.TenantId == tenant.Id && x.UserId == person.UserId);
            membership.Active = false; membership.Revision++; await db.SaveChangesAsync();
        });
        await Error(client.GetAsync("/api/v1/customers"), HttpStatusCode.Forbidden, "forbidden");
        await Error(Send(client, HttpMethod.Post, "/api/v1/customers", body, key: key), HttpStatusCode.Forbidden, "forbidden");
    }

    [PostgresApiFact]
    public async Task Antiforgery_and_final_headers_cover_success_and_errors()
    {
        var tenant = await fixture.CreateTenant(); var person = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(person);
        var success = await client.GetAsync("/api/v1/customers"); Headers(success);
        client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        var csrf = await Send(client, HttpMethod.Post, "/api/v1/customers", new { name = "Rejected" }); Headers(csrf);
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode); Assert.Equal("csrf_invalid", (await Json(csrf)).GetProperty("code").GetString());
        await RefreshCsrf(client);
        var invalid = await Send(client, HttpMethod.Post, "/api/v1/customers", new { name = "", maliciousExtra = true }); Headers(invalid); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var missing = await client.GetAsync($"/api/v1/customers/{Guid.NewGuid()}"); Headers(missing); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var document = await Send(client, HttpMethod.Post, "/api/v1/documents", new { name = "synthetic.pdf", contentType = "application/pdf", size = 5, sha256 = new string('0', 64) });
        Headers(document); Assert.Equal(HttpStatusCode.ServiceUnavailable, document.StatusCode);
        var anonymous = await fixture.Client().GetAsync("/api/v1/customers"); Headers(anonymous); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [PostgresApiFact]
    public async Task Literal_search_and_filters_cannot_change_query_structure()
    {
        var tenant = await fixture.CreateTenant(); var person = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(person);
        _ = await Ok<Customer>(Send(client, HttpMethod.Post, "/api/v1/customers", new { name = "Safe synthetic" }));
        var attack = Uri.EscapeDataString("' OR true; DROP TABLE \"Entities\"; --");
        foreach (var field in new[] { "search", "status", "projectId", "customerId" })
        {
            var response = await client.GetAsync($"/api/v1/customers?{field}={attack}"); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(0, (await Json(response)).GetProperty("total").GetInt32());
        }
        Assert.Equal(1, (await Json(await client.GetAsync("/api/v1/customers"))).GetProperty("total").GetInt32());
    }

    [PostgresApiFact]
    public async Task Disabled_modules_keep_history_readable_but_reject_new_commands_and_remove_dashboard_metrics()
    {
        var tenant = await fixture.CreateTenant(); var person = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(person);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var stored = await db.Tenants.SingleAsync(x => x.Id == tenant.Id);
            stored.Modules = ["core"]; stored.PolicyRevision++; await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/quotes")).StatusCode);
        await Error(Send(client, HttpMethod.Post, "/api/v1/quotes", new { title = "Disabled" }), HttpStatusCode.Forbidden, "module_disabled");
        var dashboard = await Json(await client.GetAsync("/api/v1/dashboard")); Assert.False(dashboard.TryGetProperty("quoteDrafts", out _)); Assert.False(dashboard.TryGetProperty("openInvoices", out _));
        var me = await Json(await client.GetAsync("/api/v1/auth/me")); var capabilities = me.GetProperty("tenants")[0].GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("quotes.read", capabilities); Assert.DoesNotContain("quotes.write", capabilities);
    }

    [PostgresApiFact]
    public async Task Four_eyes_bind_the_actual_proposal_revision_policy_and_second_person()
    {
        var tenant = await fixture.CreateTenant(); var first = await fixture.CreateUser(tenant.Id); var second = await fixture.CreateUser(tenant.Id);
        var alice = await fixture.Login(first); var bob = await fixture.Login(second);
        var proposal = new { name = "Approved synthetic business", timeZone = "UTC", modules = tenant.Modules, approvalActions = tenant.ApprovalActions, policyRevision = 1 };
        await Error(Send(alice, HttpMethod.Put, "/api/v1/settings", proposal, 1), HttpStatusCode.Conflict, "approval_required");
        var approval = await Ok<ApprovalRequest>(Send(alice, HttpMethod.Post, "/api/v1/approvals", new { action = "settings.change", kind = "settings", entityId = tenant.Id, revision = 1, payload = proposal }));
        await Error(Send(alice, HttpMethod.Post, $"/api/v1/approvals/{approval.Id}/approve", new { }, approval.Revision), HttpStatusCode.BadRequest, "self_approval");
        _ = await Ok<ApprovalRequest>(Send(bob, HttpMethod.Post, $"/api/v1/approvals/{approval.Id}/approve", new { }, approval.Revision));
        alice.DefaultRequestHeaders.Add("X-Approval-Id", approval.Id.ToString());
        var changed = new { name = "Unapproved edit", timeZone = "UTC", modules = tenant.Modules, approvalActions = tenant.ApprovalActions, policyRevision = 1 };
        await Error(Send(alice, HttpMethod.Put, "/api/v1/settings", changed, 1), HttpStatusCode.Conflict, "approval_invalid");
        var result = await Json(await Send(alice, HttpMethod.Put, "/api/v1/settings", proposal, 1)); Assert.Equal(2, result.GetProperty("policyRevision").GetInt64());
        Assert.Equal(proposal.name, result.GetProperty("name").GetString());
        await Error(Send(alice, HttpMethod.Put, "/api/v1/settings", proposal, 2), HttpStatusCode.Conflict, "approval_invalid");
    }

    [PostgresApiFact]
    public async Task Last_director_cannot_be_removed_and_module_dependencies_cannot_be_bypassed()
    {
        var tenant = await fixture.CreateTenant(); var person = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(person);
        await Error(Send(client, HttpMethod.Put, $"/api/v1/members/{person.UserId}", new { role = "Worker", active = true, revision = 1 }, 1), HttpStatusCode.BadRequest, "last_director");
        await Error(Send(client, HttpMethod.Put, "/api/v1/settings", new { name = tenant.Name, timeZone = "UTC", modules = new[] { "core", "finance" }, approvalActions = Array.Empty<string>(), policyRevision = 1 }, 1), HttpStatusCode.BadRequest, "module_dependency");
        Assert.Equal(1, (await Json(await client.GetAsync("/api/v1/settings"))).GetProperty("policyRevision").GetInt64());
    }

    [PostgresApiFact]
    public async Task Invitation_cannot_take_over_an_existing_account_or_reset_its_password()
    {
        var original = await fixture.CreateTenant(); var target = await fixture.CreateTenant();
        var existing = await fixture.CreateUser(original.Id); var inviter = await fixture.CreateUser(target.Id); var admin = await fixture.Login(inviter);
        var invitation = await Json(await Send(admin, HttpMethod.Post, "/api/v1/members/invitations", new { email = existing.Email, role = "Office" }));
        var token = invitation.GetProperty("token").GetString()!; var anonymous = fixture.Client(); await RefreshCsrf(anonymous);
        await Error(Send(anonymous, HttpMethod.Post, "/api/v1/auth/activate", new { email = existing.Email, token, password = "Hijack-Synthetic-Password1!", displayName = "Hijack" }), HttpStatusCode.BadRequest, "existing_account_login_required");
        await Error(Send(anonymous, HttpMethod.Post, "/api/v1/auth/reset", new { email = existing.Email, token, password = "Hijack-Synthetic-Password1!" }), HttpStatusCode.BadRequest, "invalid_recovery");
        var owner = await fixture.Login(existing);
        await Error(Send(owner, HttpMethod.Post, "/api/v1/auth/activate", new { email = existing.Email, token, password = "Replacement-Synthetic-Password1!", displayName = "Owner" }), HttpStatusCode.BadRequest, "invitation_not_recovery");
        var accepted = await Send(owner, HttpMethod.Post, "/api/v1/auth/activate", new { email = existing.Email, token, password = (string?)null, displayName = "Owner" }); Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        await Error(Send(owner, HttpMethod.Post, "/api/v1/auth/activate", new { email = existing.Email, token, displayName = "Owner" }), HttpStatusCode.BadRequest, "invalid_invitation");
        _ = await fixture.Login(existing);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(2, await db.Memberships.CountAsync(x => x.UserId == existing.UserId));
            Assert.Equal(BusinessRole.Office, (await db.Memberships.SingleAsync(x => x.TenantId == target.Id && x.UserId == existing.UserId)).Role);
        });
    }

    [PostgresApiFact]
    public async Task Password_reset_tokens_cannot_activate_memberships_and_successful_reset_revokes_old_cookies()
    {
        var tenant = await fixture.CreateTenant(); var person = await fixture.CreateUser(tenant.Id, BusinessRole.Worker); var oldSession = await fixture.Login(person);
        var token = await fixture.InScope(async services =>
        {
            var manager = services.GetRequiredService<UserManager<ApplicationUser>>(); return await manager.GeneratePasswordResetTokenAsync((await manager.FindByIdAsync(person.UserId.ToString()))!);
        });
        var anonymous = fixture.Client(); await RefreshCsrf(anonymous);
        var rejectedActivation = await Send(anonymous, HttpMethod.Post, "/api/v1/auth/activate", new { email = person.Email, token, password = "New-Synthetic-Password1!", displayName = "Person" });
        Assert.Equal(HttpStatusCode.BadRequest, rejectedActivation.StatusCode);
        Assert.Contains((await Json(rejectedActivation)).GetProperty("code").GetString(), new[] { "invalid_invitation", "text_too_long" });
        var reset = await Send(anonymous, HttpMethod.Post, "/api/v1/auth/reset", new { email = person.Email, token, password = "New-Synthetic-Password1!" }); Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldSession.GetAsync("/api/v1/auth/me")).StatusCode);
        await fixture.InScope(async services => Assert.Equal(BusinessRole.Worker, (await services.GetRequiredService<FirstBlickDbContext>().Memberships.SingleAsync(x => x.TenantId == tenant.Id && x.UserId == person.UserId)).Role));
        _ = await fixture.Login(person with { Password = "New-Synthetic-Password1!" });
    }

    [PostgresApiFact]
    public async Task JSON_request_budget_is_enforced_before_deserialization()
    {
        var tenant = await fixture.CreateTenant(); var person = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(person);
        await Error(Send(client, HttpMethod.Post, "/api/v1/customers", new { name = "Oversized", notes = new string('x', 2_100_000) }), HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        Assert.Equal(0, (await Json(await client.GetAsync("/api/v1/customers"))).GetProperty("total").GetInt32());
    }

    private static void Headers(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("SAMEORIGIN", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    { var response = await pending; Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); return AppJson.Read<T>(await response.Content.ReadAsStringAsync()); }
    private static async Task Error(Task<HttpResponseMessage> pending, HttpStatusCode status, string code)
    { var response = await pending; var content = await response.Content.ReadAsStringAsync(); Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {content}"); Assert.Equal(code, JsonDocument.Parse(content).RootElement.GetProperty("code").GetString()); }
}
