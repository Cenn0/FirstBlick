using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class BusinessCreationApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Director_can_create_an_isolated_business_and_replay_the_same_command()
    {
        var original = await fixture.CreateTenant("Original synthetic business");
        var owner = await fixture.CreateUser(original.Id);
        var otherDirector = await fixture.CreateUser(original.Id);
        await GrantOperator(owner);
        var client = await fixture.Login(owner);
        var key = Guid.NewGuid().ToString("N");
        var body = new { name = "Second synthetic business", timeZone = "Europe/Berlin" };

        var first = await Send(client, HttpMethod.Post, "/api/v1/businesses", body, key: key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var result = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var created = result.RootElement.GetProperty("id").GetGuid();
        Assert.NotEqual(original.Id, created);
        Assert.Equal(body.name, result.RootElement.GetProperty("name").GetString());

        var replay = await Send(client, HttpMethod.Post, "/api/v1/businesses", body, key: key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotency-Replayed"));
        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(created, replayBody.RootElement.GetProperty("id").GetGuid());

        var state = await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            return new
            {
                Tenants = await db.Tenants.CountAsync(x => x.Id == created),
                Owners = await db.Memberships.CountAsync(x => x.TenantId == created && x.UserId == owner.UserId && x.Active && x.Role == BusinessRole.ManagingDirector),
                OtherMembers = await db.Memberships.CountAsync(x => x.TenantId == created && x.UserId == otherDirector.UserId),
                Audit = await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == created && x.Action == "business.created")
            };
        });
        Assert.Equal(1, state.Tenants);
        Assert.Equal(1, state.Owners);
        Assert.Equal(0, state.OtherMembers);
        Assert.Equal(1, state.Audit);

        var nonOperator = await fixture.Login(otherDirector);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(nonOperator, HttpMethod.Post, "/api/v1/businesses", body)).StatusCode);

        client.DefaultRequestHeaders.Remove("X-Tenant-Id");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", created.ToString());
        var settings = await client.GetAsync("/api/v1/settings");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        using var settingsBody = JsonDocument.Parse(await settings.Content.ReadAsStringAsync());
        Assert.Equal(body.name, settingsBody.RootElement.GetProperty("name").GetString());

        var stranger = await fixture.Login(otherDirector);
        stranger.DefaultRequestHeaders.Remove("X-Tenant-Id");
        stranger.DefaultRequestHeaders.Add("X-Tenant-Id", created.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/api/v1/settings")).StatusCode);
    }

    [PostgresApiFact]
    public async Task Non_director_and_invalid_input_cannot_create_a_business()
    {
        var original = await fixture.CreateTenant();
        var office = await fixture.CreateUser(original.Id, BusinessRole.Office);
        var officeClient = await fixture.Login(office);
        var denied = await Send(officeClient, HttpMethod.Post, "/api/v1/businesses", new { name = "Denied synthetic business", timeZone = "UTC" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var director = await fixture.CreateUser(original.Id);
        var directorClient = await fixture.Login(director);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(directorClient, HttpMethod.Post, "/api/v1/businesses", new { name = "Denied without operator role", timeZone = "UTC" })).StatusCode);
        await GrantOperator(director);
        var invalid = await Send(directorClient, HttpMethod.Post, "/api/v1/businesses", new { name = " ", timeZone = "UTC" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidZone = await Send(directorClient, HttpMethod.Post, "/api/v1/businesses", new { name = "Synthetic business", timeZone = "Invalid/Zone" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidZone.StatusCode);
        Assert.Equal(1, await fixture.InScope(async services => await services.GetRequiredService<FirstBlickDbContext>().Tenants.CountAsync()));
    }

    [PostgresApiFact]
    public async Task Revoked_platform_permission_cannot_replay_an_earlier_creation()
    {
        var original = await fixture.CreateTenant();
        var owner = await fixture.CreateUser(original.Id);
        await GrantOperator(owner);
        var client = await fixture.Login(owner);
        var key = Guid.NewGuid().ToString("N");
        var body = new { name = "Revocation synthetic business", timeZone = "UTC" };
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Post, "/api/v1/businesses", body, key: key)).StatusCode);

        await fixture.InScope(async services =>
        {
            var manager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var account = (await manager.FindByIdAsync(owner.UserId.ToString()))!;
            Assert.True((await manager.RemoveClaimAsync(account, new Claim(Bootstrap.OperatorClaimType, Bootstrap.OperatorClaimValue))).Succeeded);
        });
        var replay = await Send(client, HttpMethod.Post, "/api/v1/businesses", body, key: key);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.False(replay.Headers.Contains("Idempotency-Replayed"));
    }

    [PostgresApiFact]
    public async Task Existing_confirmed_director_can_receive_the_operator_role_through_the_host_command()
    {
        var tenant = await fixture.CreateTenant();
        var director = await fixture.CreateUser(tenant.Id);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Platform:GrantEmail"] = director.Email }).Build();
        await fixture.InScope(services => Bootstrap.GrantOperator(services, config));
        await fixture.InScope(services => Bootstrap.GrantOperator(services, config));
        Assert.Equal(1, await fixture.InScope(async services =>
            await services.GetRequiredService<FirstBlickDbContext>().UserClaims.CountAsync(x => x.UserId == director.UserId
                && x.ClaimType == Bootstrap.OperatorClaimType && x.ClaimValue == Bootstrap.OperatorClaimValue)));

        var office = await fixture.CreateUser(tenant.Id, BusinessRole.Office);
        var deniedConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Platform:GrantEmail"] = office.Email }).Build();
        var denied = await Assert.ThrowsAsync<DomainException>(() => fixture.InScope(services => Bootstrap.GrantOperator(services, deniedConfig)));
        Assert.Equal("operator_grant_denied", denied.Code);
    }

    private Task GrantOperator(ApiTestUser actor) => fixture.InScope(async services =>
    {
        var manager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var account = (await manager.FindByIdAsync(actor.UserId.ToString()))!;
        Assert.True((await manager.AddClaimAsync(account, new Claim(Bootstrap.OperatorClaimType, Bootstrap.OperatorClaimValue))).Succeeded);
    });
}
