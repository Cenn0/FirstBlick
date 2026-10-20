using System.Security.Cryptography;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using FirstBlick.Infrastructure.Storage;

namespace FirstBlick.Api;

/// <summary>
/// Writes immutable original/validation/receipt files inside an existing business command.
/// Database metadata and the command receipt commit atomically. Object-store writes are
/// versioned external side effects; an interrupted transaction may leave an unreferenced
/// object version, never a publicly downloadable partially booked invoice.
/// </summary>
internal static class ValidatedExchangeDocuments
{
    public static async Task<DocumentObject> Save(FirstBlickDbContext db, TenantScope scope, IObjectStore objects,
        Guid id, byte[] bytes, string name, string contentType, string sourceKind, Guid sourceId,
        string evidenceDigest, Guid? projectId, string procedureVersion, CancellationToken ct)
    {
        Guard.Require(bytes.Length is > 0 and <= 25 * 1024 * 1024, "document_budget", "The retained exchange file exceeds the document budget.");
        Guard.Require(contentType is "application/xml" or "application/json" or "application/pdf" or "image/svg+xml",
            "invalid_exchange_type", "This exchange artifact type is not supported.");
        Guard.Text(name, "Exchange filename", 180);
        Guard.Require(name == Path.GetFileName(name) && !name.Any(c => char.IsControl(c) || c is '/' or '\\'), "invalid_filename", "Use a plain exchange filename.");
        var doc = new DocumentObject
        {
            Id = id, TenantId = scope.TenantId, UploadedBy = scope.UserId, Name = name, ContentType = contentType,
            Size = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), State = "Ready",
            ProjectId = projectId, ProjectKind = projectId.HasValue ? "projects" : null,
            SourceKind = sourceKind, SourceId = sourceId, SourceDigest = evidenceDigest, RendererVersion = procedureVersion
        };
        doc.ObjectKey = $"{scope.TenantId:N}/{id:N}/exchange.bin";
        using var stream = new MemoryStream(bytes, writable: false);
        doc.VersionId = await objects.Put(doc.ObjectKey, stream, contentType, ct);
        Guard.Require(!string.IsNullOrEmpty(doc.VersionId) && doc.VersionId != "null", "storage_versioning_required", "Exchange originals require immutable object versions.");
        // Read back the original version, not only its declared content length.
        var verified = await DocumentEndpoints.ReadVerified(objects, doc, ct);
        Guard.Require(verified.Length == bytes.Length, "document_integrity", "The retained exchange version could not be verified.");
        db.Documents.Add(doc);
        db.Audit.Add(new AuditEntry { TenantId = scope.TenantId, ActorId = scope.UserId, Action = "document.exchange-retained",
            Kind = "documents", EntityId = id, Revision = doc.Revision, ContentDigest = ContentDigest.Of(DocumentEndpoints.View(doc)) });
        return doc;
    }
}
