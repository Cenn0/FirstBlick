namespace FirstBlick.Core.Technical;

/// <summary>A contiguous section of the roof with its own through-thickness material path.
/// Sections share the same layer boundaries; their area fractions form a full partition.</summary>
public sealed record InhomogeneousThermalSection(string Name, double AreaFraction,
    IReadOnlyList<ThermalLayer> Layers);

/// <summary>
/// Approximation for non-metallic, aligned, plane inhomogeneous layers only.
/// The supplied fractions and material values are project inputs, never catalogue defaults.
/// </summary>
public sealed record InhomogeneousThermalInput(
    double InsideTemperatureC, double OutsideTemperatureC,
    double InsideSurfaceResistanceM2KW, double OutsideSurfaceResistanceM2KW,
    string MaterialDataSource, bool NoMetalBridgingConfirmed,
    IReadOnlyList<InhomogeneousThermalSection> Sections);

public sealed record InhomogeneousThermalSectionResult(string Name, double AreaFraction,
    ThermalResult Path);
public sealed record InhomogeneousThermalResult(
    InhomogeneousThermalInput Input, IReadOnlyList<InhomogeneousThermalSectionResult> Sections,
    double UpperResistanceM2KW, double LowerResistanceM2KW,
    double EstimatedResistanceM2KW, double UValueWm2K, double HeatFluxWm2,
    double UpperToLowerRatio, double HalfSpreadPercent,
    bool MetalBridgingModelled, bool ConstructionComplianceAssessed,
    CalculationProvenance Provenance);

/// <summary>
/// Separately calculates two limiting idealisations: independent through-thickness
/// sections and layer-by-layer isothermal planes. Their arithmetic resistance mean
/// is a bounded estimate, NOT a verified DIN/ISO or building compliance certificate.
/// </summary>
public static class InhomogeneousThermalCalculator
{
    public static InhomogeneousThermalResult Calculate(InhomogeneousThermalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var source = TechnicalInput.Label(input.MaterialDataSource, "materialDataSource", 1000);
        TechnicalInput.Count(input.Sections, "sections", 1, 16);
        // A caller's unchecked confirmation is not a structural assessment. In the
        // absence of this assertion even the bounded approximation is unavailable.
        if (!input.NoMetalBridgingConfirmed)
            throw new ArgumentException("This approximation excludes metallic bridges and fasteners requiring corrections.", nameof(input));

        var names = new HashSet<string>(StringComparer.Ordinal);
        var pathResults = new List<InhomogeneousThermalSectionResult>();
        var fractions = 0d;
        int? layerCount = null;
        double[]? referenceThicknesses = null;
        foreach (var (section, index) in input.Sections.Select((value, index) => (value, index)))
        {
            if (section is null) throw new ArgumentException($"Section {index + 1} is missing.", nameof(input));
            var name = TechnicalInput.Label(section.Name, $"sections[{index}].name", 200);
            if (!names.Add(name)) throw new ArgumentException("Section names must be unique.", nameof(input));
            var fraction = TechnicalInput.Positive(section.AreaFraction, $"sections[{index}].areaFraction", 1);
            TechnicalInput.Count(section.Layers, $"sections[{index}].layers", 1, 60);
            if (layerCount is null) layerCount = section.Layers.Count;
            if (section.Layers.Count != layerCount)
                throw new ArgumentException("All roof sections must have the same number of aligned layers.", nameof(input));

            var path = ThermalCalculator.Calculate(new(input.InsideTemperatureC, input.OutsideTemperatureC,
                input.InsideSurfaceResistanceM2KW, input.OutsideSurfaceResistanceM2KW, section.Layers));
            var thicknesses = path.Input.Layers.Select(layer => layer.ThicknessM).ToArray();
            if (referenceThicknesses is null) referenceThicknesses = thicknesses;
            else if (thicknesses.Where((thickness, layer) => Math.Abs(thickness - referenceThicknesses[layer]) > 1e-9).Any())
                throw new ArgumentException("Every section must share identical physical layer boundaries (1e-9 m tolerance).", nameof(input));
            pathResults.Add(new(name, fraction, path));
            fractions += fraction;
        }
        if (Math.Abs(fractions - 1) > 1e-10)
            throw new ArgumentException("Section area fractions must sum to one (1e-10 tolerance).", nameof(input));

        var normalizedResults = pathResults.OrderBy(section => section.Name, StringComparer.Ordinal)
            .Select(section => section with { AreaFraction = section.AreaFraction / fractions }).ToArray();
        var upper = 1d / normalizedResults.Sum(section =>
            section.AreaFraction / section.Path.TotalResistanceM2KW);
        var lower = input.InsideSurfaceResistanceM2KW + input.OutsideSurfaceResistanceM2KW;
        for (var layer = 0; layer < layerCount!.Value; layer++)
        {
            var atLayer = layer;
            lower += 1d / normalizedResults.Sum(section =>
                section.AreaFraction / section.Path.Layers[atLayer].ResistanceM2KW);
        }
        var ratio = upper / lower;
        if (!double.IsFinite(ratio) || ratio > 1.5 + 1e-12 || upper + 1e-10 < lower)
            throw new ArgumentException("The resistance-bound method is outside its supported ratio range (upper/lower <= 1.5).", nameof(input));

        var average = (upper + lower) / 2;
        var normalized = input with
        {
            MaterialDataSource = source,
            Sections = Array.AsReadOnly(normalizedResults.Select(section => new InhomogeneousThermalSection(
                section.Name, section.AreaFraction, section.Path.Input.Layers)).ToArray())
        };
        var u = 1 / average;
        return new(normalized, Array.AsReadOnly(normalizedResults),
            upper, lower, average, u, (input.InsideTemperatureC - input.OutsideTemperatureC) * u,
            ratio, 100 * (upper - lower) / (upper + lower), false, false,
            CalculationEvidence.Create("inhomogeneous-resistance-bound-estimate", normalized,
                "One-dimensional area-weighted path conductance supplies the upper resistance limit; isothermal layer planes supply the lower limit.",
                "Only aligned, plane, non-metallic sections are considered. The upper/lower resistance ratio must not exceed 1.5.",
                "No air cavities, ventilation, metal fasteners, moisture, workmanship, contact resistance, thermal bridge correction or multidimensional heat flow is modelled.",
                "All area fractions, surface resistances, layer thicknesses and design conductivities require independently reviewed project sources.",
                "The arithmetic resistance mean is a preliminary U-value estimate, not a verified ISO 6946:2017 calculation or DIN thermal-protection assessment."));
    }
}
