using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FirstBlick.Api;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class GaebCatalogApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private const string Source = """
        <GAEB xmlns="http://www.gaeb.de/GAEB_DA_XML/DA83/3.3">
          <GAEBInfo><Version>3.3</Version><VersDate>2021-05</VersDate></GAEBInfo>
          <PrjInfo><Cur>EUR</Cur></PrjInfo>
          <Award><DP>83</DP><BoQ><BoQInfo><LblBoQ>Test tender</LblBoQ></BoQInfo>
            <BoQBody><BoQCtgy RNoPart="01"><BoQBody><Itemlist>
              <Item ID="g-1" RNoPart="001"><Qty>10</Qty><QU>m²</QU><Description><CompleteText><OutlineText><OutlTxt><TextOutlTxt><span>Roof sealing</span></TextOutlTxt></OutlTxt></OutlineText></CompleteText></Description></Item>
            </Itemlist></BoQBody></BoQCtgy></BoQBody></BoQ></Award>
        </GAEB>
        """;

    private static GaebCatalogRequest Request() => new(Convert.ToBase64String(Encoding.UTF8.GetBytes(Source)),
        [new("01.001", 12.50m, 1.2m, 40m, 10m, 19m, "Independently reviewed supplier and work estimates")]);

    [PostgresApiFact]
    public async Task Real_postgresql_transfer_requires_matching_review_and_preserves_immutable_gaeb_origin()
    {
        var tenant = await fixture.CreateTenant();
        var user = await fixture.CreateUser(tenant.Id);
        var client = await fixture.Login(user);
        var request = Request();
        using var raw = await Raw(client, "/api/v1/integrations/v1/gaeb/preview", Source);
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        var preview = await Ok<GaebCatalogPreview>(Send(client, HttpMethod.Post,
            "/api/v1/integrations/v1/gaeb/catalog-preview", request));
        Assert.False(preview.SchemaValidated);
        Assert.Equal("X83", preview.Phase);
        Assert.Equal(12.50m, Assert.Single(preview.Candidates).UnitMaterialCost);
        var route = "/api/v1/integrations/v1/gaeb/catalog-import";
        using var stale = await Send(client, HttpMethod.Post, route, request with { PreviewDigest = new string('a', 64) });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var key = Guid.NewGuid().ToString("N");
        var imported = await Ok<GaebCatalogImportReceipt>(Send(client, HttpMethod.Post,
            route, request with { PreviewDigest = preview.Digest }, key: key));
        var reference = Assert.Single(imported.Items);
        var item = await fixture.Read<CatalogItem>(tenant.Id, reference.CatalogItemId);
        Assert.Equal("Roof sealing", item.Name);
        Assert.Equal("m²", item.Unit);
        Assert.Equal(12.50m, item.UnitMaterialCost);
        Assert.Equal("01.001", item.GaebOrigin!.PositionKey);
        Assert.Equal(preview.SourceSha256, item.GaebOrigin.SourceSha256);
        using var replay = await Send(client, HttpMethod.Post, route,
            request with { PreviewDigest = preview.Digest }, key: key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        using var duplicate = await Send(client, HttpMethod.Post, route,
            request with { PreviewDigest = preview.Digest });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        item.GaebOrigin = null;
        using var tamper = await Send(client, HttpMethod.Put,
            $"/api/v1/catalog-items/{item.Id}", item, item.Revision);
        Assert.Equal(HttpStatusCode.BadRequest, tamper.StatusCode);
        await fixture.InScope(async sp =>
        {
            var db = sp.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(1, await db.Entities.IgnoreQueryFilters().CountAsync(x =>
                x.TenantId == tenant.Id && x.Kind == "catalog-items"));
        });
    }

    [PostgresApiFact]
    public async Task New_catalog_mapping_does_not_accept_unreviewed_originals_or_other_roles()
    {
        var tenant = await fixture.CreateTenant();
        var manager = await fixture.Login(await fixture.CreateUser(tenant.Id));
        var missing = Request() with
        {
            Selections = [new GaebCatalogSelection("missing", 12, 1, 30, 0, 19, "Independent reference")]
        };
        using var error = await Send(manager, HttpMethod.Post,
            "/api/v1/integrations/v1/gaeb/catalog-preview", missing);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        var worker = await fixture.Login(await fixture.CreateUser(tenant.Id, BusinessRole.Worker));
        using var denied = await Send(worker, HttpMethod.Post,
            "/api/v1/integrations/v1/gaeb/catalog-preview", Request());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> action)
    {
        using var response = await action;
        var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, "HTTP " + (int)response.StatusCode + ": " + json);
        return AppJson.Read<T>(json);
    }

    private static Task<HttpResponseMessage> Raw(HttpClient client, string path, string content)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path) { Content =
            new ByteArrayContent(Encoding.UTF8.GetBytes(content)) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = "utf-8" };
        return client.SendAsync(message);
    }
}
