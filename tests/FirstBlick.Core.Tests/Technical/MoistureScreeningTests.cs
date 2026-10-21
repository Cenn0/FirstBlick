using FirstBlick.Core.Technical;
using Xunit;

namespace FirstBlick.Core.Tests.Technical;

public sealed class MoistureScreeningTests
{
    private static MoistureScreeningInput Example() => new(20, 0, 60, 80, 0.13, 0.04,
        [new("Insulation", 0.1, 0.04, 10, "synthetic declared value"),
         new("Cladding", 0.02, 0.2, 100, "synthetic declared value")]);

    [Fact]
    public void SeparateHeatAndSdResistancesYieldIndependentBoundaryResults()
    {
        var report = MoistureScreening.Calculate(Example());
        Assert.Equal(2.77, report.TotalResistanceM2KW, 12);
        Assert.Equal(3, report.TotalSdM, 12);
        Assert.Equal(20 / 2.77, report.HeatFluxWm2, 11);
        var first = report.Interfaces[0];
        Assert.Equal(20 - 20 / 2.77 * (0.13 + 2.5), first.TemperatureC, 11);
        var pvInside = MoistureScreening.SaturationPressurePa(20) * .6;
        var pvOutside = MoistureScreening.SaturationPressurePa(0) * .8;
        Assert.Equal(pvInside + (pvOutside - pvInside) / 3, first.VapourPressurePa, 9);
        Assert.Equal(pvOutside, report.Interfaces[^1].VapourPressurePa, 9);
        Assert.True(report.RequiresDetailedAssessment);
        Assert.False(report.Provenance.IsNormative);
        Assert.Equal(64, report.Provenance.InputSha256.Length);
    }

    [Fact]
    public void EqualTemperaturesAndRelativeHumiditiesProduceUniformPressureAndNoFalsePositive()
    {
        var report = MoistureScreening.Calculate(Example() with
        {
            OutsideTemperatureC = 20, InsideRelativeHumidityPercent = 50, OutsideRelativeHumidityPercent = 50
        });
        Assert.Equal(0, report.HeatFluxWm2);
        Assert.Equal(50, report.InsideSurfaceRelativeHumidityPercent, 10);
        Assert.All(report.Interfaces, x =>
        {
            Assert.Equal(20, x.TemperatureC, 10);
            Assert.Equal(50, x.RelativeHumidityPercent, 10);
            Assert.False(x.ExceedsSaturation);
        });
        Assert.False(report.AnyInterfaceSupersaturated);
        Assert.True(report.RequiresDetailedAssessment); // no screening can certify a roof
    }

    [Fact]
    public void ColdSideCanBeSupersaturatedWithoutDeclaringAnyNormativeConclusion()
    {
        var result = MoistureScreening.Calculate(Example() with
        {
            InsideRelativeHumidityPercent = 90, OutsideRelativeHumidityPercent = 50
        });
        Assert.True(result.AnyInterfaceSupersaturated);
        Assert.Contains(result.Interfaces, point => point.ExceedsSaturation);
        Assert.False(result.Provenance.IsNormative);
    }

    [Fact]
    public void InputMutationCannotAlterRetainedEvidence()
    {
        var layers = Example().Layers.ToArray();
        var input = Example() with { Layers = layers };
        var snapshot = MoistureScreening.Calculate(input);
        layers[0] = layers[0] with { VapourResistanceFactor = 1000 };
        Assert.Equal(10, snapshot.Input.Layers[0].VapourResistanceFactor);
        Assert.NotEqual(snapshot.Provenance.InputSha256, MoistureScreening.Calculate(input).Provenance.InputSha256);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(101)]
    public void InvalidRhFailsClosed(double relativeHumidity)
        => Assert.ThrowsAny<ArgumentException>(() => MoistureScreening.Calculate(
            Example() with { InsideRelativeHumidityPercent = relativeHumidity }));

    [Fact]
    public void AbsentAndImplausibleMaterialsFailClosed()
    {
        Assert.ThrowsAny<ArgumentException>(() => MoistureScreening.Calculate(Example() with { Layers = [] }));
        Assert.ThrowsAny<ArgumentException>(() => MoistureScreening.Calculate(Example() with
        { Layers = [new("Bad", .1, .04, .99, "source")] }));
        Assert.ThrowsAny<ArgumentException>(() => MoistureScreening.Calculate(Example() with
        { Layers = [new("Bad", 0, .04, 1, "source")] }));
        Assert.ThrowsAny<ArgumentException>(() => MoistureScreening.Calculate(Example() with
        { Layers = [new("Bad", .1, .04, 1, "")] }));
        Assert.ThrowsAny<ArgumentException>(() => MoistureScreening.SaturationPressurePa(-80));
    }

    [Fact]
    public void WaterAndIceApproximationMeetAtZeroDegrees()
    {
        Assert.Equal(610.5, MoistureScreening.SaturationPressurePa(0));
        Assert.True(MoistureScreening.SaturationPressurePa(-1) < 610.5);
        Assert.True(MoistureScreening.SaturationPressurePa(1) > 610.5);
    }
}
