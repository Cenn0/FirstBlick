using FirstBlick.Core.Technical;
using Xunit;

namespace FirstBlick.Core.Tests.Technical;

public sealed class ThermalTests
{
    private static ThermalInput Example() => new(20, -10, 0.13, 0.04,
        [new("Layer A", 0.1, 0.04, "Synthetic reference"), new("Layer B", 0.02, 0.2, "Synthetic reference")]);

    [Fact]
    public void SeriesResistanceAndBoundaryTemperaturesConserveEnergy()
    {
        var result = ThermalCalculator.Calculate(Example());
        // Independent elementary reference: 0.1/0.04 + 0.02/0.2 + 0.13 + 0.04 = 2.77.
        Assert.Equal(2.77, result.TotalResistanceM2KW, 12);
        Assert.Equal(0.36101083032490975, result.UValueWm2K, 12);
        Assert.Equal(10.830324909747291, result.HeatFluxWm2, 11);
        Assert.Equal(20, result.Interfaces[0].TemperatureC + result.HeatFluxWm2 * 0.13, 11);
        Assert.Equal(-10, result.Interfaces[^1].TemperatureC - result.HeatFluxWm2 * 0.04, 11);
        Assert.Equal(30, result.HeatFluxWm2 * result.TotalResistanceM2KW, 11);
        Assert.False(result.Provenance.IsNormative);
        Assert.NotEmpty(result.Provenance.Limitations);
    }

    [Fact]
    public void EqualBoundaryTemperaturesProduceNoFlux()
    {
        var result = ThermalCalculator.Calculate(Example() with { InsideTemperatureC = 15, OutsideTemperatureC = 15 });
        Assert.Equal(0, result.HeatFluxWm2);
        Assert.All(result.Interfaces, point => Assert.Equal(15, point.TemperatureC));
    }

    [Fact]
    public void ReversingBoundaryTemperaturesReversesFluxWithoutChangingUValue()
    {
        var outward = ThermalCalculator.Calculate(Example());
        var inward = ThermalCalculator.Calculate(Example() with { InsideTemperatureC = -10, OutsideTemperatureC = 20 });
        Assert.Equal(-outward.HeatFluxWm2, inward.HeatFluxWm2, 12);
        Assert.Equal(outward.UValueWm2K, inward.UValueWm2K);
    }

    [Fact]
    public void IdealSurfaceBoundariesAndLayerSplittingRetainResistance()
    {
        var a = new ThermalInput(20, 0, 0, 0, [new("Whole", 0.2, 0.05, "Reference")]);
        var b = a with { Layers = [new("Half A", 0.1, 0.05, "Reference"), new("Half B", 0.1, 0.05, "Reference")] };
        var one = ThermalCalculator.Calculate(a);
        var two = ThermalCalculator.Calculate(b);
        Assert.Equal(4, one.TotalResistanceM2KW);
        Assert.Equal(one.UValueWm2K, two.UValueWm2K);
        Assert.Equal(20, one.Interfaces[0].TemperatureC);
        Assert.Equal(0, one.Interfaces[^1].TemperatureC);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public void UnsupportedConductivityIsRejected(double conductivity)
        => Assert.ThrowsAny<ArgumentException>(() => ThermalCalculator.Calculate(Example() with
        {
            Layers = [new("Bad", 0.1, conductivity, "Reference")]
        }));

    [Fact]
    public void AbsentInputsAndInvalidBoundsAreRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => ThermalCalculator.Calculate(null!));
        Assert.ThrowsAny<ArgumentException>(() => ThermalCalculator.Calculate(Example() with { Layers = [] }));
        Assert.ThrowsAny<ArgumentException>(() => ThermalCalculator.Calculate(Example() with { Layers = [null!] }));
        Assert.ThrowsAny<ArgumentException>(() => ThermalCalculator.Calculate(Example() with { InsideTemperatureC = double.NaN }));
        Assert.ThrowsAny<ArgumentException>(() => ThermalCalculator.Calculate(Example() with { OutsideSurfaceResistanceM2KW = -0.1 }));
        Assert.ThrowsAny<ArgumentException>(() => ThermalCalculator.Calculate(Example() with { Layers = [new("A", 0, 0.04, "Source")] }));
        Assert.ThrowsAny<ArgumentException>(() => ThermalCalculator.Calculate(Example() with { Layers = [new("A", 0.1, 0.04, "")] }));
        Assert.ThrowsAny<ArgumentException>(() => ThermalCalculator.Calculate(Example() with
        {
            Layers = Enumerable.Repeat(new ThermalLayer("A", 0.1, 0.04, "Source"), 61).ToArray()
        }));
    }

    [Fact]
    public void InputEvidenceIsStableAndSnapshotIsIsolatedFromCallerMutation()
    {
        var layers = Example().Layers.ToArray();
        var input = Example() with { Layers = layers };
        var result = ThermalCalculator.Calculate(input);
        Assert.Equal(result.Provenance.InputSha256, ThermalCalculator.Calculate(input).Provenance.InputSha256);
        Assert.Equal(64, result.Provenance.InputSha256.Length);
        layers[0] = layers[0] with { ThicknessM = 0.3 };
        Assert.Equal(0.1, result.Input.Layers[0].ThicknessM);
        Assert.NotEqual(result.Provenance.InputSha256, ThermalCalculator.Calculate(input).Provenance.InputSha256);
    }

    [Fact]
    public void IndependentPathsUseAreaWeightedConductanceAndRetainScope()
    {
        var input = new ParallelThermalInput(20, 0, 0, 0,
            [new("Path A", 0.75, [new("A", 0.2, 0.05, "Synthetic reference")]),
             new("Path B", 0.25, [new("B", 0.2, 0.2, "Synthetic reference")])]);
        var result = ThermalCalculator.CalculateParallelPaths(input);
        // Resistances 4 and 1; conductance 0.75/4 + 0.25/1 = 0.4375.
        Assert.Equal(0.4375, result.UValueWm2K);
        Assert.Equal(8.75, result.HeatFluxWm2);
        Assert.Equal(16d / 7, result.EquivalentResistanceM2KW, 12);
        Assert.False(result.Provenance.IsNormative);
        Assert.Contains(result.Provenance.Limitations, text => text.Contains("not the ISO 6946"));
        Assert.ThrowsAny<ArgumentException>(() => ThermalCalculator.CalculateParallelPaths(input with
        {
            Paths = [input.Paths[0] with { AreaFraction = 0.5 }, input.Paths[1]]
        }));
        Assert.ThrowsAny<ArgumentException>(() => ThermalCalculator.CalculateParallelPaths(input with
        {
            Paths = [input.Paths[0] with { AreaFraction = double.NaN }]
        }));
    }

    [Fact]
    public void IdenticalParallelPathsReduceToSinglePath()
    {
        var input = Example();
        var parallel = ThermalCalculator.CalculateParallelPaths(new(input.InsideTemperatureC, input.OutsideTemperatureC,
            input.InsideSurfaceResistanceM2KW, input.OutsideSurfaceResistanceM2KW,
            [new("A", 0.4, input.Layers), new("B", 0.6, input.Layers)]));
        Assert.Equal(ThermalCalculator.Calculate(input).UValueWm2K, parallel.UValueWm2K, 12);
    }

    [Fact]
    public void SuppliedBridgeCoefficientsHaveCorrectDimensionsAndCanBeSigned()
    {
        var input = new ThermalBridgeInput(100, 0.2, "Common synthetic dimensional convention",
            [new("Edge", 20, 0.05, "Supplied reference")], [new("Point", 5, 0.2, "Supplied reference")]);
        var result = ThermalBridgeCalculator.Aggregate(input);
        Assert.Equal(20, result.BaseHeatLossCoefficientWK);
        Assert.Equal(1, result.LinearHeatLossCoefficientWK);
        Assert.Equal(1, result.PointHeatLossCoefficientWK);
        Assert.Equal(22, result.TotalHeatLossCoefficientWK);
        Assert.Equal(0.22, result.EquivalentTransmittanceWm2K, 12);
        Assert.Equal(0.02, result.DeltaTransmittanceWm2K, 12);
        var negative = ThermalBridgeCalculator.Aggregate(input with
        {
            LinearBridges = [new("Signed reference", 20, -0.01, "Supplied reference")], PointBridges = []
        });
        Assert.Equal(19.8, negative.TotalHeatLossCoefficientWK, 12);
        Assert.False(negative.Provenance.IsNormative);
    }

    [Fact]
    public void InvalidBridgeEvidenceAndNonPhysicalAggregateAreRejected()
    {
        var input = new ThermalBridgeInput(10, 0.2, "Reference convention", [], []);
        Assert.ThrowsAny<ArgumentException>(() => ThermalBridgeCalculator.Aggregate(input with { AreaM2 = double.Epsilon }));
        Assert.ThrowsAny<ArgumentException>(() => ThermalBridgeCalculator.Aggregate(input with { BaseUValueWm2K = double.PositiveInfinity }));
        Assert.ThrowsAny<ArgumentException>(() => ThermalBridgeCalculator.Aggregate(input with { ReferenceConvention = "" }));
        Assert.ThrowsAny<ArgumentException>(() => ThermalBridgeCalculator.Aggregate(input with
        {
            LinearBridges = [new("Bridge", 10, -1, "Reference")]
        }));
        Assert.ThrowsAny<ArgumentException>(() => ThermalBridgeCalculator.Aggregate(input with
        {
            PointBridges = [new("Bridge", 0, 1, "Reference")]
        }));
        Assert.ThrowsAny<ArgumentException>(() => ThermalBridgeCalculator.Aggregate(input with
        {
            PointBridges = [new("Bridge", 1, double.NaN, "Reference")]
        }));
    }
}
