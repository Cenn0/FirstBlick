using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
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

public sealed class LedgerDocumentApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static readonly string[] Kinds = ["customer-communications", "vehicle-costs"];
    private static readonly byte[] Pdf = "%PDF-1.4\nSynthetic ledger original.\n%%EOF\n"u8.ToArray();
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [PostgresApiFact]
    public async Task Multiple_verified_originals_preserve_ledger_bytes_parent_revision_and_totals_with_exact_replay()
    {
        var objects = new OriginalStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        foreach (var kind in Kinds)
        {
            var source = await CreateSource(client, kind); var before = await Stored(tenant.Id, kind, source.Id);
            var parentBefore = await Stored(tenant.Id, source.ParentKind, source.ParentId);
            using var contextResponse = await client.GetAsync(ContextPath(source));
            Assert.True(contextResponse.Headers.CacheControl?.Private);
            Assert.True(contextResponse.Headers.CacheControl?.NoStore);
            var context = await Ok<JsonElement>(Task.FromResult(contextResponse));
            Assert.Equal(kind, context.GetProperty("sourceKind").GetString());
            Assert.Equal(source.ParentKind, context.GetProperty("parentKind").GetString());
            Assert.Equal(source.ParentId, context.GetProperty("parentId").GetGuid());
            Assert.Equal(source.Id, Id(context.GetProperty("source")));
            Assert.Equal(tenant.Id, context.GetProperty("source").GetProperty("tenantId").GetGuid());
            Assert.Equal(source.Digest, context.GetProperty("source").GetProperty("digest").GetString());
            var stageKey = Guid.NewGuid().ToString("N"); var uploadKey = Guid.NewGuid().ToString("N");
            var staged = await Stage(client, source, Pdf, key: stageKey);
            using (var replay = await Send(client, HttpMethod.Post, Path(source), Metadata(Pdf), source.Revision, stageKey))
            { Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single()); Assert.Equal(Id(staged), Id(await Ok<JsonElement>(Task.FromResult(replay)))); }
            var countBefore = objects.PutCount;
            var ready = await Ok<JsonElement>(Upload(client, source, staged, Pdf, key: uploadKey));
            using (var replay = await Upload(client, source, staged, Pdf, key: uploadKey))
            { Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single()); }
            Assert.Equal(countBefore + 1, objects.PutCount);
            var second = await Stage(client, source, Png, "Original.png", "image/png");
            await Ok<JsonElement>(Upload(client, source, second, Png, "image/png"));
            var list = await Ok<JsonElement>(client.GetAsync(Path(source)));
            Assert.Equal(2, list.GetProperty("total").GetInt32()); Assert.True(list.GetProperty("canAttach").GetBoolean());
            Assert.Equal(source.Digest, ready.GetProperty("sourceDigest").GetString());
            Assert.Equal(kind == "customer-communications" ? LedgerDocumentEndpoints.CommunicationOriginalVersion : LedgerDocumentEndpoints.VehicleCostOriginalVersion, ready.GetProperty("rendererVersion").GetString());
            Assert.Equal(JsonValueKind.Null, ready.GetProperty("projectId").ValueKind);
            await VerifyDownload(client, $"{Path(source)}/{Id(ready)}/download", Pdf);
            await VerifyDownload(client, $"/api/v1/documents/{Id(ready)}/download", Pdf);
            Assert.Equal(before, await Stored(tenant.Id, kind, source.Id));
            Assert.Equal(parentBefore, await Stored(tenant.Id, source.ParentKind, source.ParentId));
        }
    }

    [PostgresApiFact]
    public async Task Correction_blocks_staging_upload_and_successful_replays_but_preserves_download_and_original()
    {
        var objects = new OriginalStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        foreach (var kind in Kinds)
        {
            var source = await CreateSource(client, kind); var before = await Stored(tenant.Id, kind, source.Id);
            var key = Guid.NewGuid().ToString("N"); var staged = await Stage(client, source, Pdf, key: key);
            var uploadKey = Guid.NewGuid().ToString("N"); var ready = await Ok<JsonElement>(Upload(client, source, staged, Pdf, key: uploadKey));
            var unfinished = await Stage(client, source, Pdf, "unfinished.pdf");
            await Correct(client, source);
            var list = await Ok<JsonElement>(client.GetAsync(Path(source))); Assert.False(list.GetProperty("canAttach").GetBoolean());
            await Error(Send(client, HttpMethod.Post, Path(source), Metadata(Pdf), source.Revision, key), "ledger_original_closed");
            await Error(Upload(client, source, staged, Pdf, key: uploadKey), "ledger_original_closed");
            await Error(Upload(client, source, unfinished, Pdf), "ledger_original_closed");
            await VerifyDownload(client, $"{Path(source)}/{Id(ready)}/download", Pdf);
            Assert.Equal(before, await Stored(tenant.Id, kind, source.Id));
        }
    }

    [PostgresApiFact]
    public async Task Historical_archived_parents_retain_ready_and_archived_originals_but_cannot_accept_uploads()
    {
        var objects = new OriginalStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        foreach (var kind in Kinds)
        {
            var source = await CreateSource(client, kind); var staged = await Stage(client, source, Pdf);
            var ready = await Ok<JsonElement>(Upload(client, source, staged, Pdf));
            await Ok<JsonElement>(Send(client, HttpMethod.Post, $"/api/v1/documents/{Id(ready)}/archive", new { }, Revision(ready)));
            var unfinished = await Stage(client, source, Pdf, "unfinished.pdf");
            await ArchiveParent(tenant.Id, source);
            var list = await Ok<JsonElement>(client.GetAsync(Path(source))); Assert.False(list.GetProperty("canAttach").GetBoolean());
            Assert.Equal(2, list.GetProperty("total").GetInt32());
            Assert.Contains(list.GetProperty("items").EnumerateArray(), x => x.GetProperty("state").GetString() == "Archived");
            await Error(Send(client, HttpMethod.Post, Path(source), Metadata(Pdf), source.Revision), "ledger_original_closed");
            await Error(Upload(client, source, unfinished, Pdf), "ledger_original_closed");
            await VerifyDownload(client, $"{Path(source)}/{Id(ready)}/download", Pdf);
            await VerifyDownload(client, $"/api/v1/documents/{Id(ready)}/download", Pdf);
        }
    }

    [PostgresApiFact]
    public async Task Source_tenant_and_generic_route_substitutions_are_rejected()
    {
        var objects = new OriginalStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        var otherTenant = await fixture.CreateTenant(); using var other = await Login(factory, await fixture.CreateUser(otherTenant.Id));
        foreach (var kind in Kinds)
        {
            var source = await CreateSource(client, kind); var different = await CreateSource(client, kind);
            var staged = await Stage(client, source, Pdf);
            await Error(Upload(client, different, staged, Pdf), "ledger_document_mismatch");
            await Error(Upload(client, null, staged, Pdf), "ledger_document_route");
            await Error(client.GetAsync($"{Path(different)}/{Id(staged)}/download"), "not_found", HttpStatusCode.NotFound);
            await Error(other.GetAsync(Path(source)), "not_found", HttpStatusCode.NotFound);
            await Error(other.GetAsync(ContextPath(source)), "not_found", HttpStatusCode.NotFound);
            await Error(Send(other, HttpMethod.Post, Path(source), Metadata(Pdf), source.Revision), "not_found", HttpStatusCode.NotFound);
            await Error(Upload(other, source, staged, Pdf), "not_found", HttpStatusCode.NotFound);
            await Error(other.GetAsync($"/api/v1/documents/{Id(staged)}"), "not_found", HttpStatusCode.NotFound);
        }
        Assert.Equal(0, objects.PutCount);
    }

    [PostgresApiFact]
    public async Task Current_source_rights_and_module_are_required_even_for_successful_receipt_replay()
    {
        var objects = new OriginalStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); var actor = await fixture.CreateUser(tenant.Id);
        using var client = await Login(factory, actor);
        foreach (var kind in Kinds)
        {
            var source = await CreateSource(client, kind); var key = Guid.NewGuid().ToString("N");
            var staged = await Stage(client, source, Pdf, key: key); var uploadKey = Guid.NewGuid().ToString("N");
            var ready = await Ok<JsonElement>(Upload(client, source, staged, Pdf, key: uploadKey));
            using var accountant = await Login(factory, await fixture.CreateUser(tenant.Id, BusinessRole.Accounting));
            await Ok<JsonElement>(accountant.GetAsync(Path(source)));
            await Ok<JsonElement>(accountant.GetAsync(ContextPath(source)));
            await VerifyDownload(accountant, $"{Path(source)}/{Id(ready)}/download", Pdf);
            await Error(Send(accountant, HttpMethod.Post, Path(source), Metadata(Pdf), source.Revision), "forbidden", HttpStatusCode.Forbidden);
            using var worker = await Login(factory, await fixture.CreateUser(tenant.Id, BusinessRole.Worker));
            await Error(worker.GetAsync(Path(source)), "forbidden", HttpStatusCode.Forbidden);
            await Error(worker.GetAsync(ContextPath(source)), "forbidden", HttpStatusCode.Forbidden);
            await Error(worker.GetAsync($"/api/v1/documents/{Id(ready)}"), "forbidden", HttpStatusCode.Forbidden);
            var visible = await Ok<JsonElement>(worker.GetAsync("/api/v1/documents")); Assert.Empty(visible.GetProperty("items").EnumerateArray());
            await ChangeRole(actor, BusinessRole.Accounting);
            await Error(Send(client, HttpMethod.Post, Path(source), Metadata(Pdf), source.Revision, key), "forbidden", HttpStatusCode.Forbidden);
            await Error(Upload(client, source, staged, Pdf, key: uploadKey), "forbidden", HttpStatusCode.Forbidden);
            await ChangeRole(actor, BusinessRole.ManagingDirector);
            if (kind == "vehicle-costs")
            {
                await fixture.InScope(async services =>
                {
                    var db = services.GetRequiredService<FirstBlickDbContext>(); var business = await db.Tenants.SingleAsync(x => x.Id == tenant.Id);
                    business.Modules = business.Modules.Where(x => x != "purchasing").ToArray(); await db.SaveChangesAsync();
                });
                await Error(Send(client, HttpMethod.Post, Path(source), Metadata(Pdf), source.Revision, key), "module_disabled", HttpStatusCode.Forbidden);
                await Error(Upload(client, source, staged, Pdf, key: uploadKey), "module_disabled", HttpStatusCode.Forbidden);
                await VerifyDownload(client, $"{Path(source)}/{Id(ready)}/download", Pdf);
            }
        }
    }

    [PostgresApiFact]
    public async Task Stage_validates_original_revision_metadata_and_idempotency_fingerprint()
    {
        var objects = new OriginalStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        foreach (var kind in Kinds)
        {
            var source = await CreateSource(client, kind); var body = Metadata(Pdf);
            await Error(Send(client, HttpMethod.Post, Path(source), body), "revision_required", HttpStatusCode.PreconditionRequired);
            await Error(Send(client, HttpMethod.Post, Path(source), body, 9), "revision_conflict", HttpStatusCode.Conflict);
            foreach (var (invalid, code) in new (DocumentRequest, string)[]
            {
                (body with { ProjectId = Guid.NewGuid() }, "ledger_document_type"),
                (body with { Name = "file.txt", ContentType = "text/plain" }, "ledger_document_type"),
                (body with { Name = "../file.pdf" }, "invalid_filename"), (body with { Sha256 = "invalid" }, "invalid_checksum"),
                (body with { Size = 0 }, "invalid_file_size"), (body with { Size = DocumentEndpoints.MaximumBytes + 1L }, "invalid_file_size")
            }) await Error(Send(client, HttpMethod.Post, Path(source), invalid, 1), code);
            var key = Guid.NewGuid().ToString("N"); await Stage(client, source, Pdf, key: key);
            await Error(Send(client, HttpMethod.Post, Path(source), body with { Name = "changed.pdf" }, 1, key), "idempotency_conflict", HttpStatusCode.Conflict);
            var list = await Ok<JsonElement>(client.GetAsync(Path(source))); Assert.Equal(1, list.GetProperty("total").GetInt32());
        }
        Assert.Equal(0, objects.PutCount);
    }

    [PostgresApiFact]
    public async Task Upload_checks_length_type_hash_signature_and_revision_without_replacing_verified_originals()
    {
        var objects = new OriginalStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        foreach (var kind in Kinds)
        {
            var source = await CreateSource(client, kind); var stage = await Stage(client, source, Pdf);
            await Error(client.GetAsync($"{Path(source)}/{Id(stage)}/download"), "document_not_ready");
            await Error(Upload(client, source, stage, Pdf, revision: 20), "revision_conflict", HttpStatusCode.Conflict);
            await Error(Upload(client, source, stage, Pdf, "image/png"), "invalid_content_type");
            await Error(Upload(client, source, stage, Pdf[..^1]), "invalid_file_size");
            var changed = (byte[])Pdf.Clone(); changed[10] ^= 1;
            await Error(Upload(client, source, stage, changed), "checksum_mismatch");
            var wrong = Encoding.UTF8.GetBytes("This is not a PDF original"); var wrongStage = await Stage(client, source, wrong);
            await Error(Upload(client, source, wrongStage, wrong), "invalid_file_content");
            var ready = await Ok<JsonElement>(Upload(client, source, stage, Pdf));
            await Error(Upload(client, source, ready, Pdf), "invalid_transition");
        }
        Assert.Equal(2, objects.PutCount);
    }

    [PostgresApiFact]
    public async Task Corrupted_unversioned_and_mismatched_storage_never_publishes_ready_and_same_key_can_retry()
    {
        var objects = new OriginalStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        foreach (var kind in Kinds)
        {
            var source = await CreateSource(client, kind); var stage = await Stage(client, source, Pdf); var key = Guid.NewGuid().ToString("N");
            foreach (var failure in new[] { "version", "bytes", "head" })
            {
                objects.Failure = failure;
                await Error(Upload(client, source, stage, Pdf, key: key), failure == "version" ? "storage_versioning_required" : "document_integrity");
                var current = await Ok<JsonElement>(client.GetAsync($"/api/v1/documents/{Id(stage)}"));
                Assert.Equal("Staged", current.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, current.GetProperty("versionId").ValueKind);
            }
            objects.Failure = ""; var ready = await Ok<JsonElement>(Upload(client, source, stage, Pdf, key: key));
            objects.Failure = "bytes";
            await Error(client.GetAsync($"{Path(source)}/{Id(ready)}/download"), "document_integrity");
            await Error(client.GetAsync($"/api/v1/documents/{Id(ready)}/download"), "document_integrity");
            objects.Failure = "";
        }
    }

    [PostgresApiFact]
    public async Task Invalid_source_binding_fails_lists_metadata_downloads_and_resume_on_all_routes()
    {
        var objects = new OriginalStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        foreach (var kind in Kinds)
        {
            var source = await CreateSource(client, kind); var stage = await Stage(client, source, Pdf);
            var ready = await Ok<JsonElement>(Upload(client, source, stage, Pdf));
            await fixture.InScope(async services =>
            {
                var db = services.GetRequiredService<FirstBlickDbContext>(); var document = await db.Documents.IgnoreQueryFilters().SingleAsync(x => x.Id == Id(ready));
                document.SourceDigest = new string('0', 64); await db.SaveChangesAsync();
            });
            await Error(client.GetAsync(Path(source)), "ledger_document_mismatch");
            await Error(client.GetAsync($"{Path(source)}/{Id(ready)}/download"), "ledger_document_mismatch");
            await Error(client.GetAsync($"/api/v1/documents/{Id(ready)}"), "ledger_document_mismatch");
            await Error(client.GetAsync($"/api/v1/documents/{Id(ready)}/download"), "ledger_document_mismatch");
            await Error(client.GetAsync("/api/v1/documents"), "ledger_document_mismatch");
        }
    }

    [PostgresApiFact]
    public async Task Unconfigured_storage_creates_no_metadata_and_site_managers_cannot_finish_other_uploads()
    {
        var objects = new OriginalStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); using var office = await Login(factory, await fixture.CreateUser(tenant.Id, BusinessRole.Office));
        var source = await CreateSource(office, "vehicle-costs"); objects.Configured = false;
        await Error(Send(office, HttpMethod.Post, Path(source), Metadata(Pdf), 1), "storage_unconfigured", HttpStatusCode.ServiceUnavailable);
        objects.Configured = true;
        using var manager = await Login(factory, await fixture.CreateUser(tenant.Id, BusinessRole.SiteManager));
        var staged = await Stage(manager, source, Pdf);
        using var other = await Login(factory, await fixture.CreateUser(tenant.Id, BusinessRole.SiteManager));
        await Error(Upload(other, source, staged, Pdf), "forbidden", HttpStatusCode.Forbidden);
        await Ok<JsonElement>(Upload(office, source, staged, Pdf));
        var list = await Ok<JsonElement>(office.GetAsync(Path(source))); Assert.Equal(1, list.GetProperty("total").GetInt32());
    }

    private sealed record Source(string Kind, Guid Id, long Revision, string Digest, string ParentKind, Guid ParentId, long ParentRevision);
    private static async Task<Source> CreateSource(HttpClient client, string kind)
    {
        if (kind == "customer-communications")
        {
            var customer = await Ok<Customer>(Send(client, HttpMethod.Post, "/api/v1/customers", new { name = "Original test customer" }));
            var at = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds());
            var change = await Ok<CustomerCommunicationChange>(Send(client, HttpMethod.Post, $"/api/v1/customers/{customer.Id}/communications",
                new CustomerCommunicationRequest(CustomerCommunicationChannel.Email, CustomerCommunicationDirection.Incoming, at, "Original source", "Received message"), customer.Revision));
            return new(kind, change.Entry.Id, change.Entry.Revision, change.Entry.Digest, "customers", customer.Id, change.CustomerRevision);
        }
        var vehicle = await Ok<Equipment>(Send(client, HttpMethod.Post, "/api/v1/equipment", new Equipment
        { Name = "Original van", Kind = EquipmentKind.Vehicle, InventoryNumber = "ORIGINAL-" + Guid.NewGuid().ToString("N") }));
        var cost = await Ok<VehicleCostChange>(Send(client, HttpMethod.Post, $"/api/v1/equipment/{vehicle.Id}/vehicle-costs",
            new VehicleCostDetails(null, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), VehicleCostCategory.Fuel, 123.45m, "EUR", "RECEIPT-1", ""), vehicle.Revision));
        return new(kind, cost.Cost.Entry.Id, cost.Cost.Entry.Revision, cost.Cost.Entry.Digest, "equipment", vehicle.Id, cost.Vehicle.Revision);
    }
    private static Task<JsonElement> Correct(HttpClient client, Source source) => source.Kind == "customer-communications"
        ? Ok<JsonElement>(Send(client, HttpMethod.Post, $"/api/v1/customers/{source.ParentId}/communications/{source.Id}/void", new { sourceDigest = source.Digest, reason = "Wrong original" }, source.ParentRevision))
        : Ok<JsonElement>(Send(client, HttpMethod.Post, $"/api/v1/equipment/{source.ParentId}/vehicle-costs/{source.Id}/reverse", new { sourceDigest = source.Digest, reason = "Wrong original" }, source.ParentRevision));
    private Task ArchiveParent(Guid tenant, Source source) => fixture.InScope(async services =>
    {
        var db = services.GetRequiredService<FirstBlickDbContext>();
        var row = await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenant && x.Kind == source.ParentKind && x.Id == source.ParentId);
        var entity = AppJson.Read(source.ParentKind, row.Data); entity.Archived = true; row.Archived = true; row.Data = AppJson.Serialize(entity); await db.SaveChangesAsync();
    });
    private Task<string> Stored(Guid tenant, string kind, Guid id) => fixture.InScope(async services => await services.GetRequiredService<FirstBlickDbContext>()
        .Entities.IgnoreQueryFilters().Where(x => x.TenantId == tenant && x.Kind == kind && x.Id == id).Select(x => x.Data).SingleAsync());
    private Task ChangeRole(ApiTestUser user, BusinessRole role) => fixture.InScope(async services =>
    {
        var db = services.GetRequiredService<FirstBlickDbContext>(); var membership = await db.Memberships.IgnoreQueryFilters().SingleAsync(x => x.TenantId == user.TenantId && x.UserId == user.UserId);
        membership.Role = role; membership.Revision++; await db.SaveChangesAsync();
    });
    private WebApplicationFactory<Program> WithStore(OriginalStore objects) => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
    { services.RemoveAll<IObjectStore>(); services.AddSingleton<IObjectStore>(objects); }));
    private static async Task<HttpClient> Login(WebApplicationFactory<Program> factory, ApiTestUser user)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Tenant-Id", user.TenantId.ToString()); await RefreshCsrf(client);
        await Ok<JsonElement>(Send(client, HttpMethod.Post, "/api/v1/auth/login", new { user.Email, user.Password })); await RefreshCsrf(client); return client;
    }
    private static Guid Id(JsonElement document) => document.GetProperty("id").GetGuid();
    private static long Revision(JsonElement document) => document.GetProperty("revision").GetInt64();
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Path(Source source) => $"/api/v1/{source.Kind}/{source.Id}/documents";
    private static string ContextPath(Source source) => $"/api/v1/{source.Kind}/{source.Id}/document-context";
    private static DocumentRequest Metadata(byte[] bytes, string name = "Original.pdf", string type = "application/pdf") => new(name, type, bytes.Length, Hash(bytes), null);
    private static Task<JsonElement> Stage(HttpClient client, Source source, byte[] bytes, string name = "Original.pdf", string type = "application/pdf", string? key = null)
        => Ok<JsonElement>(Send(client, HttpMethod.Post, Path(source), Metadata(bytes, name, type), source.Revision, key));
    private static Task<HttpResponseMessage> Upload(HttpClient client, Source? source, JsonElement document, byte[] bytes, string type = "application/pdf", string? key = null, long? revision = null)
    {
        var path = source is null ? $"/api/v1/documents/{Id(document)}/content" : $"{Path(source)}/{Id(document)}/content";
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(type); request.Headers.IfMatch.ParseAdd($"\"{revision ?? Revision(document)}\"");
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N")); return client.SendAsync(request);
    }
    private static async Task VerifyDownload(HttpClient client, string path, byte[] bytes)
    {
        using var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync()); Assert.Equal(Hash(bytes), response.Headers.GetValues("X-Content-SHA256").Single());
        Assert.True(response.Headers.CacheControl!.NoStore); Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
    }
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}"); return JsonSerializer.Deserialize<T>(text, AppJson.Options)!;
    }
    private static async Task Error(Task<HttpResponseMessage> pending, string code, HttpStatusCode expected = HttpStatusCode.BadRequest)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(expected, response.StatusCode); Assert.Equal(code, JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
    }
    private sealed class OriginalStore : IObjectStore
    {
        private readonly ConcurrentDictionary<(string Key, string Version), (byte[] Bytes, string Type)> objects = new();
        public bool Configured { get; set; } = true;
        public string Failure { get; set; } = "";
        public int PutCount { get; private set; }
        public async Task<string?> Put(string key, Stream body, string contentType, CancellationToken ct)
        {
            PutCount++; using var buffer = new MemoryStream(); await body.CopyToAsync(buffer, ct); var version = Guid.NewGuid().ToString("N");
            objects[(key, version)] = (buffer.ToArray(), contentType); return Failure == "version" ? "null" : version;
        }
        public Task<ObjectMetadata?> Head(string key, string? version, CancellationToken ct) => Task.FromResult<ObjectMetadata?>(version is not null
            && objects.TryGetValue((key, version), out var value) ? new ObjectMetadata(value.Bytes.Length, Failure == "head" ? "wrong-version" : version) : null);
        public Task<(Stream Body, string ContentType)> Get(string key, string? version, CancellationToken ct)
        {
            var value = objects[(key, version!)]; var bytes = (byte[])value.Bytes.Clone(); if (Failure == "bytes") bytes[0] ^= 1;
            return Task.FromResult<(Stream, string)>((new MemoryStream(bytes), value.Type));
        }
    }
}
