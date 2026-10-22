using FirstBlick.Core.Business;
using System.Text.Json.Serialization;

namespace FirstBlick.Core.Technical;

public enum EmergencyDrainageScenario { ResidualAfterPrimaryDesignFlow, PrimaryUnavailable }

public sealed record DrainageCatchment([property: JsonRequired] string Name,
    [property: JsonRequired] double EffectiveAreaM2,
    [property: JsonRequired] double DesignRunoffCoefficient,
    [property: JsonRequired] double PrimaryCapacityLs,
    [property: JsonRequired] double EmergencyCapacityLs,
    [property: JsonRequired] string AreaSource,
    [property: JsonRequired] string CapacitySource);
public sealed record RoofDrainageInput([property: JsonRequired] string RainfallSource,
    [property: JsonRequired] double DesignRainfallLsHa,
    [property: JsonRequired] double StormRainfallLsHa,
    [property: JsonRequired] EmergencyDrainageScenario EmergencyScenario,
    [property: JsonRequired] IReadOnlyList<DrainageCatchment> Catchments);
public sealed record DrainageCatchmentResult(string Name, double PrimaryDemandLs, double StormInflowLs,
    double CreditedPrimaryFlowLs, double EmergencyDemandLs, double PrimaryDeficitLs, double EmergencyDeficitLs);
public sealed record RoofDrainageResult(RoofDrainageInput Input, IReadOnlyList<DrainageCatchmentResult> Catchments,
    bool HydraulicDesignApproved, CalculationProvenance Provenance);

/// <summary>Flow balance of explicitly supplied, independent catchments; no pipe or outlet sizing.</summary>
public static class RoofDrainage
{
    public static RoofDrainageResult Calculate(RoofDrainageInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var source = TechnicalInput.Label(input.RainfallSource, nameof(input.RainfallSource), 500);
        var design = TechnicalInput.Positive(input.DesignRainfallLsHa, nameof(input.DesignRainfallLsHa), 10000);
        var storm = TechnicalInput.Range(input.StormRainfallLsHa, nameof(input.StormRainfallLsHa), design, 10000);
        Guard.Require(Enum.IsDefined(input.EmergencyScenario), "drainage_scenario", "Select the explicit emergency scenario.");
        TechnicalInput.Count(input.Catchments, nameof(input.Catchments), 1, 32);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<DrainageCatchment>();
        var results = new List<DrainageCatchmentResult>();
        foreach (var item in input.Catchments)
        {
            ArgumentNullException.ThrowIfNull(item);
            var name = TechnicalInput.Label(item.Name, nameof(item.Name));
            Guard.Require(names.Add(name), "duplicate_catchment", "Catchment names must be unique.");
            var area = TechnicalInput.Positive(item.EffectiveAreaM2, nameof(item.EffectiveAreaM2), 1000000);
            var runoff = TechnicalInput.Positive(item.DesignRunoffCoefficient, nameof(item.DesignRunoffCoefficient), 1);
            var primary = TechnicalInput.Range(item.PrimaryCapacityLs, nameof(item.PrimaryCapacityLs), 0, 1000000);
            var emergency = TechnicalInput.Range(item.EmergencyCapacityLs, nameof(item.EmergencyCapacityLs), 0, 1000000);
            normalized.Add(new(name, area, runoff, primary, emergency,
                TechnicalInput.Label(item.AreaSource, nameof(item.AreaSource), 500),
                TechnicalInput.Label(item.CapacitySource, nameof(item.CapacitySource), 500)));
            var demand = design * area * runoff / 10000;
            // No retention credit during the supplied storm; never credit spare primary capacity
            // or the capacity of another catchment. The failed-primary case credits nothing.
            var inflow = storm * area / 10000;
            var credit = input.EmergencyScenario == EmergencyDrainageScenario.PrimaryUnavailable ? 0 : Math.Min(demand, primary);
            var requiredEmergency = Math.Max(0, inflow - credit);
            results.Add(new(name, demand, inflow, credit, requiredEmergency,
                Math.Max(0, demand - primary), Math.Max(0, requiredEmergency - emergency)));
        }
        var frozen = new RoofDrainageInput(source, design, storm, input.EmergencyScenario, normalized.AsReadOnly());
        return new(frozen, results.AsReadOnly(), false,
            CalculationEvidence.Create("supplied-catchment-flow-balance", frozen,
                "Rainfall, effective catchment areas, runoff coefficients and capacities at the applicable water heads and flow paths must be supplied with sources; no location data or rule selection is inferred.",
                "Catchments are independent. Physical overlap, connecting flow paths, retention, pipe hydraulics, siphonic effects, blockage, overflow height and backwater are not modelled.",
                "Storm inflow uses a runoff coefficient of one. Residual mode credits at most the lesser of primary design demand and supplied primary capacity; unavailable-primary mode credits zero.",
                "Zero computed deficit is only an arithmetic comparison, not an EN 12056-3 / DIN 1986-100 assessment or an approved drainage design."));
    }
}
