using FirstBlick.Core.Business;

namespace FirstBlick.Core.Technical;

public enum MoistureHeatFlow { Upward, Horizontal, Downward }
public enum MoistureExteriorBoundary { OutsideAir, WellVentilatedCavity }
public enum MoistureClimateTreatment { WallAir, RoofMinusTwoKelvin, SuppliedEquivalentRoofTemperature }
public enum MoistureMaterialKind { HomogeneousLayer, Membrane, UnventilatedAirLayer }
public sealed record MoistureResistanceLayer(string Name, double ResistanceM2KW, double SdM,
    MoistureMaterialKind Kind, string DesignValueSource);
public sealed record MoistureDesignMonth(int Month, double InsideTemperatureC, double OutsideAirTemperatureC,
    double InsideRelativeHumidityPercent, double OutsideRelativeHumidityPercent,
    double? EquivalentOutsideTemperatureC = null);
public sealed record MoistureApplicability(bool ParallelHomogeneousLayers, bool NoAirflowThroughAssembly,
    bool NoRainAbsorption, bool NegligibleCapillaryAndHygroscopicEffects, bool ConstantDesignProperties,
    bool ClimateSelectionReviewed, string ConstructionReview, string ClimateSource);
public sealed record Iso13788BalanceInput(int ClimateCalendarYear, int TrialMonth, MoistureHeatFlow HeatFlow,
    MoistureExteriorBoundary ExteriorBoundary, MoistureClimateTreatment ClimateTreatment,
    bool SubdivideThermalLayers, IReadOnlyList<MoistureResistanceLayer> Layers,
    IReadOnlyList<MoistureDesignMonth> Months, MoistureApplicability Applicability);
public sealed record VapourBalanceNode(int Index, int LayerIndex, double LayerFraction, string Name,
    double CumulativeResistanceM2KW, double CumulativeSdM);
public sealed record InterfaceWaterBalance(int NodeIndex, double TemperatureC, double SaturationPressurePa,
    double EndVapourPressurePa, double StartMassKgM2, double CondensedKgM2,
    double EvaporatedKgM2, double EndMassKgM2, double MaximumMassKgM2);
public sealed record MonthlyWaterBalance(int Sequence, int Month, int Days, double InsideSurfaceResistanceM2KW,
    double OutsideSurfaceResistanceM2KW, double OutsideThermalTemperatureC,
    double InsideVapourPressurePa, double OutsideVapourPressurePa,
    double InsideSurfaceRelativeHumidityPercent, double OutsideSurfaceRelativeHumidityPercent,
    double CondensedKgM2, double EvaporatedKgM2, double EndStoredMassKgM2,
    double MassConservationErrorKgM2, int Subperiods, IReadOnlyList<InterfaceWaterBalance> WetInterfaces);
public sealed record InterfaceAnnualMaximum(int NodeIndex, double MaximumMassKgM2, int Month,
    double EndMassKgM2);
public sealed record StandardProcedureEvidence(string Standard, string Clauses, string ProcedureVersion,
    bool ImplementsSpecifiedProcedure, bool ConstructionComplianceAssessed, bool Din4108Part3Assessment,
    IReadOnlyList<string> RequiredFurtherChecks);
public enum AnnualMoistureClassification { NoInterstitialCondensation, CondensateFullyReevaporates, ResidualCondensate }
public sealed record Iso13788BalanceResult(Iso13788BalanceInput Input, IReadOnlyList<VapourBalanceNode> Grid,
    int StartingMonth, AnnualMoistureClassification Classification, IReadOnlyList<MonthlyWaterBalance> Months,
    IReadOnlyList<InterfaceAnnualMaximum> InterfaceMaxima, double TotalCondensedKgM2,
    double TotalEvaporatedKgM2, double ResidualCondensateKgM2,
    StandardProcedureEvidence StandardProcedure, CalculationProvenance Provenance);
public sealed record Iso13788DryingInput(Iso13788BalanceInput Assembly, int WettedLayerIndex, int StartingMonth,
    string InitialAssessmentReview);
public enum DryingClassification { DriedWithoutRedistribution, DriedWithRedistribution, NotDryWithinTenYears }
public sealed record Iso13788DryingResult(Iso13788DryingInput Input, IReadOnlyList<VapourBalanceNode> Grid,
    int InitiallyWettedNode, double InitialExcessMassKgM2, int? DryingTimeMonths,
    DryingClassification Classification, IReadOnlyList<MonthlyWaterBalance> Months,
    IReadOnlyList<InterfaceAnnualMaximum> InterfaceMaxima, double ResidualMassKgM2,
    StandardProcedureEvidence StandardProcedure, CalculationProvenance Provenance);

/// <summary>
/// Independent implementation of ISO 13788:2012 clauses 6 and 7.
/// Piecewise linear vapour-pressure envelopes enforce saturation at wet interfaces;
/// exact drying events split a month instead of losing excess evaporation by clipping.
/// This procedure is not a transient heat/moisture simulation or a DIN 4108-3 assessment.
/// </summary>
public static class Iso13788Balance
{
    public const double VapourPermeabilityAir = 2e-10; // kg/(m s Pa), ISO 13788:2012 6.2
    public const double NumericalMassToleranceKgM2 = 1e-12;
    private const double RateTolerance = 1e-18;
    private const int MaximumGridNodes = 256;
    private sealed record MonthProfile(double[] Temperature, double[] Saturation, double InsidePressure,
        double OutsidePressure, double Rsi, double Rse, double ThermalOutside);
    private sealed record PressureState(double[] Pressure, double[] Rates, double NetBoundaryInflow);

    public static Iso13788BalanceResult Calculate(Iso13788BalanceInput input)
    {
        var normalized = Normalize(input);
        var grid = Grid(normalized, null);
        var profiles = normalized.Months.Select(m => Profile(normalized, grid, m)).ToArray();
        var dry = new double[grid.Count];
        var hasCondensation = profiles.Select(profile => Pressures(grid, profile, dry).Rates.Any(x => x > RateTolerance)).ToArray();
        var trial = normalized.TrialMonth - 1;
        var start = trial;
        if (hasCondensation.Any(x => x))
        {
            if (!hasCondensation[trial])
            {
                for (var step = 1; step <= 12; step++)
                    if (hasCondensation[(trial + step) % 12]) { start = (trial + step) % 12; break; }
            }
            else
            {
                for (var step = 1; step < 12; step++)
                {
                    var previous = (trial - step + 12) % 12;
                    if (!hasCondensation[previous]) { start = (previous + 1) % 12; break; }
                }
            }
        }
        var stored = new double[grid.Count];
        var months = new List<MonthlyWaterBalance>();
        for (var sequence = 0; sequence < 12; sequence++)
        {
            var month = (start + sequence) % 12;
            months.Add(Advance(normalized, grid, profiles[month], stored, month + 1, sequence + 1));
        }
        var residual = stored.Sum();
        var condensed = months.Sum(x => x.CondensedKgM2);
        var classification = residual > NumericalMassToleranceKgM2 ? AnnualMoistureClassification.ResidualCondensate
            : condensed > NumericalMassToleranceKgM2 ? AnnualMoistureClassification.CondensateFullyReevaporates
            : AnnualMoistureClassification.NoInterstitialCondensation;
        return new(normalized, grid, start + 1, classification, months.AsReadOnly(), Maxima(months, stored),
            condensed, months.Sum(x => x.EvaporatedKgM2), residual, Procedure("6.2–6.5; 4.1–4.4; Annex E"),
            CalculationEvidence.Create("iso13788-monthly-diffusion-balance", normalized,
                "ISO 13788:2012 monthly interstitial diffusion procedure with stored water, multiple condensation interfaces and exact within-month drying events.",
                "The legacy isNormative flag remains false because this record does not certify an entire building or establish DIN 4108-3 conformity; StandardProcedure identifies the implemented ISO calculation.",
                "No capillary liquid transfer, airflow, rain absorption, moisture-dependent properties or hygroscopic storage. Explicit suitability and design-climate review are mandatory.",
                "Annual re-evaporation is not by itself a material-damage or runoff approval; interface maxima require construction-specific assessment.",
                "WetInterfaces lists interfaces with nonzero stored water or mass transfer; all calculation interfaces remain identified in Grid."));
    }

    public static Iso13788DryingResult Dry(Iso13788DryingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var assembly = Normalize(input.Assembly);
        TechnicalInput.Label(input.InitialAssessmentReview, nameof(input.InitialAssessmentReview), 2000);
        Guard.Require(input.StartingMonth is >= 1 and <= 12 && input.WettedLayerIndex >= 0 && input.WettedLayerIndex < assembly.Layers.Count,
            "invalid_drying_input", "Select an existing wetted layer and a calendar starting month.");
        Guard.Require(assembly.Layers[input.WettedLayerIndex].Kind == MoistureMaterialKind.HomogeneousLayer
            && assembly.Layers[input.WettedLayerIndex].ResistanceM2KW > 0, "invalid_drying_layer", "The standardized excess-water test needs a homogeneous layer with a defined midpoint.");
        var baseline = Calculate(assembly);
        Guard.Require(baseline.Classification != AnnualMoistureClassification.ResidualCondensate,
            "drying_baseline_failed", "The assembly already accumulates interstitial moisture annually; resolve the clause-6 assessment before applying the clause-7 drying comparison.");
        var normalized = input with { Assembly = assembly, InitialAssessmentReview = input.InitialAssessmentReview.Trim() };
        var grid = Grid(assembly, input.WettedLayerIndex);
        var initiallyWet = grid.Single(x => x.LayerIndex == input.WettedLayerIndex && Math.Abs(x.LayerFraction - .5) < 1e-12).Index;
        var profiles = assembly.Months.Select(m => Profile(assembly, grid, m)).ToArray();
        var stored = new double[grid.Count]; stored[initiallyWet] = 1; // prescribed excess-water comparison, not an invented measured initial moisture
        var months = new List<MonthlyWaterBalance>(); int? dryingMonths = null;
        for (var sequence = 0; sequence < 120; sequence++)
        {
            var month = (input.StartingMonth - 1 + sequence) % 12;
            months.Add(Advance(assembly, grid, profiles[month], stored, month + 1, sequence + 1));
            Guard.Require(months.Sum(x => x.WetInterfaces.Count) <= 8000, "moisture_result_budget", "The detailed drying evidence exceeds its bounded result size.");
            if (stored.Sum() <= NumericalMassToleranceKgM2) { dryingMonths = sequence + 1; break; }
        }
        var moved = months.Any(m => m.WetInterfaces.Any(x => x.NodeIndex != initiallyWet && x.MaximumMassKgM2 > NumericalMassToleranceKgM2));
        var category = dryingMonths is null ? DryingClassification.NotDryWithinTenYears
            : moved ? DryingClassification.DriedWithRedistribution : DryingClassification.DriedWithoutRedistribution;
        return new(normalized, grid, initiallyWet, 1, dryingMonths, category, months.AsReadOnly(), Maxima(months, stored), stored.Sum(),
            Procedure("7.1–7.4 using the 6.4 monthly procedure; 4.1–4.4; Annex E"),
            CalculationEvidence.Create("iso13788-excess-water-drying", normalized,
                "ISO 13788:2012 clause-7 comparison starts with exactly 1 kg/m² excess water at the selected layer midpoint and repeats the explicitly supplied climate year for at most 120 months.",
                "All interfaces are tracked; moving the water into another layer is not counted as drying the construction.",
                "Drying time is the first completed monthly step with no remaining interface water; material degradation and runoff risks require separate review.",
                "This is a standardized diffusion drying comparison, not a prediction of actual construction drying, a DIN 4108-3 verdict, or approval of supplied design data."));
    }

    private static StandardProcedureEvidence Procedure(string clauses) => new("ISO 13788:2012", clauses, "1.0.0", true, false, false,
        Array.AsReadOnly(new[] { "Design-value and local-climate suitability", "National construction-specific rules and method applicability", "Maximum-water damage and runoff risk", "Independent project-level specialist review" }));

    private static Iso13788BalanceInput Normalize(Iso13788BalanceInput input)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(input.Applicability);
        Guard.Require(input.ClimateCalendarYear is >= 1900 and <= 3000 && input.TrialMonth is >= 1 and <= 12,
            "invalid_climate_year", "Specify the representative climate calendar year and trial month.");
        Guard.Require(Enum.IsDefined(input.HeatFlow) && Enum.IsDefined(input.ExteriorBoundary) && Enum.IsDefined(input.ClimateTreatment),
            "invalid_moisture_method", "Select supported heat-flow and boundary treatments.");
        var scope = input.Applicability;
        Guard.Require(scope.ParallelHomogeneousLayers && scope.NoAirflowThroughAssembly && scope.NoRainAbsorption
            && scope.NegligibleCapillaryAndHygroscopicEffects && scope.ConstantDesignProperties && scope.ClimateSelectionReviewed,
            "iso13788_not_applicable", "The simplified diffusion method must not be used where its stated applicability assumptions fail.");
        var review = TechnicalInput.Label(scope.ConstructionReview, "Construction review", 2000);
        var climateSource = TechnicalInput.Label(scope.ClimateSource, "Climate source", 1000);
        TechnicalInput.Count(input.Layers, nameof(input.Layers), 1, 60);
        var layers = input.Layers.Select((layer, i) =>
        {
            ArgumentNullException.ThrowIfNull(layer);
            Guard.Require(Enum.IsDefined(layer.Kind), "invalid_material_kind", "Unsupported material category.");
            var r = TechnicalInput.Range(layer.ResistanceM2KW, $"layers[{i}].resistanceM2KW", 0, 50);
            var sd = TechnicalInput.Range(layer.SdM, $"layers[{i}].sdM", .000001, 1_000_000);
            if (layer.Kind == MoistureMaterialKind.UnventilatedAirLayer)
                Guard.Require(Math.Abs(sd - .01) < 1e-12, "air_layer_sd", "ISO 13788 uses sd = 0.01 m for an unventilated air layer; its thermal resistance requires its own source.");
            return new MoistureResistanceLayer(TechnicalInput.Label(layer.Name, $"layers[{i}].name"), r, sd, layer.Kind,
                TechnicalInput.Label(layer.DesignValueSource, $"layers[{i}].designValueSource", 1000));
        }).ToArray();
        TechnicalInput.Count(input.Months, nameof(input.Months), 12, 12);
        Guard.Require(input.Months.All(m => m is not null && m.Month is >= 1 and <= 12)
            && input.Months.Select(m => m.Month).Distinct().Count() == 12, "incomplete_climate", "One explicit record for every calendar month is required.");
        var months = input.Months.OrderBy(m => m.Month).Select(m =>
        {
            TechnicalInput.Range(m.InsideTemperatureC, "Indoor temperature", -50, 80);
            TechnicalInput.Range(m.OutsideAirTemperatureC, "Outdoor air temperature", -50, 80);
            TechnicalInput.Range(m.InsideRelativeHumidityPercent, "Indoor RH", 0, 100);
            TechnicalInput.Range(m.OutsideRelativeHumidityPercent, "Outdoor RH", 0, 100);
            if (input.ClimateTreatment == MoistureClimateTreatment.SuppliedEquivalentRoofTemperature && input.ExteriorBoundary == MoistureExteriorBoundary.OutsideAir)
            {
                Guard.Require(m.EquivalentOutsideTemperatureC.HasValue, "roof_thermal_boundary_required", "Supply each monthly equivalent roof temperature; do not reuse a radiation-adjusted temperature to calculate outdoor vapour pressure.");
                TechnicalInput.Range(m.EquivalentOutsideTemperatureC!.Value, "Equivalent roof temperature", -50, 80);
            }
            else Guard.Require(m.EquivalentOutsideTemperatureC is null, "unexpected_thermal_boundary", "This boundary mode derives its thermal temperature; an additional equivalent temperature would be ambiguous.");
            if (input.ClimateTreatment == MoistureClimateTreatment.RoofMinusTwoKelvin && input.ExteriorBoundary == MoistureExteriorBoundary.OutsideAir)
                TechnicalInput.Range(m.OutsideAirTemperatureC - 2, "Equivalent roof temperature", -50, 80);
            return m with { };
        }).ToArray();
        if (input.ExteriorBoundary == MoistureExteriorBoundary.WellVentilatedCavity)
            Guard.Require(input.ClimateTreatment == MoistureClimateTreatment.WallAir,
                "ventilated_boundary_conflict", "At a well-ventilated exterior cavity use outdoor air conditions and supply only the layers inside the cavity; roof radiation does not apply at this boundary.");
        return input with { Layers = Array.AsReadOnly(layers), Months = Array.AsReadOnly(months),
            Applicability = scope with { ConstructionReview = review, ClimateSource = climateSource } };
    }

    private static IReadOnlyList<VapourBalanceNode> Grid(Iso13788BalanceInput input, int? wetLayer)
    {
        var nodes = new List<VapourBalanceNode> { new(0, -1, 0, "Inside surface", 0, 0) };
        var totalR = 0d; var totalSd = 0d;
        for (var i = 0; i < input.Layers.Count; i++)
        {
            var layer = input.Layers[i];
            var slices = input.SubdivideThermalLayers ? Math.Max(1, (int)Math.Ceiling(layer.ResistanceM2KW / .25)) : 1;
            if (wetLayer == i) slices = Math.Max(2, slices + slices % 2);
            Guard.Require(nodes.Count + slices <= MaximumGridNodes, "moisture_grid_budget", "The selected layer subdivision exceeds 256 calculation interfaces.");
            for (var j = 1; j <= slices; j++)
            {
                var fraction = (double)j / slices;
                nodes.Add(new(nodes.Count, i, fraction, j == slices ? $"After {layer.Name}" : $"Within {layer.Name}: {j}/{slices}",
                    totalR + layer.ResistanceM2KW * fraction, totalSd + layer.SdM * fraction));
            }
            totalR += layer.ResistanceM2KW; totalSd += layer.SdM;
        }
        for (var i = 1; i < nodes.Count; i++)
            Guard.Require(nodes[i].CumulativeSdM > nodes[i - 1].CumulativeSdM,
                "moisture_precision", "Adjacent vapour resistances cannot be distinguished numerically at the supplied scale.");
        return nodes.AsReadOnly();
    }

    private static MonthProfile Profile(Iso13788BalanceInput input, IReadOnlyList<VapourBalanceNode> grid, MoistureDesignMonth month)
    {
        var rsi = input.HeatFlow switch { MoistureHeatFlow.Upward => .10, MoistureHeatFlow.Horizontal => .13, MoistureHeatFlow.Downward => .17, _ => throw new ArgumentOutOfRangeException(nameof(input)) };
        var rse = input.ExteriorBoundary == MoistureExteriorBoundary.WellVentilatedCavity ? rsi : .04;
        var outside = input.ClimateTreatment switch
        {
            MoistureClimateTreatment.RoofMinusTwoKelvin => month.OutsideAirTemperatureC - 2,
            MoistureClimateTreatment.SuppliedEquivalentRoofTemperature => month.EquivalentOutsideTemperatureC!.Value,
            _ => month.OutsideAirTemperatureC
        };
        var totalR = rsi + grid[^1].CumulativeResistanceM2KW + rse;
        var flux = (month.InsideTemperatureC - outside) / totalR;
        var temperature = grid.Select(n => month.InsideTemperatureC - flux * (rsi + n.CumulativeResistanceM2KW)).ToArray();
        var saturation = temperature.Select(SaturationPressure).ToArray();
        // ISO 4.2.3 roof heat boundary and 4.2.4 outdoor vapour boundary are intentionally distinct.
        return new(temperature, saturation, SaturationPressure(month.InsideTemperatureC) * month.InsideRelativeHumidityPercent / 100,
            SaturationPressure(month.OutsideAirTemperatureC) * month.OutsideRelativeHumidityPercent / 100, rsi, rse, outside);
    }

    public static double SaturationPressure(double temperatureC)
    {
        TechnicalInput.Range(temperatureC, nameof(temperatureC), -50, 80);
        return temperatureC >= 0 ? 610.5 * Math.Exp(17.269 * temperatureC / (237.3 + temperatureC))
            : 610.5 * Math.Exp(21.875 * temperatureC / (265.5 + temperatureC));
    }

    private static PressureState Pressures(IReadOnlyList<VapourBalanceNode> grid, MonthProfile profile, double[] water)
    {
        var last = grid.Count - 1;
        var heights = (double[])profile.Saturation.Clone(); heights[0] = profile.InsidePressure; heights[last] = profile.OutsidePressure;
        var anchors = new List<int> { 0 };
        for (var i = 1; i < last; i++) if (water[i] > NumericalMassToleranceKgM2) anchors.Add(i);
        anchors.Add(last);
        var path = new List<int>();
        double Slope(int a, int b) => (heights[b] - heights[a]) / (grid[b].CumulativeSdM - grid[a].CumulativeSdM);
        for (var part = 0; part < anchors.Count - 1; part++)
        {
            var hull = new List<int> { anchors[part] };
            for (var i = anchors[part] + 1; i <= anchors[part + 1]; i++)
            {
                hull.Add(i);
                while (hull.Count >= 3 && Slope(hull[^3], hull[^2]) >= Slope(hull[^2], hull[^1]))
                    hull.RemoveAt(hull.Count - 2);
            }
            if (path.Count > 0) hull.RemoveAt(0);
            path.AddRange(hull);
        }
        var pressure = new double[grid.Count]; var rates = new double[grid.Count];
        for (var segment = 0; segment < path.Count - 1; segment++)
        {
            var a = path[segment]; var b = path[segment + 1]; var slope = Slope(a, b);
            for (var i = a; i <= b; i++) pressure[i] = heights[a] + slope * (grid[i].CumulativeSdM - grid[a].CumulativeSdM);
        }
        for (var k = 1; k < path.Count - 1; k++)
        {
            var i = path[k]; var rate = VapourPermeabilityAir * (Slope(i, path[k + 1]) - Slope(path[k - 1], i));
            if (Math.Abs(rate) <= RateTolerance) rate = 0;
            Guard.Require(double.IsFinite(rate) && (water[i] > NumericalMassToleranceKgM2 || rate >= 0),
                "moisture_numerical_failure", "The diffusion solution attempted evaporation from a dry interface.");
            rates[i] = rate;
        }
        for (var i = 1; i < last; i++)
            Guard.Require(pressure[i] <= profile.Saturation[i] + 1e-6,
                "moisture_numerical_failure", "The vapour-pressure envelope exceeds saturation without identifying an interface.");
        var boundaryInflow = VapourPermeabilityAir * (Slope(path[^2], path[^1]) - Slope(path[0], path[1]));
        return new(pressure, rates, boundaryInflow);
    }

    private static MonthlyWaterBalance Advance(Iso13788BalanceInput input, IReadOnlyList<VapourBalanceNode> grid,
        MonthProfile profile, double[] stored, int month, int sequence)
    {
        var before = (double[])stored.Clone(); var maximum = (double[])stored.Clone();
        var generated = new double[stored.Length]; var evaporated = new double[stored.Length];
        var days = DateTime.DaysInMonth(input.ClimateCalendarYear, month);
        var remaining = days * 86400d; var boundaryMass = 0d; var parts = 0;
        while (remaining > 1e-7)
        {
            Guard.Require(++parts <= MaximumGridNodes + 2, "moisture_numerical_failure", "The drying-event solver did not converge within its interface budget.");
            var state = Pressures(grid, profile, stored);
            var seconds = remaining;
            for (var i = 1; i < stored.Length - 1; i++)
                if (state.Rates[i] < -RateTolerance && stored[i] > NumericalMassToleranceKgM2)
                    seconds = Math.Min(seconds, stored[i] / -state.Rates[i]);
            Guard.Require(double.IsFinite(seconds) && seconds > 0, "moisture_numerical_failure", "Invalid drying-event time.");
            for (var i = 1; i < stored.Length - 1; i++)
            {
                var change = state.Rates[i] * seconds;
                if (change > 0) generated[i] += change; else evaporated[i] -= change;
                stored[i] += change;
                Guard.Require(stored[i] >= -1e-9 && double.IsFinite(stored[i]), "moisture_numerical_failure", "Negative or nonfinite water inventory.");
                if (stored[i] <= NumericalMassToleranceKgM2) stored[i] = 0;
                maximum[i] = Math.Max(maximum[i], stored[i]);
            }
            boundaryMass += state.NetBoundaryInflow * seconds; remaining -= seconds;
        }
        var conservationError = stored.Sum() - before.Sum() - boundaryMass;
        Guard.Require(Math.Abs(conservationError) <= 1e-8 * Math.Max(1, Math.Abs(boundaryMass)),
            "moisture_mass_balance", "The boundary fluxes do not conserve the interface water inventory.");
        var endPressure = Pressures(grid, profile, stored).Pressure;
        var records = Enumerable.Range(1, stored.Length - 2)
            .Where(i => maximum[i] > NumericalMassToleranceKgM2 || generated[i] > NumericalMassToleranceKgM2 || evaporated[i] > NumericalMassToleranceKgM2)
            .Select(i => new InterfaceWaterBalance(i, profile.Temperature[i], profile.Saturation[i], endPressure[i], before[i], generated[i], evaporated[i], stored[i], maximum[i])).ToArray();
        return new(sequence, month, days, profile.Rsi, profile.Rse, profile.ThermalOutside, profile.InsidePressure, profile.OutsidePressure,
            100 * profile.InsidePressure / profile.Saturation[0], 100 * profile.OutsidePressure / profile.Saturation[^1],
            generated.Sum(), evaporated.Sum(), stored.Sum(), conservationError, parts, Array.AsReadOnly(records));
    }

    private static IReadOnlyList<InterfaceAnnualMaximum> Maxima(IReadOnlyList<MonthlyWaterBalance> months, double[] stored)
        => Array.AsReadOnly(months.SelectMany(m => m.WetInterfaces.Select(i => new { m.Month, Water = i }))
            .GroupBy(x => x.Water.NodeIndex).OrderBy(g => g.Key).Select(g =>
            {
                var peak = g.OrderByDescending(x => x.Water.MaximumMassKgM2).First();
                return new InterfaceAnnualMaximum(g.Key, peak.Water.MaximumMassKgM2, peak.Month, stored[g.Key]);
            }).ToArray());
}
