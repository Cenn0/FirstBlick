using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FirstBlick.Api;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Integrations;
using static FirstBlick.Api.Tests.PostgresApiFixture;
using static FirstBlick.Api.Tests.ExchangeApiHarness;

namespace FirstBlick.Api.Tests;

[Collection("Official XML engine")]
public sealed class OfficialGaebEndpointTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static OfficialGaebBidRequest Request(Project project, byte[] source) =>
        new(project.Id, project.Revision, Convert.ToBase64String(source), OfficialGaebFixtures.Prices(source));

    [PostgresApiFact]
    public async Task Original_X83_is_validated_and_reexported_X84_with_immutable_audited_project_receipts()
    {
        var tenant = await fixture.CreateTenant();
        var user = await fixture.CreateUser(tenant.Id);
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Synthetic GAEB client" }, user.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id,
            Name = "Synthetic GAEB building", Address = "Synthetic location" }, user.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id,
            BuildingId = building.Id, Name = "Synthetic X83 export example" }, user.UserId);
        var original = OfficialGaebFixtures.Tender();
        await using var host = new ExchangeApiHarness(fixture);
        using var client = await host.Login(user);

        using var originalBody = new StringContent(Encoding.UTF8.GetString(original), Encoding.UTF8, "application/xml");
        using var validate = await client.PostAsync("/api/v1/integrations/v2/gaeb/validate", originalBody);
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        using (var json = JsonDocument.Parse(await validate.Content.ReadAsStringAsync()))
        {
            Assert.True(json.RootElement.GetProperty("validation").GetProperty("valid").GetBoolean());
            Assert.False(json.RootElement.GetProperty("originalBytesRetained").GetBoolean());
        }

        var request = Request(project, original);
        var preview = await Ok<OfficialGaebBidPreview>(Send(client, HttpMethod.Post,
            "/api/v1/integrations/v2/gaeb/bid-preview", request));
        Assert.True(preview.OriginalSchema.Valid);
        Assert.True(preview.ExportSchema.Valid);
        Assert.True(preview.CanExport);
        Assert.Equal("X83", preview.SourcePhase);
        Assert.Equal("X84", preview.OutputPhase);
        Assert.Equal(310m, preview.TotalNet);
        Assert.Equal(0, host.Store.PutCount);

        var approved = request with { PreviewDigest = preview.Digest };
        var key = Guid.NewGuid().ToString("N");
        var committed = await Ok<JsonElement>(Send(client, HttpMethod.Post,
            "/api/v1/integrations/v2/gaeb/bid-export", approved, key: key));
        Assert.Equal(3, host.Store.PutCount);
        var id = committed.GetProperty("id").GetGuid();
        Assert.False(committed.GetProperty("externallySubmitted").GetBoolean());
        var documents = committed.GetProperty("documents").EnumerateArray().ToArray();
        Assert.Equal(3, documents.Length);
        var sourceDocument = documents.Single(x => x.GetProperty("name").GetString()!.StartsWith("GAEB-Original-"));
        var bidDocument = documents.Single(x => x.GetProperty("name").GetString()!.StartsWith("GAEB-X84-"));
        var receiptDocument = documents.Single(x => x.GetProperty("name").GetString()!.StartsWith("GAEB-Beleg-"));
        Assert.Equal(original, await DownloadExport(client, id, sourceDocument));
        var bid = await DownloadExport(client, id, bidDocument);
        new GaebSchemaValidator(OfficialXmlValidationTests.Bundle()).RequireValid(bid);
        var xml = TradeXml.Load(bid).Document;
        Assert.Equal("FirstBlick", xml.Root!.Elements().First().Elements()
            .Single(x => x.Name.LocalName == "ProgSystem").Value);
        var receipt = await DownloadExport(client, id, receiptDocument);
        using var evidence = JsonDocument.Parse(receipt);
        Assert.Equal(preview.Digest, evidence.RootElement.GetProperty("digest").GetString());
        Assert.False(evidence.RootElement.GetProperty("recipientTransmissionPerformed").GetBoolean());

        var replay = await Ok<JsonElement>(Send(client, HttpMethod.Post,
            "/api/v1/integrations/v2/gaeb/bid-export", approved, key: key));
        Assert.Equal(id, replay.GetProperty("id").GetGuid());
        Assert.Equal(3, host.Store.PutCount);
        using var listed = await client.GetAsync($"/api/v1/integrations/v2/gaeb/exports/{id}");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var generalDocument = await client.GetAsync($"/api/v1/documents/{bidDocument.GetProperty("id").GetGuid()}/download");
        Assert.Equal(HttpStatusCode.Forbidden, generalDocument.StatusCode);
    }

    [PostgresApiFact]
    public async Task Changed_source_and_project_must_not_reuse_an_old_X84_review()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Synthetic" }, user.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id,
            CustomerId = customer.Id, Name = "Synthetic", Address = "Synthetic" }, user.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id,
            CustomerId = customer.Id, BuildingId = building.Id, Name = "Synthetic" }, user.UserId);
        await using var host = new ExchangeApiHarness(fixture);
        using var client = await host.Login(user);
        var original = OfficialGaebFixtures.Tender();
        var request = Request(project, original);
        var preview = await Ok<OfficialGaebBidPreview>(Send(client, HttpMethod.Post,
            "/api/v1/integrations/v2/gaeb/bid-preview", request));
        Assert.Equal("gaeb_bid_preview_changed", await Error(Send(client, HttpMethod.Post,
            "/api/v1/integrations/v2/gaeb/bid-export", request with { PreviewDigest = new('0', 64) })));
        Assert.Equal("gaeb_project_changed", await Error(Send(client, HttpMethod.Post,
            "/api/v1/integrations/v2/gaeb/bid-preview", request with { ExpectedProjectRevision = project.Revision + 1 })));
        var altered = request with { Mapping = request.Mapping with {
            Prices = request.Mapping.Prices.Select(x => x with { UnitPrice = 200m }).ToArray()
        }, PreviewDigest = preview.Digest };
        Assert.Equal("gaeb_bid_preview_changed", await Error(Send(client, HttpMethod.Post,
            "/api/v1/integrations/v2/gaeb/bid-export", altered)));
        Assert.Equal(0, host.Store.PutCount);
    }

    private static async Task<byte[]> DownloadExport(HttpClient client, Guid exchangeId, JsonElement document)
    {
        var id = document.GetProperty("id").GetGuid();
        using var response = await client.GetAsync($"/api/v1/integrations/v2/gaeb/exports/{exchangeId}/documents/{id}/download");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(document.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Equal(document.GetProperty("size").GetInt64(), bytes.LongLength);
        return bytes;
    }
}
