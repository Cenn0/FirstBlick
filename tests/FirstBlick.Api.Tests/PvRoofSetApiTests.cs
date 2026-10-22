using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Core.Technical;
using FirstBlick.Infrastructure.Persistence;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class PvRoofSetApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static PvLayoutInput Plane(double width, double height) => new(
        width, height, 1, 1, 420, 0, 0, 0, 0, PvModuleOrientation.Portrait, [],
        "Synthetic measured roof plane", "Synthetic module source");

    private static PvRoofSetInput Input() => new("Synthetic roof set", [
        new("South", Plane(4, 2)), new("North", Plane(2, 2))
    ]);

    [PostgresApiFact]
    public async Task Authenticated_multi_plane_preview_can_be_saved_and_recomputed_as_an_immutable_project_record()
    {
        var tenant = await fixture.CreateTenant();
        var director = await fixture.CreateUser(tenant.Id);
        using var client = await fixture.Login(director);
        using var preview = await Send(client, HttpMethod.Post, "/api/v1/technical/roof/pv-multi-layout", Input());
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var json = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal(12, json.RootElement.GetProperty("installedModules").GetInt32());
        Assert.Equal(5040, json.RootElement.GetProperty("nominalDcPowerWp").GetDouble());
        Assert.Equal(2, json.RootElement.GetProperty("planes").GetArrayLength());
        Assert.False(json.RootElement.GetProperty("structureOrElectricalDesignApproved").GetBoolean());
        Assert.False(json.RootElement.GetProperty("provenance").GetProperty("isNormative").GetBoolean());

        var customer = await fixture.Save(new Customer {
            TenantId = tenant.Id, Name = "Synthetic multi-PV customer" }, director.UserId);
        var building = await fixture.Save(new Building {
            TenantId = tenant.Id, CustomerId = customer.Id, Name = "Synthetic multi-PV building",
            Address = "Synthetic" }, director.UserId);
        var project = await fixture.Save(new Project {
            TenantId = tenant.Id, CustomerId = customer.Id, BuildingId = building.Id,
            Name = "Synthetic two-side roof" }, director.UserId);
        using var saved = await Send(client, HttpMethod.Post, "/api/v1/technical/runs", new {
            projectId = project.Id, roofRevisionId = (Guid?)null,
            title = "Two measured roof planes", method = "roof.pv-multi-layout", input = Input()
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var record = AppJson.Read<TechnicalRecord>(await saved.Content.ReadAsStringAsync());
        record.Validate();
        Assert.False(record.IsNormative);
        Assert.Equal("roof.pv-multi-layout", record.Method);
        Assert.Equal(12, record.Result.GetProperty("installedModules").GetInt32());
        Assert.Equal("South", record.Result.GetProperty("planes")[0].GetProperty("name").GetString());
        Assert.Equal(record.Digest, (await fixture.Read<TechnicalRecord>(tenant.Id, record.Id)).Digest);
    }

    [PostgresApiFact]
    public async Task Duplicated_plane_names_invalid_geometry_and_worker_access_are_rejected()
    {
        var tenant = await fixture.CreateTenant();
        var director = await fixture.CreateUser(tenant.Id);
        using var client = await fixture.Login(director);
        using var duplicate = await Send(client, HttpMethod.Post, "/api/v1/technical/roof/pv-multi-layout",
            Input() with { Planes = [new("South", Plane(4, 2)), new(" SOUTH ", Plane(2, 2))] });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using var invalid = await Send(client, HttpMethod.Post, "/api/v1/technical/roof/pv-multi-layout",
            Input() with { Planes = [new("South", Plane(4, 2) with {
                Exclusions = [new("Outside", 3.5, 0, 1, 1)] })] });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var worker = await fixture.Login(await fixture.CreateUser(tenant.Id, BusinessRole.Worker));
        using var denied = await Send(worker, HttpMethod.Post, "/api/v1/technical/roof/pv-multi-layout", Input());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
}
