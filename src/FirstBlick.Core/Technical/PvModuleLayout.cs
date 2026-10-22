using FirstBlick.Core.Business;

namespace FirstBlick.Core.Technical;

/// <summary>Orientation of nominal rectangular modules on the measured roof plane.</summary>
public enum PvModuleOrientation { Portrait, Landscape, MostModules }

public sealed record PvExclusion(string Name, double X, double Y, double WidthM, double HeightM);

/// <summary>
/// Coordinates refer to metres on the roof surface, not the horizontal projection.
/// X runs across the measured plane width and Y along its measured plane height.
/// </summary>
public sealed record PvLayoutInput(double RoofPlaneWidthM, double RoofPlaneHeightM,
    double ModuleWidthM, double ModuleHeightM, double ModuleRatedPowerWp,
    double EdgeClearanceM, double ModuleGapXM, double ModuleGapYM,
    double ExclusionClearanceM, PvModuleOrientation Orientation,
    IReadOnlyList<PvExclusion> Exclusions, string RoofMeasurementSource, string ModuleDataSource);

public sealed record PvModulePlacement(int Row, int Column, double X, double Y,
    double WidthM, double HeightM);

public sealed record PvLayoutResult(PvLayoutInput Input, PvModuleOrientation SelectedOrientation,
    double RoofPlaneAreaM2, int GridCandidateCount, int ObstructionRejectedCount,
    int InstalledModules, double InstalledModuleAreaM2, double NominalDcPowerWp,
    IReadOnlyList<PvModulePlacement> Placements, bool StructureOrElectricalDesignApproved,
    CalculationProvenance Provenance);

/// <summary>
/// A deterministic, origin-aligned rectangular grid. No yield, shading, structural,
/// fire, electrical, wind, snow or manufacturer-approved mounting design is inferred.
/// </summary>
public static class PvModuleLayout
{
    private const int MaxGridCandidates = 2500;
    private const int MaxExclusions = 64;
    private const double FitToleranceM = 1e-9;

    public static PvLayoutResult Calculate(PvLayoutInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var roofWidth = TechnicalInput.Range(input.RoofPlaneWidthM, nameof(input.RoofPlaneWidthM), .1, 100);
        var roofHeight = TechnicalInput.Range(input.RoofPlaneHeightM, nameof(input.RoofPlaneHeightM), .1, 100);
        var moduleWidth = TechnicalInput.Range(input.ModuleWidthM, nameof(input.ModuleWidthM), .1, 5);
        var moduleHeight = TechnicalInput.Range(input.ModuleHeightM, nameof(input.ModuleHeightM), .1, 5);
        var ratedPower = TechnicalInput.Range(input.ModuleRatedPowerWp, nameof(input.ModuleRatedPowerWp), 10, 1500);
        var edge = TechnicalInput.Range(input.EdgeClearanceM, nameof(input.EdgeClearanceM), 0, 5);
        var gapX = TechnicalInput.Range(input.ModuleGapXM, nameof(input.ModuleGapXM), 0, 1);
        var gapY = TechnicalInput.Range(input.ModuleGapYM, nameof(input.ModuleGapYM), 0, 1);
        var exclusionClearance = TechnicalInput.Range(input.ExclusionClearanceM, nameof(input.ExclusionClearanceM), 0, 5);
        Guard.Require(Enum.IsDefined(input.Orientation), "pv_orientation", "Choose a supported module orientation.");
        var roofSource = TechnicalInput.Label(input.RoofMeasurementSource, nameof(input.RoofMeasurementSource), 500);
        var moduleSource = TechnicalInput.Label(input.ModuleDataSource, nameof(input.ModuleDataSource), 500);
        TechnicalInput.Count(input.Exclusions, nameof(input.Exclusions), 0, MaxExclusions);
        var exclusions = input.Exclusions.Select((item, index) =>
        {
            Guard.Require(item is not null, "pv_exclusion", $"Exclusion {index + 1} is missing.");
            var name = TechnicalInput.Label(item.Name, $"exclusions[{index}].name", 200);
            var x = TechnicalInput.Range(item.X, $"exclusions[{index}].x", 0, roofWidth);
            var y = TechnicalInput.Range(item.Y, $"exclusions[{index}].y", 0, roofHeight);
            var width = TechnicalInput.Range(item.WidthM, $"exclusions[{index}].widthM", .001, roofWidth);
            var height = TechnicalInput.Range(item.HeightM, $"exclusions[{index}].heightM", .001, roofHeight);
            Guard.Require(x + width <= roofWidth + FitToleranceM && y + height <= roofHeight + FitToleranceM,
                "pv_exclusion", "Every exclusion must fit on the measured roof plane.");
            return new PvExclusion(name, x, y, width, height);
        }).ToArray();

        var normalized = input with { RoofMeasurementSource = roofSource, ModuleDataSource = moduleSource,
            Exclusions = Array.AsReadOnly(exclusions) };
        (List<PvModulePlacement> Placements, int Candidates, int Rejected)? portrait = input.Orientation != PvModuleOrientation.Landscape
            ? Place(roofWidth, roofHeight, moduleWidth, moduleHeight, edge, gapX, gapY, exclusionClearance, exclusions)
            : null;
        (List<PvModulePlacement> Placements, int Candidates, int Rejected)? landscape = input.Orientation != PvModuleOrientation.Portrait
            ? Place(roofWidth, roofHeight, moduleHeight, moduleWidth, edge, gapX, gapY, exclusionClearance, exclusions)
            : null;
        var useLandscape = input.Orientation == PvModuleOrientation.Landscape
            || input.Orientation == PvModuleOrientation.MostModules
                && landscape!.Value.Placements.Count > portrait!.Value.Placements.Count;
        var chosen = useLandscape ? landscape!.Value : portrait!.Value;
        return new(normalized, useLandscape ? PvModuleOrientation.Landscape : PvModuleOrientation.Portrait,
            roofWidth * roofHeight, chosen.Candidates, chosen.Rejected, chosen.Placements.Count,
            chosen.Placements.Count * moduleWidth * moduleHeight, chosen.Placements.Count * ratedPower,
            Array.AsReadOnly(chosen.Placements.ToArray()), false,
            CalculationEvidence.Create("pv-roof-plane-grid", normalized,
                "Measurements and exclusion coordinates are on one rectangular roof plane, not horizontal projected dimensions.",
                "The grid starts at the lower-left clearance boundary; it does not optimise offsets, staggered rows or multiple roof faces.",
                "Exclusions and clearances are user-supplied. No shade, access routes, setbacks, yield or mounting-system suitability is certified.",
                "Nominal DC power is the number of modules times the user-supplied nameplate power; it is not AC capacity or annual generation.",
                "No wind, snow, fire, electrical, structural or building-regulation approval is provided."));
    }

    private static (List<PvModulePlacement> Placements, int Candidates, int Rejected) Place(
        double roofWidth, double roofHeight, double width, double height, double edge,
        double gapX, double gapY, double clearance, IReadOnlyList<PvExclusion> exclusions)
    {
        var usableWidth = Math.Max(0, roofWidth - 2 * edge);
        var usableHeight = Math.Max(0, roofHeight - 2 * edge);
        var columns = usableWidth + FitToleranceM < width ? 0 :
            Math.Max(0, (int)Math.Floor((usableWidth + gapX + FitToleranceM) / (width + gapX)));
        var rows = usableHeight + FitToleranceM < height ? 0 :
            Math.Max(0, (int)Math.Floor((usableHeight + gapY + FitToleranceM) / (height + gapY)));
        Guard.Require((long)columns * rows <= MaxGridCandidates, "pv_layout_budget",
            "The module grid exceeds 2,500 candidates. Split the roof into separately reviewed fields.");
        var placements = new List<PvModulePlacement>();
        var rejected = 0;
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var x = edge + column * (width + gapX);
            var y = edge + row * (height + gapY);
            // Do not use the numerical fit tolerance to permit actual roof-edge overshoot.
            if (x + width > roofWidth - edge || y + height > roofHeight - edge)
            { rejected++; continue; }
            if (exclusions.Any(blocked => Intersects(x, y, width, height, blocked, clearance)))
            { rejected++; continue; }
            placements.Add(new PvModulePlacement(row + 1, column + 1, x, y, width, height));
        }
        return (placements, rows * columns, rejected);
    }

    private static bool Intersects(double x, double y, double width, double height,
        PvExclusion obstacle, double clearance) =>
        x < obstacle.X + obstacle.WidthM + clearance
        && x + width > obstacle.X - clearance
        && y < obstacle.Y + obstacle.HeightM + clearance
        && y + height > obstacle.Y - clearance;
}
