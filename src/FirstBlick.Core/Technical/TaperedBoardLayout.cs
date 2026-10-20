using FirstBlick.Core.Business;

namespace FirstBlick.Core.Technical;

/// <summary>Only a one-way rectangular roof ending at one continuous low edge is represented.</summary>
public enum TaperedFlowAxis { AlongLength, AlongWidth }

/// <summary>
/// A supplier-supplied, not FirstBlick-certified, nominal rectangular wedge. Its thin-to-thick
/// change is along the physical board run; the thickness is constant across the board width.
/// All dimensions and thicknesses are vertical/metres, not rounded catalogue millimetres.
/// </summary>
public sealed record TaperedBoardSku(string Sku, double ThinEdgeM, double ThickEdgeM,
    int UnitsPerPack, decimal? PricePerBoardEur);

public sealed record TaperedBoardCatalog(string Manufacturer, string ProductRange, string CatalogRevision,
    string SourceReference, double BoardRunM, double BoardCrossM, IReadOnlyList<TaperedBoardSku> Boards);

public sealed record TaperedBoardLayoutInput(double RoofLengthM, double RoofWidthM, double LowEdgeThicknessM,
    double SlopePercent, TaperedFlowAxis FlowAxis, bool LowEdgeAtCoordinateZero,
    string MeasuredRoofSource, string DrainageDesignReview, TaperedBoardCatalog Catalog);

public sealed record TaperedBoardPlacement(int Row, int Column, string Sku,
    double X, double Y, double LengthM, double WidthM, int RotationDegrees,
    double ThinEdgeM, double ThickEdgeM, double InstalledVolumeM3,
    double CutWasteVolumeM3, bool RequiresCut);

public sealed record TaperedBoardOrder(string Sku, int UsedBoards, int BoardsRequiringCut,
    int UnitsPerPack, int PacksToOrder, int BoardsOrdered, int AdditionalBoardsFromPacks,
    double InstalledVolumeM3, double UnusedCutVolumeM3, decimal? EstimatedPackCostEur);

public sealed record TaperedBoardLayoutResult(TaperedBoardLayoutInput Input,
    double RoofAreaM2, double ThinEdgeM, double HighEdgeM, double InstalledVolumeM3,
    double RequiredContinuousVolumeM3, double CutWasteVolumeM3, int InstalledBoards,
    IReadOnlyList<TaperedBoardPlacement> Placements, IReadOnlyList<TaperedBoardOrder> Orders,
    decimal? TotalEstimatedPackCostEur, bool CatalogIndependentlyVerified,
    bool DrainageOrStructureApproved, CalculationProvenance Provenance);

/// <summary>
/// Exact manufacturer-SKU matching and non-overlapping panel grid. NO substitution,
/// thickness interpolation between products, hidden default manufacturer or offcut reuse.
/// Unsupported wedge positions fail closed; this is not a drainage or wind-load design.
/// </summary>
public static class TaperedBoardLayout
{
    private const double ThicknessToleranceM = 1e-7;
    private const int MaxPieces = 2500;

    public static TaperedBoardLayoutResult Calculate(TaperedBoardLayoutInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Catalog);
        var length = TechnicalInput.Range(input.RoofLengthM, nameof(input.RoofLengthM), .01, 1000);
        var width = TechnicalInput.Range(input.RoofWidthM, nameof(input.RoofWidthM), .01, 1000);
        var thin = TechnicalInput.Range(input.LowEdgeThicknessM, nameof(input.LowEdgeThicknessM), .01, 2);
        var slope = TechnicalInput.Range(input.SlopePercent, nameof(input.SlopePercent), .01, 10) / 100;
        Guard.Require(Enum.IsDefined(input.FlowAxis), "invalid_flow_axis", "Select a supported one-way slope direction.");
        var measured = TechnicalInput.Label(input.MeasuredRoofSource, nameof(input.MeasuredRoofSource), 500);
        var drainage = TechnicalInput.Label(input.DrainageDesignReview, nameof(input.DrainageDesignReview), 1000);
        var catalog = input.Catalog;
        var manufacturer = TechnicalInput.Label(catalog.Manufacturer, "manufacturer", 200);
        var series = TechnicalInput.Label(catalog.ProductRange, "productRange", 200);
        var revision = TechnicalInput.Label(catalog.CatalogRevision, "catalogRevision", 200);
        var source = TechnicalInput.Label(catalog.SourceReference, "sourceReference", 1000);
        var run = TechnicalInput.Range(catalog.BoardRunM, "boardRunM", .2, 3);
        var cross = TechnicalInput.Range(catalog.BoardCrossM, "boardCrossM", .2, 3);
        Guard.Items(catalog.Boards, "catalogBoards", 500, 1);
        var boardSkus = new Dictionary<string, TaperedBoardSku>(StringComparer.Ordinal);
        foreach (var board in catalog.Boards)
        {
            Guard.Require(board is not null, "invalid_board", "A manufacturer board record must be supplied.");
            var sku = TechnicalInput.Label(board.Sku, "boardSku", 100);
            var low = TechnicalInput.Range(board.ThinEdgeM, "thinEdgeM", .01, 2);
            var high = TechnicalInput.Range(board.ThickEdgeM, "thickEdgeM", .01, 2);
            Guard.Require(board.UnitsPerPack is >= 1 and <= 200, "invalid_pack_size", "Use an explicit manufacturer pack size from 1 to 200.");
            Guard.Require(high > low && Math.Abs((high - low) - slope * run) <= ThicknessToleranceM,
                "unsupported_board_slope", "Every supplied wedge SKU must match the exact one-way slope and board run. A constant or differently sloped board cannot be substituted.");
            Guard.Require(board.PricePerBoardEur is null or (> 0 and <= 10_000_000),
                "invalid_board_price", "A supplied board price must be strictly positive. Unknown prices must remain null.");
            Guard.Require(boardSkus.TryAdd(sku, board with { Sku = sku }), "duplicate_board_sku", "Supplier SKUs must be unique.");
        }
        var flow = input.FlowAxis == TaperedFlowAxis.AlongLength ? length : width;
        var span = input.FlowAxis == TaperedFlowAxis.AlongLength ? width : length;
        Guard.Require(thin + slope * flow <= 2, "excessive_thickness", "The calculated high edge exceeds the supported two-metre thickness limit.");
        var columnCount = (int)Math.Ceiling(flow / run - 1e-10);
        var rowCount = (int)Math.Ceiling(span / cross - 1e-10);
        Guard.Require(columnCount > 0 && rowCount > 0 && (long)columnCount * rowCount <= MaxPieces,
            "layout_budget", "This plan exceeds the 2,500-piece layout budget; partition the roof into separately verified fields.");

        var placements = new List<TaperedBoardPlacement>(rowCount * columnCount);
        var rotation = input.FlowAxis == TaperedFlowAxis.AlongLength
            ? (input.LowEdgeAtCoordinateZero ? 0 : 180)
            : (input.LowEdgeAtCoordinateZero ? 90 : 270);
        var variants = boardSkus.Values.OrderBy(x => x.Sku, StringComparer.Ordinal).ToArray();
        for (var col = 0; col < columnCount; col++)
        {
            var along = col * run;
            var placedRun = Math.Min(run, flow - along);
            var incoming = thin + along * slope;
            var stock = variants.FirstOrDefault(x => Math.Abs(x.ThinEdgeM - incoming) <= ThicknessToleranceM);
            Guard.Require(stock is not null, "manufacturer_board_missing",
                $"No exact manufacturer SKU supports strip {col + 1}, starting at {incoming:G9} m. The plan has not been completed.");
            for (var row = 0; row < rowCount; row++)
            {
                var across = row * cross;
                var placedCross = Math.Min(cross, span - across);
                var x = input.FlowAxis == TaperedFlowAxis.AlongLength
                    ? (input.LowEdgeAtCoordinateZero ? along : length - along - placedRun) : across;
                var y = input.FlowAxis == TaperedFlowAxis.AlongWidth
                    ? (input.LowEdgeAtCoordinateZero ? along : width - along - placedRun) : across;
                var physicalLength = input.FlowAxis == TaperedFlowAxis.AlongLength ? placedRun : placedCross;
                var physicalWidth = input.FlowAxis == TaperedFlowAxis.AlongLength ? placedCross : placedRun;
                var endThickness = incoming + placedRun * slope;
                var installed = placedRun * placedCross * (incoming + endThickness) / 2;
                var stockVolume = run * cross * (stock!.ThinEdgeM + stock.ThickEdgeM) / 2;
                var waste = stockVolume - installed;
                Guard.Require(waste >= -1e-8, "invalid_panel_geometry", "The panel cut would have more volume than the original stock.");
                placements.Add(new(row + 1, col + 1, stock.Sku, x, y, physicalLength, physicalWidth,
                    rotation, incoming, endThickness, installed, Math.Max(0, waste),
                    placedRun < run - 1e-9 || placedCross < cross - 1e-9));
            }
        }
        var installedVolume = placements.Sum(x => x.InstalledVolumeM3);
        var expectedVolume = length * width * (thin + slope * flow / 2);
        Guard.Require(Math.Abs(installedVolume - expectedVolume) <= 1e-8 * Math.Max(1, expectedVolume),
            "layout_conservation_error", "The panel grid does not reproduce the analytically integrated roof wedge volume.");
        var orders = placements.GroupBy(x => x.Sku, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var sku = boardSkus[group.Key];
                var number = group.Count();
                var packs = (number + sku.UnitsPerPack - 1) / sku.UnitsPerPack;
                var ordered = packs * sku.UnitsPerPack;
                return new TaperedBoardOrder(group.Key, number, group.Count(x => x.RequiresCut),
                    sku.UnitsPerPack, packs, ordered, ordered - number,
                    group.Sum(x => x.InstalledVolumeM3), group.Sum(x => x.CutWasteVolumeM3),
                    sku.PricePerBoardEur is { } price ? decimal.Round(checked(price * ordered), 2, MidpointRounding.AwayFromZero) : null);
            }).ToArray();
        decimal? cost = orders.All(x => x.EstimatedPackCostEur is not null)
            ? orders.Sum(x => x.EstimatedPackCostEur!.Value) : null;
        var normalizedCatalog = catalog with { Manufacturer = manufacturer, ProductRange = series,
            CatalogRevision = revision, SourceReference = source, Boards = Array.AsReadOnly(variants) };
        var normalized = input with { MeasuredRoofSource = measured, DrainageDesignReview = drainage, Catalog = normalizedCatalog };
        return new(normalized, length * width, thin, thin + slope * flow, installedVolume, expectedVolume,
            placements.Sum(x => x.CutWasteVolumeM3), placements.Count, placements.AsReadOnly(),
            Array.AsReadOnly(orders), cost, false, false,
            CalculationEvidence.Create("manufacturer-catalog-tapered-board-layout", normalized,
                "One-way affine slope towards a continuous low edge; every strip requires an explicitly supplied, dimensionally matching supplier SKU.",
                "Supplier/manufacturer identity, catalogue revision and part data are user-supplied evidence, NOT independently verified product availability, approvals or certified characteristics.",
                "Panel run and width are fixed per product range. Rectangular edge cuts are accounted as waste; offcuts are not reused. Pack rounding may add surplus whole boards.",
                "No roof penetrations, drains inside the field, multiple slopes, sumps, saddles, staggered joints, mechanical fixings, adhesive, installation sequence, fire/wind-load or structural checking.",
                "No independent drainage review, minimum-waterfall requirement, DIN/EN approval, material compatibility, actual lead times or final manufacturer sign-off."));
    }
}
