using System.Net;
using System.Text;
using FirstBlick.Api;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class GaebCatalogBoundaryTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private const string PreviewPath = "/api/v1/integrations/v1/gaeb/catalog-preview";
    private const string ImportPath = "/api/v1/integrations/v1/gaeb/catalog-import";
    private const string Source = """
        <GAEB xmlns="http://www.gaeb.de/GAEB_DA_XML/DA83/3.3">
          <GAEBInfo><Version>3.3</Version><VersDate>2021-05</VersDate></GAEBInfo>
          <PrjInfo><Cur>EUR</Cur></PrjInfo><Award><DP>83</DP><BoQ><BoQInfo><LblBoQ>Synthetic boundary reference</LblBoQ></BoQInfo>
          <BoQBody><BoQCtgy RNoPart="01"><BoQBody><Itemlist>
            <Item ID="i-1" RNoPart="001"><Qty>10</Qty><QU>m²</QU><Description><CompleteText><OutlineText><OutlTxt><TextOutlTxt><span>Synthetic first work</span></TextOutlTxt></OutlTxt></OutlineText></CompleteText></Description></Item>
            <Item ID="i-2" RNoPart="002"><Qty>2</Qty><QU>m</QU><Description><CompleteText><OutlineText><OutlTxt><TextOutlTxt><span>Synthetic second work</span></TextOutlTxt></OutlTxt></OutlineText></CompleteText></Description></Item>
          </Itemlist></BoQBody></BoQCtgy></BoQBody></BoQ></Award>
        </GAEB>
        """;
    private static GaebCatalogSelection Selection(string position = "01.001") => new(position, 12.5m, 1.2m, 40, 10, 19, "Independent synthetic cost evidence");
    private static GaebCatalogRequest Request(params string[] positions) => new(Convert.ToBase64String(Encoding.UTF8.GetBytes(Source)), positions.Select(Selection).ToArray());
    private async Task<int> Count(Guid tenantId) => await fixture.InScope(async sp => await sp.GetRequiredService<FirstBlickDbContext>().Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && x.Kind == "catalog-items"));
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> action)
    {
        using var response = await action; var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {json}"); return AppJson.Read<T>(json);
    }

    [PostgresApiFact]
    public async Task Identical_bytes_need_a_separate_review_in_each_business()
    {
        var a = await fixture.CreateTenant(); var b = await fixture.CreateTenant();
        var clientA = await fixture.Login(await fixture.CreateUser(a.Id));
        var clientB = await fixture.Login(await fixture.CreateUser(b.Id));
        var request = Request("01.001");
        var previewA = await Ok<GaebCatalogPreview>(Send(clientA, HttpMethod.Post, PreviewPath, request));
        var previewB = await Ok<GaebCatalogPreview>(Send(clientB, HttpMethod.Post, PreviewPath, request));
        Assert.Equal(previewA.SourceSha256, previewB.SourceSha256);
        Assert.NotEqual(previewA.Digest, previewB.Digest);
        using var wrongBusiness = await Send(clientB, HttpMethod.Post, ImportPath, request with { PreviewDigest = previewA.Digest });
        Assert.Equal(HttpStatusCode.Conflict, wrongBusiness.StatusCode);
        Assert.Equal(0, await Count(a.Id)); Assert.Equal(0, await Count(b.Id));
        var accepted = await Ok<GaebCatalogImportReceipt>(Send(clientB, HttpMethod.Post, ImportPath, request with { PreviewDigest = previewB.Digest }));
        Assert.Single(accepted.Items); Assert.Equal(1, await Count(b.Id)); Assert.Equal(0, await Count(a.Id));
    }

    [PostgresApiFact]
    public async Task Every_cost_field_must_be_supplied_in_JSON_while_explicit_zero_remains_valid()
    {
        var tenant = await fixture.CreateTenant(); var client = await fixture.Login(await fixture.CreateUser(tenant.Id));
        var explicitFields = new Dictionary<string, object?>
        {
            ["positionKey"] = "01.001", ["unitMaterialCost"] = "0", ["unitLaborHours"] = "0", ["hourlyRate"] = "0",
            ["markupPercent"] = "0", ["vatPercent"] = "0", ["priceEvidence"] = "Reviewed synthetic zero-cost item"
        };
        foreach (var missing in new[] { "unitMaterialCost", "unitLaborHours", "hourlyRate", "markupPercent", "vatPercent", "priceEvidence", "positionKey" })
        {
            var incomplete = new Dictionary<string, object?>(explicitFields); incomplete.Remove(missing);
            using var denied = await Send(client, HttpMethod.Post, PreviewPath, new { sourceBase64 = Request("01.001").SourceBase64, selections = new[] { incomplete } });
            Assert.True(denied.StatusCode == HttpStatusCode.BadRequest, $"Missing {missing}: HTTP {(int)denied.StatusCode}");
        }
        var complete = await Ok<GaebCatalogPreview>(Send(client, HttpMethod.Post, PreviewPath, new { sourceBase64 = Request("01.001").SourceBase64, selections = new[] { explicitFields } }));
        var candidate = Assert.Single(complete.Candidates); Assert.Equal(0m, candidate.UnitMaterialCost); Assert.Equal(0m, candidate.HourlyRate);
        Assert.Equal(0, await Count(tenant.Id));
    }

    [PostgresApiFact]
    public async Task Null_or_empty_position_keys_are_rejected_as_bad_input_not_partial_imports()
    {
        var tenant = await fixture.CreateTenant(); var client = await fixture.Login(await fixture.CreateUser(tenant.Id));
        foreach (var key in new string?[] { null, "", " ", new('x', 241) })
        {
            var request = Request("01.001") with { Selections = [Selection() with { PositionKey = key! }] };
            using var denied = await Send(client, HttpMethod.Post, PreviewPath, request);
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        }
        using var nullItem = await Send(client, HttpMethod.Post, PreviewPath, Request("01.001") with { Selections = [null!] });
        Assert.Equal(HttpStatusCode.BadRequest, nullItem.StatusCode); Assert.Equal(0, await Count(tenant.Id));
    }

    [PostgresApiFact]
    public async Task A_duplicate_later_in_the_batch_rolls_back_the_whole_new_import()
    {
        var tenant = await fixture.CreateTenant(); var client = await fixture.Login(await fixture.CreateUser(tenant.Id));
        var already = Request("01.002");
        var oldPreview = await Ok<GaebCatalogPreview>(Send(client, HttpMethod.Post, PreviewPath, already));
        var oldReceipt = await Ok<GaebCatalogImportReceipt>(Send(client, HttpMethod.Post, ImportPath, already with { PreviewDigest = oldPreview.Digest }));
        var batch = Request("01.001", "01.002");
        var preview = await Ok<GaebCatalogPreview>(Send(client, HttpMethod.Post, PreviewPath, batch));
        using var duplicate = await Send(client, HttpMethod.Post, ImportPath, batch with { PreviewDigest = preview.Digest });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(1, await Count(tenant.Id));
        var retained = await fixture.Read<CatalogItem>(tenant.Id, Assert.Single(oldReceipt.Items).CatalogItemId);
        Assert.Equal("01.002", retained.GaebOrigin!.PositionKey);
        var firstOnly = Request("01.001");
        var firstPreview = await Ok<GaebCatalogPreview>(Send(client, HttpMethod.Post, PreviewPath, firstOnly));
        var firstReceipt = await Ok<GaebCatalogImportReceipt>(Send(client, HttpMethod.Post, ImportPath, firstOnly with { PreviewDigest = firstPreview.Digest }));
        Assert.Single(firstReceipt.Items); Assert.Equal(2, await Count(tenant.Id));
    }
}
