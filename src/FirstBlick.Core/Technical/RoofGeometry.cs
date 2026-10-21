namespace FirstBlick.Core.Technical;

public readonly record struct PlanePoint(double X, double Y);
public sealed record PolygonOpening(string Name, IReadOnlyList<PlanePoint> Vertices);
/// <summary>All vertices are in a common local coordinate system on the actual roof plane, measured in metres.</summary>
public sealed record PolygonRoofInput(
    IReadOnlyList<PlanePoint> Vertices,
    IReadOnlyList<PolygonOpening> Openings,
    string MeasurementSource);
public sealed record PolygonRoofResult(
    PolygonRoofInput Input,
    double GrossAreaM2,
    double OpeningAreaM2,
    double NetAreaM2,
    double OuterPerimeterM,
    double OpeningPerimeterM,
    CalculationProvenance Provenance);
/// <summary>A single roof plane: length parallel to the ridge, horizontal run perpendicular to it, and non-negative rise.</summary>
public sealed record RectangularRoofInput(double RidgeLengthM, double HorizontalRunM, double RiseM, string MeasurementSource);
public sealed record RectangularRoofResult(
    RectangularRoofInput Input,
    double SlopingLengthM,
    double PitchDegrees,
    double ProjectedAreaM2,
    double SurfaceAreaM2,
    double PerimeterM,
    CalculationProvenance Provenance);

public static class RoofGeometry
{
    private const double DistanceToleranceM = 1e-9;

    public static RectangularRoofResult Rectangle(RectangularRoofInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var length = TechnicalInput.Range(input.RidgeLengthM, nameof(input.RidgeLengthM), 1e-6, 100000);
        var run = TechnicalInput.Range(input.HorizontalRunM, nameof(input.HorizontalRunM), 1e-6, 100000);
        var rise = TechnicalInput.Range(input.RiseM, nameof(input.RiseM), 0, 100000);
        var source = TechnicalInput.Label(input.MeasurementSource, nameof(input.MeasurementSource), 500);
        var pitch = Math.Atan2(rise, run) * 180 / Math.PI;
        if (pitch >= 89) throw new ArgumentException("This roof-plane model requires a pitch below 89 degrees.", nameof(input));
        var slopeLength = Math.Sqrt(run * run + rise * rise);
        var normalized = input with { MeasurementSource = source };
        return new(normalized, slopeLength, pitch, length * run, length * slopeLength, 2 * (length + slopeLength),
            CalculationEvidence.Create("rectangular-roof-plane", normalized,
                "Pure geometry for one rectangular roof plane. A two-sided roof requires two planes.",
                "No overhangs, openings, waste allowances, load verification or contractual measurement rules are inferred."));
    }

    public static PolygonRoofResult Calculate(PolygonRoofInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var source = TechnicalInput.Label(input.MeasurementSource, nameof(input.MeasurementSource), 500);
        TechnicalInput.Count(input.Openings, nameof(input.Openings), 0, 100);
        var vertexBudget = (input.Vertices?.Count ?? 0) + input.Openings.Sum(opening => opening?.Vertices?.Count ?? 0);
        if (vertexBudget > 2000)
            throw new ArgumentException("At most 2000 vertices are accepted across the outer ring and all openings.", nameof(input));
        var outer = ValidateRing(input.Vertices, "vertices");
        var openings = input.Openings.Select((opening, index) =>
        {
            if (opening is null) throw new ArgumentException($"Opening {index + 1} is missing.", nameof(input));
            return new PolygonOpening(TechnicalInput.Label(opening.Name, $"openings[{index}].name"),
                Array.AsReadOnly(ValidateRing(opening.Vertices, $"openings[{index}].vertices")));
        }).ToArray();
        foreach (var opening in openings)
        {
            if (opening.Vertices.Any(point => !StrictlyInside(point, outer)) || RingsIntersect(outer, opening.Vertices))
                throw new ArgumentException("Every opening must lie strictly inside the outer roof boundary without touching it.", nameof(input));
        }
        for (var i = 0; i < openings.Length; i++)
        for (var j = i + 1; j < openings.Length; j++)
        {
            var a = openings[i].Vertices;
            var b = openings[j].Vertices;
            if (RingsIntersect(a, b) || StrictlyInside(a[0], b) || StrictlyInside(b[0], a))
                throw new ArgumentException("Openings must not overlap, nest or touch.", nameof(input));
        }
        var gross = Area(outer);
        var holes = openings.Sum(opening => Area(opening.Vertices));
        if (gross - holes < 1e-8)
            throw new ArgumentException("The net roof area must be at least 1e-8 square metres.", nameof(input));
        var normalized = input with
        {
            Vertices = Array.AsReadOnly(outer), Openings = Array.AsReadOnly(openings), MeasurementSource = source
        };
        return new(normalized, gross, holes, gross - holes, Perimeter(outer),
            openings.Sum(opening => Perimeter(opening.Vertices)),
            CalculationEvidence.Create("simple-roof-plane-polygon", normalized,
                "Vertices are local coordinates on the actual plane, not a horizontal projection or geographic coordinates.",
                "Simple outer ring and disjoint contained openings only; touching, nested and self-intersecting rings are rejected.",
                "Quantities are geometric areas and lengths. No contractual deduction, rounding, pitch conversion or waste rule is applied."));
    }

    private static PlanePoint[] ValidateRing(IReadOnlyList<PlanePoint>? values, string field)
    {
        TechnicalInput.Count(values, field, 3, 1000);
        var vertices = values!.ToArray();
        foreach (var point in vertices)
        {
            TechnicalInput.Range(point.X, $"{field}.x", -1e6, 1e6);
            TechnicalInput.Range(point.Y, $"{field}.y", -1e6, 1e6);
        }
        if (vertices.Length > 3 && vertices[0] == vertices[^1]) vertices = vertices[..^1];
        if (vertices.Length < 3) throw new ArgumentException("A ring needs at least three distinct vertices.", field);
        for (var i = 0; i < vertices.Length; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Length];
            var c = vertices[(i + 2) % vertices.Length];
            if (Distance(a, b) < 1e-6) throw new ArgumentException("Edges must be at least 1e-6 metres long.", field);
            if (Orientation(a, b, c) == 0 && (b.X - a.X) * (c.X - b.X) + (b.Y - a.Y) * (c.Y - b.Y) < 0)
                throw new ArgumentException("Adjacent edges must not double back over each other.", field);
            for (var j = i + 1; j < vertices.Length; j++)
            {
                if (j == i + 1 || i == 0 && j == vertices.Length - 1) continue;
                if (SegmentsIntersect(a, b, vertices[j], vertices[(j + 1) % vertices.Length]))
                    throw new ArgumentException("The ring must not intersect or touch itself.", field);
            }
        }
        if (Area(vertices) < 1e-8) throw new ArgumentException("A ring area must be at least 1e-8 square metres.", field);
        return vertices;
    }

    private static int Orientation(PlanePoint a, PlanePoint b, PlanePoint c)
    {
        var value = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        var tolerance = DistanceToleranceM * Math.Max(1, Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y));
        return Math.Abs(value) <= tolerance ? 0 : value > 0 ? 1 : -1;
    }

    private static bool OnSegment(PlanePoint a, PlanePoint b, PlanePoint point)
        => Orientation(a, b, point) == 0
           && point.X >= Math.Min(a.X, b.X) - DistanceToleranceM && point.X <= Math.Max(a.X, b.X) + DistanceToleranceM
           && point.Y >= Math.Min(a.Y, b.Y) - DistanceToleranceM && point.Y <= Math.Max(a.Y, b.Y) + DistanceToleranceM;

    private static bool SegmentsIntersect(PlanePoint a, PlanePoint b, PlanePoint c, PlanePoint d)
    {
        var abC = Orientation(a, b, c);
        var abD = Orientation(a, b, d);
        var cdA = Orientation(c, d, a);
        var cdB = Orientation(c, d, b);
        if (abC * abD < 0 && cdA * cdB < 0) return true;
        return abC == 0 && OnSegment(a, b, c) || abD == 0 && OnSegment(a, b, d)
               || cdA == 0 && OnSegment(c, d, a) || cdB == 0 && OnSegment(c, d, b);
    }

    private static bool RingsIntersect(IReadOnlyList<PlanePoint> a, IReadOnlyList<PlanePoint> b)
    {
        for (var i = 0; i < a.Count; i++)
        for (var j = 0; j < b.Count; j++)
            if (SegmentsIntersect(a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count])) return true;
        return false;
    }

    private static bool StrictlyInside(PlanePoint point, IReadOnlyList<PlanePoint> ring)
    {
        var inside = false;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            if (OnSegment(a, b, point)) return false;
            if ((a.Y > point.Y) != (b.Y > point.Y)
                && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static double Area(IReadOnlyList<PlanePoint> vertices)
    {
        // Translation before the cross products avoids cancellation for coordinates far from the local origin.
        var origin = vertices[0];
        var twiceArea = 0d;
        for (var i = 1; i < vertices.Count - 1; i++)
            twiceArea += (vertices[i].X - origin.X) * (vertices[i + 1].Y - origin.Y)
                         - (vertices[i].Y - origin.Y) * (vertices[i + 1].X - origin.X);
        return Math.Abs(twiceArea) / 2;
    }

    private static double Perimeter(IReadOnlyList<PlanePoint> vertices)
        => Enumerable.Range(0, vertices.Count).Sum(i => Distance(vertices[i], vertices[(i + 1) % vertices.Count]));

    private static double Distance(PlanePoint a, PlanePoint b)
        => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
}
