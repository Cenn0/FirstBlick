using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;
using FirstBlick.Infrastructure.Integrations;
using FirstBlick.Infrastructure.Persistence;
using FirstBlick.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record OfficialGaebBidRequest(
    [property: JsonRequired] Guid ProjectId,
    [property: JsonRequired] long ExpectedProjectRevision,
    [property: JsonRequired] string SourceBase64,
    [property: JsonRequired] GaebBidInput Mapping,
    string? PreviewDigest = null);

public sealed record OfficialGaebBidPreview(string Contract, Guid TenantId, Guid ProjectId, long ProjectRevision,
    string SourceSha256, GaebSchemaResult OriginalSchema, GaebSchemaResult ExportSchema,
    string MappingDigest, string XmlSha256, string SourcePhase, string OutputPhase,
    decimal TotalNet, IReadOnlyList<GaebBidLineResult> Lines,
    IReadOnlyList<string> PhaseExcludedSourceFields, string Digest, bool CanExport,
    bool DeliveryPerformed, bool IndependentlyAcceptedByRecipient);

/// <summary>
/// Strict original GAEB XSD before parsing business meaning; explicit X83→X84 or X84 repricing;
/// revalidation, digest-bound human review, immutable versioned original/output/receipt artifacts.
/// Deliberately no remote GAEB schemaLocation or invented specialist pricing semantics.
/// </summary>
public static class OfficialGaebExchangeEndpoints
{
    private sealed record Work(OfficialGaebBidPreview Preview, byte[] Original, GaebBidResult Output);
    private const string SourceKind = "gaeb-exchanges";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/integrations/v2/gaeb/validate", async (HttpContext http,
            [AsParameters] BusinessCommandServices c, StandardsRuntime standards) =>
        {
            await c.Commands.Authorize("catalog-items", false, http.RequestAborted);
            var bytes = await TradePreviewEndpoints.ReadXml(http);
            var validation = standards.Gaeb.Validate(bytes);
            await c.Commands.Authorize("catalog-items", false, http.RequestAborted);
            return Results.Ok(new
            {
                contract = "firstblick.gaeb-original-schema-validation/2.0.0",
                validation, businessImportAuthorized = false, readyForBid = validation.Valid,
                originalBytesRetained = false
            });
        }).WithTags("gaeb-exchange");

        api.MapPost("/integrations/v2/gaeb/bid-preview", async (OfficialGaebBidRequest request,
            [AsParameters] BusinessCommandServices c, StandardsRuntime standards, CancellationToken ct) =>
        {
            await c.Commands.Authorize("catalog-items", true, ct);
            var prepared = await Prepare(request, c, standards, ct);
            await c.Commands.Authorize("catalog-items", true, ct);
            return Results.Ok(prepared.Preview);
        }).WithTags("gaeb-exchange");

        api.MapPost("/integrations/v2/gaeb/bid-export",
            (OfficialGaebBidRequest request, [AsParameters] BusinessCommandServices c,
             StandardsRuntime standards, IObjectStore objects) =>
            c.Execute("catalog-items", "gaeb.x84-export", request, async ct =>
            {
                Guard.Require(objects.Configured, "storage_unconfigured",
                    "Immutable, versioned document storage is required before a GAEB export can be approved.");
                var work = await Prepare(request, c, standards, ct);
                Guard.Require(work.Preview.CanExport, "gaeb_bid_schema_invalid",
                    "The original or generated X84 fails the pinned original GAEB XSD. No export is retained.");
                Guard.Require(request.PreviewDigest == work.Preview.Digest && request.PreviewDigest is { Length: 64 },
                    "gaeb_bid_preview_changed", "The GAEB original, project, output, schema configuration or reviewed prices have changed.");

                var exchangeId = Guid.NewGuid();
                var originalId = Guid.NewGuid(); var outputId = Guid.NewGuid(); var receiptId = Guid.NewGuid();
                var receipt = new
                {
                    contract = "firstblick.gaeb-bid-exchange-receipt/2.0.0",
                    exchangeId, c.Scope.TenantId, work.Preview.ProjectId, work.Preview.ProjectRevision,
                    work.Preview.SourceSha256, work.Preview.XmlSha256, work.Preview.MappingDigest,
                    work.Preview.Digest, work.Preview.OriginalSchema, work.Preview.ExportSchema,
                    work.Preview.TotalNet, work.Preview.Lines, work.Preview.PhaseExcludedSourceFields,
                    originalDocumentId = originalId, x84DocumentId = outputId, receiptDocumentId = receiptId,
                    reviewedBy = c.Scope.UserId, reviewedAtUtc = DateTimeOffset.UtcNow,
                    recipientTransmissionPerformed = false, recipientAcceptanceConfirmed = false
                };
                var inputDoc = await ValidatedExchangeDocuments.Save(c.Db, c.Scope, objects, originalId,
                    work.Original, $"GAEB-Original-{exchangeId:N}.xml", "application/xml",
                    SourceKind, exchangeId, work.Preview.Digest, work.Preview.ProjectId, "gaeb-original/2.0.0", ct);
                var bidDoc = await ValidatedExchangeDocuments.Save(c.Db, c.Scope, objects, outputId,
                    work.Output.Xml, $"GAEB-X84-{exchangeId:N}.xml", "application/xml",
                    SourceKind, exchangeId, work.Preview.Digest, work.Preview.ProjectId, "gaeb-x84/2.0.0", ct);
                var receiptDoc = await ValidatedExchangeDocuments.Save(c.Db, c.Scope, objects, receiptId,
                    Encoding.UTF8.GetBytes(AppJson.Serialize(receipt)), $"GAEB-Beleg-{exchangeId:N}.json", "application/json",
                    SourceKind, exchangeId, work.Preview.Digest, work.Preview.ProjectId, "gaeb-review-receipt/2.0.0", ct);
                return new
                {
                    id = exchangeId, contract = "firstblick.gaeb-bid-export/2.0.0", preview = work.Preview,
                    documents = new[] { DocumentEndpoints.View(inputDoc), DocumentEndpoints.View(bidDoc), DocumentEndpoints.View(receiptDoc) },
                    downloadUrl = $"/api/v1/integrations/v2/gaeb/exports/{exchangeId:D}/documents/{outputId:D}/download",
                    externallySubmitted = false, recipientAcceptanceConfirmed = false
                };
            })).WithTags("gaeb-exchange");

        // Generic /documents deliberately refuses this specialized business source kind.
        // Access is restricted to current GAEB catalogue writers AND to the original project here.
        api.MapGet("/integrations/v2/gaeb/exports/{exchangeId:guid}", async (Guid exchangeId,
            [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await c.Commands.Authorize("catalog-items", true, ct);
            var docs = await c.Db.Documents.AsNoTracking().Where(x =>
                x.SourceKind == SourceKind && x.SourceId == exchangeId).OrderBy(x => x.Name).ToListAsync(ct);
            Guard.Require(docs.Count == 3 && docs.All(x => x.ProjectId.HasValue && x.ProjectId == docs[0].ProjectId),
                "not_found", "The GAEB export and its complete immutable evidence are not available.");
            _ = await c.Current<Project>(docs[0].ProjectId!.Value, ct, false);
            return Results.Ok(new
            {
                contract = "firstblick.gaeb-export-evidence/2.0.0", id = exchangeId,
                projectId = docs[0].ProjectId, documents = docs.Select(DocumentEndpoints.View),
                schemaValidationAndRecipientAcceptanceAreSeparate = true
            });
        }).WithTags("gaeb-exchange");

        api.MapGet("/integrations/v2/gaeb/exports/{exchangeId:guid}/documents/{documentId:guid}/download",
            async (Guid exchangeId, Guid documentId, HttpContext http,
                [AsParameters] BusinessCommandServices c, IObjectStore objects, CancellationToken ct) =>
        {
            await c.Commands.Authorize("catalog-items", true, ct);
            var doc = await c.Db.Documents.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == documentId && x.SourceKind == SourceKind && x.SourceId == exchangeId, ct)
                ?? throw new DomainException("not_found", "The GAEB exchange artifact is unavailable.");
            Guard.Require(doc.ProjectId.HasValue && doc.State == "Ready",
                "gaeb_artifact_unavailable", "The requested artifact has no authorized project or immutable ready state.");
            _ = await c.Current<Project>(doc.ProjectId!.Value, ct, false);
            var bytes = await DocumentEndpoints.ReadVerified(objects, doc, ct);
            http.Response.Headers["X-Content-SHA256"] = doc.Sha256;
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(bytes, doc.ContentType, fileDownloadName: doc.Name, lastModified: doc.CreatedAt);
        }).WithTags("gaeb-exchange");
    }

    private static async Task<Work> Prepare(OfficialGaebBidRequest request, BusinessCommandServices c,
        StandardsRuntime standards, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Mapping);
        Guard.Id(request.ProjectId, "GAEB project");
        Guard.Require(request.ExpectedProjectRevision > 0, "project_revision_required", "Select a current project revision.");
        var project = await c.Current<Project>(request.ProjectId, ct, false);
        Guard.Require(project.Revision == request.ExpectedProjectRevision, "gaeb_project_changed",
            "The project was changed after the GAEB bid review.");
        Guard.Require(request.SourceBase64 is { Length: > 0 and <= 1_400_000 },
            "gaeb_xml_budget", "Provide at most 1 MiB of explicitly selected original GAEB XML bytes as Base64.");
        byte[] original;
        try { original = Convert.FromBase64String(request.SourceBase64); }
        catch (FormatException) { throw new DomainException("invalid_base64", "Supply a valid Base64-encoded GAEB XML original."); }
        var source = standards.Gaeb.Validate(original);
        Guard.Require(source.Valid, "gaeb_original_schema_invalid",
            "The original XML does not pass the pinned GAEB DA XML 3.3 X83/X84 official XSD. Inspect the validation endpoint before pricing.");
        var output = GaebBidWriter.Render(original, request.Mapping);
        var export = standards.Gaeb.Validate(output.Xml);
        var digest = ContentDigest.Of(new
        {
            contract = "firstblick.gaeb-bid-preview/2.0.0", c.Scope.TenantId, project.Id, project.Revision,
            sourceSha256 = source.SourceSha256, sourceSchemaBundleSha256 = source.SchemaBundleSha256,
            sourceSchemaEdition = source.SchemaEdition, output.MappingDigest,
            output.XmlSha256, exportSchemaBundleSha256 = export.SchemaBundleSha256,
            exportSchemaEdition = export.SchemaEdition, export.Valid,
            excluded = output.PhaseExcludedSourceFields
        });
        var preview = new OfficialGaebBidPreview("firstblick.gaeb-bid-preview/2.0.0", c.Scope.TenantId,
            project.Id, project.Revision, source.SourceSha256, source, export, output.MappingDigest,
            output.XmlSha256, output.SourcePhase, output.TargetPhase, output.TotalNet,
            output.Lines, output.PhaseExcludedSourceFields, digest,
            export.Valid, false, false);
        return new(preview, original, output);
    }
}
