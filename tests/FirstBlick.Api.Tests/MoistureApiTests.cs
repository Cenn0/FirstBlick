using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Technical;
using FirstBlick.Infrastructure.Persistence;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class MoistureApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static MoistureScreeningInput Input() => new(20, 0, 65, 80, .13, .04,
        [new("Synthetic insulation", .2, .04, 10, "Explicit test data"),
         new("Synthetic outer layer", .01, .2, 50, "Explicit test data")]);

    [PostgresApiFact]
    public async Task Preview_and_retained_technical_report_both_refuse_normative_claims()
    {
        var tenant = await fixture.CreateTenant();
        var director = await fixture.CreateUser(tenant.Id);
        var client = await fixture.Login(director);
        using var preview = await Send(client, HttpMethod.Post, "/api/v1/technical/moisture/screening", Input());
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var payload = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.False(payload.RootElement.GetProperty("provenance").GetProperty("isNormative").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("requiresDetailedAssessment").GetBoolean());
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Moisture synthetic customer" }, director.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id,
            Name = "Synthetic roof", Address = "Synthetic" }, director.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id,
            BuildingId = building.Id, Name = "Synthetic moisture review" }, director.UserId);
        var request = new { projectId = project.Id, roofRevisionId = (Guid?)null, title = "Screening, not certification",
            method = "moisture.screening", input = Input() };
        using var recorded = await Send(client, HttpMethod.Post, "/api/v1/technical/runs", request);
        Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);
        var technical = AppJson.Read<TechnicalRecord>(await recorded.Content.ReadAsStringAsync());
        Assert.False(technical.IsNormative);
        Assert.Equal("moisture.screening", technical.Method);
        technical.Validate();
        Assert.Equal(technical.Digest, (await fixture.Read<TechnicalRecord>(tenant.Id, technical.Id)).Digest);
    }

    [PostgresApiFact]
    public async Task Worker_cannot_use_technical_screening_and_invalid_material_is_rejected()
    {
        var tenant = await fixture.CreateTenant();
        var worker = await fixture.Login(await fixture.CreateUser(tenant.Id, FirstBlick.Core.Security.BusinessRole.Worker));
        using var denied = await Send(worker, HttpMethod.Post, "/api/v1/technical/moisture/screening", Input());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var manager = await fixture.Login(await fixture.CreateUser(tenant.Id));
        using var bad = await Send(manager, HttpMethod.Post, "/api/v1/technical/moisture/screening",
            Input() with { Layers = [new("Bad", .2, .04, 0, "Source")] });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
