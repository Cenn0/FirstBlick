using FirstBlick.Core.Business;

namespace FirstBlick.Core.Technical;

/// <summary>A named, independently measured roof plane. Coordinates never transfer between planes.</summary>
public sealed record PvRoofSetPlane(string Name, PvLayoutInput Layout);

/// <summary>Each plane has its own local coordinate system; overlapping physical roofs are not detected.</summary>
public sealed record PvRoofSetInput(string RoofSetSource, IReadOnlyList<PvRoofSetPlane> Planes);

public sealed record PvRoofSetPlaneResult(string Name, PvLayoutResult Layout);

public sealed record PvRoofSetResult(PvRoofSetInput Input,
    int InstalledModules, double NominalDcPowerWp, double RoofPlaneAreaM2,
    double InstalledModuleAreaM2, int GridCandidateCount, int ObstructionRejectedCount,
    IReadOnlyList<PvRoofSetPlaneResult> Planes, bool StructureOrElectricalDesignApproved,
    CalculationProvenance Provenance);

/// <summary>
/// Combines several bounded, separately measured PV rectangles without inventing
/// shared coordinates, roof overlaps, structural suitability or installation approval.
/// </summary>
public static class PvRoofSetLayout
{
    private const int MaxPlanes = 8;
    private const int MaxCandidates = 10000;

    public static PvRoofSetResult Calculate(PvRoofSetInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var source = TechnicalInput.Label(input.RoofSetSource, nameof(input.RoofSetSource), 500);
        TechnicalInput.Count(input.Planes, nameof(input.Planes), 1, MaxPlanes);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedPlanes = new List<PvRoofSetPlane>();
        var calculated = new List<PvRoofSetPlaneResult>();
        var candidates = 0;
        var rejected = 0;
        foreach (var plane in input.Planes)
        {
            Guard.Require(plane is not null && plane.Layout is not null,
                "pv_roof_plane", "Every roof plane needs a name and complete measured layout.");
            var name = TechnicalInput.Label(plane.Name, nameof(plane.Name), 200);
            Guard.Require(names.Add(name), "pv_roof_plane", "Roof plane names must be unique.");
            var layout = PvModuleLayout.Calculate(plane.Layout);
            candidates += layout.GridCandidateCount;
            Guard.Require(candidates <= MaxCandidates, "pv_roof_set_budget",
                "The combined roof set exceeds 10,000 grid candidates.");
            rejected += layout.ObstructionRejectedCount;
            normalizedPlanes.Add(new PvRoofSetPlane(name, layout.Input));
            calculated.Add(new PvRoofSetPlaneResult(name, layout));
        }
        var normalized = new PvRoofSetInput(source, Array.AsReadOnly(normalizedPlanes.ToArray()));
        var frozen = Array.AsReadOnly(calculated.ToArray());
        return new PvRoofSetResult(normalized, frozen.Sum(p => p.Layout.InstalledModules),
            frozen.Sum(p => p.Layout.NominalDcPowerWp),
            frozen.Sum(p => p.Layout.RoofPlaneAreaM2),
            frozen.Sum(p => p.Layout.InstalledModuleAreaM2), candidates, rejected, frozen, false,
            CalculationEvidence.Create("pv-roof-set-grid", normalized,
                "Every named roof plane is an independent rectangular surface with its own local coordinates and measured source.",
                "Areas are added arithmetically; overlap, connecting edges and shared obstacles between physically adjacent planes are not detected.",
                "The result is a sum of bounded single-plane origin-aligned grids, not a joint arrangement or yield optimisation.",
                "No shadow, mounting, fire, electrical, wind, snow, structural, access or building-regulation assessment is included."));
    }
}
