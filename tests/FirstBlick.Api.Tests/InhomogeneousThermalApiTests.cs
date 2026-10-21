using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using FirstBlick.Core.Security;
using FirstBlick.Core.Technical;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class InhomogeneousThermalApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static InhomogeneousThermalInput Input() => new(20, 0, .13, .04,
        "Synthetic measured framing fractions and explicitly supplied conductivities", true,
        [
            new("Timber", .15, [
                new("Lining", .02, .2, "Synthetic"),
                new("Rafter", .2, .12, "Synthetic"),
                new("Sheathing", .02, .2, "Synthetic")]),
            new("Insulation", .85, [
                new("Lining", .02, .2, "Synthetic"),
                new("Filling", .2, .04, "Synthetic"),
                new("Sheathing", .02, .2, "Synthetic")])
        ]);

    [PostgresApiFact]
    public async Task Authenticated_preview_and_project_evidence_are_replayable_and_non_normative()
    {
        var tenant = await fixture.CreateTenant(); var director = await fixture.CreateUser(tenant.Id);
        using var client = await fixture.Login(director);
        using var response = await Send(client, HttpMethod.Post, "/api/v1/technical/thermal/inhomogeneous", Input());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(.23453042708102192, json.RootElement.GetProperty("uValueWm2K").GetDouble(), 11);
        Assert.False(json.RootElement.GetProperty("constructionComplianceAssessed").GetBoolean());
        Assert.False(json.RootElement.GetProperty("metalBridgingModelled").GetBoolean());
        Assert.False(json.RootElement.GetProperty("provenance").GetProperty("isNormative").GetBoolean());

        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Synthetic customer" }, director.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id, Name = "Synthetic building", Address = "Synthetic" }, director.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id,
            BuildingId = building.Id, Name = "Synthetic rafter roof" }, director.UserId);
        using var saved = await Send(client, HttpMethod.Post, "/api/v1/technical/runs",
            new { projectId = project.Id, roofRevisionId = (Guid?)null,
                title = "Unapproved resistance estimate", method = "thermal.inhomogeneous", input = Input() });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var record = AppJson.Read<TechnicalRecord>(await saved.Content.ReadAsStringAsync());
        record.Validate();
        Assert.False(record.IsNormative);
        Assert.Equal(record.Digest, (await fixture.Read<TechnicalRecord>(tenant.Id, record.Id)).Digest);
        var worker = await fixture.Login(await fixture.CreateUser(tenant.Id, BusinessRole.Worker));
        using var forbidden = await Send(worker, HttpMethod.Post, "/api/v1/technical/thermal/inhomogeneous", Input());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [PostgresApiFact]
    public async Task Outside_boundaries_or_uncertain_metal_bridges_are_rejected_without_saving()
    {
        var tenant = await fixture.CreateTenant();
        using var client = await fixture.Login(await fixture.CreateUser(tenant.Id));
        using var invalid = await Send(client, HttpMethod.Post, "/api/v1/technical/thermal/inhomogeneous",
            Input() with { NoMetalBridgingConfirmed = false });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var input = Input();
        using var invalidFractions = await Send(client, HttpMethod.Post, "/api/v1/technical/thermal/inhomogeneous",
            input with { Sections = [input.Sections[0] with { AreaFraction = .5 }, input.Sections[1]] });
        Assert.Equal(HttpStatusCode.BadRequest, invalidFractions.StatusCode);
    }
}
