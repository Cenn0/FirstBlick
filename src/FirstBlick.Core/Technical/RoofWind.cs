using FirstBlick.Core.Business;
using System.Text.Json.Serialization;

namespace FirstBlick.Core.Technical;

public sealed record WindPressureZone([property: JsonRequired] string Name,
    [property: JsonRequired] double LoadedAreaM2,
    [property: JsonRequired] double ExternalPeakPressureKNm2,
    [property: JsonRequired] double ExternalCoefficient,
    [property: JsonRequired] double InternalPeakPressureKNm2,
    [property: JsonRequired] double InternalCoefficient,
    [property: JsonRequired] string ValueSource);
public sealed record RoofWindInput([property: JsonRequired] string CaseSource,
    [property: JsonRequired] IReadOnlyList<WindPressureZone> Zones);
public sealed record WindPressureZoneResult(string Name, double ExternalPressureKNm2, double InternalPressureKNm2,
    double NetInwardPressureKNm2, double NormalForceKN, double OutwardSuctionKNm2);
public sealed record RoofWindResult(RoofWindInput Input, IReadOnlyList<WindPressureZoneResult> Zones,
    bool StructuralDesignApproved, CalculationProvenance Provenance);

/// <summary>Independent local surface-pressure cases. No structural response or fastener sizing.</summary>
public static class RoofWind
{
    public static RoofWindResult Calculate(RoofWindInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var source = TechnicalInput.Label(input.CaseSource, nameof(input.CaseSource), 500);
        TechnicalInput.Count(input.Zones, nameof(input.Zones), 1, 32);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var zones = new List<WindPressureZone>();
        var results = new List<WindPressureZoneResult>();
        foreach (var item in input.Zones)
        {
            ArgumentNullException.ThrowIfNull(item);
            var name = TechnicalInput.Label(item.Name, nameof(item.Name));
            Guard.Require(names.Add(name), "duplicate_wind_zone", "Pressure zone names must be unique.");
            var area = TechnicalInput.Positive(item.LoadedAreaM2, nameof(item.LoadedAreaM2), 1000000);
            var qe = TechnicalInput.Range(item.ExternalPeakPressureKNm2, nameof(item.ExternalPeakPressureKNm2), 0, 100);
            var qi = TechnicalInput.Range(item.InternalPeakPressureKNm2, nameof(item.InternalPeakPressureKNm2), 0, 100);
            var ce = TechnicalInput.Range(item.ExternalCoefficient, nameof(item.ExternalCoefficient), -10, 10);
            var ci = TechnicalInput.Range(item.InternalCoefficient, nameof(item.InternalCoefficient), -10, 10);
            zones.Add(new(name, area, qe, ce, qi, ci, TechnicalInput.Label(item.ValueSource, nameof(item.ValueSource), 500)));
            var external = qe * ce;
            var interior = qi * ci;
            var net = external - interior;
            results.Add(new(name, external, interior, net, net * area, Math.Max(0, -net)));
        }
        var frozen = new RoofWindInput(source, zones.AsReadOnly());
        return new(frozen, results.AsReadOnly(), false,
            CalculationEvidence.Create("supplied-local-wind-pressure", frozen,
                "One supplied wind case; positive net pressure acts inward normal to each local surface, negative pressure outward. Forces on different roof planes are not summed.",
                "No wind-zone, terrain, height, opening, effective-area coefficient interpolation, local-zone geometry, dynamic response or governing-case selection is performed.",
                "The supplied peak pressures and pressure coefficients require independent source and applicability checks. Friction, load combinations, resistance, anchors, fasteners and substrate are not assessed.",
                "This is local pressure arithmetic, not an EN 1991-1-4 or national-annex assessment, structural approval or fastening plan."));
    }
}
