using System.Text;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;

namespace FirstBlick.Api;

public sealed record GaebCatalogSelection(string PositionKey, decimal UnitMaterialCost,
    decimal UnitLaborHours, decimal HourlyRate, decimal MarkupPercent, decimal VatPercent,
    string PriceEvidence);
public sealed record GaebCatalogRequest(string SourceBase64, IReadOnlyList<GaebCatalogSelection> Selections,
    string? PreviewDigest = null);
public sealed record GaebCatalogCandidate(string PositionKey, string Description, string Unit,
    decimal? TenderQuantity, decimal? OfferedUnitPrice, GaebCatalogOrigin Origin,
    decimal UnitMaterialCost, decimal UnitLaborHours, decimal HourlyRate, decimal MarkupPercent,
    decimal VatPercent);
public sealed record GaebCatalogPreview(string Contract, string SourceSha256, string Phase,
    IReadOnlyList<GaebCatalogCandidate> Candidates, string Digest,
    IReadOnlyList<string> ReviewIssues, bool SchemaValidated);
public sealed record GaebCatalogImportedLine(string PositionKey, Guid CatalogItemId, string Name, string Unit);
public sealed record GaebCatalogImportReceipt(string Contract, string SourceSha256,
    string ReviewedPreviewDigest, IReadOnlyList<GaebCatalogImportedLine> Items);

/// <summary>
/// Explicitly reviewed transfer into the *internal* service catalogue; no GAEB bid export.
/// Tender unit prices are never silently interpreted as internal costs or sales prices.
/// </summary>
public static class GaebCatalogEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/integrations/v1/gaeb/preview", async (HttpContext http,
            [AsParameters] BusinessCommandServices c) =>
        {
            await c.Commands.Authorize("catalog-items", true, http.RequestAborted);
            return Results.Ok(GaebXml.Preview(await TradePreviewEndpoints.ReadXml(http)));
        }).WithTags("integrations");

        api.MapPost("/integrations/v1/gaeb/catalog-preview", async (GaebCatalogRequest body,
            [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await c.Commands.Authorize("catalog-items", true, ct);
            return Results.Ok(Build(body, c.Scope.TenantId));
        }).WithTags("integrations");

        api.MapPost("/integrations/v1/gaeb/catalog-import", (GaebCatalogRequest body,
            [AsParameters] BusinessCommandServices c) =>
            c.Execute("catalog-items", "adapter.gaeb.catalog-import", body, async ct =>
            {
                var preview = Build(body, c.Scope.TenantId);
                Guard.Require(body.PreviewDigest == preview.Digest, "gaeb_preview_changed",
                    "GAEB bytes, selected mapping or source contents differ from the reviewed catalogue preview.");
                var inserted = new List<GaebCatalogImportedLine>(preview.Candidates.Count);
                foreach (var candidate in preview.Candidates)
                {
                    var item = new CatalogItem
                    {
                        TenantId = c.Scope.TenantId, Name = candidate.Description, Unit = candidate.Unit,
                        UnitMaterialCost = candidate.UnitMaterialCost, UnitLaborHours = candidate.UnitLaborHours,
                        HourlyRate = candidate.HourlyRate, MarkupPercent = candidate.MarkupPercent,
                        VatPercent = candidate.VatPercent, GaebOrigin = candidate.Origin
                    };
                    item.Validate();
                    await c.SaveNew(item, "adapter.gaeb.catalog-import", ct);
                    inserted.Add(new GaebCatalogImportedLine(candidate.PositionKey, item.Id, item.Name, item.Unit));
                }
                return new GaebCatalogImportReceipt("firstblick.gaeb-catalog-import/1.0.0",
                    preview.SourceSha256, preview.Digest, inserted.AsReadOnly());
            })).WithTags("integrations");
    }

    internal static GaebCatalogPreview Build(GaebCatalogRequest body, Guid tenantId)
    {
        Guard.Require(body is not null && body.SourceBase64 is { Length: > 0 and <= 1_400_000 },
            "gaeb_input_budget", "A bounded Base64-encoded source is required.");
        Guard.Items(body.Selections, "GAEB selections", 100, 1);
        Guard.Require(body.Selections.Select(x => x.PositionKey).Distinct(StringComparer.Ordinal).Count() == body.Selections.Count,
            "gaeb_duplicate_selection", "Each GAEB position may be selected only once.");
        byte[] source;
        try { source = Convert.FromBase64String(body.SourceBase64); }
        catch (FormatException) { throw new DomainException("gaeb_base64", "The GAEB source must be valid Base64 of the reviewed XML file."); }
        var document = GaebXml.Preview(source);
        Guard.Require(document.Currency == "EUR", "gaeb_currency_unverified",
            "Declare the source currency explicitly as EUR before mapping commercial costs.");
        var known = document.Items.ToDictionary(x => x.PositionKey, StringComparer.Ordinal);
        var candidates = new List<GaebCatalogCandidate>();
        foreach (var selection in body.Selections)
        {
            Guard.Require(known.TryGetValue(selection.PositionKey, out var line),
                "gaeb_unknown_position", "The selected position does not occur in the submitted GAEB source.");
            Guard.Require(!string.IsNullOrWhiteSpace(line!.Description) && line.Description.Length <= 240
                && !line.ReviewIssues.Contains("description_truncated_for_preview")
                && !string.IsNullOrWhiteSpace(line.Unit),
                "gaeb_mapping_incomplete", "Selected GAEB positions require a short complete description and a declared unit.");
            Guard.Text(selection.PriceEvidence, "GAEB price evidence", 2000);
            var origin = new GaebCatalogOrigin(document.SourceSha256, line.PositionKey,
                line.ItemDigest, document.ExchangePhase, selection.PriceEvidence.Trim());
            var candidate = new GaebCatalogCandidate(line.PositionKey, line.Description, line.Unit,
                line.TenderQuantity, line.OfferedUnitPrice, origin, selection.UnitMaterialCost,
                selection.UnitLaborHours, selection.HourlyRate, selection.MarkupPercent, selection.VatPercent);
            // Reuse the real catalogue arithmetic bounds. External price is *not* used as internal cost.
            var item = new CatalogItem
            {
                TenantId = tenantId, Name = candidate.Description, Unit = candidate.Unit,
                UnitMaterialCost = candidate.UnitMaterialCost, UnitLaborHours = candidate.UnitLaborHours,
                HourlyRate = candidate.HourlyRate, MarkupPercent = candidate.MarkupPercent,
                VatPercent = candidate.VatPercent, GaebOrigin = candidate.Origin
            };
            item.Validate();
            candidates.Add(candidate);
        }
        var result = new GaebCatalogPreview("firstblick.gaeb-catalog-preview/1.0.0",
            document.SourceSha256, document.ExchangePhase, candidates.AsReadOnly(), "",
            Array.AsReadOnly(new[]
            {
                "reviewed_internal_catalogue_mapping_only",
                "source_has_not_been_xsd_certified",
                "tender_quantities_are_not_imported_as_quote_or_material_reservations",
                "offered_unit_prices_are_not_automatically_reinterpreted_as_internal_costs"
            }), false);
        return result with { Digest = ContentDigest.Of(result) };
    }
}
