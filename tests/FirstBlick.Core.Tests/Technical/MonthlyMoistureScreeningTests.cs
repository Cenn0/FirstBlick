using FirstBlick.Core.Technical;
using Xunit;

namespace FirstBlick.Core.Tests.Technical;

public sealed class MonthlyMoistureScreeningTests
{
    private static MonthlyMoistureScreeningInput Sample()
        => new(.13, .04, [new("Synthetic insulation", .12, .04, 10, "Independent test source")],
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
        Assert.Contains(1, result.SupersaturatedMonths);
        Assert.DoesNotContain(2, result.SupersaturatedMonths);
        Assert.True(result.RequiresDetailedAssessment);
        Assert.False(result.Provenance.IsNormative);
        Assert.Equal(64, result.Provenance.InputSha256.Length);
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
