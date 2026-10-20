using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Core.Technical;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class AdvancedTechnicalApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static TaperedBoardLayoutInput Boards() => new(2.5, 1, .08, 2,
        TaperedFlowAxis.AlongLength, true, "Synthetic geometry", "Reviewed continuous low edge",
        new("Synthetic supplier", "Uncertified example", "catalog-1", "Synthetic nominal dimensions",
            1, .5, [new("A", .08, .10, 2, 10), new("B", .10, .12, 2, 20), new("C", .12, .14, 2, 30)]));

    private static Iso13788BalanceInput Moisture() => new(2026, 1,
        MoistureHeatFlow.Upward, MoistureExteriorBoundary.OutsideAir,
        MoistureClimateTreatment.WallAir, false,
        [new("Synthetic insulation", 2, 1, MoistureMaterialKind.HomogeneousLayer, "Synthetic numerical input"),
         new("Synthetic weather membrane", .1, 30, MoistureMaterialKind.Membrane, "Synthetic numerical input")],
        Enumerable.Range(1, 12).Select(m => new MoistureDesignMonth(m, 20, m <= 3 ? 0 : 12, 45, 75)).ToArray(),
        new(true, true, true, true, true, true, "Not an approved construction", "Explicit synthetic climate series"));

    [PostgresApiFact]
    public async Task Supplier_board_layout_covers_area_and_can_be_retained_only_as_a_non_normative_record()
    {
        var tenant = await fixture.CreateTenant(); var director = await fixture.CreateUser(tenant.Id);
        using var client = await fixture.Login(director);
        using var response = await Send(client, HttpMethod.Post, "/api/v1/technical/roof/tapered-board-layout", Boards());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(6, body.RootElement.GetProperty("installedBoards").GetInt32());
        Assert.Equal(.2625, body.RootElement.GetProperty("installedVolumeM3").GetDouble(), 8);
        Assert.False(body.RootElement.GetProperty("catalogIndependentlyVerified").GetBoolean());
        Assert.False(body.RootElement.GetProperty("provenance").GetProperty("isNormative").GetBoolean());
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Synthetic customer" }, director.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id,
            Name = "Synthetic building", Address = "Synthetic" }, director.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id,
            BuildingId = building.Id, Name = "Synthetic installation plan" }, director.UserId);
        using var saved = await Send(client, HttpMethod.Post, "/api/v1/technical/runs",
            new { projectId = project.Id, roofRevisionId = (Guid?)null, title = "Uncertified supplier geometry",
                method = "roof.tapered-board-layout", input = Boards() });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var record = AppJson.Read<TechnicalRecord>(await saved.Content.ReadAsStringAsync());
        Assert.False(record.IsNormative);
        record.Validate();
        Assert.Equal(record.Digest, (await fixture.Read<TechnicalRecord>(tenant.Id, record.Id)).Digest);

        var worker = await fixture.Login(await fixture.CreateUser(tenant.Id, BusinessRole.Worker));
        using var forbidden = await Send(worker, HttpMethod.Post, "/api/v1/technical/roof/tapered-board-layout", Boards());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [PostgresApiFact]
    public async Task Monthly_iso_water_balance_and_drying_have_explicit_non_approval_flags()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        using var client = await fixture.Login(user);
        using var balance = await Send(client, HttpMethod.Post, "/api/v1/technical/moisture/iso13788/balance", Moisture());
        Assert.Equal(HttpStatusCode.OK, balance.StatusCode);
        using var json = JsonDocument.Parse(await balance.Content.ReadAsStringAsync());
        Assert.Equal(12, json.RootElement.GetProperty("months").GetArrayLength());
        Assert.True(json.RootElement.GetProperty("standardProcedure").GetProperty("implementsSpecifiedProcedure").GetBoolean());
        Assert.False(json.RootElement.GetProperty("standardProcedure").GetProperty("din4108Part3Assessment").GetBoolean());
        Assert.False(json.RootElement.GetProperty("provenance").GetProperty("isNormative").GetBoolean());

        using var drying = await Send(client, HttpMethod.Post, "/api/v1/technical/moisture/iso13788/drying",
            new Iso13788DryingInput(Moisture(), 0, 1, "Synthetic clause-7 excess water assessment only"));
        Assert.Equal(HttpStatusCode.OK, drying.StatusCode);
        using var dryingJson = JsonDocument.Parse(await drying.Content.ReadAsStringAsync());
        Assert.Equal(1, dryingJson.RootElement.GetProperty("initialExcessMassKgM2").GetDouble());
        Assert.False(dryingJson.RootElement.GetProperty("standardProcedure").GetProperty("constructionComplianceAssessed").GetBoolean());

        using var invalid = await Send(client, HttpMethod.Post, "/api/v1/technical/moisture/iso13788/balance",
            Moisture() with { Months = Moisture().Months.Take(11).ToArray() });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
