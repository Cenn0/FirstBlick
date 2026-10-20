using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FirstBlick.Api;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using FirstBlick.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class TradeSourceApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private const string Root = "/api/v1/integrations/v1/trade-sources/";
    private const string Gaeb = """
        <GAEB xmlns="http://www.gaeb.de/GAEB_DA_XML/DA83/3.3">
          <!-- Preserve this original comment and its whitespace; never round-trip XML. -->
          <GAEBInfo><Version>3.3</Version><VersDate>2021-05</VersDate></GAEBInfo>
          <PrjInfo><Cur>EUR</Cur></PrjInfo>
          <Award><DP>83</DP><BoQ><BoQInfo><LblBoQ>Synthetic original tender</LblBoQ></BoQInfo>
            <BoQBody><BoQCtgy RNoPart="01"><BoQBody><Itemlist>
              <Item ID="g-1" RNoPart="001"><Qty>10</Qty><QU>m²</QU><Description><CompleteText><OutlineText><OutlTxt><TextOutlTxt><span>Roof sealing</span></TextOutlTxt></OutlTxt></OutlineText></CompleteText></Description></Item>
            </Itemlist></BoQBody></BoQCtgy></BoQBody></BoQ></Award>
        </GAEB>
        """;
    private const string Invoice = """
        <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"
          xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
          xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
          <cbc:ID>SOURCE-TEST-1</cbc:ID><cbc:IssueDate>2026-10-20</cbc:IssueDate><cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
          <cac:AccountingSupplierParty><cac:Party><cac:PartyName><cbc:Name>Synthetic supplier</cbc:Name></cac:PartyName></cac:Party></cac:AccountingSupplierParty>
          <cac:AccountingCustomerParty><cac:Party><cac:PartyName><cbc:Name>Synthetic buyer</cbc:Name></cac:PartyName></cac:Party></cac:AccountingCustomerParty>
          <cac:LegalMonetaryTotal><cbc:TaxExclusiveAmount>50.00</cbc:TaxExclusiveAmount><cbc:TaxInclusiveAmount>59.50</cbc:TaxInclusiveAmount><cbc:PayableAmount>59.50</cbc:PayableAmount></cac:LegalMonetaryTotal>
          <cac:InvoiceLine><cbc:ID>1</cbc:ID><cbc:InvoicedQuantity unitCode="C62">1</cbc:InvoicedQuantity><cbc:LineExtensionAmount>50.00</cbc:LineExtensionAmount><cac:Item><cbc:Name>Repair</cbc:Name></cac:Item></cac:InvoiceLine>
        </Invoice>
        """;

    private static byte[] Bytes(string format) => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(format == "gaeb" ? Gaeb : Invoice)).ToArray();
    private static CaptureTradeSourceRequest Body(string format, byte[]? bytes = null) => new(
        format == "gaeb" ? "original.x83" : "invoice.xml", Convert.ToBase64String(bytes ?? Bytes(format)),
        Convert.ToHexStringLower(SHA256.HashData(bytes ?? Bytes(format))));

    [PostgresApiFact]
    public async Task Both_formats_retain_original_BOM_comments_and_whitespace_without_posting_business_records()
    {
        var tenant = await fixture.CreateTenant(); var person = await fixture.CreateUser(tenant.Id);
        var objects = new VersionedTestStore(); await using var factory = WithStore(objects); using var client = await Login(factory, person);
        foreach (var format in new[] { "gaeb", "e-invoice" })
        {
            var receipt = await Ok<TradeSourceReceipt>(Send(client, HttpMethod.Post, Root + format, Body(format)));
            Assert.True(receipt.ContentVerified); Assert.False(receipt.AlreadyStored);
            Assert.Equal(Body(format).ExpectedSha256, receipt.Source.Sha256);
            var detail = await Ok<JsonElement>(client.GetAsync(Root + format + "/" + receipt.Source.Id));
            Assert.True(detail.GetProperty("contentVerified").GetBoolean());
            Assert.Equal(receipt.Source.Sha256, detail.GetProperty("preview").GetProperty("sourceSha256").GetString());
            Assert.Empty(detail.GetProperty("catalogItems").EnumerateArray());
            using var download = await client.GetAsync(Root + format + "/" + receipt.Source.Id + "/original");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(Bytes(format), await download.Content.ReadAsByteArrayAsync());
            Assert.Equal("application/octet-stream", download.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal(receipt.Source.Sha256, download.Headers.GetValues("X-Content-SHA256").Single());
            if (format == "e-invoice")
            {
                Assert.False(detail.GetProperty("preview").GetProperty("externalValidationPerformed").GetBoolean());
                Assert.False(detail.GetProperty("preview").GetProperty("mayPostAutomatically").GetBoolean());
            }
        }
        await fixture.InScope(async sp =>
        {
            var db = sp.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(2, await db.Documents.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant.Id));
            Assert.False(await db.Entities.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenant.Id && (x.Kind == "supplier-invoices" || x.Kind == "catalog-items")));
            Assert.Equal(2, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant.Id && x.Action == "trade.source.capture"));
        });
    }

    [PostgresApiFact]
    public async Task New_keys_parallel_users_and_idempotency_replay_do_not_duplicate_originals()
    {
        var tenant = await fixture.CreateTenant(); var a = await fixture.CreateUser(tenant.Id); var b = await fixture.CreateUser(tenant.Id);
        var objects = new VersionedTestStore(); await using var factory = WithStore(objects);
        using var first = await Login(factory, a); using var second = await Login(factory, b);
        var key = Guid.NewGuid().ToString("N");
        var pending = await Task.WhenAll(
            Ok<TradeSourceReceipt>(Send(first, HttpMethod.Post, Root + "gaeb", Body("gaeb"), key: key)),
            Ok<TradeSourceReceipt>(Send(second, HttpMethod.Post, Root + "gaeb", Body("gaeb") with { FileName = "renamed.xml" })));
        Assert.Equal(pending[0].Source.Id, pending[1].Source.Id);
        Assert.Single(pending, x => !x.AlreadyStored); Assert.Single(pending, x => x.AlreadyStored);
        using var replay = await Send(first, HttpMethod.Post, Root + "gaeb", Body("gaeb"), key: key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(1, objects.PutCount);
        var list = await Ok<JsonElement>(first.GetAsync(Root + "gaeb?pageSize=1"));
        Assert.Equal(1, list.GetProperty("total").GetInt32()); Assert.False(list.GetProperty("contentVerified").GetBoolean());
        Assert.Single(list.GetProperty("items").EnumerateArray());
    }

    [PostgresApiFact]
    public async Task Identical_bytes_in_different_businesses_are_separate_and_foreign_source_ids_are_inaccessible()
    {
        var a = await fixture.CreateTenant(); var b = await fixture.CreateTenant();
        var objects = new VersionedTestStore(); await using var factory = WithStore(objects);
        using var first = await Login(factory, await fixture.CreateUser(a.Id)); using var second = await Login(factory, await fixture.CreateUser(b.Id));
        var sourceA = await Ok<TradeSourceReceipt>(Send(first, HttpMethod.Post, Root + "gaeb", Body("gaeb")));
        var sourceB = await Ok<TradeSourceReceipt>(Send(second, HttpMethod.Post, Root + "gaeb", Body("gaeb")));
        Assert.NotEqual(sourceA.Source.Id, sourceB.Source.Id); Assert.NotEqual(sourceA.Source.DocumentId, sourceB.Source.DocumentId);
        Assert.Equal(2, objects.PutCount);
        foreach (var path in new[] { Root + "gaeb/" + sourceA.Source.Id, Root + "gaeb/" + sourceA.Source.Id + "/original", "/api/v1/documents/" + sourceA.Source.DocumentId + "/download" })
        {
            using var denied = await second.GetAsync(path); Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.False(denied.Headers.Contains("X-Content-SHA256"));
        }
    }

    [PostgresApiFact]
    public async Task Finance_sources_are_not_disclosed_through_archive_or_generic_document_routes()
    {
        var tenant = await fixture.CreateTenant(); var objects = new VersionedTestStore(); await using var factory = WithStore(objects);
        using var accounting = await Login(factory, await fixture.CreateUser(tenant.Id, BusinessRole.Accounting));
        var source = await Ok<TradeSourceReceipt>(Send(accounting, HttpMethod.Post, Root + "e-invoice", Body("e-invoice")));
        using var site = await Login(factory, await fixture.CreateUser(tenant.Id, BusinessRole.SiteManager));
        foreach (var path in new[] { Root + "e-invoice", Root + "e-invoice/" + source.Source.Id, Root + "e-invoice/" + source.Source.Id + "/original", "/api/v1/documents/" + source.Source.DocumentId + "/download" })
        { using var denied = await site.GetAsync(path); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        var documents = await Ok<JsonElement>(site.GetAsync("/api/v1/documents"));
        Assert.Equal(0, documents.GetProperty("total").GetInt32());
        using var gaebDenied = await Send(accounting, HttpMethod.Post, Root + "gaeb", Body("gaeb"));
        Assert.Equal(HttpStatusCode.Forbidden, gaebDenied.StatusCode);
    }

    [PostgresApiFact]
    public async Task Even_a_director_cannot_fabricate_edit_or_delete_a_source_receipt_generically()
    {
        var tenant = await fixture.CreateTenant(); var person = await fixture.CreateUser(tenant.Id);
        var objects = new VersionedTestStore(); await using var factory = WithStore(objects); using var client = await Login(factory, person);
        var receipt = await Ok<TradeSourceReceipt>(Send(client, HttpMethod.Post, Root + "gaeb", Body("gaeb")));
        var original = await fixture.Read<GaebSource>(tenant.Id, receipt.Source.Id);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            var path = "/api/v1/gaeb-sources" + (method == HttpMethod.Post ? "" : "/" + original.Id);
            using var denied = await Send(client, method, path, method == HttpMethod.Delete ? null : original, original.Revision);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var overwrite = await Send(client, HttpMethod.Put, "/api/v1/documents/" + receipt.Source.DocumentId + "/content", new { });
        Assert.False(overwrite.IsSuccessStatusCode);
        var current = await fixture.Read<GaebSource>(tenant.Id, original.Id); Assert.Equal(original.Digest, current.Digest);
    }

    [PostgresApiFact]
    public async Task Invalid_hash_paths_DTDs_and_wrong_syntax_are_rejected_before_storage()
    {
        var tenant = await fixture.CreateTenant(); var objects = new VersionedTestStore(); await using var factory = WithStore(objects);
        using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        var dtd = Encoding.UTF8.GetBytes("<!DOCTYPE a [<!ENTITY secret SYSTEM 'file:///tmp/private'>]><a>&secret;</a>");
        var requests = new[]
        {
            Body("gaeb") with { ExpectedSha256 = new string('0', 64) },
            Body("gaeb") with { FileName = "../source.x83" },
            Body("gaeb") with { SourceBase64 = "not-base64!" },
            Body("gaeb", dtd), Body("gaeb", Bytes("e-invoice")),
            Body("gaeb", new byte[1_048_577])
        };
        foreach (var body in requests)
        { using var response = await Send(client, HttpMethod.Post, Root + "gaeb", body); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); }
        Assert.Equal(0, objects.PutCount);
    }

    [PostgresApiFact]
    public async Task Unversioned_storage_rolls_back_metadata_audit_and_receipt()
    {
        var tenant = await fixture.CreateTenant(); var objects = new VersionedTestStore { ReturnUnversioned = true }; await using var factory = WithStore(objects);
        using var client = await Login(factory, await fixture.CreateUser(tenant.Id)); var key = Guid.NewGuid().ToString("N");
        using var failed = await Send(client, HttpMethod.Post, Root + "gaeb", Body("gaeb"), key: key);
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        await fixture.InScope(async sp =>
        {
            var db = sp.GetRequiredService<FirstBlickDbContext>();
            Assert.False(await db.Entities.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenant.Id && x.Kind == "gaeb-sources"));
            Assert.False(await db.Documents.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenant.Id));
            Assert.False(await db.Audit.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenant.Id && x.Action.StartsWith("trade.source")));
            Assert.False(await db.Receipts.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenant.Id && x.Key == key));
        });
        objects.ReturnUnversioned = false;
        var recovered = await Ok<TradeSourceReceipt>(Send(client, HttpMethod.Post, Root + "gaeb", Body("gaeb"), key: key));
        Assert.False(recovered.AlreadyStored);
    }

    [PostgresApiFact]
    public async Task Readback_detects_same_length_corruption_before_publication_and_on_subsequent_downloads()
    {
        var tenant = await fixture.CreateTenant(); var objects = new VersionedTestStore { CorruptReads = true }; await using var factory = WithStore(objects);
        using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        using var failed = await Send(client, HttpMethod.Post, Root + "gaeb", Body("gaeb"));
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        var empty = await Ok<JsonElement>(client.GetAsync(Root + "gaeb")); Assert.Equal(0, empty.GetProperty("total").GetInt32());
        objects.CorruptReads = false;
        var source = await Ok<TradeSourceReceipt>(Send(client, HttpMethod.Post, Root + "gaeb", Body("gaeb")));
        objects.CorruptReads = true;
        foreach (var suffix in new[] { "", "/original" })
        { using var damaged = await client.GetAsync(Root + "gaeb/" + source.Source.Id + suffix); Assert.Equal(HttpStatusCode.BadRequest, damaged.StatusCode); Assert.False(damaged.Headers.Contains("X-Content-SHA256")); }
        using var duplicate = await Send(client, HttpMethod.Post, Root + "gaeb", Body("gaeb"));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }

    [PostgresApiFact]
    public async Task Retained_GAEB_original_resolves_reviewed_catalogue_imports_by_exact_source_hash()
    {
        var tenant = await fixture.CreateTenant(); var objects = new VersionedTestStore(); await using var factory = WithStore(objects);
        using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        var source = await Ok<TradeSourceReceipt>(Send(client, HttpMethod.Post, Root + "gaeb", Body("gaeb")));
        var request = new GaebCatalogRequest(Body("gaeb").SourceBase64, [new("01.001", 10m, 1m, 40m, 10m, 19m, "Synthetic explicitly reviewed costs")]);
        var preview = await Ok<GaebCatalogPreview>(Send(client, HttpMethod.Post, "/api/v1/integrations/v1/gaeb/catalog-preview", request));
        var imported = await Ok<GaebCatalogImportReceipt>(Send(client, HttpMethod.Post, "/api/v1/integrations/v1/gaeb/catalog-import", request with { PreviewDigest = preview.Digest }));
        var detail = await Ok<JsonElement>(client.GetAsync(Root + "gaeb/" + source.Source.Id));
        var link = Assert.Single(detail.GetProperty("catalogItems").EnumerateArray());
        Assert.Equal(Assert.Single(imported.Items).CatalogItemId, link.GetProperty("id").GetGuid());
        Assert.Equal("01.001", link.GetProperty("positionKey").GetString()); Assert.Equal(1, objects.PutCount);
    }

    [PostgresApiFact]
    public async Task Revoked_membership_and_disabled_module_block_capture_and_idempotent_replay_but_not_retained_history()
    {
        var tenant = await fixture.CreateTenant(); var person = await fixture.CreateUser(tenant.Id);
        var objects = new VersionedTestStore(); await using var factory = WithStore(objects); using var client = await Login(factory, person);
        var key = Guid.NewGuid().ToString("N");
        var source = await Ok<TradeSourceReceipt>(Send(client, HttpMethod.Post, Root + "e-invoice", Body("e-invoice"), key: key));
        await fixture.InScope(async sp =>
        {
            var db = sp.GetRequiredService<FirstBlickDbContext>();
            (await db.Tenants.SingleAsync(x => x.Id == tenant.Id)).Modules = ["core", "commercial"];
            await db.SaveChangesAsync();
        });
        using var blocked = await Send(client, HttpMethod.Post, Root + "e-invoice", Body("e-invoice"), key: key);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        using var history = await client.GetAsync(Root + "e-invoice/" + source.Source.Id); Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        await fixture.InScope(async sp =>
        {
            var db = sp.GetRequiredService<FirstBlickDbContext>();
            (await db.Memberships.SingleAsync(x => x.TenantId == tenant.Id && x.UserId == person.UserId)).Active = false;
            await db.SaveChangesAsync();
        });
        using var denied = await client.GetAsync(Root + "e-invoice/" + source.Source.Id); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [PostgresApiFact]
    public async Task Tampering_with_JSONB_receipt_metadata_is_detected_without_returning_original_bytes()
    {
        var tenant = await fixture.CreateTenant(); var person = await fixture.CreateUser(tenant.Id);
        var objects = new VersionedTestStore(); await using var factory = WithStore(objects); using var client = await Login(factory, person);
        var receipt = await Ok<TradeSourceReceipt>(Send(client, HttpMethod.Post, Root + "gaeb", Body("gaeb")));
        await fixture.InScope(async sp =>
        {
            var db = sp.GetRequiredService<FirstBlickDbContext>();
            var row = await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenant.Id && x.Kind == "gaeb-sources" && x.Id == receipt.Source.Id);
            var tampered = AppJson.Read<GaebSource>(row.Data); tampered.FileName = "forged.xml"; row.Data = AppJson.Serialize(tampered);
            await db.SaveChangesAsync();
        });
        using var response = await client.GetAsync(Root + "gaeb/" + receipt.Source.Id + "/original");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("trade_source_changed", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.False(response.Headers.Contains("X-Content-SHA256"));
    }

    private WebApplicationFactory<Program> WithStore(IObjectStore objects) => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
    { services.RemoveAll<IObjectStore>(); services.AddSingleton(objects); }));
    private static async Task<HttpClient> Login(WebApplicationFactory<Program> factory, ApiTestUser user)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Tenant-Id", user.TenantId.ToString()); await RefreshCsrf(client);
        await Ok<JsonElement>(Send(client, HttpMethod.Post, "/api/v1/auth/login", new { user.Email, user.Password })); await RefreshCsrf(client); return client;
    }
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}"); return AppJson.Read<T>(text);
    }
    private sealed class VersionedTestStore : IObjectStore
    {
        private readonly ConcurrentDictionary<(string Key, string Version), byte[]> data = new();
        private int putCount;
        public bool Configured => true;
        public bool ReturnUnversioned { get; set; }
        public bool CorruptReads { get; set; }
        public int PutCount => putCount;
        public async Task<string?> Put(string key, Stream body, string contentType, CancellationToken ct)
        {
            Interlocked.Increment(ref putCount); using var buffer = new MemoryStream(); await body.CopyToAsync(buffer, ct);
            var version = Guid.NewGuid().ToString("N"); data[(key, version)] = buffer.ToArray(); return ReturnUnversioned ? null : version;
        }
        public Task<ObjectMetadata?> Head(string key, string? version, CancellationToken ct) => Task.FromResult<ObjectMetadata?>(
            version is not null && data.TryGetValue((key, version), out var bytes) ? new(bytes.Length, version) : null);
        public Task<(Stream Body, string ContentType)> Get(string key, string? version, CancellationToken ct)
        {
            var bytes = (byte[])data[(key, version!)].Clone(); if (CorruptReads) bytes[0] ^= 1;
            return Task.FromResult<(Stream, string)>((new MemoryStream(bytes), "application/octet-stream"));
        }
    }
}
