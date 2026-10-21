using FirstBlick.Core.Business;

namespace FirstBlick.Core.Technical;

/// <summary>A rectangular roof field placed in the common surveyed roof coordinate system.</summary>
public sealed record TaperedRoofField(string FieldId, double OriginX, double OriginY, TaperedBoardLayoutInput Layout);

/// <summary>
/// Full rectangle partitioned into adjoining, independently reviewed one-way drainage fields.
/// Fields must cover the rectangle without overlaps, gaps or thickness jumps.
/// </summary>
public sealed record TaperedRoofAssemblyInput(double RoofLengthM, double RoofWidthM, string PlanRevision,
    string SurveySource, string DrainageReview, IReadOnlyList<TaperedRoofField> Fields);

public sealed record TaperedRoofFieldResult(string FieldId, double OriginX, double OriginY, TaperedBoardLayoutResult Layout);
public sealed record TaperedRoofPlacedBoard(string FieldId, double GlobalX, double GlobalY, TaperedBoardPlacement Panel);
public sealed record TaperedRoofOrder(string Manufacturer, string ProductRange, string CatalogRevision,
    string SourceReference, TaperedBoardOrder Quantity);
public sealed record TaperedRoofAssemblyResult(TaperedRoofAssemblyInput Input, double RoofAreaM2,
    double InstalledVolumeM3, double CutWasteVolumeM3, int InstalledBoards,
    IReadOnlyList<TaperedRoofFieldResult> Fields, IReadOnlyList<TaperedRoofPlacedBoard> Placements,
    IReadOnlyList<TaperedRoofOrder> Orders, decimal? TotalEstimatedPackCostEur,
    bool CatalogIndependentlyVerified, bool DrainageOrStructureApproved, CalculationProvenance Provenance);

/// <summary>
/// Composes the existing manufacturer-SKU-matched one-way panel plans. Shared boundaries
/// must have identical theoretical thicknesses; this is not a point-drain or valley-forming
/// CAD solver. Packs are consolidated only for identical supplier/catalogue/SKU identities.
/// </summary>
public static class TaperedRoofAssembly
{
    private const double CoordinateToleranceM = 1e-8;
    private const double ThicknessToleranceM = 1e-7;

    public static TaperedRoofAssemblyResult Calculate(TaperedRoofAssemblyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var length = TechnicalInput.Range(input.RoofLengthM, "roofLengthM", .01, 1000);
        var width = TechnicalInput.Range(input.RoofWidthM, "roofWidthM", .01, 1000);
        var revision = TechnicalInput.Label(input.PlanRevision, "planRevision", 200);
        var source = TechnicalInput.Label(input.SurveySource, "surveySource", 1000);
        var drainage = TechnicalInput.Label(input.DrainageReview, "drainageReview", 2000);
        Guard.Items(input.Fields, "roofFields", 32, 1);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var prepared = new List<TaperedRoofFieldResult>();
        foreach (var field in input.Fields)
        {
            Guard.Require(field is not null && field.Layout is not null,
                "invalid_roof_field", "Every roof field requires a reviewed layout.");
            var id = TechnicalInput.Label(field!.FieldId, "fieldId", 100);
            Guard.Require(ids.Add(id), "duplicate_roof_field", "Roof field identifiers must be unique.");
            var x = TechnicalInput.Range(field.OriginX, "originX", 0, length);
            var y = TechnicalInput.Range(field.OriginY, "originY", 0, width);
            var layout = TaperedBoardLayout.Calculate(field.Layout);
            Guard.Require(x + layout.Input.RoofLengthM <= length + CoordinateToleranceM
                && y + layout.Input.RoofWidthM <= width + CoordinateToleranceM,
                "roof_field_outside", "The field extends beyond the surveyed roof boundary.");
            prepared.Add(new(id, x, y, layout));
        }
        prepared.Sort((a, b) => StringComparer.Ordinal.Compare(a.FieldId, b.FieldId));
        Guard.Require(prepared.Sum(x => x.Layout.InstalledBoards) <= 2500,
            "roof_assembly_budget", "The complete roof plan exceeds the bounded 2,500-board output.");
        for (var i = 0; i < prepared.Count; i++)
        {
            for (var j = i + 1; j < prepared.Count; j++)
                ValidateNeighbourhood(prepared[i], prepared[j]);
        }
        var area = length * width;
        Guard.Require(Math.Abs(prepared.Sum(x => x.Layout.RoofAreaM2) - area)
            <= CoordinateToleranceM * Math.Max(1, area), "roof_field_coverage",
            "The separately reviewed fields must cover the entire roof rectangle without gaps.");

        var placements = prepared.SelectMany(x => x.Layout.Placements.Select(p =>
            new TaperedRoofPlacedBoard(x.FieldId, x.OriginX + p.X, x.OriginY + p.Y, p))).ToArray();
        var orderLines = prepared.SelectMany(field => field.Layout.Orders.Select(order => new
        {
            Catalog = field.Layout.Input.Catalog,
            Order = order,
            Sku = field.Layout.Input.Catalog.Boards.Single(b => b.Sku == order.Sku)
        })).ToArray();
        var orders = orderLines.GroupBy(x => new
        {
            x.Catalog.Manufacturer, x.Catalog.ProductRange, x.Catalog.CatalogRevision,
            x.Catalog.SourceReference, x.Order.Sku
        }).OrderBy(x => x.Key.Manufacturer, StringComparer.Ordinal)
          .ThenBy(x => x.Key.ProductRange, StringComparer.Ordinal)
          .ThenBy(x => x.Key.CatalogRevision, StringComparer.Ordinal)
          .ThenBy(x => x.Key.SourceReference, StringComparer.Ordinal)
          .ThenBy(x => x.Key.Sku, StringComparer.Ordinal)
          .Select(group =>
          {
              var first = group.First();
              Guard.Require(group.All(x => x.Order.UnitsPerPack == first.Order.UnitsPerPack
                  && x.Sku.PricePerBoardEur == first.Sku.PricePerBoardEur
                  && x.Sku.ThinEdgeM == first.Sku.ThinEdgeM
                  && x.Sku.ThickEdgeM == first.Sku.ThickEdgeM
                  && x.Catalog.BoardRunM == first.Catalog.BoardRunM
                  && x.Catalog.BoardCrossM == first.Catalog.BoardCrossM),
                  "inconsistent_manufacturer_catalog",
                  "The same supplier, product, revision and SKU have contradictory dimensions, prices or pack sizes.");
              var count = group.Sum(x => x.Order.UsedBoards);
              var packs = (count + first.Order.UnitsPerPack - 1) / first.Order.UnitsPerPack;
              var ordered = packs * first.Order.UnitsPerPack;
              var price = first.Sku.PricePerBoardEur;
              var estimate = price is { } unitPrice
                  ? decimal.Round(checked(unitPrice * ordered), 2, MidpointRounding.AwayFromZero) : (decimal?)null;
              var quantity = new TaperedBoardOrder(group.Key.Sku, count,
                  group.Sum(x => x.Order.BoardsRequiringCut), first.Order.UnitsPerPack,
                  packs, ordered, ordered - count, group.Sum(x => x.Order.InstalledVolumeM3),
                  group.Sum(x => x.Order.UnusedCutVolumeM3), estimate);
              return new TaperedRoofOrder(group.Key.Manufacturer, group.Key.ProductRange,
                  group.Key.CatalogRevision, group.Key.SourceReference, quantity);
          }).ToArray();
        decimal? totalPrice = orders.All(x => x.Quantity.EstimatedPackCostEur.HasValue)
            ? orders.Sum(x => x.Quantity.EstimatedPackCostEur!.Value) : null;
        var normalized = input with { PlanRevision = revision, SurveySource = source, DrainageReview = drainage,
            Fields = Array.AsReadOnly(prepared.Select(f =>
                new TaperedRoofField(f.FieldId, f.OriginX, f.OriginY, f.Layout.Input)).ToArray()) };
        return new(normalized, area, prepared.Sum(x => x.Layout.InstalledVolumeM3),
            prepared.Sum(x => x.Layout.CutWasteVolumeM3), placements.Length, Array.AsReadOnly(prepared.ToArray()),
            Array.AsReadOnly(placements), Array.AsReadOnly(orders), totalPrice, false, false,
            CalculationEvidence.Create("manufacturer-catalog-multifield-tapered-board-layout", normalized,
                "Partition of a surveyed rectangle into adjacent one-way wedge fields, with collision, full-area and boundary-thickness checks.",
                "Each field is calculated only against explicitly supplied supplier/revision/SKU data, without substituting unavailable stock.",
                "Identical catalogue and SKU lines share pack-order rounding across fields; different revisions and sources are never merged.",
                "No point drains, valley/saddle cutting, taper transformations, offcut reuse, penetrations or installation-sequence optimization.",
                "Matching interface thickness does not verify waterproofing, falls, drainage capacity, wind uplift, statics or any manufacturer approval.",
                "The result is a non-normative, unapproved estimate and must not be used as a binding fabrication or purchasing order."));
    }

    private static double Right(TaperedRoofFieldResult x) => x.OriginX + x.Layout.Input.RoofLengthM;
    private static double Top(TaperedRoofFieldResult x) => x.OriginY + x.Layout.Input.RoofWidthM;

    private static void ValidateNeighbourhood(TaperedRoofFieldResult a, TaperedRoofFieldResult b)
    {
        var overlapX = Math.Min(Right(a), Right(b)) - Math.Max(a.OriginX, b.OriginX);
        var overlapY = Math.Min(Top(a), Top(b)) - Math.Max(a.OriginY, b.OriginY);
        Guard.Require(overlapX <= CoordinateToleranceM || overlapY <= CoordinateToleranceM,
            "roof_field_overlap", "Roof fields overlap. Duplicated boards and material must not be ordered.");

        if (overlapY > CoordinateToleranceM &&
            (Math.Abs(Right(a) - b.OriginX) <= CoordinateToleranceM ||
             Math.Abs(Right(b) - a.OriginX) <= CoordinateToleranceM))
        {
            var x = Math.Abs(Right(a) - b.OriginX) <= CoordinateToleranceM ? Right(a) : Right(b);
            CheckThickness(a, b, x, Math.Max(a.OriginY, b.OriginY));
            CheckThickness(a, b, x, Math.Min(Top(a), Top(b)));
        }
        if (overlapX > CoordinateToleranceM &&
            (Math.Abs(Top(a) - b.OriginY) <= CoordinateToleranceM ||
             Math.Abs(Top(b) - a.OriginY) <= CoordinateToleranceM))
        {
            var y = Math.Abs(Top(a) - b.OriginY) <= CoordinateToleranceM ? Top(a) : Top(b);
            CheckThickness(a, b, Math.Max(a.OriginX, b.OriginX), y);
            CheckThickness(a, b, Math.Min(Right(a), Right(b)), y);
        }
    }

    private static void CheckThickness(TaperedRoofFieldResult a, TaperedRoofFieldResult b, double x, double y)
    {
        Guard.Require(Math.Abs(Thickness(a, x, y) - Thickness(b, x, y)) <= ThicknessToleranceM,
            "roof_field_discontinuity", $"Fields {a.FieldId} and {b.FieldId} have incompatible thicknesses at their shared boundary.");
    }

    private static double Thickness(TaperedRoofFieldResult field, double x, double y)
    {
        var layout = field.Layout.Input;
        var along = layout.FlowAxis == TaperedFlowAxis.AlongLength ? x - field.OriginX : y - field.OriginY;
        var extent = layout.FlowAxis == TaperedFlowAxis.AlongLength ? layout.RoofLengthM : layout.RoofWidthM;
        var fromLow = layout.LowEdgeAtCoordinateZero ? along : extent - along;
        return layout.LowEdgeThicknessM + fromLow * layout.SlopePercent / 100;
    }
}
