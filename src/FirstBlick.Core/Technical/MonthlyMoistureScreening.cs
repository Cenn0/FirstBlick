namespace FirstBlick.Core.Technical;

public sealed record MonthlyMoistureClimate(int Month, double InsideTemperatureC,
    double OutsideTemperatureC, double InsideRelativeHumidityPercent, double OutsideRelativeHumidityPercent);

public sealed record MonthlyMoistureScreeningInput(double InsideSurfaceResistanceM2KW,
    double OutsideSurfaceResistanceM2KW, IReadOnlyList<MoistureLayer> Layers,
    IReadOnlyList<MonthlyMoistureClimate> Months);

public sealed record MonthlyMoistureScreeningResult(MonthlyMoistureScreeningInput Input,
    IReadOnlyList<MonthlyMoistureResult> Months, IReadOnlyList<int> SupersaturatedMonths,
    IReadOnlyList<int> HighInteriorSurfaceHumidityMonths, bool RequiresDetailedAssessment,
    CalculationProvenance Provenance);

public sealed record MonthlyMoistureResult(int Month, MoistureScreeningResult Screening);

/// <summary>
/// Repeated independent steady-state checks using 12 explicitly supplied climates.
/// NO condensate accumulation/drying and NO ISO 13788/DIN 4108-3 period-balance claims.
/// </summary>
public static class MonthlyMoistureScreening
{
    public static MonthlyMoistureScreeningResult Calculate(MonthlyMoistureScreeningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        TechnicalInput.Count(input.Months, nameof(input.Months), 12, 12);
        GuardMonths(input.Months);
        var sorted = input.Months.OrderBy(x => x.Month).ToArray();
        var results = sorted.Select(m => new MonthlyMoistureResult(m.Month,
            MoistureScreening.Calculate(new MoistureScreeningInput(m.InsideTemperatureC,
                m.OutsideTemperatureC, m.InsideRelativeHumidityPercent, m.OutsideRelativeHumidityPercent,
                input.InsideSurfaceResistanceM2KW, input.OutsideSurfaceResistanceM2KW, input.Layers)))).ToArray();
        var normalized = input with { Layers = results[0].Screening.Input.Layers,
            Months = Array.AsReadOnly(sorted) };
        return new(normalized, Array.AsReadOnly(results),
            Array.AsReadOnly(results.Where(x => x.Screening.AnyInterfaceSupersaturated).Select(x => x.Month).ToArray()),
            Array.AsReadOnly(results.Where(x => x.Screening.InsideSurfaceAboveEightyPercentRh).Select(x => x.Month).ToArray()),
            true, CalculationEvidence.Create("monthly-independent-vapour-screening", normalized,
                "Twelve independent stationary calculations using 12 explicit indoor/outdoor temperatures and relative humidities.",
                "No month-to-month water storage, condensate flux, evaporation, drying reserve or year-end balance.",
                "Neither ISO 13788 nor DIN 4108-3 conformity is established by these 12 screening snapshots.",
                "Specialist assessment and construction-/climate-specific authoritative input are mandatory."));
    }

    private static void GuardMonths(IReadOnlyList<MonthlyMoistureClimate> months)
    {
        if (months.Any(x => x is null) || months.Select(x => x.Month).Distinct().Count() != 12
            || months.Any(x => x.Month is < 1 or > 12))
            throw new ArgumentException("Exactly one climate record for each calendar month 1..12 is required.", nameof(months));
    }
}
