using System.Security.Cryptography;
using System.Text.Json.Serialization;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;
using FirstBlick.Infrastructure.Persistence;
using FirstBlick.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record CaptureTradeSourceRequest(
    [property: JsonRequired] string FileName,
    [property: JsonRequired] string SourceBase64,
    [property: JsonRequired] string ExpectedSha256);
public sealed record TradeSourceView(string Contract, Guid Id, Guid TenantId, string Format,
    Guid DocumentId, string FileName, string Sha256, long Size, string ObjectVersionId,
    Guid ReceivedBy, DateTimeOffset ReceivedAt, string Digest);
public sealed record TradeSourceReceipt(TradeSourceView Source, bool AlreadyStored, bool ContentVerified);
public sealed record TradeSourceCatalogLink(Guid Id, string PositionKey, string Name, string Unit, bool Archived);

/// <summary>
/// Receives exact original bytes, not a normalized XML serialization. The versioned
/// object, immutable source receipt, document metadata, audit and command receipt
/// are published together. A failed database transaction may leave an unreachable
/// storage object; it never publishes a source with unverified bytes.
/// </summary>
public static class TradeSourceEndpoints
{
    private const string CaptureVersion = "firstblick.original-xml/1.0.0";
    private const string Route = "/integrations/v1/trade-sources/{format}";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost(Route, (string format, CaptureTradeSourceRequest body, HttpContext http,
            FirstBlickDbContext db, TenantScope scope, EntityStore store, IObjectStore objects, CommandExecutor commands) =>
            commands.Execute(http, ParentKind(format), "trade.source.capture", body, async ct =>
            {
                TradeSource.ValidateFileName(body.FileName, format);
                Guard.Require(body.SourceBase64 is { Length: > 0 and <= 1_400_000 }, "xml_budget", "Supply at most 1 MiB of Base64-encoded original XML bytes.");
                byte[] bytes;
                try { bytes = Convert.FromBase64String(body.SourceBase64); }
                catch (FormatException) { throw new DomainException("invalid_base64", "The source must contain valid Base64 of the original XML file."); }
                Guard.Require(bytes.Length is > 0 and <= TradeXml.MaximumBytes, "xml_budget", "Original XML is limited to 1 MiB.");
                var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
                Guard.Require(body.ExpectedSha256 == sha256, "checksum_mismatch", "The submitted bytes differ from the file selected for capture.");
                _ = Preview(format, bytes); // Apply the existing bounded, DTD/XXE-blocked parser before writing anything.
                Guard.Require(objects.Configured, "storage_unconfigured", "Configure versioned object storage before retaining original XML.");
                var kind = Kind(format);
                var existing = await db.Entities.AsNoTracking().SingleOrDefaultAsync(x => x.Kind == kind && x.UniqueKey == "sha256:" + sha256, ct);
                if (existing is not null)
                {
                    var previous = Decode(existing, scope.TenantId, format);
                    _ = await ReadOriginal(previous, db, objects, ct);
                    return new TradeSourceReceipt(View(previous), true, true);
                }

                TradeSource source = format == "gaeb" ? new GaebSource() : new ElectronicInvoiceSource();
                source.TenantId = scope.TenantId; source.DocumentId = Guid.NewGuid();
                source.FileName = body.FileName; source.Sha256 = sha256; source.Size = bytes.Length;
                source.ReceivedBy = scope.UserId; source.ReceivedAt = DateTimeOffset.UtcNow;
                source.CreatedAt = source.UpdatedAt = source.ReceivedAt;
                var document = new DocumentObject
                {
                    Id = source.DocumentId, TenantId = scope.TenantId, UploadedBy = scope.UserId,
                    Name = source.FileName, ContentType = "application/octet-stream", Size = source.Size,
                    Sha256 = sha256, State = "Ready", SourceKind = kind, SourceId = source.Id,
                    RendererVersion = CaptureVersion, ObjectKey = ObjectKey(source), CreatedAt = source.ReceivedAt
                };
                using var content = new MemoryStream(bytes, writable: false);
                document.VersionId = await objects.Put(document.ObjectKey, content, document.ContentType, ct);
                Guard.Require(!string.IsNullOrWhiteSpace(document.VersionId) && document.VersionId != "null",
                    "storage_versioning_required", "Original XML requires a non-null immutable storage version.");
                source.ObjectVersionId = document.VersionId!;
                source.Digest = source.ComputeDigest(); source.Validate(); document.SourceDigest = source.Digest;
                // HEAD alone cannot prove content integrity. Read back this exact version before publication.
                _ = await DocumentEndpoints.ReadVerified(objects, document, ct);
                await store.Save(source, "trade.source.capture", true, ct);
                db.Documents.Add(document);
                db.Audit.Add(new AuditEntry
                {
                    TenantId = scope.TenantId, ActorId = scope.UserId, Action = "trade.source.original",
                    Kind = "documents", EntityId = document.Id, Revision = document.Revision, ContentDigest = sha256
                });
                return new TradeSourceReceipt(View(source), false, true);
            })).WithTags("integrations");

        api.MapGet(Route, async (string format, HttpContext http, FirstBlickDbContext db,
            TenantScope scope, CommandExecutor commands, CancellationToken ct) =>
        {
            var kind = Kind(format); await commands.Authorize(kind, false, ct);
            var page = int.TryParse(http.Request.Query["page"], out var number) ? Math.Clamp(number, 1, 100_000) : 1;
            var size = int.TryParse(http.Request.Query["pageSize"], out var count) ? Math.Clamp(count, 1, 50) : 20;
            var query = db.Entities.AsNoTracking().Where(x => x.Kind == kind && !x.Archived);
            var total = await query.CountAsync(ct);
            var rows = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
            return Results.Ok(new { items = rows.Select(row => View(Decode(row, scope.TenantId, format))), total, page, pageSize = size, contentVerified = false });
        }).WithTags("integrations");

        api.MapGet(Route + "/{id:guid}", async (string format, Guid id, FirstBlickDbContext db,
            TenantScope scope, IObjectStore objects, CommandExecutor commands, CancellationToken ct) =>
        {
            await commands.Authorize(Kind(format), false, ct);
            var source = await Find(format, id, db, scope, ct);
            var bytes = await ReadOriginal(source, db, objects, ct);
            return Results.Ok(new { source = View(source), preview = Preview(format, bytes),
                catalogItems = await CatalogLinks(source, db, scope, ct), contentVerified = true });
        }).WithTags("integrations");

        api.MapGet(Route + "/{id:guid}/original", async (string format, Guid id, HttpContext http,
            FirstBlickDbContext db, TenantScope scope, IObjectStore objects, CommandExecutor commands, CancellationToken ct) =>
        {
            await commands.Authorize(Kind(format), false, ct);
            var source = await Find(format, id, db, scope, ct);
            var bytes = await ReadOriginal(source, db, objects, ct);
            http.Response.Headers["X-Content-SHA256"] = source.Sha256;
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            // Do not execute or render externally supplied XML in the application's origin.
            return Results.File(bytes, "application/octet-stream", fileDownloadName: source.FileName);
        }).WithTags("integrations");
    }

    private static string Kind(string format) => format switch
    {
        "gaeb" => "gaeb-sources", "e-invoice" => "e-invoice-sources",
        _ => throw new DomainException("not_found", "This original-source format is not supported.")
    };
    private static string ParentKind(string format) => format switch
    {
        "gaeb" => "catalog-items", "e-invoice" => "supplier-invoices",
        _ => throw new DomainException("not_found", "This original-source format is not supported.")
    };
    private static object Preview(string format, byte[] bytes) => format == "gaeb"
        ? GaebXml.Preview(bytes) : ElectronicInvoiceXml.Preview(bytes);
    private static string ObjectKey(TradeSource source) => $"{source.TenantId:N}/{source.DocumentId:N}/original.xml";
    private static TradeSourceView View(TradeSource s) => new(TradeSource.Contract, s.Id, s.TenantId, s.Format,
        s.DocumentId, s.FileName, s.Sha256, s.Size, s.ObjectVersionId, s.ReceivedBy, s.ReceivedAt, s.Digest);

    private static TradeSource Decode(StoredEntity row, Guid tenant, string format)
    {
        Guard.Require(row.TenantId == tenant && row.Kind == Kind(format), "trade_source_changed", "Source metadata belongs to another business or format.");
        var source = (TradeSource)AppJson.Read(row.Kind, row.Data);
        source.Validate();
        Guard.Require(source.TenantId == row.TenantId && source.Id == row.Id && source.Revision == row.Revision
            && source.Archived == row.Archived && row.UniqueKey == "sha256:" + source.Sha256,
            "trade_source_changed", "Source JSON and stored identity no longer agree.");
        return source;
    }

    private static async Task<TradeSource> Find(string format, Guid id, FirstBlickDbContext db, TenantScope scope, CancellationToken ct)
    {
        var kind = Kind(format);
        var row = await db.Entities.AsNoTracking().SingleOrDefaultAsync(x => x.Kind == kind && x.Id == id, ct)
            ?? throw new DomainException("not_found", "The original source is unavailable in this business.");
        return Decode(row, scope.TenantId, format);
    }

    private static async Task<byte[]> ReadOriginal(TradeSource source, FirstBlickDbContext db, IObjectStore objects, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.DocumentId, ct);
        Guard.Require(document is not null && document.TenantId == source.TenantId
            && document.SourceKind == Kind(source.Format) && document.SourceId == source.Id
            && document.SourceDigest == source.Digest && document.Sha256 == source.Sha256
            && document.Size == source.Size && document.VersionId == source.ObjectVersionId
            && document.Name == source.FileName && document.ObjectKey == ObjectKey(source)
            && document.ContentType == "application/octet-stream" && document.RendererVersion == CaptureVersion
            && document.State is "Ready" or "Archived",
            "document_integrity", "The stored original no longer matches its immutable source receipt.");
        return await DocumentEndpoints.ReadVerified(objects, document!, ct);
    }

    private static async Task<IReadOnlyList<TradeSourceCatalogLink>> CatalogLinks(TradeSource source,
        FirstBlickDbContext db, TenantScope scope, CancellationToken ct)
    {
        if (source.Format != "gaeb") return [];
        var prefix = "gaeb:" + source.Sha256 + ":line:";
        var rows = await db.Entities.AsNoTracking().Where(x => x.Kind == "catalog-items" && x.UniqueKey != null && x.UniqueKey.StartsWith(prefix))
            .OrderBy(x => x.Id).Take(1001).ToListAsync(ct);
        Guard.Require(rows.Count <= 1000, "source_link_budget", "The source's catalogue history exceeds its supported position budget.");
        var result = new List<TradeSourceCatalogLink>();
        foreach (var row in rows)
        {
            var item = AppJson.Read<CatalogItem>(row.Data); item.Validate();
            Guard.Require(item.Id == row.Id && item.TenantId == scope.TenantId && item.Revision == row.Revision
                && item.Archived == row.Archived && item.GaebOrigin is not null && item.GaebOrigin.SourceSha256 == source.Sha256
                && EntityProjection.UniqueKey(item) == row.UniqueKey,
                "trade_source_changed", "An imported catalogue reference is inconsistent with its original source.");
            result.Add(new(item.Id, item.GaebOrigin!.PositionKey, item.Name, item.Unit, item.Archived));
        }
        return result.AsReadOnly();
    }
}
