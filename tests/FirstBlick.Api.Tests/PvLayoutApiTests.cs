using System.Net;
using System.Text.Json;
using FirstBlick.Api;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Core.Technical;
using FirstBlick.Infrastructure.Persistence;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class PvLayoutApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static PvLayoutInput Input() => new(3.5, 2.1, 1, 1.6, 420, 0, 0, 0, 0,
        PvModuleOrientation.MostModules, [],
        "Synthetic measured plane", "Synthetic module datasheet");

    [PostgresApiFact]
    public async Task Authorized_calculation_and_immutable_technical_record_share_the_same_non_normative_geometry()
    {
        var tenant = await fixture.CreateTenant();
        var director = await fixture.CreateUser(tenant.Id);
        using var client = await fixture.Login(director);
        using var preview = await Send(client, HttpMethod.Post, "/api/v1/technical/roof/pv-layout", Input());
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var json = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal("Landscape", json.RootElement.GetProperty("selectedOrientation").GetString());
        Assert.Equal(4, json.RootElement.GetProperty("installedModules").GetInt32());
        Assert.False(json.RootElement.GetProperty("structureOrElectricalDesignApproved").GetBoolean());
        Assert.False(json.RootElement.GetProperty("provenance").GetProperty("isNormative").GetBoolean());

        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Synthetic PV customer" }, director.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id,
            Name = "Synthetic PV building", Address = "Synthetic" }, director.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id,
            BuildingId = building.Id, Name = "Synthetic PV roof" }, director.UserId);
        using var saved = await Send(client, HttpMethod.Post, "/api/v1/technical/runs", new {
            projectId = project.Id, roofRevisionId = (Guid?)null, title = "Geometry only",
            method = "roof.pv-layout", input = Input()
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var stored = AppJson.Read<TechnicalRecord>(await saved.Content.ReadAsStringAsync());
        stored.Validate();
        Assert.False(stored.IsNormative);
        Assert.Equal(4, stored.Result.GetProperty("installedModules").GetInt32());
        Assert.Equal(stored.Digest, (await fixture.Read<TechnicalRecord>(tenant.Id, stored.Id)).Digest);
    }

    [PostgresApiFact]
    public async Task Unsupported_obstruction_and_worker_access_are_rejected()
    {
        var tenant = await fixture.CreateTenant();
        var director = await fixture.CreateUser(tenant.Id);
        using var client = await fixture.Login(director);
        using var invalid = await Send(client, HttpMethod.Post, "/api/v1/technical/roof/pv-layout",
            Input() with { Exclusions = [new("Outside plane", 3.3, 0, 1, 1)] });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var worker = await fixture.Login(await fixture.CreateUser(tenant.Id, BusinessRole.Worker));
        using var forbidden = await Send(worker, HttpMethod.Post, "/api/v1/technical/roof/pv-layout", Input());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
}
