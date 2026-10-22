using FirstBlick.Core.Business;
using System.Text.Json.Serialization;

namespace FirstBlick.Core.Technical;

public sealed record SnowLoadZone([property: JsonRequired] string Name,
    [property: JsonRequired] double HorizontalAreaM2,
    [property: JsonRequired] double ShapeCoefficient,
    [property: JsonRequired] string ValueSource);
public sealed record RoofSnowInput([property: JsonRequired] string CaseSource,
    [property: JsonRequired] double GroundSnowLoadKNm2,
    [property: JsonRequired] double ExposureCoefficient,
    [property: JsonRequired] double ThermalCoefficient,
    [property: JsonRequired] IReadOnlyList<SnowLoadZone> Zones);
public sealed record SnowLoadZoneResult(string Name, double VerticalLoadKNm2, double VerticalForceKN);
public sealed record RoofSnowResult(RoofSnowInput Input, IReadOnlyList<SnowLoadZoneResult> Zones,
    double TotalVerticalForceKN, bool StructuralDesignApproved, CalculationProvenance Provenance);

/// <summary>One explicitly supplied snow case on disjoint, horizontally projected roof zones.</summary>
public static class RoofSnow
{
    public static RoofSnowResult Calculate(RoofSnowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var source = TechnicalInput.Label(input.CaseSource, nameof(input.CaseSource), 500);
        var ground = TechnicalInput.Positive(input.GroundSnowLoadKNm2, nameof(input.GroundSnowLoadKNm2), 100);
        var exposure = TechnicalInput.Positive(input.ExposureCoefficient, nameof(input.ExposureCoefficient), 10);
        var thermal = TechnicalInput.Positive(input.ThermalCoefficient, nameof(input.ThermalCoefficient), 10);
        TechnicalInput.Count(input.Zones, nameof(input.Zones), 1, 32);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var zones = new List<SnowLoadZone>();
        var results = new List<SnowLoadZoneResult>();
        foreach (var item in input.Zones)
        {
            ArgumentNullException.ThrowIfNull(item);
            var name = TechnicalInput.Label(item.Name, nameof(item.Name));
            Guard.Require(names.Add(name), "duplicate_snow_zone", "Snow zone names must be unique.");
            var area = TechnicalInput.Positive(item.HorizontalAreaM2, nameof(item.HorizontalAreaM2), 1000000);
            var shape = TechnicalInput.Range(item.ShapeCoefficient, nameof(item.ShapeCoefficient), 0, 20);
            zones.Add(new(name, area, shape, TechnicalInput.Label(item.ValueSource, nameof(item.ValueSource), 500)));
            var load = shape * exposure * thermal * ground;
            results.Add(new(name, load, load * area));
        }
        var frozen = new RoofSnowInput(source, ground, exposure, thermal, zones.AsReadOnly());
        return new(frozen, results.AsReadOnly(), results.Sum(z => z.VerticalForceKN), false,
            CalculationEvidence.Create("supplied-roof-snow-case", frozen,
                "One supplied snow arrangement; loads act vertically per horizontal projected area. The caller must supply non-overlapping zones from one simultaneous case; physical geometry is not checked.",
                "No location, altitude, national snow-zone data, slope-based shape coefficient, drifting, sliding, exceptional snow case or governing arrangement is inferred.",
                "The scalar total is only the arithmetic sum of these supplied vertical zone forces. Load combinations, beams, connections, deflection, resistance and support reactions are not assessed.",
                "This is load arithmetic, not an EN 1991-1-3 or national-annex assessment, structural approval or a constructive roof verification."));
    }
}
