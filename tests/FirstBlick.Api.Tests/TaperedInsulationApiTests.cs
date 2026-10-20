using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Technical;
using FirstBlick.Infrastructure.Persistence;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class TaperedInsulationApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static TaperedInsulationInput Input()
        => new(12, 10, .08, 0, 2, "Synthetic test plan");

    [PostgresApiFact]
    public async Task Preview_and_tamper_checked_persisted_result_remain_non_normative()
    {
        var tenant = await fixture.CreateTenant();
        var director = await fixture.CreateUser(tenant.Id);
        var client = await fixture.Login(director);
        using var preview = await Send(client, HttpMethod.Post,
            "/api/v1/technical/roof/tapered-insulation", Input());
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var json = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal(21.6, json.RootElement.GetProperty("calculatedVolumeM3").GetDouble(), 8);
        Assert.False(json.RootElement.GetProperty("provenance").GetProperty("isNormative").GetBoolean());

        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Synthetic customer" }, director.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id,
            Name = "Synthetic test roof", Address = "Synthetic site" }, director.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id,
            BuildingId = building.Id, Name = "Synthetic tapered insulation review" }, director.UserId);
        var request = new { projectId = project.Id, roofRevisionId = (Guid?)null,
            title = "Tapered insulation quantity aid only", method = "roof.tapered-insulation", input = Input() };
        using var saved = await Send(client, HttpMethod.Post, "/api/v1/technical/runs", request);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var record = AppJson.Read<TechnicalRecord>(await saved.Content.ReadAsStringAsync());
        Assert.Equal("roof.tapered-insulation", record.Method);
        Assert.False(record.IsNormative);
        record.Validate();
        Assert.Equal(record.Digest, (await fixture.Read<TechnicalRecord>(tenant.Id, record.Id)).Digest);
    }

    [PostgresApiFact]
    public async Task Staff_without_technical_privileges_cannot_calculate_and_bad_input_is_rejected()
    {
        var tenant = await fixture.CreateTenant();
        var worker = await fixture.Login(await fixture.CreateUser(tenant.Id, FirstBlick.Core.Security.BusinessRole.Worker));
        using var denied = await Send(worker, HttpMethod.Post,
            "/api/v1/technical/roof/tapered-insulation", Input());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var manager = await fixture.Login(await fixture.CreateUser(tenant.Id));
        using var invalid = await Send(manager, HttpMethod.Post,
            "/api/v1/technical/roof/tapered-insulation", Input() with { WidthGradientPercent = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
