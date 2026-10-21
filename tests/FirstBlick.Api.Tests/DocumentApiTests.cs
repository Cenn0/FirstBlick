using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using FirstBlick.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PdfSharp.Pdf.IO;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class DocumentApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Issued_quote_freezes_parties_and_decimal_values_and_binds_acceptance_order_and_multipage_PDF_to_complete_digest()
    {
        var tenant = await fixture.CreateTenant("Original seller"); var person = await fixture.CreateUser(tenant.Id);
        var objects = new FaultInjectingObjectStore(); await using var factory = WithStore(objects);
        using var client = await Login(factory, person);
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Müller & Żółć", Address = "Musterstraße 42" }, person.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id, Name = "Test roof", Address = customer.Address }, person.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id, BuildingId = building.Id, Name = "Synthetic PDF project" }, person.UserId);
        var draftBody = new Quote { CustomerId = customer.Id, ProjectId = project.Id, Title = "Dachsanierung – original",
            Lines = Enumerable.Range(1, 110).Select(i => new QuoteLine { Description = $"Position {i}: Äußere Dachfläche, geprüfte synthetische Menge mit ausführlicher Leistungsbeschreibung.",
                Quantity = 12.345678m, Unit = "m²", UnitMaterialCost = 17.23m, UnitLaborHours = .75m, HourlyRate = 55m, MarkupPercent = 12.5m, DiscountPercent = 2.5m, VatPercent = 19m }).ToList() };
        var draftJson = await Ok<JsonElement>(Send(client, HttpMethod.Post, "/api/v1/quotes", draftBody));
        var quantity = draftJson.GetProperty("lines")[0].GetProperty("quantity");
        Assert.Equal(JsonValueKind.String, quantity.ValueKind); Assert.Equal("12.345678", quantity.GetString());
        var draft = draftJson.Deserialize<Quote>(AppJson.Options)!;
        var quote = await Ok<Quote>(Send(client, HttpMethod.Post, $"/api/v1/quotes/{draft.Id}/freeze", new { }, draft.Revision));
        Assert.NotNull(quote.Presentation); Assert.NotEqual(quote.Snapshot!.Digest, quote.IssuedDigest);
        var issuedDigest = quote.IssuedDigest;
        customer.Name = "Changed master data"; customer.Address = "Different address";
        await Ok<Customer>(Send(client, HttpMethod.Put, $"/api/v1/customers/{customer.Id}", customer, customer.Revision));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            (await db.Tenants.SingleAsync(t => t.Id == tenant.Id)).Name = "Changed seller"; await db.SaveChangesAsync();
        });
        var stored = await fixture.Read<Quote>(tenant.Id, quote.Id);
        Assert.Equal("Original seller", stored.Presentation!.SellerName); Assert.Equal("Müller & Żółć", stored.Presentation.BuyerName);
        Assert.Equal("Musterstraße 42", stored.Presentation.BuyerAddress); Assert.Equal(issuedDigest, stored.IssuedDigest);
        await Error(Send(client, HttpMethod.Post, $"/api/v1/quotes/{quote.Id}/accept", new { digest = quote.Snapshot.Digest }, quote.Revision), "stale_content");
        var document = await Ok<JsonElement>(Send(client, HttpMethod.Post, $"/api/v1/quotes/{quote.Id}/pdf", new { }, quote.Revision));
        Assert.Equal(issuedDigest, document.GetProperty("sourceDigest").GetString());
        var download = await client.GetAsync($"/api/v1/documents/{document.GetProperty("id").GetGuid()}/download");
        Assert.True(download.IsSuccessStatusCode, await download.Content.ReadAsStringAsync());
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(document.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Equal("application/pdf", download.Content.Headers.ContentType!.MediaType);
        using var pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        Assert.True(pdf.PageCount > 5); Assert.Equal("Frozen source SHA-256: " + issuedDigest, pdf.Info.Subject);
        quote = await Ok<Quote>(Send(client, HttpMethod.Post, $"/api/v1/quotes/{quote.Id}/accept", new { digest = issuedDigest }, quote.Revision));
        var order = await Ok<Order>(Send(client, HttpMethod.Post, $"/api/v1/orders/from-quote/{quote.Id}", new { }, quote.Revision));
        Assert.Equal(issuedDigest, order.QuotePresentation!.Digest);
        order.QuotePresentation = order.QuotePresentation with { BuyerName = "Tampered accepted party" };
        Assert.Equal("order_presentation_changed", Assert.Throws<DomainException>(order.Validate).Code);
    }

    [PostgresApiFact]
    public async Task Download_detects_same_size_corruption_and_never_discloses_a_foreign_tenant_document()
    {
        var tenant = await fixture.CreateTenant(); var person = await fixture.CreateUser(tenant.Id);
        var objects = new FaultInjectingObjectStore(); await using var factory = WithStore(objects);
        using var client = await Login(factory, person);
        var bytes = Encoding.UTF8.GetBytes("Synthetic UTF-8 content with exactly known bytes: Müller.\n");
        var staged = await Stage(client, bytes); var id = staged.GetProperty("id").GetGuid();
        await Ok<JsonElement>(Upload(client, id, bytes, staged.GetProperty("revision").GetInt64()));
        var good = await client.GetAsync($"/api/v1/documents/{id}/download");
        Assert.Equal(bytes, await good.Content.ReadAsByteArrayAsync());
        objects.CorruptReads = true;
        await Error(client.GetAsync($"/api/v1/documents/{id}/download"), "document_integrity");
        var otherTenant = await fixture.CreateTenant(); var other = await fixture.CreateUser(otherTenant.Id);
        using var foreign = await Login(factory, other);
        var denied = await foreign.GetAsync($"/api/v1/documents/{id}/download");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.False(denied.Headers.Contains("X-Content-SHA256"));
    }

    [PostgresApiFact]
    public async Task Upload_requires_exact_checksum_and_an_immutable_storage_version_before_marking_ready()
    {
        var tenant = await fixture.CreateTenant(); var person = await fixture.CreateUser(tenant.Id);
        var objects = new FaultInjectingObjectStore { ReturnUnversionedPut = true }; await using var factory = WithStore(objects);
        using var client = await Login(factory, person);
        var bytes = "Synthetic attachment bytes"u8.ToArray();
        var staged = await Stage(client, bytes); var id = staged.GetProperty("id").GetGuid(); var revision = staged.GetProperty("revision").GetInt64();
        var changed = (byte[])bytes.Clone(); changed[0] ^= 1;
        await Error(Upload(client, id, changed, revision), "checksum_mismatch"); Assert.Equal(0, objects.PutCount);
        await Error(Upload(client, id, bytes, revision), "storage_versioning_required");
        var current = await Ok<JsonElement>(client.GetAsync($"/api/v1/documents/{id}"));
        Assert.Equal("Staged", current.GetProperty("state").GetString()); Assert.Equal(revision, current.GetProperty("revision").GetInt64());
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.False(await db.Audit.IgnoreQueryFilters().AnyAsync(a => a.EntityId == id && a.Action == "document.ready"));
        });
    }

    private WebApplicationFactory<Program> WithStore(IObjectStore objects) => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
    { services.RemoveAll<IObjectStore>(); services.AddSingleton(objects); }));

    private static async Task<HttpClient> Login(WebApplicationFactory<Program> factory, ApiTestUser person)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Tenant-Id", person.TenantId.ToString()); await RefreshCsrf(client);
        await Ok<JsonElement>(Send(client, HttpMethod.Post, "/api/v1/auth/login", new { person.Email, person.Password }));
        await RefreshCsrf(client); return client;
    }

    private static Task<JsonElement> Stage(HttpClient client, byte[] bytes) => Ok<JsonElement>(Send(client, HttpMethod.Post, "/api/v1/documents", new
    { name = "synthetic.txt", contentType = "text/plain", size = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }));

    private static Task<HttpResponseMessage> Upload(HttpClient client, Guid id, byte[] bytes, long revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/documents/{id}/content") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain"); request.Headers.IfMatch.ParseAdd($"\"{revision}\"");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N")); return client.SendAsync(request);
    }

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {json}"); return JsonSerializer.Deserialize<T>(json, AppJson.Options)!;
    }
    private static async Task Error(Task<HttpResponseMessage> pending, string code)
    {
        using var response = await pending; var body = await response.Content.ReadAsStringAsync();
        Assert.False(response.IsSuccessStatusCode); Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private sealed class FaultInjectingObjectStore : IObjectStore
    {
        private readonly ConcurrentDictionary<(string Key, string Version), (byte[] Bytes, string ContentType)> objects = new();
        public bool Configured => true;
        public bool CorruptReads { get; set; }
        public bool ReturnUnversionedPut { get; set; }
        public int PutCount { get; private set; }
        public async Task<string?> Put(string key, Stream body, string contentType, CancellationToken ct)
        {
            PutCount++; using var buffer = new MemoryStream(); await body.CopyToAsync(buffer, ct); var version = Guid.NewGuid().ToString("N");
            objects[(key, version)] = (buffer.ToArray(), contentType); return ReturnUnversionedPut ? null : version;
        }
        public Task<ObjectMetadata?> Head(string key, string? version, CancellationToken ct) => Task.FromResult<ObjectMetadata?>(
            version is not null && objects.TryGetValue((key, version), out var value) ? new ObjectMetadata(value.Bytes.Length, version) : null);
        public Task<(Stream Body, string ContentType)> Get(string key, string? version, CancellationToken ct)
        {
            var value = objects[(key, version!)]; var bytes = (byte[])value.Bytes.Clone(); if (CorruptReads) bytes[0] ^= 1;
            return Task.FromResult<(Stream, string)>((new MemoryStream(bytes), value.ContentType));
        }
    }
}
