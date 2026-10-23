using System.Net;
using System.Text.Json;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
        var invalid = await Send(directorClient, HttpMethod.Post, "/api/v1/businesses", new { name = " ", timeZone = "UTC" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidZone = await Send(directorClient, HttpMethod.Post, "/api/v1/businesses", new { name = "Synthetic business", timeZone = "Invalid/Zone" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidZone.StatusCode);
        Assert.Equal(1, await fixture.InScope(async services => await services.GetRequiredService<FirstBlickDbContext>().Tenants.CountAsync()));
    }
}
