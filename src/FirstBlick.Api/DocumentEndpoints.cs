using System.Security.Cryptography;
using System.Text;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Documents;
using FirstBlick.Infrastructure.Persistence;
using FirstBlick.Infrastructure.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record DocumentRequest(string Name, string ContentType, long Size, string Sha256, Guid? ProjectId);

public static class DocumentEndpoints
{
    public const int MaximumBytes = 25 * 1024 * 1024;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/documents", async (HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, CommandExecutor commands, CancellationToken ct) =>
        {
            await commands.Authorize("documents", false, ct);
            var page = int.TryParse(http.Request.Query["page"], out var p) ? Math.Clamp(p, 1, 100_000) : 1;
            var pageSize = int.TryParse(http.Request.Query["pageSize"], out var s) ? Math.Clamp(s, 1, 100) : 30;
            var search = http.Request.Query["search"].ToString().Trim(); Guard.Text(search, "Search", 200, false);
            var query = db.Documents.AsNoTracking().Where(x => x.State != "Archived");
            var allowedSources = BusinessRegistry.Entities.Keys.Where(k => AccessRules.CanRead(scope.Role, k)).ToArray();
            query = query.Where(x => x.SourceKind == null || allowedSources.Contains(x.SourceKind));
            if (search.Length > 0) query = query.Where(x => x.Name.ToLower().Contains(search.ToLower()));
            if (Guid.TryParse(http.Request.Query["projectId"], out var project)) query = query.Where(x => x.ProjectId == project);
            if (scope.Role == BusinessRole.Worker)
            {
                var ids = await RecordPolicy.VisibleIds("projects", scope, store, ct) ?? [];
                query = query.Where(x => x.ProjectId.HasValue && ids.Contains(x.ProjectId.Value));
            }
            var total = await query.CountAsync(ct);
            var rows = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            return Results.Ok(new { items = rows.Select(View), total, page, pageSize });
        }).WithTags("documents");

        api.MapGet("/documents/{id:guid}", async (Guid id, HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, CommandExecutor commands, CancellationToken ct) =>
        {
            await commands.Authorize("documents", false, ct);
            var doc = await Find(db, id, ct); await EnsureVisible(doc, scope, store, ct);
            http.Response.Headers.ETag = $"\"{doc.Revision}\"";
            return Results.Ok(View(doc));
        }).WithTags("documents");

        api.MapPost("/documents", (DocumentRequest body, HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, IObjectStore objects, CommandExecutor commands) =>
            commands.Execute(http, "documents", "document.stage", body, async ct =>
            {
                Guard.Require(objects.Configured, "storage_unconfigured", "Configure versioned object storage before uploading documents.");
                ValidateMetadata(body);
                if (body.ProjectId.HasValue) { _ = await store.Get<Project>(body.ProjectId.Value, ct); await RecordPolicy.EnsureVisible("projects", body.ProjectId.Value, scope, store, ct); }
                Guard.Require(scope.Role != BusinessRole.Worker || body.ProjectId.HasValue, "forbidden", "Site uploads must belong to an assigned project.");
                var doc = new DocumentObject { TenantId = scope.TenantId, UploadedBy = scope.UserId, Name = body.Name,
                    ContentType = body.ContentType, Size = body.Size, Sha256 = body.Sha256.ToLowerInvariant(), ProjectId = body.ProjectId,
                    ProjectKind = body.ProjectId.HasValue ? "projects" : null };
                doc.ObjectKey = $"{scope.TenantId:N}/{doc.Id:N}/original";
                db.Documents.Add(doc); Audit(db, scope, doc, "document.stage"); return View(doc);
            })).WithTags("documents");

        api.MapPut("/documents/{id:guid}/content", Upload).WithTags("documents");

        api.MapGet("/documents/{id:guid}/download", async (Guid id, HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, IObjectStore objects, CommandExecutor commands, CancellationToken ct) =>
        {
            await commands.Authorize("documents", false, ct);
            var doc = await Find(db, id, ct); await EnsureVisible(doc, scope, store, ct);
            Guard.Require(doc.State is "Ready" or "Archived", "document_not_ready", "The document upload has not been verified.");
            var bytes = await ReadVerified(objects, doc, ct);
            http.Response.Headers["X-Content-SHA256"] = doc.Sha256;
            return Results.File(bytes, doc.ContentType, fileDownloadName: doc.Name, lastModified: doc.CreatedAt);
        }).WithTags("documents");

        api.MapPost("/documents/{id:guid}/archive", (Guid id, HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, CommandExecutor commands) =>
            commands.Execute(http, "documents", "document.archive", new { id }, async ct =>
            {
                var doc = await Find(db, id, ct); await EnsureVisible(doc, scope, store, ct); CommandExecutor.Match(http, doc.Revision);
                Guard.Require(scope.Role is BusinessRole.ManagingDirector or BusinessRole.Office, "forbidden", "Only business administration can archive documents.");
                Guard.Require(doc.State == "Ready", "invalid_transition", "Only a verified document can be archived.");
                doc.State = "Archived"; doc.Revision++; Audit(db, scope, doc, "document.archive"); return View(doc);
            })).WithTags("documents");

        api.MapPost("/quotes/{id:guid}/pdf", (Guid id, HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, IObjectStore objects, CommandExecutor commands) =>
            commands.Execute(http, "quotes", "quote.pdf", new { id }, async ct =>
            {
                var quote = await store.Get<Quote>(id, ct); CommandExecutor.Match(http, quote.Revision);
                Guard.Require(quote.Snapshot is not null, "missing_snapshot", "Issue and freeze the quotation before producing its PDF.");
                var bytes = BusinessPdf.Quote(quote);
                return await SaveGenerated(db, scope, objects, bytes, $"Angebot-{quote.Number}.pdf", quote.ProjectId, "quotes", id, quote.IssuedDigest, ct);
            })).WithTags("documents");

        api.MapPost("/invoices/{id:guid}/pdf", (Guid id, HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, IObjectStore objects, CommandExecutor commands) =>
            commands.Execute(http, "invoices", "invoice.pdf", new { id }, async ct =>
            {
                var invoice = await store.Get<Invoice>(id, ct); CommandExecutor.Match(http, invoice.Revision);
                Guard.Require(invoice.Status == InvoiceStatus.Issued, "invalid_transition", "Issue the invoice before producing its PDF.");
                var order = await store.Get<Order>(invoice.OrderId, ct);
                return await SaveGenerated(db, scope, objects, BusinessPdf.Invoice(invoice), $"Rechnung-{invoice.Number}.pdf", order.ProjectId, "invoices", id, invoice.IssuedDigest, ct);
            })).WithTags("documents");
    }

    private static async Task<IResult> Upload(Guid id, HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, IObjectStore objects, CommandExecutor commands)
    {
        var ct = http.RequestAborted; await commands.Authorize("documents", true, ct);
        var initial = await Find(db, id, ct); await EnsureVisible(initial, scope, store, ct);
        Guard.Require(initial.UploadedBy == scope.UserId || scope.Role is BusinessRole.ManagingDirector or BusinessRole.Office, "forbidden", "You cannot complete another person's upload.");
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaximumBytes;
        Guard.Require(http.Request.ContentLength == initial.Size && initial.Size <= MaximumBytes, "invalid_file_size", "Content-Length must equal the staged file size (maximum 25 MiB).");
        Guard.Require(http.Request.ContentType?.Split(';')[0] == initial.ContentType, "invalid_content_type", "The upload content type differs from its staged metadata.");
        using var buffer = new MemoryStream((int)initial.Size);
        var block = new byte[81920]; long length = 0;
        while (true)
        {
            var count = await http.Request.Body.ReadAsync(block, ct); if (count == 0) break;
            length += count; Guard.Require(length <= initial.Size, "payload_too_large", "The upload exceeds its declared size.");
            await buffer.WriteAsync(block.AsMemory(0, count), ct);
        }
        Guard.Require(length == initial.Size, "invalid_file_size", "The upload was interrupted or truncated.");
        var bytes = buffer.ToArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Guard.Require(digest == initial.Sha256, "checksum_mismatch", "The upload checksum differs from the original file.");
        ValidateContent(initial.ContentType, bytes);
        return await commands.Execute(http, "documents", "document.upload", new { id, size = length, sha256 = digest }, async token =>
        {
            // Reload under the same tenant lock used by all metadata mutations.
            await db.Entry(initial).ReloadAsync(token);
            await EnsureVisible(initial, scope, store, token); CommandExecutor.Match(http, initial.Revision);
            Guard.Require(initial.UploadedBy == scope.UserId || scope.Role is BusinessRole.ManagingDirector or BusinessRole.Office, "forbidden", "Upload authority changed while the content was being transferred.");
            Guard.Require(initial.State == "Staged", "invalid_transition", "This document already has a verified version; create a new document for changed bytes.");
            buffer.Position = 0; initial.VersionId = await objects.Put(initial.ObjectKey, buffer, initial.ContentType, token);
            Guard.Require(!string.IsNullOrEmpty(initial.VersionId) && initial.VersionId != "null", "storage_versioning_required", "Enable bucket versioning; an immutable version identifier is required.");
            var metadata = await objects.Head(initial.ObjectKey, initial.VersionId, token);
            Guard.Require(metadata is not null && metadata.Size == initial.Size && metadata.VersionId == initial.VersionId, "document_integrity", "The stored file could not be verified.");
            initial.State = "Ready"; initial.Revision++; Audit(db, scope, initial, "document.ready"); return View(initial);
        });
    }

    public static async Task<object> SaveGenerated(FirstBlickDbContext db, TenantScope scope, IObjectStore objects, byte[] bytes, string name, Guid projectId, string sourceKind, Guid sourceId, string digest, CancellationToken ct)
    {
        Guard.Require(objects.Configured, "storage_unconfigured", "Configure versioned object storage before creating stored PDFs.");
        Guard.Require(bytes.Length is > 0 and <= MaximumBytes, "payload_too_large", "The generated report exceeds the document size limit.");
        var doc = new DocumentObject { TenantId = scope.TenantId, UploadedBy = scope.UserId, Name = name, ContentType = "application/pdf", Size = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), State = "Ready", ProjectId = projectId, ProjectKind = "projects",
            SourceKind = sourceKind, SourceId = sourceId, SourceDigest = digest, RendererVersion = BusinessPdf.Version };
        doc.ObjectKey = $"{scope.TenantId:N}/{doc.Id:N}/report.pdf";
        using var body = new MemoryStream(bytes); doc.VersionId = await objects.Put(doc.ObjectKey, body, doc.ContentType, ct);
        Guard.Require(!string.IsNullOrEmpty(doc.VersionId) && doc.VersionId != "null", "storage_versioning_required", "Enable bucket versioning before generating reports.");
        var metadata = await objects.Head(doc.ObjectKey, doc.VersionId, ct);
        Guard.Require(metadata is not null && metadata.Size == doc.Size && metadata.VersionId == doc.VersionId, "document_integrity", "The generated report could not be verified in storage.");
        db.Documents.Add(doc); Audit(db, scope, doc, "document.generated"); return View(doc);
    }

    public static object View(DocumentObject d) => new { d.Id, d.TenantId, d.Name, d.ContentType, d.Size, d.Sha256, d.VersionId, d.State, d.Revision, d.ProjectId, d.SourceKind, d.SourceId, d.SourceDigest, d.RendererVersion, d.CreatedAt };

    internal static async Task<byte[]> ReadVerified(IObjectStore objects, DocumentObject doc, CancellationToken ct)
    {
        Guard.Require(doc.Size is > 0 and <= MaximumBytes, "document_integrity", "The stored document size is outside the verified limit.");
        var metadata = await objects.Head(doc.ObjectKey, doc.VersionId, ct);
        Guard.Require(metadata is not null && metadata.Size == doc.Size && metadata.VersionId == doc.VersionId,
            "document_integrity", "The stored document version is missing or its size differs. Restore or investigate it before download.");
        var (source, _) = await objects.Get(doc.ObjectKey, doc.VersionId, ct);
        await using var stream = source;
        using var buffer = new MemoryStream((int)doc.Size);
        var block = new byte[81920]; long length = 0;
        while (true)
        {
            var count = await stream.ReadAsync(block, ct); if (count == 0) break;
            length += count;
            Guard.Require(length <= doc.Size, "document_integrity", "The object content exceeds its recorded size.");
            await buffer.WriteAsync(block.AsMemory(0, count), ct);
        }
        var bytes = buffer.ToArray();
        Guard.Require(length == doc.Size && Convert.ToHexStringLower(SHA256.HashData(bytes)) == doc.Sha256,
            "document_integrity", "The downloaded bytes do not match their recorded SHA-256 checksum. Restore or investigate this version.");
        return bytes;
    }
    private static async Task<DocumentObject> Find(FirstBlickDbContext db, Guid id, CancellationToken ct) =>
        await db.Documents.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new DomainException("not_found", "The document is unavailable in this business.");

    private static async Task EnsureVisible(DocumentObject doc, TenantScope scope, EntityStore store, CancellationToken ct)
    {
        if (scope.Role == BusinessRole.Worker)
        {
            Guard.Require(doc.ProjectId.HasValue, "forbidden", "The document is not part of an assigned project.");
            await RecordPolicy.EnsureVisible("projects", doc.ProjectId.Value, scope, store, ct);
        }
        if (doc.SourceId.HasValue && doc.SourceKind is not null)
        {
            Guard.Require(AccessRules.CanRead(scope.Role, doc.SourceKind), "forbidden", "The source record is not available to your role.");
            await RecordPolicy.EnsureVisible(doc.SourceKind, doc.SourceId.Value, scope, store, ct);
        }
    }

    private static void ValidateMetadata(DocumentRequest body)
    {
        Guard.Text(body.Name, "Filename", 180); Guard.OptionalId(body.ProjectId, "ProjectId");
        Guard.Require(body.Name == Path.GetFileName(body.Name) && !body.Name.Any(c => char.IsControl(c) || c is '/' or '\\'), "invalid_filename", "Use a plain filename without path or control characters.");
        Guard.Require(body.Size is > 0 and <= MaximumBytes, "invalid_file_size", "Files must contain between one byte and 25 MiB.");
        Guard.Require(body.Sha256?.Length == 64 && body.Sha256.All(char.IsAsciiHexDigit), "invalid_checksum", "Provide the original file SHA-256 checksum.");
        var extension = Path.GetExtension(body.Name).ToLowerInvariant();
        Guard.Require((body.ContentType, extension) is ("application/pdf", ".pdf") or ("image/png", ".png") or ("image/jpeg", ".jpg" or ".jpeg") or ("text/plain", ".txt") or ("text/csv", ".csv"),
            "unsupported_file_type", "Supported attachments are PDF, PNG, JPEG, UTF-8 text and CSV.");
    }

    private static void ValidateContent(string contentType, byte[] bytes)
    {
        var valid = contentType switch
        {
            "application/pdf" => bytes.AsSpan().StartsWith("%PDF-"u8),
            "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/jpeg" => bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255,
            "text/plain" or "text/csv" => !bytes.Contains((byte)0),
            _ => false
        };
        if (valid && contentType.StartsWith("text/", StringComparison.Ordinal))
        {
            try { _ = new UTF8Encoding(false, true).GetString(bytes); } catch (DecoderFallbackException) { valid = false; }
        }
        Guard.Require(valid, "invalid_file_content", "The file signature or encoding does not match its declared content type.");
    }

    private static void Audit(FirstBlickDbContext db, TenantScope scope, DocumentObject doc, string action) => db.Audit.Add(new AuditEntry
    { TenantId = scope.TenantId, ActorId = scope.UserId, Action = action, Kind = "documents", EntityId = doc.Id, Revision = doc.Revision, ContentDigest = ContentDigest.Of(View(doc)) });
}
