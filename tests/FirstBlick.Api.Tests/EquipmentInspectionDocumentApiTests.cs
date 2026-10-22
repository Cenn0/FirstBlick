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

public sealed class EquipmentInspectionDocumentApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private sealed record InspectionChange(Equipment Equipment, EquipmentInspection Inspection, long AffectedAssignmentCount);
    private static readonly byte[] Pdf = "%PDF-1.4\nSynthetic retained inspection report.\n%%EOF\n"u8.ToArray();
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [PostgresApiFact]
    public async Task Site_management_retains_multiple_originals_and_archived_downloads_without_rewriting_the_sealed_inspection()
    {
        var objects = new TestObjectStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id, BusinessRole.SiteManager);
        using var client = await Login(factory, user); var change = await Completed(client);
        var originalInspection = AppJson.Serialize(change.Inspection); var originalEquipment = AppJson.Serialize(change.Equipment);
        var first = await Stage(client, change.Inspection, Pdf); var key = Guid.NewGuid().ToString("N");
        var uploaded = await Ok<JsonElement>(Upload(client, change.Inspection.Id, first, Pdf, key: key));
        Assert.Equal("Ready", uploaded.GetProperty("state").GetString()); Assert.False(string.IsNullOrWhiteSpace(uploaded.GetProperty("versionId").GetString()));
        Assert.Equal(change.Inspection.Completion!.Digest, uploaded.GetProperty("sourceDigest").GetString());
        Assert.Equal(EquipmentInspectionDocumentEndpoints.OriginalVersion, uploaded.GetProperty("rendererVersion").GetString());
        Assert.Equal(JsonValueKind.Null, uploaded.GetProperty("projectId").ValueKind);
        using var replay = await Upload(client, change.Inspection.Id, first, Pdf, key: key);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single()); Assert.Equal(1, objects.PutCount);
        var second = await Stage(client, change.Inspection, Png, "original.png", "image/png");
        await Ok<JsonElement>(Upload(client, change.Inspection.Id, second, Png, "image/png"));
        Assert.Equal(2, objects.PutCount);
        using (var download = await client.GetAsync(DownloadPath(change.Inspection.Id, uploaded)))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode); Assert.Equal(Pdf, await download.Content.ReadAsByteArrayAsync());
            Assert.Equal("application/pdf", download.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal(Hash(Pdf), download.Headers.GetValues("X-Content-SHA256").Single());
            Assert.True(download.Headers.CacheControl!.NoStore);
        }
        using var administrator = await Login(factory, await fixture.CreateUser(tenant.Id));
        await Ok<JsonElement>(Send(administrator, HttpMethod.Post, $"/api/v1/documents/{Id(uploaded)}/archive", new { }, Revision(uploaded)));
        var list = await Ok<JsonElement>(client.GetAsync(DocumentsPath(change.Inspection.Id)));
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        Assert.Contains(list.GetProperty("items").EnumerateArray(), x => x.GetProperty("state").GetString() == "Archived");
        using (var archived = await client.GetAsync(DownloadPath(change.Inspection.Id, uploaded)))
        { Assert.Equal(HttpStatusCode.OK, archived.StatusCode); Assert.Equal(Pdf, await archived.Content.ReadAsByteArrayAsync()); }
        Assert.Equal(originalInspection, AppJson.Serialize(await fixture.Read<EquipmentInspection>(tenant.Id, change.Inspection.Id)));
        Assert.Equal(originalEquipment, AppJson.Serialize(await fixture.Read<Equipment>(tenant.Id, change.Equipment.Id)));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(2, await db.Documents.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant.Id));
            Assert.Equal(2, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant.Id && x.Action == "document.ready"));
        });
    }

    [PostgresApiFact]
    public async Task Read_roles_and_current_module_rights_apply_to_upload_and_idempotent_replay()
    {
        var objects = new TestObjectStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id, BusinessRole.SiteManager);
        using var client = await Login(factory, user); var change = await Completed(client);
        var stageKey = Guid.NewGuid().ToString("N"); var body = Metadata(Pdf);
        var stage = await Ok<JsonElement>(Send(client, HttpMethod.Post, DocumentsPath(change.Inspection.Id), body, change.Inspection.Revision, stageKey));
        var uploadKey = Guid.NewGuid().ToString("N"); var ready = await Ok<JsonElement>(Upload(client, change.Inspection.Id, stage, Pdf, key: uploadKey));
        using var accountant = await Login(factory, await fixture.CreateUser(tenant.Id, BusinessRole.Accounting));
        await Ok<JsonElement>(accountant.GetAsync(DocumentsPath(change.Inspection.Id)));
        using (var download = await accountant.GetAsync(DownloadPath(change.Inspection.Id, ready))) Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        await Error(Send(accountant, HttpMethod.Post, DocumentsPath(change.Inspection.Id), body, change.Inspection.Revision), "forbidden", HttpStatusCode.Forbidden);
        using var worker = await Login(factory, await fixture.CreateUser(tenant.Id, BusinessRole.Worker));
        await Error(worker.GetAsync(DocumentsPath(change.Inspection.Id)), "forbidden", HttpStatusCode.Forbidden);
        await Error(worker.GetAsync($"/api/v1/documents/{Id(ready)}"), "forbidden", HttpStatusCode.Forbidden);
        await ChangeRole(user, BusinessRole.Worker);
        await Error(Send(client, HttpMethod.Post, DocumentsPath(change.Inspection.Id), body, change.Inspection.Revision, stageKey), "forbidden", HttpStatusCode.Forbidden);
        await Error(Upload(client, change.Inspection.Id, stage, Pdf, key: uploadKey), "forbidden", HttpStatusCode.Forbidden);
        await ChangeRole(user, BusinessRole.SiteManager);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var business = await db.Tenants.SingleAsync(x => x.Id == tenant.Id);
            business.Modules = business.Modules.Where(x => x != "purchasing").ToArray(); await db.SaveChangesAsync();
        });
        await Error(Send(client, HttpMethod.Post, DocumentsPath(change.Inspection.Id), body, change.Inspection.Revision, stageKey), "module_disabled", HttpStatusCode.Forbidden);
        await Error(Upload(client, change.Inspection.Id, stage, Pdf, key: uploadKey), "module_disabled", HttpStatusCode.Forbidden);
        await Ok<JsonElement>(client.GetAsync(DocumentsPath(change.Inspection.Id)));
        using (var download = await client.GetAsync(DownloadPath(change.Inspection.Id, ready))) Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(1, objects.PutCount);
    }

    [PostgresApiFact]
    public async Task Source_and_tenant_substitution_are_rejected_and_generic_upload_cannot_bypass_inspection_permissions()
    {
        var objects = new TestObjectStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        var first = await Completed(client); var second = await Completed(client);
        var stage = await Stage(client, first.Inspection, Pdf);
        await Error(Upload(client, second.Inspection.Id, stage, Pdf), "inspection_document_mismatch");
        await Error(Upload(client, null, stage, Pdf), "inspection_document_route");
        await Error(client.GetAsync(DownloadPath(second.Inspection.Id, stage)), "not_found", HttpStatusCode.NotFound);
        var otherTenant = await fixture.CreateTenant(); using var other = await Login(factory, await fixture.CreateUser(otherTenant.Id));
        await Error(other.GetAsync(DocumentsPath(first.Inspection.Id)), "not_found", HttpStatusCode.NotFound);
        await Error(Send(other, HttpMethod.Post, DocumentsPath(first.Inspection.Id), Metadata(Pdf), first.Inspection.Revision), "not_found", HttpStatusCode.NotFound);
        await Error(Upload(other, first.Inspection.Id, stage, Pdf), "not_found", HttpStatusCode.NotFound);
        await Error(other.GetAsync($"/api/v1/documents/{Id(stage)}"), "not_found", HttpStatusCode.NotFound);
        await Error(other.GetAsync(DownloadPath(first.Inspection.Id, stage)), "not_found", HttpStatusCode.NotFound);
        Assert.Equal(0, objects.PutCount);
    }

    [PostgresApiFact]
    public async Task Only_completed_inspections_accept_originals_with_explicit_current_source_revision()
    {
        var objects = new TestObjectStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        var planned = await Planned(client);
        await Error(Send(client, HttpMethod.Post, DocumentsPath(planned.Inspection.Id), Metadata(Pdf), planned.Inspection.Revision), "inspection_not_completed");
        await Error(client.GetAsync(DocumentsPath(planned.Inspection.Id)), "inspection_not_completed");
        var cancelled = await Ok<InspectionChange>(Send(client, HttpMethod.Post, $"/api/v1/equipment/{planned.Equipment.Id}/inspection-cancel",
            new { inspectionId = planned.Inspection.Id, inspectionRevision = planned.Inspection.Revision, reason = "Synthetic cancellation" }, planned.Equipment.Revision));
        await Error(Send(client, HttpMethod.Post, DocumentsPath(cancelled.Inspection.Id), Metadata(Pdf), cancelled.Inspection.Revision), "inspection_not_completed");
        var completed = await Completed(client);
        await Error(Send(client, HttpMethod.Post, DocumentsPath(completed.Inspection.Id), Metadata(Pdf)), "revision_required", HttpStatusCode.PreconditionRequired);
        await Error(Send(client, HttpMethod.Post, DocumentsPath(completed.Inspection.Id), Metadata(Pdf), completed.Inspection.Revision - 1), "revision_conflict", HttpStatusCode.Conflict);
        var key = Guid.NewGuid().ToString("N");
        var staged = await Ok<JsonElement>(Send(client, HttpMethod.Post, DocumentsPath(completed.Inspection.Id), Metadata(Pdf), completed.Inspection.Revision, key));
        using var replay = await Send(client, HttpMethod.Post, DocumentsPath(completed.Inspection.Id), Metadata(Pdf), completed.Inspection.Revision, key);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(Id(staged), Id(await Ok<JsonElement>(Task.FromResult(replay))));
        await Error(Send(client, HttpMethod.Post, DocumentsPath(completed.Inspection.Id), Metadata(Pdf) with { Name = "changed.pdf" }, completed.Inspection.Revision, key),
            "idempotency_conflict", HttpStatusCode.Conflict);
    }

    [PostgresApiFact]
    public async Task Original_metadata_rejects_project_rebinding_unsupported_types_paths_checksums_and_size_budgets()
    {
        var objects = new TestObjectStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        var change = await Completed(client); var body = Metadata(Pdf);
        foreach (var (invalid, code) in new (DocumentRequest Body, string Code)[]
        {
            (body with { ProjectId = Guid.NewGuid() }, "inspection_document_type"),
            (body with { Name = "report.txt", ContentType = "text/plain" }, "inspection_document_type"),
            (body with { Name = "../report.pdf" }, "invalid_filename"),
            (body with { Name = "report.png" }, "unsupported_file_type"),
            (body with { Sha256 = "invalid" }, "invalid_checksum"),
            (body with { Size = 0 }, "invalid_file_size"),
            (body with { Size = DocumentEndpoints.MaximumBytes + 1L }, "invalid_file_size")
        }) await Error(Send(client, HttpMethod.Post, DocumentsPath(change.Inspection.Id), invalid, change.Inspection.Revision), code);
        var list = await Ok<JsonElement>(client.GetAsync(DocumentsPath(change.Inspection.Id))); Assert.Equal(0, list.GetProperty("total").GetInt32());
        Assert.Equal(0, objects.PutCount);
    }

    [PostgresApiFact]
    public async Task Upload_requires_staged_size_hash_type_signature_and_document_revision_and_cannot_replace_verified_bytes()
    {
        var objects = new TestObjectStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        var change = await Completed(client); var stage = await Stage(client, change.Inspection, Pdf);
        await Error(client.GetAsync(DownloadPath(change.Inspection.Id, stage)), "document_not_ready");
        await Error(Upload(client, change.Inspection.Id, stage, Pdf, revision: 99), "revision_conflict", HttpStatusCode.Conflict);
        await Error(Upload(client, change.Inspection.Id, stage, Pdf, "image/png"), "invalid_content_type");
        await Error(Upload(client, change.Inspection.Id, stage, Pdf[..^1]), "invalid_file_size");
        var different = (byte[])Pdf.Clone(); different[10] ^= 1;
        await Error(Upload(client, change.Inspection.Id, stage, different), "checksum_mismatch");
        var wrongSignature = Encoding.UTF8.GetBytes("Not a PDF despite the declared filename"); var bad = await Stage(client, change.Inspection, wrongSignature);
        await Error(Upload(client, change.Inspection.Id, bad, wrongSignature), "invalid_file_content");
        Assert.Equal(0, objects.PutCount);
        var ready = await Ok<JsonElement>(Upload(client, change.Inspection.Id, stage, Pdf));
        await Error(Upload(client, change.Inspection.Id, ready, Pdf), "invalid_transition"); Assert.Equal(1, objects.PutCount);
    }

    [PostgresApiFact]
    public async Task Unversioned_or_corrupted_storage_never_publishes_a_ready_original_and_retry_remains_possible()
    {
        var objects = new TestObjectStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        var change = await Completed(client); var stage = await Stage(client, change.Inspection, Pdf); var key = Guid.NewGuid().ToString("N");
        objects.UnversionedPut = true;
        await Error(Upload(client, change.Inspection.Id, stage, Pdf, key: key), "storage_versioning_required");
        await AssertStaged(tenant.Id, Id(stage));
        objects.UnversionedPut = false; objects.WhitespaceVersion = true;
        await Error(Upload(client, change.Inspection.Id, stage, Pdf, key: key), "storage_versioning_required");
        await AssertStaged(tenant.Id, Id(stage));
        objects.WhitespaceVersion = false; objects.CorruptReads = true;
        await Error(Upload(client, change.Inspection.Id, stage, Pdf, key: key), "document_integrity");
        await AssertStaged(tenant.Id, Id(stage));
        objects.CorruptReads = false; objects.WrongHeadVersion = true;
        await Error(Upload(client, change.Inspection.Id, stage, Pdf, key: key), "document_integrity");
        await AssertStaged(tenant.Id, Id(stage));
        objects.WrongHeadVersion = false;
        var ready = await Ok<JsonElement>(Upload(client, change.Inspection.Id, stage, Pdf, key: key));
        Assert.Equal("Ready", ready.GetProperty("state").GetString()); Assert.Equal(5, objects.PutCount);
        await fixture.InScope(async services => Assert.Equal(1, await services.GetRequiredService<FirstBlickDbContext>().Audit.IgnoreQueryFilters()
            .CountAsync(x => x.TenantId == tenant.Id && x.Action == "document.ready")));
    }

    [PostgresApiFact]
    public async Task Download_checks_bytes_and_binding_on_dedicated_and_generic_routes()
    {
        var objects = new TestObjectStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        var change = await Completed(client); var stage = await Stage(client, change.Inspection, Pdf);
        var ready = await Ok<JsonElement>(Upload(client, change.Inspection.Id, stage, Pdf));
        objects.CorruptReads = true;
        await Error(client.GetAsync(DownloadPath(change.Inspection.Id, ready)), "document_integrity");
        await Error(client.GetAsync($"/api/v1/documents/{Id(ready)}/download"), "document_integrity");
        objects.CorruptReads = false;
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var document = await db.Documents.IgnoreQueryFilters().SingleAsync(x => x.Id == Id(ready));
            document.SourceDigest = new string('0', 64); await db.SaveChangesAsync();
        });
        await Error(client.GetAsync(DownloadPath(change.Inspection.Id, ready)), "inspection_document_mismatch");
        await Error(client.GetAsync($"/api/v1/documents/{Id(ready)}/download"), "inspection_document_mismatch");
        await Error(client.GetAsync($"/api/v1/documents/{Id(ready)}"), "inspection_document_mismatch");
        await Error(client.GetAsync(DocumentsPath(change.Inspection.Id)), "inspection_document_mismatch");
    }

    [PostgresApiFact]
    public async Task A_different_site_manager_cannot_complete_someone_elses_unfinished_upload_but_office_can()
    {
        var objects = new TestObjectStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id, BusinessRole.SiteManager);
        using var client = await Login(factory, user); var change = await Completed(client); var stage = await Stage(client, change.Inspection, Pdf);
        using var otherManager = await Login(factory, await fixture.CreateUser(tenant.Id, BusinessRole.SiteManager));
        await Error(Upload(otherManager, change.Inspection.Id, stage, Pdf), "forbidden", HttpStatusCode.Forbidden);
        using var office = await Login(factory, await fixture.CreateUser(tenant.Id, BusinessRole.Office));
        var ready = await Ok<JsonElement>(Upload(office, change.Inspection.Id, stage, Pdf));
        Assert.Equal("Ready", ready.GetProperty("state").GetString()); Assert.Equal(1, objects.PutCount);
    }

    [PostgresApiFact]
    public async Task Disabled_storage_does_not_create_dangling_document_metadata()
    {
        var objects = new TestObjectStore(); await using var factory = WithStore(objects);
        var tenant = await fixture.CreateTenant(); using var client = await Login(factory, await fixture.CreateUser(tenant.Id));
        var change = await Completed(client); objects.Configured = false;
        await Error(Send(client, HttpMethod.Post, DocumentsPath(change.Inspection.Id), Metadata(Pdf), change.Inspection.Revision), "storage_unconfigured", HttpStatusCode.ServiceUnavailable);
        var list = await Ok<JsonElement>(client.GetAsync(DocumentsPath(change.Inspection.Id))); Assert.Equal(0, list.GetProperty("total").GetInt32());
    }

    private WebApplicationFactory<Program> WithStore(TestObjectStore objects) => fixture.Factory.WithWebHostBuilder(builder =>
        builder.ConfigureServices(services => { services.RemoveAll<IObjectStore>(); services.AddSingleton<IObjectStore>(objects); }));

    private static async Task<HttpClient> Login(WebApplicationFactory<Program> factory, ApiTestUser user)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Tenant-Id", user.TenantId.ToString()); await RefreshCsrf(client);
        await Ok<JsonElement>(Send(client, HttpMethod.Post, "/api/v1/auth/login", new { user.Email, user.Password })); await RefreshCsrf(client);
        return client;
    }

    private static async Task<InspectionChange> Planned(HttpClient client)
    {
        var equipment = await Ok<Equipment>(Send(client, HttpMethod.Post, "/api/v1/equipment", new Equipment
        { Name = "Synthetic retained report equipment", InventoryNumber = "REPORT-" + Guid.NewGuid().ToString("N"), Kind = EquipmentKind.Machine }));
        return await Ok<InspectionChange>(Send(client, HttpMethod.Post, $"/api/v1/equipment/{equipment.Id}/inspection-plan", new
        { kind = EquipmentInspectionKind.Inspection, scheduledOn = DateOnly.FromDateTime(DateTime.UtcNow), title = "Synthetic inspection", notes = "Retained original test" }, equipment.Revision));
    }

    private static async Task<InspectionChange> Completed(HttpClient client)
    {
        var planned = await Planned(client); var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await Ok<InspectionChange>(Send(client, HttpMethod.Post, $"/api/v1/equipment/{planned.Equipment.Id}/inspection-complete", new
        {
            inspectionId = planned.Inspection.Id, inspectionRevision = planned.Inspection.Revision, performedOn = today,
            result = EquipmentInspectionResult.Passed, inspector = "Synthetic inspector", evidenceReference = "Synthetic original reference", notes = "",
            nextInspectionOn = today.AddMonths(6), makeAvailable = true
        }, planned.Equipment.Revision));
    }

    private Task ChangeRole(ApiTestUser user, BusinessRole role) => fixture.InScope(async services =>
    {
        var db = services.GetRequiredService<FirstBlickDbContext>(); var membership = await db.Memberships.IgnoreQueryFilters()
            .SingleAsync(x => x.TenantId == user.TenantId && x.UserId == user.UserId); membership.Role = role; membership.Revision++; await db.SaveChangesAsync();
    });
    private Task AssertStaged(Guid tenantId, Guid id) => fixture.InScope(async services =>
    {
        var db = services.GetRequiredService<FirstBlickDbContext>(); var document = await db.Documents.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenantId && x.Id == id);
        Assert.Equal("Staged", document.State); Assert.Null(document.VersionId); Assert.Equal(1, document.Revision);
        Assert.False(await db.Audit.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId && x.EntityId == id && x.Action == "document.ready"));
    });
    private static Guid Id(JsonElement document) => document.GetProperty("id").GetGuid();
    private static long Revision(JsonElement document) => document.GetProperty("revision").GetInt64();
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string DocumentsPath(Guid inspectionId) => $"/api/v1/equipment-inspections/{inspectionId}/documents";
    private static string DownloadPath(Guid inspectionId, JsonElement document) => $"{DocumentsPath(inspectionId)}/{Id(document)}/download";
    private static DocumentRequest Metadata(byte[] bytes, string name = "original.pdf", string contentType = "application/pdf") => new(name, contentType, bytes.Length, Hash(bytes), null);
    private static Task<JsonElement> Stage(HttpClient client, EquipmentInspection inspection, byte[] bytes, string name = "original.pdf", string contentType = "application/pdf") =>
        Ok<JsonElement>(Send(client, HttpMethod.Post, DocumentsPath(inspection.Id), Metadata(bytes, name, contentType), inspection.Revision));
    private static Task<HttpResponseMessage> Upload(HttpClient client, Guid? inspectionId, JsonElement document, byte[] bytes,
        string contentType = "application/pdf", string? key = null, long? revision = null)
    {
        var path = inspectionId is { } source ? $"{DocumentsPath(source)}/{Id(document)}/content" : $"/api/v1/documents/{Id(document)}/content";
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType); request.Headers.IfMatch.ParseAdd($"\"{revision ?? Revision(document)}\"");
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N")); return client.SendAsync(request);
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

    private sealed class TestObjectStore : IObjectStore
    {
        private readonly ConcurrentDictionary<(string Key, string Version), (byte[] Bytes, string ContentType)> data = new();
        public bool Configured { get; set; } = true;
        public bool UnversionedPut { get; set; }
        public bool WhitespaceVersion { get; set; }
        public bool CorruptReads { get; set; }
        public bool WrongHeadVersion { get; set; }
        public int PutCount { get; private set; }
        public async Task<string?> Put(string key, Stream body, string contentType, CancellationToken ct)
        {
            PutCount++; using var stream = new MemoryStream(); await body.CopyToAsync(stream, ct);
            var version = WhitespaceVersion ? "   " : Guid.NewGuid().ToString("N");
            data[(key, version)] = (stream.ToArray(), contentType); return UnversionedPut ? null : version;
        }
        public Task<ObjectMetadata?> Head(string key, string? version, CancellationToken ct) => Task.FromResult<ObjectMetadata?>(
            version is not null && data.TryGetValue((key, version), out var value) ? new ObjectMetadata(value.Bytes.Length, WrongHeadVersion ? "wrong-version" : version) : null);
        public Task<(Stream Body, string ContentType)> Get(string key, string? version, CancellationToken ct)
        {
            var value = data[(key, version!)]; var bytes = (byte[])value.Bytes.Clone(); if (CorruptReads) bytes[0] ^= 1;
            return Task.FromResult<(Stream, string)>((new MemoryStream(bytes), value.ContentType));
        }
    }
}
