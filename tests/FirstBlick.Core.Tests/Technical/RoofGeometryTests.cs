using FirstBlick.Core.Technical;
using Xunit;

namespace FirstBlick.Core.Tests.Technical;

public sealed class RoofGeometryTests
{
    private static PlanePoint[] Box(double x, double y, double width, double height)
        => [new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height)];
    private static PolygonRoofInput Plain() => new(Box(0, 0, 12, 10), [], "Synthetic independent measurements");

    [Fact]
    public void ThreeFourFiveRoofPlaneHasExactAreaAndLengths()
    {
        var result = RoofGeometry.Rectangle(new(12, 4, 3, "3-4-5 reference triangle"));
        Assert.Equal(5, result.SlopingLengthM);
        Assert.Equal(60, result.SurfaceAreaM2);
        Assert.Equal(48, result.ProjectedAreaM2);
        Assert.Equal(34, result.PerimeterM);
        Assert.Equal(36.86989764584402, result.PitchDegrees, 10);
        Assert.False(result.Provenance.IsNormative);
    }

    [Fact]
    public void FlatPlaneEqualsItsHorizontalProjection()
    {
        var result = RoofGeometry.Rectangle(new(12, 10, 0, "Flat reference"));
        Assert.Equal(120, result.SurfaceAreaM2);
        Assert.Equal(result.SurfaceAreaM2, result.ProjectedAreaM2);
        Assert.Equal(0, result.PitchDegrees);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.Epsilon)]
    public void InvalidRectangleDimensionsAreRejected(double run)
        => Assert.ThrowsAny<ArgumentException>(() => RoofGeometry.Rectangle(new(12, run, 3, "Reference")));

    [Fact]
    public void VerticalAndNegativeRiseAreUnsupported()
    {
        Assert.ThrowsAny<ArgumentException>(() => RoofGeometry.Rectangle(new(12, 1, 1000, "Reference")));
        Assert.ThrowsAny<ArgumentException>(() => RoofGeometry.Rectangle(new(12, 10, -1, "Reference")));
    }

    [Fact]
    public void GeometricOpeningsAreDeductedWithoutAContractualThreshold()
    {
        var result = RoofGeometry.Calculate(Plain() with { Openings = [new("Opening", Box(1, 1, 2, 3))] });
        Assert.Equal(120, result.GrossAreaM2);
        Assert.Equal(6, result.OpeningAreaM2);
        Assert.Equal(114, result.NetAreaM2);
        Assert.Equal(44, result.OuterPerimeterM);
        Assert.Equal(10, result.OpeningPerimeterM);
    }

    [Fact]
    public void OrientationTranslationAndClosedInputDoNotChangeQuantities()
    {
        var input = Plain();
        var a = RoofGeometry.Calculate(input);
        var reversed = RoofGeometry.Calculate(input with { Vertices = input.Vertices.Reverse().ToArray() });
        var translated = RoofGeometry.Calculate(input with
        {
            Vertices = input.Vertices.Select(p => new PlanePoint(p.X + 900000, p.Y - 900000)).ToArray()
        });
        var closed = RoofGeometry.Calculate(input with { Vertices = [.. input.Vertices, input.Vertices[0]] });
        Assert.Equal(a.NetAreaM2, reversed.NetAreaM2);
        Assert.Equal(a.OuterPerimeterM, reversed.OuterPerimeterM);
        Assert.Equal(a.NetAreaM2, translated.NetAreaM2);
        Assert.Equal(a.Provenance.InputSha256, closed.Provenance.InputSha256);
    }

    [Fact]
    public void ConcavePolygonHasCorrectArea()
    {
        var result = RoofGeometry.Calculate(Plain() with
        {
            Vertices = [new(0, 0), new(2, 0), new(2, 1), new(1, 1), new(1, 2), new(0, 2)]
        });
        Assert.Equal(3, result.NetAreaM2);
        Assert.Equal(8, result.OuterPerimeterM);
    }

    [Fact]
    public void SelfIntersectionRepeatedVertexAndCollinearDegeneracyAreRejected()
    {
        PlanePoint[][] bad =
        [
            [new(0, 0), new(4, 4), new(0, 4), new(4, 0)],
            [new(0, 0), new(4, 0), new(4, 4), new(4, 0), new(0, 4)],
            [new(0, 0), new(1, 0), new(2, 0)],
            [new(0, 0), new(4, 0), new(2, 0), new(2, 4), new(0, 4)],
            [new(0, 0), new(4, 0), new(double.NaN, 4)],
            [new(0, 0), new(4, 0), new(4, 0), new(0, 4)]
        ];
        foreach (var ring in bad)
            Assert.ThrowsAny<ArgumentException>(() => RoofGeometry.Calculate(Plain() with { Vertices = ring }));
    }

    [Fact]
    public void CollinearForwardBoundaryVerticesAreAllowed()
    {
        var result = RoofGeometry.Calculate(Plain() with
        {
            Vertices = [new(0, 0), new(6, 0), new(12, 0), new(12, 10), new(0, 10)]
        });
        Assert.Equal(120, result.NetAreaM2);
    }

    [Fact]
    public void OpeningsMustNotTouchCrossOverlapOrNest()
    {
        PolygonOpening[][] bad =
        [
            [new("Touches boundary", Box(0, 1, 1, 1))],
            [new("Crosses boundary", Box(11, 1, 2, 2))],
            [new("Outside", Box(20, 20, 1, 1))],
            [new("A", Box(1, 1, 3, 3)), new("B", Box(2, 2, 3, 3))],
            [new("A", Box(1, 1, 3, 3)), new("Nested", Box(2, 2, 1, 1))],
            [new("A", Box(1, 1, 2, 2)), new("Touches A", Box(3, 1, 2, 2))]
        ];
        foreach (var openings in bad)
            Assert.ThrowsAny<ArgumentException>(() => RoofGeometry.Calculate(Plain() with { Openings = openings }));
    }

    [Fact]
    public void DiagonalOpeningCannotCrossAConcaveOuterBoundary()
    {
        var input = Plain() with
        {
            Vertices = [new(0, 0), new(10, 0), new(10, 2), new(2, 2), new(2, 10), new(0, 10)],
            Openings = [new("Crossing notch", [new(1, 1), new(9, 1), new(1, 9)])]
        };
        Assert.ThrowsAny<ArgumentException>(() => RoofGeometry.Calculate(input));
    }

    [Fact]
    public void MultipleDisjointOpeningsAndInputSnapshotAreSupported()
    {
        var vertices = Box(0, 0, 12, 10);
        var result = RoofGeometry.Calculate(Plain() with
        {
            Vertices = vertices, Openings = [new("A", Box(1, 1, 1, 1)), new("B", Box(5, 5, 2, 2))]
        });
        Assert.Equal(115, result.NetAreaM2);
        vertices[0] = new(2, 2);
        Assert.Equal(new PlanePoint(0, 0), result.Input.Vertices[0]);
    }

    [Fact]
    public void AggregateVertexBudgetBoundsIntersectionWork()
    {
        var many = Enumerable.Range(0, 1000).Select(i => new PlanePoint(i, i % 2)).ToArray();
        Assert.ThrowsAny<ArgumentException>(() => RoofGeometry.Calculate(Plain() with
        {
            Openings = [new("A", many), new("B", many)]
        }));
    }
}
