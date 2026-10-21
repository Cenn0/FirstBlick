namespace FirstBlick.Core.Technical;

public sealed record ThermalLayer(string Name, double ThicknessM, double ConductivityWmK, string ValueSource);

/// <summary>Solid homogeneous layers, ordered from inside to outside; all values use SI units.</summary>
public sealed record ThermalInput(
    double InsideTemperatureC,
    double OutsideTemperatureC,
    double InsideSurfaceResistanceM2KW,
    double OutsideSurfaceResistanceM2KW,
    IReadOnlyList<ThermalLayer> Layers);

public sealed record ThermalLayerResult(string Name, double ResistanceM2KW);
public sealed record ThermalInterface(string Name, double CumulativeResistanceM2KW, double TemperatureC);
public sealed record ThermalResult(
    ThermalInput Input,
    IReadOnlyList<ThermalLayerResult> Layers,
    double TotalResistanceM2KW,
    double UValueWm2K,
    double HeatFluxWm2,
    IReadOnlyList<ThermalInterface> Interfaces,
    CalculationProvenance Provenance);

public sealed record ThermalPath(string Name, double AreaFraction, IReadOnlyList<ThermalLayer> Layers);
public sealed record ParallelThermalInput(
    double InsideTemperatureC,
    double OutsideTemperatureC,
    double InsideSurfaceResistanceM2KW,
    double OutsideSurfaceResistanceM2KW,
    IReadOnlyList<ThermalPath> Paths);
public sealed record ThermalPathResult(string Name, double AreaFraction, ThermalResult Calculation);
public sealed record ParallelThermalResult(
    ParallelThermalInput Input,
    IReadOnlyList<ThermalPathResult> Paths,
    double EquivalentResistanceM2KW,
    double UValueWm2K,
    double HeatFluxWm2,
    CalculationProvenance Provenance);

/// <summary>
/// Independent Fourier-law resistance calculations. These methods make no claim of ISO/DIN conformity.
/// In particular, independent parallel paths do not model lateral heat flow around rafters or metal bridges.
/// </summary>
public static class ThermalCalculator
{
    public static ThermalResult Calculate(ThermalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        TechnicalInput.Count(input.Layers, nameof(input.Layers), 1, 60);
        var inside = TechnicalInput.Range(input.InsideTemperatureC, nameof(input.InsideTemperatureC), -80, 120);
        var outside = TechnicalInput.Range(input.OutsideTemperatureC, nameof(input.OutsideTemperatureC), -80, 120);
        var rsi = TechnicalInput.Range(input.InsideSurfaceResistanceM2KW, nameof(input.InsideSurfaceResistanceM2KW), 0, 1);
        var rse = TechnicalInput.Range(input.OutsideSurfaceResistanceM2KW, nameof(input.OutsideSurfaceResistanceM2KW), 0, 1);
        var layers = input.Layers.Select((layer, index) =>
        {
            if (layer is null) throw new ArgumentException($"Layer {index + 1} is missing.", nameof(input));
            return new ThermalLayer(
                TechnicalInput.Label(layer.Name, $"layers[{index}].name"),
                TechnicalInput.Range(layer.ThicknessM, $"layers[{index}].thicknessM", 0.000001, 5),
                TechnicalInput.Range(layer.ConductivityWmK, $"layers[{index}].conductivityWmK", 0.001, 500),
                TechnicalInput.Label(layer.ValueSource, $"layers[{index}].valueSource", 500));
        }).ToArray();
        var normalized = input with { Layers = Array.AsReadOnly(layers) };
        var resistances = layers.Select(layer => new ThermalLayerResult(layer.Name, layer.ThicknessM / layer.ConductivityWmK)).ToArray();
        var total = rsi + resistances.Sum(layer => layer.ResistanceM2KW) + rse;
        var uValue = 1 / total;
        var flux = (inside - outside) * uValue;
        var cumulative = rsi;
        var interfaces = new List<ThermalInterface>
        {
            new("Inside surface", cumulative, inside - flux * cumulative)
        };
        foreach (var layer in resistances)
        {
            cumulative += layer.ResistanceM2KW;
            interfaces.Add(new($"After {layer.Name}", cumulative, inside - flux * cumulative));
        }
        return new(normalized, Array.AsReadOnly(resistances), total, uValue, flux, interfaces.AsReadOnly(),
            CalculationEvidence.Create("solid-plane-resistance", normalized,
                "Steady one-dimensional conduction with constant user-supplied conductivity; positive heat flux is outward.",
                "No air layers, ventilation, moisture, lateral heat flow, fasteners, thermal bridges, soil or glazing model.",
                "Surface resistances and conductivity design values require project-specific evidence. No DIN/ISO or legal compliance assessment."));
    }

    public static ParallelThermalResult CalculateParallelPaths(ParallelThermalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        TechnicalInput.Count(input.Paths, nameof(input.Paths), 1, 16);
        var fractions = input.Paths.Select((path, index) =>
        {
            if (path is null) throw new ArgumentException($"Path {index + 1} is missing.", nameof(input));
            TechnicalInput.Label(path.Name, $"paths[{index}].name");
            return TechnicalInput.Positive(path.AreaFraction, $"paths[{index}].areaFraction", 1);
        }).ToArray();
        var sum = fractions.Sum();
        if (Math.Abs(sum - 1) > 1e-10)
            throw new ArgumentException("Path area fractions must sum to one (absolute tolerance 1e-10).", nameof(input));
        var results = input.Paths.Select((path, index) => new ThermalPathResult(path.Name.Trim(), fractions[index] / sum,
            Calculate(new(input.InsideTemperatureC, input.OutsideTemperatureC,
                input.InsideSurfaceResistanceM2KW, input.OutsideSurfaceResistanceM2KW, path.Layers)))).ToArray();
        var normalized = input with
        {
            Paths = Array.AsReadOnly(results.Select(path => new ThermalPath(path.Name, path.AreaFraction, path.Calculation.Input.Layers)).ToArray())
        };
        var uValue = results.Sum(path => path.AreaFraction * path.Calculation.UValueWm2K);
        return new(normalized, Array.AsReadOnly(results), 1 / uValue, uValue,
            (input.InsideTemperatureC - input.OutsideTemperatureC) * uValue,
            CalculationEvidence.Create("ideal-independent-parallel-paths", normalized,
                "Ideal independent paths with identical boundary temperatures and no lateral heat exchange.",
                "This is not the ISO 6946 combined method and does not establish a rafter-roof or metal-bridge U-value.",
                "Each path inherits the limitations of the solid-plane-resistance model. Fractions within 1e-10 of unity are normalized."));
    }
}
