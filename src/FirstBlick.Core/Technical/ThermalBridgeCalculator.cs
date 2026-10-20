namespace FirstBlick.Core.Technical;

public sealed record LinearThermalBridge(string Name, double LengthM, double PsiWmK, string ValueSource);
public sealed record PointThermalBridge(string Name, int Count, double ChiWK, string ValueSource);
public sealed record ThermalBridgeInput(
    double AreaM2,
    double BaseUValueWm2K,
    string ReferenceConvention,
    IReadOnlyList<LinearThermalBridge> LinearBridges,
    IReadOnlyList<PointThermalBridge> PointBridges);
public sealed record ThermalBridgeResult(
    ThermalBridgeInput Input,
    double BaseHeatLossCoefficientWK,
    double LinearHeatLossCoefficientWK,
    double PointHeatLossCoefficientWK,
    double TotalHeatLossCoefficientWK,
    double DeltaTransmittanceWm2K,
    double EquivalentTransmittanceWm2K,
    CalculationProvenance Provenance);

/// <summary>Aggregates supplied coefficients; does not derive psi/chi values or simulate a thermal bridge.</summary>
public static class ThermalBridgeCalculator
{
    public static ThermalBridgeResult Aggregate(ThermalBridgeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var area = TechnicalInput.Range(input.AreaM2, nameof(input.AreaM2), 1e-8, 1e9);
        var baseU = TechnicalInput.Range(input.BaseUValueWm2K, nameof(input.BaseUValueWm2K), 1e-9, 10000);
        var convention = TechnicalInput.Label(input.ReferenceConvention, nameof(input.ReferenceConvention), 500);
        TechnicalInput.Count(input.LinearBridges, nameof(input.LinearBridges), 0, 1000);
        TechnicalInput.Count(input.PointBridges, nameof(input.PointBridges), 0, 1000);
        var linear = input.LinearBridges.Select((bridge, index) =>
        {
            if (bridge is null) throw new ArgumentException($"Linear bridge {index + 1} is missing.", nameof(input));
            return new LinearThermalBridge(
                TechnicalInput.Label(bridge.Name, $"linearBridges[{index}].name"),
                TechnicalInput.Range(bridge.LengthM, $"linearBridges[{index}].lengthM", 1e-6, 1e6),
                TechnicalInput.Range(bridge.PsiWmK, $"linearBridges[{index}].psiWmK", -100, 100),
                TechnicalInput.Label(bridge.ValueSource, $"linearBridges[{index}].valueSource", 500));
        }).ToArray();
        var points = input.PointBridges.Select((bridge, index) =>
        {
            if (bridge is null) throw new ArgumentException($"Point bridge {index + 1} is missing.", nameof(input));
            if (bridge.Count is < 1 or > 1000000)
                throw new ArgumentOutOfRangeException(nameof(input), "Each point-bridge count must be between 1 and 1000000.");
            return new PointThermalBridge(
                TechnicalInput.Label(bridge.Name, $"pointBridges[{index}].name"), bridge.Count,
                TechnicalInput.Range(bridge.ChiWK, $"pointBridges[{index}].chiWK", -100, 100),
                TechnicalInput.Label(bridge.ValueSource, $"pointBridges[{index}].valueSource", 500));
        }).ToArray();
        var normalized = input with
        {
            ReferenceConvention = convention,
            LinearBridges = Array.AsReadOnly(linear), PointBridges = Array.AsReadOnly(points)
        };
        var baseLoss = area * baseU;
        var linearLoss = linear.Sum(bridge => bridge.LengthM * bridge.PsiWmK);
        var pointLoss = points.Sum(bridge => bridge.Count * bridge.ChiWK);
        var total = baseLoss + linearLoss + pointLoss;
        if (!double.IsFinite(total) || total <= 0)
            throw new ArgumentException("The supplied coefficients produce a non-positive total heat-loss coefficient.", nameof(input));
        return new(normalized, baseLoss, linearLoss, pointLoss, total,
            (linearLoss + pointLoss) / area, total / area,
            CalculationEvidence.Create("supplied-thermal-bridge-aggregation", normalized,
                "Arithmetic aggregation H = U*A + sum(psi*length) + sum(chi*count); no two- or three-dimensional simulation.",
                "The caller must establish compatible dimensional conventions, sources, project applicability and no double counting.",
                "Signed coefficients are retained because reference conventions can produce negative corrections; total H must remain positive.",
                "The equivalent transmittance is an area-normalized aggregate, not a certified component U-value or condensation assessment."));
    }
}
