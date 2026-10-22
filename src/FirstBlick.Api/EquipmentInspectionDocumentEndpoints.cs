using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using FirstBlick.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

/// <summary>Retained originals supplement an inspection without rewriting its sealed completion.</summary>
public static class EquipmentInspectionDocumentEndpoints
{
    public const string OriginalVersion = "equipment-inspection-original-v1";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/equipment-inspections/{id:guid}/documents", async (Guid id, [AsParameters] BusinessCommandServices c,
            CancellationToken ct, int page = 1) =>
        {
            await c.Commands.Authorize("equipment-inspections", false, ct);
            var inspection = await Completed(c.Store, id, ct);
            page = Math.Clamp(page, 1, 100_000); const int pageSize = 20;
            var query = c.Db.Documents.AsNoTracking().Where(x => x.SourceKind == "equipment-inspections" && x.SourceId == id);
            var total = await query.CountAsync(ct);
            var documents = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            foreach (var document in documents) ValidateBinding(document, inspection);
            return Results.Ok(new { inspectionId = id, inspection.EquipmentId, sourceDigest = inspection.Completion!.Digest,
                items = documents.Select(DocumentEndpoints.View), total, page, pageSize });
        }).WithTags("equipment-inspections");

        api.MapPost("/equipment-inspections/{id:guid}/documents", (Guid id, DocumentRequest body,
            [AsParameters] BusinessCommandServices c, IObjectStore objects) =>
            c.Commands.Execute(c.Http, "equipment-inspections", "equipment.inspection_document_stage", body, async ct =>
            {
                var inspection = await Completed(c.Store, id, ct); CommandExecutor.Match(c.Http, inspection.Revision);
                Guard.Require(objects.Configured, "storage_unconfigured", "Configure versioned object storage before uploading originals.");
                DocumentEndpoints.ValidateMetadata(body);
                Guard.Require(body.ProjectId is null && body.ContentType is "application/pdf" or "image/png" or "image/jpeg",
                    "inspection_document_type", "Inspection originals must be PDF, PNG or JPEG files bound only to this inspection.");
                var document = new DocumentObject
                {
                    TenantId = c.Scope.TenantId, UploadedBy = c.Scope.UserId, Name = body.Name, ContentType = body.ContentType,
                    Size = body.Size, Sha256 = body.Sha256.ToLowerInvariant(), SourceKind = "equipment-inspections", SourceId = id,
                    SourceDigest = inspection.Completion!.Digest, RendererVersion = OriginalVersion
                };
                document.ObjectKey = $"{c.Scope.TenantId:N}/{document.Id:N}/inspection-original";
                ValidateBinding(document, inspection);
                c.Db.Documents.Add(document); DocumentEndpoints.Audit(c.Db, c.Scope, document, "equipment.inspection_document_stage");
                return DocumentEndpoints.View(document);
            }, async ct => { _ = await Completed(c.Store, id, ct); })).WithTags("equipment-inspections");

        api.MapPut("/equipment-inspections/{id:guid}/documents/{documentId:guid}/content", (Guid id, Guid documentId,
            [AsParameters] BusinessCommandServices c, IObjectStore objects) =>
            DocumentEndpoints.Upload(documentId, c.Http, c.Db, c.Scope, c.Store, objects, c.Commands, id)).WithTags("equipment-inspections");

        api.MapGet("/equipment-inspections/{id:guid}/documents/{documentId:guid}/download", async (Guid id, Guid documentId,
            [AsParameters] BusinessCommandServices c, IObjectStore objects, CancellationToken ct) =>
        {
            await c.Commands.Authorize("equipment-inspections", false, ct);
            var inspection = await Completed(c.Store, id, ct);
            var document = await c.Db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == documentId
                && x.SourceKind == "equipment-inspections" && x.SourceId == id, ct)
                ?? throw new DomainException("not_found", "The original is unavailable for this inspection.");
            ValidateBinding(document, inspection);
            Guard.Require(document.State is "Ready" or "Archived", "document_not_ready", "The original upload has not been verified.");
            var bytes = await DocumentEndpoints.ReadVerified(objects, document, ct);
            c.Http.Response.Headers["X-Content-SHA256"] = document.Sha256;
            c.Http.Response.Headers.CacheControl = "no-store";
            return Results.File(bytes, document.ContentType, fileDownloadName: document.Name, lastModified: document.CreatedAt);
        }).WithTags("equipment-inspections");
    }

    internal static async Task ValidateBinding(DocumentObject document, EntityStore store, CancellationToken ct)
    {
        Guard.Require(document.SourceId.HasValue, "inspection_document_mismatch", "The original inspection is missing.");
        ValidateBinding(document, await Completed(store, document.SourceId.Value, ct));
    }

    private static void ValidateBinding(DocumentObject document, EquipmentInspection inspection)
    {
        Guard.Require(document.TenantId == inspection.TenantId && document.SourceKind == "equipment-inspections"
            && document.SourceId == inspection.Id && document.ProjectId is null && document.ProjectKind is null
            && document.SourceDigest == inspection.Completion!.Digest && document.RendererVersion == OriginalVersion
            && document.CreatedAt >= inspection.Completion.RecordedAt
            && document.ContentType is "application/pdf" or "image/png" or "image/jpeg",
            "inspection_document_mismatch", "The original is not bound to this completed inspection and its frozen evidence.");
    }

    private static async Task<EquipmentInspection> Completed(EntityStore store, Guid id, CancellationToken ct)
    {
        var inspection = await store.Get<EquipmentInspection>(id, ct); inspection.Validate();
        Guard.Require(inspection.Status == EquipmentInspectionStatus.Completed && inspection.Completion is not null,
            "inspection_not_completed", "Original reports can only be attached to a completed inspection.");
        return inspection;
    }
}
