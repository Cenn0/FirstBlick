using FirstBlick.Core.Technical;
using Xunit;

namespace FirstBlick.Core.Tests.Technical;

public sealed class MonthlyMoistureScreeningTests
{
    // Synthetic, deliberately vapour-tight exterior layer: a cold inner interface can
    // exceed saturation while the outside boundary remains below saturation.
    // This is a screening regression, not a construction or DIN/ISO reference case.
    private static MonthlyMoistureScreeningInput Sample()
        => new(.13, .04,
            [
                new("Synthetic insulation", .12, .04, 1, "Independent test source"),
                new("Synthetic vapour-tight exterior layer", .02, .2, 10_000, "Independent test source")
            ],
            Enumerable.Range(1, 12).Select(m => new MonthlyMoistureClimate(m, 20, m == 1 ? -10 : 20,
                m == 1 ? 90 : 50, 50)).ToArray());

    [Fact]
    public void Twelve_months_are_evaluated_independently_and_sorted()
    {
        var input = Sample();
        var result = MonthlyMoistureScreening.Calculate(input with
        {
            Months = input.Months.Reverse().ToArray()
        });
        Assert.Equal(12, result.Months.Count);
        Assert.Equal(Enumerable.Range(1, 12), result.Months.Select(x => x.Month));
        Assert.Equal([1], result.SupersaturatedMonths);
        Assert.Contains(result.Months.Single(x => x.Month == 1).Screening.Interfaces,
            point => point.Name == "After Synthetic insulation" && point.ExceedsSaturation
                     && point.VapourPressurePa > point.SaturationPressurePa);
        Assert.DoesNotContain(2, result.SupersaturatedMonths);
        Assert.False(result.Months.Single(x => x.Month == 2).Screening.AnyInterfaceSupersaturated);
        Assert.True(result.RequiresDetailedAssessment);
        Assert.False(result.Provenance.IsNormative);
        Assert.Equal(64, result.Provenance.InputSha256.Length);
    }

    [Fact]
    public void One_homogeneous_layer_with_unsaturated_outer_boundary_is_not_a_false_alarm()
    {
        var input = Sample() with
        {
            Layers = [new MoistureLayer("Synthetic homogeneous insulation", .12, .04, 10, "Independent test source")]
        };
        var result = MonthlyMoistureScreening.Calculate(input);
        Assert.Empty(result.SupersaturatedMonths);
        Assert.All(result.Months, m => Assert.False(m.Screening.AnyInterfaceSupersaturated));
        Assert.True(result.RequiresDetailedAssessment);
    }

    [Fact]
    public void MissingOrRepeatedMonthCannotAppearToProvideAnnualCoverage()
    {
        var data = Sample();
        Assert.ThrowsAny<ArgumentException>(() => MonthlyMoistureScreening.Calculate(data with { Months = data.Months.Take(11).ToArray() }));
        var duplicated = data.Months.ToArray(); duplicated[11] = duplicated[10];
        Assert.ThrowsAny<ArgumentException>(() => MonthlyMoistureScreening.Calculate(data with { Months = duplicated }));
    }

    [Fact]
    public void ScreeningsNeverImplicitlyProduceACondensateMassOrDryingConclusion()
    {
        var result = MonthlyMoistureScreening.Calculate(Sample());
        Assert.All(result.Months, month =>
        {
            Assert.True(month.Screening.RequiresDetailedAssessment);
            Assert.False(month.Screening.Provenance.IsNormative);
        });
        Assert.Contains(result.Provenance.Limitations, x => x.Contains("No month-to-month"));
    }
}
