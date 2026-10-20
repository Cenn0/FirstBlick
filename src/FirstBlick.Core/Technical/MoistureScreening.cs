namespace FirstBlick.Core.Technical;

/// <summary>Homogeneous solid layer in inside-to-outside order. Mu is dimensionless.</summary>
public sealed record MoistureLayer(string Name, double ThicknessM, double ConductivityWmK,
    double VapourResistanceFactor, string ValueSource);

/// <summary>Single steady-state screening condition. Relative humidities are percentages (0..100).</summary>
public sealed record MoistureScreeningInput(double InsideTemperatureC, double OutsideTemperatureC,
    double InsideRelativeHumidityPercent, double OutsideRelativeHumidityPercent,
    double InsideSurfaceResistanceM2KW, double OutsideSurfaceResistanceM2KW,
    IReadOnlyList<MoistureLayer> Layers);

public sealed record MoistureInterface(string Name, double TemperatureC,
    double VapourPressurePa, double SaturationPressurePa, double RelativeHumidityPercent,
    double CumulativeSdM, bool ExceedsSaturation);

public sealed record MoistureScreeningResult(MoistureScreeningInput Input, double TotalResistanceM2KW,
    double TotalSdM, double UValueWm2K, double HeatFluxWm2,
    double InsideSurfaceTemperatureC, double InsideSurfaceRelativeHumidityPercent,
    IReadOnlyList<MoistureInterface> Interfaces, bool AnyInterfaceSupersaturated,
    bool InsideSurfaceAboveEightyPercentRh, bool RequiresDetailedAssessment,
    CalculationProvenance Provenance);

/// <summary>
/// A conservative *one-condition* heat and vapour pressure comparison, NOT a
/// DIN 4108-3, ISO 13788, Glaser period balance, condensation mass or drying calculation.
/// </summary>
public static class MoistureScreening
{
    public static MoistureScreeningResult Calculate(MoistureScreeningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        TechnicalInput.Count(input.Layers, nameof(input.Layers), 1, 60);
        var inside = TechnicalInput.Range(input.InsideTemperatureC, nameof(input.InsideTemperatureC), -50, 80);
        var outside = TechnicalInput.Range(input.OutsideTemperatureC, nameof(input.OutsideTemperatureC), -50, 80);
        var insideRh = TechnicalInput.Range(input.InsideRelativeHumidityPercent, nameof(input.InsideRelativeHumidityPercent), 0, 100);
        var outsideRh = TechnicalInput.Range(input.OutsideRelativeHumidityPercent, nameof(input.OutsideRelativeHumidityPercent), 0, 100);
        var rsi = TechnicalInput.Range(input.InsideSurfaceResistanceM2KW, nameof(input.InsideSurfaceResistanceM2KW), 0, 1);
        var rse = TechnicalInput.Range(input.OutsideSurfaceResistanceM2KW, nameof(input.OutsideSurfaceResistanceM2KW), 0, 1);
        var normalizedLayers = input.Layers.Select((layer, index) =>
        {
            if (layer is null) throw new ArgumentException($"Layer {index + 1} is missing.", nameof(input));
            return new MoistureLayer(
                TechnicalInput.Label(layer.Name, $"layers[{index}].name"),
                TechnicalInput.Positive(layer.ThicknessM, $"layers[{index}].thicknessM", 5),
                TechnicalInput.Positive(layer.ConductivityWmK, $"layers[{index}].conductivityWmK", 500),
                TechnicalInput.Range(layer.VapourResistanceFactor, $"layers[{index}].vapourResistanceFactor", 1, 1_000_000),
                TechnicalInput.Label(layer.ValueSource, $"layers[{index}].valueSource", 500));
        }).ToArray();
        if (normalizedLayers.Any(l => l.ThicknessM < 0.000001 || l.ConductivityWmK < 0.001))
            throw new ArgumentOutOfRangeException(nameof(input), "Thickness must be >= 1e-6 m and conductivity >= 0.001 W/(m K).");
        var normalized = input with { Layers = Array.AsReadOnly(normalizedLayers) };
        var resistances = normalizedLayers.Select(l => l.ThicknessM / l.ConductivityWmK).ToArray();
        var totalR = rsi + resistances.Sum() + rse;
        var totalSd = normalizedLayers.Sum(l => l.ThicknessM * l.VapourResistanceFactor);
        var heatFlux = (inside - outside) / totalR;
        var insidePv = SaturationPressurePa(inside) * insideRh / 100;
        var outsidePv = SaturationPressurePa(outside) * outsideRh / 100;
        var insideSurfaceT = inside - heatFlux * rsi;
        var insideSurfaceRh = 100 * insidePv / SaturationPressurePa(insideSurfaceT);
        var cumulativeR = rsi;
        var cumulativeSd = 0d;
        var points = new List<MoistureInterface>();
        for (var index = 0; index < normalizedLayers.Length; index++)
        {
            cumulativeR += resistances[index];
            cumulativeSd += normalizedLayers[index].ThicknessM * normalizedLayers[index].VapourResistanceFactor;
            var temperature = inside - heatFlux * cumulativeR;
            var pressure = insidePv + (outsidePv - insidePv) * cumulativeSd / totalSd;
            var saturation = SaturationPressurePa(temperature);
            var rh = 100 * pressure / saturation;
            // Exact equality is saturation, not a reliable positive condensation prediction.
            var exceeded = pressure > saturation + 1e-8;
            points.Add(new MoistureInterface($"After {normalizedLayers[index].Name}", temperature,
                pressure, saturation, rh, cumulativeSd, exceeded));
        }
        return new MoistureScreeningResult(normalized, totalR, totalSd, 1 / totalR, heatFlux,
            insideSurfaceT, insideSurfaceRh, points.AsReadOnly(),
            points.Any(x => x.ExceedsSaturation), insideSurfaceRh > 80,
            true,
            CalculationEvidence.Create("steady-vapour-screening", normalized,
                "One user-specified steady-state condition; Fourier temperature gradient, vapour pressure interpolated across sd = mu * thickness.",
                "Pressure exceeding saturation flags a possible moisture issue, not condensate mass, annual balance or guaranteed absence of risk.",
                "No month-to-month accumulation/drying, initial moisture, sorption, capillary transport, convection, rain, climate files, shading or roof ventilation.",
                "The 80% surface-RH screening flag is not a DIN/ISO acceptance threshold for this construction. All scenarios require specialist review.",
                "Not a DIN 4108-3 or ISO 13788 compliance calculation; value sources and construction applicability are unverified."));
    }

    /// <summary>Elementary Magnus-type approximation over water/ice; not an ISO 13788 implementation.</summary>
    public static double SaturationPressurePa(double temperatureC)
    {
        TechnicalInput.Range(temperatureC, nameof(temperatureC), -50, 80);
        return temperatureC >= 0
            ? 610.5 * Math.Exp(17.269 * temperatureC / (237.3 + temperatureC))
            : 610.5 * Math.Exp(21.875 * temperatureC / (265.5 + temperatureC));
    }
}
