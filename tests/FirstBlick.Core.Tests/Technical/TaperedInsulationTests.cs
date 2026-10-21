using FirstBlick.Core.Technical;
using Xunit;

namespace FirstBlick.Core.Tests.Technical;

public sealed class TaperedInsulationTests
{
    private static TaperedInsulationInput Input()
        => new(12, 10, .08, 0, 2, "Synthetic plan and slope source");

    [Fact]
    public void Exact_affine_volume_and_four_corner_thicknesses_have_independent_reference()
    {
        var result = TaperedInsulationCalculator.Calculate(Input());
        Assert.Equal(120, result.ProjectedAreaM2, 10);
        Assert.Equal(.08, result.MinimumThicknessM, 10);
        Assert.Equal(.28, result.MaximumThicknessM, 10);
        Assert.Equal(.18, result.AverageThicknessM, 10);
        Assert.Equal(21.6, result.CalculatedVolumeM3, 10);
        Assert.Equal([.08, .08, .28, .28], result.Corners.Select(x => Math.Round(x.ThicknessM, 10)));
        Assert.False(result.Provenance.IsNormative);
        Assert.Equal("rectangular-linear-tapered-insulation", result.Provenance.MethodId);
    }

    [Fact]
    public void Two_directional_gradients_integrate_to_mean_of_extreme_corners()
    {
        var result = TaperedInsulationCalculator.Calculate(new(4, 6, .1, 1, 2, "Synthetic independent source"));
        Assert.Equal(.26, result.MaximumThicknessM, 10); // .10 + .04 + .12
        Assert.Equal(.18, result.AverageThicknessM, 10);
        Assert.Equal(4.32, result.CalculatedVolumeM3, 10); // 24 m2 x .18 m
        Assert.Equal(.14, result.Corners[1].ThicknessM, 10);
        Assert.Equal(.22, result.Corners[3].ThicknessM, 10);
    }

    [Fact]
    public void Zero_gradient_is_a_constant_thickness_volume()
    {
        var result = TaperedInsulationCalculator.Calculate(Input() with { WidthGradientPercent = 0 });
        Assert.Equal(9.6, result.CalculatedVolumeM3, 10);
        Assert.All(result.Corners, corner => Assert.Equal(.08, corner.ThicknessM, 10));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(1001)]
    public void Invalid_plan_lengths_are_rejected(double value)
        => Assert.ThrowsAny<ArgumentException>(() =>
            TaperedInsulationCalculator.Calculate(Input() with { LengthM = value }));

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Unsupported_or_invalid_gradients_are_rejected(double value)
        => Assert.ThrowsAny<ArgumentException>(() =>
            TaperedInsulationCalculator.Calculate(Input() with { WidthGradientPercent = value }));

    [Fact]
    public void Extreme_total_thickness_and_unattributed_inputs_are_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            TaperedInsulationCalculator.Calculate(Input() with { WidthM = 100, WidthGradientPercent = 10 }));
        Assert.ThrowsAny<ArgumentException>(() =>
            TaperedInsulationCalculator.Calculate(Input() with { MeasurementSource = "   " }));
    }

    [Fact]
    public void Provenance_reflects_normalized_sources_and_changes_in_thickness()
    {
        var initial = TaperedInsulationCalculator.Calculate(Input());
        var trim = TaperedInsulationCalculator.Calculate(Input() with { MeasurementSource = " Synthetic plan and slope source " });
        var changed = TaperedInsulationCalculator.Calculate(Input() with { MinimumThicknessM = .09 });
        Assert.Equal(initial.Provenance.InputSha256, trim.Provenance.InputSha256);
        Assert.NotEqual(initial.Provenance.InputSha256, changed.Provenance.InputSha256);
        Assert.Equal(64, initial.Provenance.InputSha256.Length);
    }
}
