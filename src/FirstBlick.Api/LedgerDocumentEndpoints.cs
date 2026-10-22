using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using FirstBlick.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

/// <summary>Original files supplement immutable ledger entries without changing their content or totals.</summary>
public static class LedgerDocumentEndpoints
{
    public const string CommunicationOriginalVersion = "customer-communication-original-v1";
    public const string VehicleCostOriginalVersion = "vehicle-cost-original-v1";

    public static bool Supports(string? kind) => kind is "customer-communications" or "vehicle-costs";

    public static void Map(RouteGroupBuilder api)
    {
        foreach (var kind in new[] { "customer-communications", "vehicle-costs" })
        {
            api.MapGet($"/{kind}/{{id:guid}}/document-context", async (Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
            {
                await c.Commands.Authorize(kind, false, ct);
                var source = await Source(kind, id, c.Store, c.Db, ct);
                c.Http.Response.Headers.CacheControl = "private, no-store";
                return Results.Ok(new { sourceKind = kind, source.Title, source.ParentKind, source.ParentId,
                    source = new { source.Id, source.TenantId, source.Revision, source.Digest, source.RecordedAt } });
            }).WithTags(kind);

            api.MapGet($"/{kind}/{{id:guid}}/documents", async (Guid id, [AsParameters] BusinessCommandServices c,
                CancellationToken ct, int page = 1) =>
            {
                await c.Commands.Authorize(kind, false, ct);
                var source = await Source(kind, id, c.Store, c.Db, ct);
                page = Math.Clamp(page, 1, 100_000); const int pageSize = 20;
                var query = c.Db.Documents.AsNoTracking().Where(x => x.SourceKind == kind && x.SourceId == id);
                var total = await query.CountAsync(ct);
                var documents = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
                    .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
                foreach (var document in documents) ValidateBinding(document, source);
                var modules = await c.Db.Tenants.Where(x => x.Id == c.Scope.TenantId).Select(x => x.Modules).SingleAsync(ct);
                var canAttach = source.CanAttach && AccessRules.CanWrite(c.Scope.Role, kind) && modules.Contains(AccessRules.ModuleFor(kind));
                c.Http.Response.Headers.CacheControl = "private, no-store";
                return Results.Ok(new { sourceKind = kind, sourceId = id, sourceDigest = source.Digest,
                    canAttach, items = documents.Select(DocumentEndpoints.View), total, page, pageSize });
            }).WithTags(kind);

            api.MapPost($"/{kind}/{{id:guid}}/documents", (Guid id, DocumentRequest body,
                [AsParameters] BusinessCommandServices c, IObjectStore objects) =>
                c.Commands.Execute(c.Http, kind, "ledger.document_stage", body, async ct =>
                {
                    var source = await Writable(kind, id, c.Store, c.Db, ct); CommandExecutor.Match(c.Http, source.Revision);
                    Guard.Require(objects.Configured, "storage_unconfigured", "Configure versioned object storage before uploading originals.");
                    DocumentEndpoints.ValidateMetadata(body);
                    Guard.Require(body.ProjectId is null && body.ContentType is "application/pdf" or "image/png" or "image/jpeg",
                        "ledger_document_type", "Ledger originals must be PDF, PNG or JPEG files bound only to their original ledger entry.");
                    var document = new DocumentObject
                    {
                        TenantId = c.Scope.TenantId, UploadedBy = c.Scope.UserId, Name = body.Name, ContentType = body.ContentType,
                        Size = body.Size, Sha256 = body.Sha256.ToLowerInvariant(), SourceKind = kind, SourceId = id,
                        SourceDigest = source.Digest, RendererVersion = source.Version
                    };
                    document.ObjectKey = $"{c.Scope.TenantId:N}/{document.Id:N}/ledger-original";
                    ValidateBinding(document, source); c.Db.Documents.Add(document);
                    DocumentEndpoints.Audit(c.Db, c.Scope, document, "ledger.document_stage");
                    return DocumentEndpoints.View(document);
                }, async ct => { _ = await Writable(kind, id, c.Store, c.Db, ct); })).WithTags(kind);

            api.MapPut($"/{kind}/{{id:guid}}/documents/{{documentId:guid}}/content", (Guid id, Guid documentId,
                [AsParameters] BusinessCommandServices c, IObjectStore objects) =>
                DocumentEndpoints.Upload(documentId, c.Http, c.Db, c.Scope, c.Store, objects, c.Commands, new EntityReference(kind, id))).WithTags(kind);

            api.MapGet($"/{kind}/{{id:guid}}/documents/{{documentId:guid}}/download", async (Guid id, Guid documentId,
                [AsParameters] BusinessCommandServices c, IObjectStore objects, CancellationToken ct) =>
            {
                await c.Commands.Authorize(kind, false, ct);
                var source = await Source(kind, id, c.Store, c.Db, ct);
                var document = await c.Db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == documentId && x.SourceKind == kind && x.SourceId == id, ct)
                    ?? throw new DomainException("not_found", "The original is unavailable for this ledger entry.");
                ValidateBinding(document, source);
                Guard.Require(document.State is "Ready" or "Archived", "document_not_ready", "The original upload has not been verified.");
                var bytes = await DocumentEndpoints.ReadVerified(objects, document, ct);
                DocumentEndpoints.ValidateContent(document.ContentType, bytes);
                c.Http.Response.Headers["X-Content-SHA256"] = document.Sha256;
                c.Http.Response.Headers.CacheControl = "private, no-store";
                return Results.File(bytes, document.ContentType, fileDownloadName: document.Name, lastModified: document.CreatedAt);
            }).WithTags(kind);
        }
    }

    internal static async Task ValidateBinding(DocumentObject document, EntityStore store, FirstBlickDbContext db, CancellationToken ct)
    {
        Guard.Require(Supports(document.SourceKind) && document.SourceId.HasValue,
            "ledger_document_mismatch", "The original ledger source is missing.");
        ValidateBinding(document, await Source(document.SourceKind!, document.SourceId.Value, store, db, ct));
    }

    internal static async Task EnsureWritable(DocumentObject document, EntityStore store, FirstBlickDbContext db, CancellationToken ct)
    {
        Guard.Require(Supports(document.SourceKind) && document.SourceId.HasValue,
            "ledger_document_mismatch", "The original ledger source is missing.");
        ValidateBinding(document, await Writable(document.SourceKind!, document.SourceId.Value, store, db, ct));
    }

    private static void ValidateBinding(DocumentObject document, LedgerSource source)
    {
        Guard.Require(document.TenantId == source.TenantId && document.SourceKind == source.Kind && document.SourceId == source.Id
            && document.SourceDigest == source.Digest && document.RendererVersion == source.Version
            && document.ProjectId is null && document.ProjectKind is null && document.CreatedAt >= source.RecordedAt
            && document.ContentType is "application/pdf" or "image/png" or "image/jpeg",
            "ledger_document_mismatch", "The original file does not match the retained ledger entry and its frozen digest.");
        Guard.Require(document.State is "Staged" or "Ready" or "Archived"
            && (document.State == "Staged" ? document.VersionId is null && document.Revision == 1
                : !string.IsNullOrWhiteSpace(document.VersionId) && document.VersionId != "null" && document.Revision >= 2)
            && document.Sha256 is { Length: 64 } && document.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "ledger_document_mismatch", "The original file has no consistent immutable storage evidence.");
        DocumentEndpoints.ValidateMetadata(new(document.Name, document.ContentType, document.Size, document.Sha256, null));
    }

    private static async Task<LedgerSource> Writable(string kind, Guid id, EntityStore store, FirstBlickDbContext db, CancellationToken ct)
    {
        var source = await Source(kind, id, store, db, ct);
        Guard.Require(source.CanAttach, "ledger_original_closed", "Archived or corrected ledger entries retain their files but cannot receive new uploads.");
        return source;
    }

    private static async Task<LedgerSource> Source(string kind, Guid id, EntityStore store, FirstBlickDbContext db, CancellationToken ct)
    {
        if (kind == "customer-communications")
        {
            var entry = await Fresh<CustomerCommunication>(id, db, ct); entry.Validate();
            var customer = await Fresh<Customer>(entry.CustomerId, db, ct); customer.Validate();
            var key = $"communication:{id:N}";
            var corrected = await db.Entities.AsNoTracking().AnyAsync(x => x.Kind == "customer-communication-voids" && x.UniqueKey == key, ct);
            return new(kind, entry.Id, entry.TenantId, entry.Revision, entry.Digest, entry.RecordedAt,
                CommunicationOriginalVersion, !customer.Archived && !corrected, entry.Subject, "customers", customer.Id);
        }
        Guard.Require(kind == "vehicle-costs", "ledger_document_mismatch", "Choose a supported original ledger entry.");
        var cost = await Fresh<VehicleCost>(id, db, ct); cost.Validate();
        var vehicle = await Fresh<Equipment>(cost.VehicleId, db, ct); vehicle.Validate();
        var reversalLinks = db.Links.Where(x => x.SourceKind == "vehicle-ledger-reversals" && x.TargetKind == kind && x.TargetId == id);
        var reversed = await db.Entities.AsNoTracking().AnyAsync(x => x.Kind == "vehicle-ledger-reversals" && reversalLinks.Any(l => l.SourceId == x.Id), ct);
        return new(kind, cost.Id, cost.TenantId, cost.Revision, cost.Digest, cost.RecordedAt,
            VehicleCostOriginalVersion, !vehicle.Archived && !reversed, cost.Reference, "equipment", vehicle.Id);
    }

    // Upload reads happen before and after the tenant lock. Never reuse a tracked
    // parent snapshot: another request may have archived it while bytes arrived.
    private static async Task<T> Fresh<T>(Guid id, FirstBlickDbContext db, CancellationToken ct) where T : TenantEntity
    {
        var kind = BusinessRegistry.KindOf<T>();
        var data = await db.Entities.AsNoTracking().Where(x => x.Kind == kind && x.Id == id).Select(x => x.Data).SingleOrDefaultAsync(ct)
            ?? throw new DomainException("not_found", "The original source is unavailable in this business.");
        return AppJson.Read<T>(data);
    }

    private sealed record LedgerSource(string Kind, Guid Id, Guid TenantId, long Revision, string Digest,
        DateTimeOffset RecordedAt, string Version, bool CanAttach, string Title, string ParentKind, Guid ParentId);
}
