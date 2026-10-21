using FirstBlick.Core.Technical;
using Xunit;

namespace FirstBlick.Core.Tests.Technical;

public sealed class InhomogeneousThermalTests
{
    private static InhomogeneousThermalInput RafterRoof() => new(
        20, -20, .13, .04, "Independently provided synthetic layer values", true,
        [
            new("Rafter", .15, [
                new("Inner lining", .02, .2, "Synthetic lining"),
                new("Timber rafter", .2, .12, "Synthetic timber"),
                new("Outer lining", .02, .2, "Synthetic lining")]),
            new("Insulation", .85, [
                new("Inner lining", .02, .2, "Synthetic lining"),
                new("Insulation between rafters", .2, .04, "Synthetic insulation"),
                new("Outer lining", .02, .2, "Synthetic lining")])
        ]);

    [Fact]
    public void Independently_calculated_resistance_bounds_produce_a_traceable_estimate()
    {
        var result = InhomogeneousThermalCalculator.Calculate(RafterRoof());
        // Independent section arithmetic: R_wood=0.37+0.2/0.12, R_insulation=0.37+0.2/0.04.
        // Upper = 1/(0.15/R_wood+0.85/R_insulation).
        // Lower = 0.13+0.04+2*(0.02/0.2)+1/(0.15/(0.2/0.12)+0.85/(0.2/0.04)).
        Assert.Equal(4.311524310118266, result.UpperResistanceM2KW, 11);
        Assert.Equal(4.216153846153846, result.LowerResistanceM2KW, 11);
        Assert.Equal(4.263839078136056, result.EstimatedResistanceM2KW, 11);
        Assert.Equal(.23453042708102192, result.UValueWm2K, 11);
        Assert.Equal(9.381217083240877, result.HeatFluxWm2, 10);
        Assert.True(result.UpperToLowerRatio > 1 && result.UpperToLowerRatio < 1.5);
        Assert.Equal(1.1183637822245285, result.HalfSpreadPercent, 9);
        Assert.False(result.MetalBridgingModelled);
        Assert.False(result.ConstructionComplianceAssessed);
        Assert.False(result.Provenance.IsNormative);
    }

    [Fact]
    public void Identical_paths_reproduce_the_homogeneous_series_solution_and_permutation_is_stable()
    {
        var input = RafterRoof();
        var same = input.Sections[0].Layers;
        var homogeneous = input with { Sections = [
            input.Sections[0], input.Sections[1] with { Layers = same }] };
        var result = InhomogeneousThermalCalculator.Calculate(homogeneous);
        var series = ThermalCalculator.Calculate(new(20, -20, .13, .04, same));
        Assert.Equal(series.TotalResistanceM2KW, result.UpperResistanceM2KW, 12);
        Assert.Equal(series.TotalResistanceM2KW, result.LowerResistanceM2KW, 12);
        Assert.Equal(series.UValueWm2K, result.UValueWm2K, 12);
        Assert.Equal(0, result.HalfSpreadPercent, 10);
        var reversed = InhomogeneousThermalCalculator.Calculate(homogeneous with { Sections = homogeneous.Sections.Reverse().ToArray() });
        Assert.Equal(result.Provenance.InputSha256, reversed.Provenance.InputSha256);
        Assert.Equal(result.UValueWm2K, reversed.UValueWm2K);
    }

    [Fact]
    public void Incompatible_physical_layer_boundaries_and_missing_provenance_are_rejected()
    {
        var example = RafterRoof();
        var modifiedLayers = example.Sections[1].Layers.ToArray();
        modifiedLayers[1] = modifiedLayers[1] with { ThicknessM = .201 };
        Assert.ThrowsAny<ArgumentException>(() => InhomogeneousThermalCalculator.Calculate(example with
        { Sections = [example.Sections[0], example.Sections[1] with { Layers = modifiedLayers }] }));
        Assert.ThrowsAny<ArgumentException>(() => InhomogeneousThermalCalculator.Calculate(example with
        { MaterialDataSource = " " }));
        Assert.ThrowsAny<ArgumentException>(() => InhomogeneousThermalCalculator.Calculate(example with
        { NoMetalBridgingConfirmed = false }));
    }

    [Fact]
    public void Invalid_fractions_missing_layers_and_unbounded_ratios_fail_closed()
    {
        var example = RafterRoof();
        Assert.ThrowsAny<ArgumentException>(() => InhomogeneousThermalCalculator.Calculate(example with
        { Sections = [example.Sections[0] with { AreaFraction = double.NaN }, example.Sections[1]] }));
        Assert.ThrowsAny<ArgumentException>(() => InhomogeneousThermalCalculator.Calculate(example with
        { Sections = [example.Sections[0] with { AreaFraction = .25 }, example.Sections[1]] }));
        Assert.ThrowsAny<ArgumentException>(() => InhomogeneousThermalCalculator.Calculate(example with
        { Sections = [example.Sections[0], example.Sections[1] with { Layers = example.Sections[1].Layers[..2] }] }));
        Assert.ThrowsAny<ArgumentException>(() => InhomogeneousThermalCalculator.Calculate(example with
        { Sections = [example.Sections[0], example.Sections[1] with { Name = "Rafter" }] }));

        // Two crossed heterogeneous layers: each full path is highly resistive,
        // while independently equilibrated layer planes are conductive.
        InhomogeneousThermalInput crossed = new(20, 0, 0, 0, "Synthetic crossed paths", true,
        [
            new("A", .5, [new("A1", .1, .001, "Synthetic"), new("A2", .1, 1, "Synthetic")]),
            new("B", .5, [new("B1", .1, 1, "Synthetic"), new("B2", .1, .001, "Synthetic")])
        ]);
        Assert.ThrowsAny<ArgumentException>(() => InhomogeneousThermalCalculator.Calculate(crossed));
    }

    [Fact]
    public void Inputs_are_copied_for_reproducibility()
    {
        var input = RafterRoof();
        var supplied = input.Sections.ToArray();
        var result = InhomogeneousThermalCalculator.Calculate(input with { Sections = supplied });
        supplied[0] = supplied[0] with { Name = "Changed" };
        Assert.Equal("Rafter", result.Input.Sections.Single(x => x.Name == "Rafter").Name);
        Assert.Equal(64, result.Provenance.InputSha256.Length);
    }
}
