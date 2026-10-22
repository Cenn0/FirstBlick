using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Core.Technical;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class RoofActionsApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static object Input(string mode) => mode switch {
        "drainage" => new RoofDrainageInput("Synthetic 5 minute site pair", 300, 600,
            EmergencyDrainageScenario.PrimaryUnavailable,
            [new("West", 200, .8, 4, 6, "Synthetic effective area", "Synthetic head-dependent capacity")]),
        "wind" => new RoofWindInput("Synthetic single wind direction",
            [new("West", 12, .8, -1.5, .6, .2, "Synthetic pressure/area/coefficient references")]),
        _ => new RoofSnowInput("Synthetic snow arrangement", 1.8, .8, 1,
            [new("West", 50, .8, "Synthetic horizontal projection and shape")])
    };

    [PostgresApiFact]
    public Task Preview_persistence_replay_and_tenant_isolation_use_the_real_authorized_API_drainage() => Preview_persistence_replay_and_tenant_isolation_use_the_real_authorized_API("drainage");

    [PostgresApiFact]
    public Task Preview_persistence_replay_and_tenant_isolation_use_the_real_authorized_API_wind() => Preview_persistence_replay_and_tenant_isolation_use_the_real_authorized_API("wind");

    [PostgresApiFact]
    public Task Preview_persistence_replay_and_tenant_isolation_use_the_real_authorized_API_snow() => Preview_persistence_replay_and_tenant_isolation_use_the_real_authorized_API("snow");

    private async Task Preview_persistence_replay_and_tenant_isolation_use_the_real_authorized_API(string mode)
    {
        var tenant = await fixture.CreateTenant();
        var director = await fixture.CreateUser(tenant.Id);
        using var client = await fixture.Login(director);
        using var preview = await Send(client, HttpMethod.Post, "/api/v1/technical/roof/" + mode, Input(mode));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var expected = TechnicalRecords.Evaluate("roof." + mode, JsonSerializer.SerializeToElement(Input(mode), AppJson.Options));
        using var response = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal(TechnicalRecords.Canonical(expected), TechnicalRecords.Canonical(response.RootElement));
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Synthetic action customer" }, director.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id, Name = "Synthetic roof", Address = "Synthetic" }, director.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id, BuildingId = building.Id, Name = "Synthetic actions" }, director.UserId);
        var body = new { projectId = project.Id, title = "Synthetic reviewed action", roofRevisionId = (Guid?)null, method = "roof." + mode, input = Input(mode) };
        var key = Guid.NewGuid().ToString();
        using var saved = await Send(client, HttpMethod.Post, "/api/v1/technical/runs", body, key: key);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var record = AppJson.Read<TechnicalRecord>(await saved.Content.ReadAsStringAsync());
        record.Validate();
        Assert.Equal(TechnicalRecords.Canonical(expected), TechnicalRecords.Canonical(record.Result));
        Assert.False(record.IsNormative);
        (await fixture.Read<TechnicalRecord>(tenant.Id, record.Id)).Validate();
        using var replay = await Send(client, HttpMethod.Post, "/api/v1/technical/runs", body, key: key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(record.Id, AppJson.Read<TechnicalRecord>(await replay.Content.ReadAsStringAsync()).Id);
        var foreign = await fixture.CreateTenant();
        using var foreignClient = await fixture.Login(await fixture.CreateUser(foreign.Id));
        using var rejected = await Send(foreignClient, HttpMethod.Post, "/api/v1/technical/runs", body);
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
    }

    [PostgresApiFact]
    public Task Workers_and_disabled_modules_cannot_calculate_drainage() => Workers_and_disabled_modules_cannot_calculate("drainage");

    [PostgresApiFact]
    public Task Workers_and_disabled_modules_cannot_calculate_wind() => Workers_and_disabled_modules_cannot_calculate("wind");

    [PostgresApiFact]
    public Task Workers_and_disabled_modules_cannot_calculate_snow() => Workers_and_disabled_modules_cannot_calculate("snow");

    private async Task Workers_and_disabled_modules_cannot_calculate(string mode)
    {
        var tenant = await fixture.CreateTenant();
        using var worker = await fixture.Login(await fixture.CreateUser(tenant.Id, BusinessRole.Worker));
        using var denied = await Send(worker, HttpMethod.Post, "/api/v1/technical/roof/" + mode, Input(mode));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var withoutTechnical = await fixture.CreateTenant(modules: ["core"]);
        using var director = await fixture.Login(await fixture.CreateUser(withoutTechnical.Id));
        using var disabled = await Send(director, HttpMethod.Post, "/api/v1/technical/roof/" + mode, Input(mode));
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
    }

    [PostgresApiFact]
    public async Task Missing_scenario_and_missing_zero_capable_coefficients_are_not_silently_defaulted()
    {
        var tenant = await fixture.CreateTenant();
        using var client = await fixture.Login(await fixture.CreateUser(tenant.Id));
        using var missingScenario = await Send(client, HttpMethod.Post, "/api/v1/technical/roof/drainage", new {
            rainfallSource = "Synthetic", designRainfallLsHa = 300, stormRainfallLsHa = 600,
            catchments = new[] { new { name = "West", effectiveAreaM2 = 200, designRunoffCoefficient = .8,
                primaryCapacityLs = 4, emergencyCapacityLs = 6, areaSource = "Synthetic", capacitySource = "Synthetic" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingScenario.StatusCode);
        using var missingCoefficient = await Send(client, HttpMethod.Post, "/api/v1/technical/roof/wind", new {
            caseSource = "Synthetic", zones = new[] { new { name = "West", loadedAreaM2 = 10,
                externalPeakPressureKNm2 = .8, externalCoefficient = -1.5, internalPeakPressureKNm2 = .6,
                valueSource = "Synthetic" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingCoefficient.StatusCode);
    }

    [PostgresApiFact]
    public async Task Revoked_membership_denies_all_three_previews_despite_an_existing_login_cookie()
    {
        var tenant = await fixture.CreateTenant();
        var person = await fixture.CreateUser(tenant.Id);
        using var client = await fixture.Login(person);
        await fixture.InScope(async services => {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var membership = await db.Memberships.SingleAsync(x => x.TenantId == tenant.Id && x.UserId == person.UserId);
            membership.Active = false; membership.Revision++; await db.SaveChangesAsync();
        });
        foreach (var mode in new[] { "drainage", "wind", "snow" })
        {
            using var denied = await Send(client, HttpMethod.Post, "/api/v1/technical/roof/" + mode, Input(mode));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
    }
}
