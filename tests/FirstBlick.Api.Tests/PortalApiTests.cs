using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class PortalApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task A_fresh_partner_invitation_activates_a_personal_account_with_no_implicit_content()
    {
        var tenant = await fixture.CreateTenant(); var director = await fixture.CreateUser(tenant.Id); var admin = await fixture.Login(director);
        var email = $"new-partner-{Guid.NewGuid():N}@example.test";
        var invitation = await Ok<JsonElement>(Send(admin, HttpMethod.Post, "/api/v1/members/invitations", new { email, role = "Partner" }));
        var anonymous = fixture.Client(); await RefreshCsrf(anonymous);
        const string password = "Personal-Partner-Test-Password1!";
        await Status(Send(anonymous, HttpMethod.Post, "/api/v1/auth/activate", new { email, password, token = invitation.GetProperty("token").GetString(), displayName = "Personal synthetic partner" }), HttpStatusCode.OK);
        var person = await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var user = await db.Users.SingleAsync(x => x.Email == email);
            return new ApiTestUser(tenant.Id, user.Id, email, password);
        });
        var partner = await fixture.Login(person);
        var me = await Ok<JsonElement>(partner.GetAsync("/api/v1/auth/me")); Assert.Equal("Partner", me.GetProperty("tenants")[0].GetProperty("role").GetString());
        Assert.Equal(0, (await Ok<JsonElement>(partner.GetAsync("/api/v1/portal/shares"))).GetProperty("total").GetInt32());
        await Status(partner.GetAsync("/api/v1/customers"), HttpStatusCode.Forbidden);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var business = await db.Tenants.SingleAsync(x => x.Id == tenant.Id);
            business.Modules = business.Modules.Where(x => x != "portal").ToArray(); await db.SaveChangesAsync();
        });
        await Status(Send(admin, HttpMethod.Post, "/api/v1/members/invitations", new { email = "disabled-partner@example.test", role = "Partner" }), HttpStatusCode.Forbidden);
    }

    [PostgresApiFact]
    public async Task Named_partner_sees_only_explicit_sales_content_and_acceptance_replay_is_denied_after_revocation()
    {
        var session = await Setup(); using var director = session.DirectorClient; using var partner = session.PartnerClient;
        var share = await Grant(director, session.Partner.UserId, session.Quote);
        var list = await Ok<JsonElement>(partner.GetAsync("/api/v1/portal/shares"));
        Assert.Equal(1, list.GetProperty("total").GetInt32()); Assert.Equal(share.Id, list.GetProperty("items")[0].GetProperty("id").GetGuid());
        await Status(partner.GetAsync($"/api/v1/quotes/{session.Quote.Id}"), HttpStatusCode.Forbidden);
        await Status(partner.GetAsync("/api/v1/portal-shares"), HttpStatusCode.Forbidden);
        var detail = await Ok<JsonElement>(partner.GetAsync($"/api/v1/portal/shares/{share.Id}"));
        var quote = detail.GetProperty("quote");
        Assert.Equal("Synthetic buyer", quote.GetProperty("presentation").GetProperty("buyerName").GetString());
        Assert.Equal(session.Quote.Snapshot!.Gross, quote.GetProperty("gross").Deserialize<decimal>(AppJson.Options));
        var keys = PropertyNames(detail).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var privateField in new[] { "unitMaterialCost", "unitLaborHours", "hourlyRate", "markupPercent", "discountPercent", "unitCost", "inputs", "notes", "recipientUserId", "grantedBy" })
            Assert.DoesNotContain(privateField, keys);
        var other = await fixture.CreateUser(session.Partner.TenantId, BusinessRole.Partner);
        using var otherClient = await fixture.Login(other);
        await Status(otherClient.GetAsync($"/api/v1/portal/shares/{share.Id}"), HttpStatusCode.NotFound);
        Assert.Equal(0, (await Ok<JsonElement>(otherClient.GetAsync("/api/v1/portal/shares"))).GetProperty("total").GetInt32());
        await Status(Send(partner, HttpMethod.Post, $"/api/v1/portal/shares/{share.Id}/accept", new { digest = session.Quote.Snapshot.Digest }, session.Quote.Revision), HttpStatusCode.Forbidden);
        await Status(Send(partner, HttpMethod.Post, $"/api/v1/portal/shares/{share.Id}/accept", new { digest = session.Quote.IssuedDigest }), (HttpStatusCode)428);
        var key = Guid.NewGuid().ToString("N"); var path = $"/api/v1/portal/shares/{share.Id}/accept";
        var accepted = await Ok<JsonElement>(Send(partner, HttpMethod.Post, path, new { digest = session.Quote.IssuedDigest }, session.Quote.Revision, key));
        Assert.Equal("Accepted", accepted.GetProperty("quote").GetProperty("status").GetString());
        using var replay = await Send(partner, HttpMethod.Post, path, new { digest = session.Quote.IssuedDigest }, session.Quote.Revision, key);
        Assert.True(replay.IsSuccessStatusCode); Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        await Ok<PortalShare>(Send(director, HttpMethod.Post, $"/api/v1/portal/grants/{share.Id}/revoke", new { }, share.Revision));
        await Status(partner.GetAsync($"/api/v1/portal/shares/{share.Id}"), HttpStatusCode.NotFound);
        await Status(Send(partner, HttpMethod.Post, path, new { digest = session.Quote.IssuedDigest }, session.Quote.Revision, key), HttpStatusCode.NotFound);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(1, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == session.Partner.TenantId && x.Action == "portal.quote_accept"));
        });
    }

    [PostgresApiFact]
    public async Task Feedback_is_version_bound_and_filtered_while_portal_disable_and_membership_withdrawal_stop_access()
    {
        var session = await Setup(); using var director = session.DirectorClient; using var partner = session.PartnerClient;
        var share = await Grant(director, session.Partner.UserId, session.Quote, false);
        var secondShare = await Grant(director, session.Partner.UserId, session.Quote, false);
        var key = Guid.NewGuid().ToString("N"); var path = $"/api/v1/portal/shares/{share.Id}/feedback";
        var feedback = await Ok<JsonElement>(Send(partner, HttpMethod.Post, path, new { message = "Please clarify the released position." }, share.Revision, key));
        Assert.Equal(share.SourceDigest, feedback.GetProperty("sourceDigest").GetString()); Assert.False(feedback.GetProperty("fromBusiness").GetBoolean());
        await Ok<PortalFeedback>(Send(director, HttpMethod.Post, $"/api/v1/portal/grants/{share.Id}/reply", new { message = "This reply refers to that exact released offer." }, share.Revision));
        var thread = await Ok<JsonElement>(director.GetAsync($"/api/v1/portal/grants/{share.Id}/feedback?page=1&pageSize=1"));
        Assert.Equal(2, thread.GetProperty("total").GetInt32()); Assert.Single(thread.GetProperty("items").EnumerateArray());
        var otherThread = await Ok<JsonElement>(director.GetAsync($"/api/v1/portal/grants/{secondShare.Id}/feedback")); Assert.Equal(0, otherThread.GetProperty("total").GetInt32());
        await Status(Send(partner, HttpMethod.Post, $"/api/v1/portal/shares/{share.Id}/accept", new { digest = share.SourceDigest }, session.Quote.Revision), HttpStatusCode.Forbidden);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(t => t.Id == session.Partner.TenantId);
            tenant.Modules = tenant.Modules.Where(x => x != "portal").ToArray(); await db.SaveChangesAsync();
        });
        await Status(partner.GetAsync($"/api/v1/portal/shares/{share.Id}"), HttpStatusCode.Forbidden);
        await Status(Send(partner, HttpMethod.Post, path, new { message = "Please clarify the released position." }, share.Revision, key), HttpStatusCode.Forbidden);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(t => t.Id == session.Partner.TenantId);
            tenant.Modules = AccessRules.AllModules; (await db.Memberships.SingleAsync(m => m.TenantId == tenant.Id && m.UserId == session.Partner.UserId)).Active = false;
            await db.SaveChangesAsync();
        });
        await Status(partner.GetAsync("/api/v1/portal/shares"), HttpStatusCode.Forbidden);
    }

    [PostgresApiFact]
    public async Task Grant_cannot_target_an_unrelated_identity_draft_or_unreviewed_document_and_has_a_bounded_lifetime()
    {
        var session = await Setup(); using var director = session.DirectorClient;
        var otherTenant = await fixture.CreateTenant(); var outsider = await fixture.CreateUser(otherTenant.Id, BusinessRole.Partner);
        await Status(Send(director, HttpMethod.Post, "/api/v1/portal/grants", new { recipientUserId = outsider.UserId, sourceKind = "quotes", sourceId = session.Quote.Id,
            expiresAt = DateTimeOffset.UtcNow.AddDays(2), title = "Invalid recipient", allowAcceptance = true }), HttpStatusCode.BadRequest);
        await Status(Send(director, HttpMethod.Post, "/api/v1/portal/grants", new { recipientUserId = session.Partner.UserId, sourceKind = "quotes", sourceId = session.Quote.Id,
            expiresAt = DateTimeOffset.UtcNow.AddDays(91), title = "Too long", allowAcceptance = true }), HttpStatusCode.BadRequest);
        await Status(Send(director, HttpMethod.Post, "/api/v1/portal-shares", new PortalShare()), HttpStatusCode.BadRequest);
        await Status(Send(director, HttpMethod.Post, "/api/v1/portal/grants", new { recipientUserId = session.Partner.UserId, sourceKind = "documents", sourceId = Guid.NewGuid(),
            expiresAt = DateTimeOffset.UtcNow.AddDays(2), title = "Missing file", allowAcceptance = false }), HttpStatusCode.BadRequest);
        var draft = new Quote { TenantId = session.Partner.TenantId, CustomerId = session.Quote.CustomerId, ProjectId = session.Quote.ProjectId,
            Title = "Unissued", Lines = session.Quote.Lines.Select(x => x with { }).ToList() };
        await fixture.Save(draft, session.Director.UserId);
        await Status(Send(director, HttpMethod.Post, "/api/v1/portal/grants", new { recipientUserId = session.Partner.UserId, sourceKind = "quotes", sourceId = draft.Id,
            expiresAt = DateTimeOffset.UtcNow.AddDays(2), title = "Draft", allowAcceptance = true }), HttpStatusCode.BadRequest);
    }

    private async Task<(ApiTestUser Director, ApiTestUser Partner, HttpClient DirectorClient, HttpClient PartnerClient, Quote Quote)> Setup()
    {
        var tenant = await fixture.CreateTenant(); var director = await fixture.CreateUser(tenant.Id); var partner = await fixture.CreateUser(tenant.Id, BusinessRole.Partner);
        var directorClient = await fixture.Login(director); var partnerClient = await fixture.Login(partner);
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Synthetic buyer", Address = "Synthetic street 1", Notes = "Confidential CRM note" }, director.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id, Name = "Synthetic building", Address = customer.Address }, director.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id, BuildingId = building.Id, Name = "Synthetic project" }, director.UserId);
        var quote = await fixture.Save(new Quote { TenantId = tenant.Id, CustomerId = customer.Id, ProjectId = project.Id, Title = "Synthetic shared offer",
            Lines = [new QuoteLine { Description = "Released sale position", Quantity = 3.25m, Unit = "m²", UnitMaterialCost = 42m, UnitLaborHours = .5m, HourlyRate = 60m, MarkupPercent = 20m, VatPercent = 19m }] }, director.UserId);
        quote = await Ok<Quote>(Send(directorClient, HttpMethod.Post, $"/api/v1/quotes/{quote.Id}/freeze", new { }, quote.Revision));
        return (director, partner, directorClient, partnerClient, quote);
    }
    private static Task<PortalShare> Grant(HttpClient client, Guid recipient, Quote quote, bool allow = true) => Ok<PortalShare>(Send(client, HttpMethod.Post, "/api/v1/portal/grants", new
    { recipientUserId = recipient, sourceKind = "quotes", sourceId = quote.Id, expiresAt = DateTimeOffset.UtcNow.AddDays(7), title = "Released offer", allowAcceptance = allow }));
    private static IEnumerable<string> PropertyNames(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().SelectMany(x => new[] { x.Name }.Concat(PropertyNames(x.Value))),
        JsonValueKind.Array => value.EnumerateArray().SelectMany(PropertyNames),
        _ => []
    };
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var json = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {json}"); return AppJson.Read<T>(json);
    }
    private static async Task Status(Task<HttpResponseMessage> pending, HttpStatusCode expected)
    { using var response = await pending; Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}"); }
}
