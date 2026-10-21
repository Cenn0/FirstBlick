using System.Text.Json.Serialization;
using FirstBlick.Core.Technical;

namespace FirstBlick.Core.Business;

public enum RoofRevisionStatus { Draft, Published }
public enum RoofDetailKind { Ridge, Eave, Verge, Valley, Hip, Penetration, Drain, Other }

public sealed record RoofSurface
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public PolygonRoofInput Geometry { get; set; } = null!;
}

/// <summary>Descriptive roof details. They do not add or deduct area or imply drainage/load calculations.</summary>
public sealed record RoofDetail
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? SurfaceId { get; set; }
    public RoofDetailKind Kind { get; set; }
    public string Name { get; set; } = "";
    public decimal? LengthM { get; set; }
    public int Count { get; set; } = 1;
    public string Notes { get; set; } = "";
}

public sealed record RoofSurfaceSnapshot(Guid Id, string Name, PolygonRoofResult Geometry);
public sealed record RoofDetailSnapshot(Guid Id, Guid? SurfaceId, RoofDetailKind Kind, string Name, decimal? LengthM, int Count, string Notes);
public sealed record RoofSnapshot(
    Guid RoofRevisionId, Guid TenantId, Guid BuildingId, Guid? ProjectId, Guid? PreviousRevisionId, int Version,
    string Title, string RevisionReason, DateTimeOffset PublishedAt,
    IReadOnlyList<RoofSurfaceSnapshot> Surfaces, IReadOnlyList<RoofDetailSnapshot> Details,
    double GrossAreaM2, double OpeningAreaM2, double NetAreaM2, string MethodVersion, string Digest, bool IsNormative);

public sealed class RoofRevision : TenantEntity
{
    public Guid BuildingId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? PreviousRevisionId { get; set; }
    public int Version { get; set; } = 1;
    public string Title { get; set; } = "";
    public string RevisionReason { get; set; } = "";
    public RoofRevisionStatus Status { get; set; }
    public List<RoofSurface> Surfaces { get; set; } = [];
    public List<RoofDetail> Details { get; set; } = [];
    public DateTimeOffset? PublishedAt { get; set; }
    public RoofSnapshot? Snapshot { get; set; }

    public override void Validate()
    {
        var surfaces = ValidateInputs();
        if (Status == RoofRevisionStatus.Draft)
        {
            Guard.Require(PublishedAt is null && Snapshot is null, "invalid_roof_state", "A draft roof cannot contain published evidence.");
            return;
        }
        Guard.Require(PublishedAt.HasValue && Snapshot is not null && Surfaces.Count > 0, "missing_roof_snapshot", "A published roof requires its date, surfaces and frozen snapshot.");
        Guard.Utc(PublishedAt.Value, nameof(PublishedAt));
        Guard.Require(PublishedAt.Value >= CreatedAt, "invalid_time", "A roof cannot be published before its creation.");
        var expected = RoofWorkflow.BuildSnapshot(this, surfaces, PublishedAt.Value);
        Guard.Require(ContentDigest.Of(expected) == ContentDigest.Of(Snapshot), "roof_snapshot_changed", "Published roof geometry and details are immutable; create a new draft revision.");
    }

    internal IReadOnlyList<RoofSurfaceSnapshot> ValidateInputs()
    {
        ValidateCommon(); Guard.Id(BuildingId, nameof(BuildingId)); Guard.OptionalId(ProjectId, nameof(ProjectId));
        Guard.OptionalId(PreviousRevisionId, nameof(PreviousRevisionId));
        Guard.Require(PreviousRevisionId != Id && Version is >= 1 and <= 1_000_000 && (Version == 1) == !PreviousRevisionId.HasValue,
            "invalid_roof_version", "The first roof version has no predecessor; later versions require another published revision.");
        Guard.Text(Title, nameof(Title)); Guard.Text(RevisionReason, nameof(RevisionReason), 2000, Version > 1);
        Guard.Require(Enum.IsDefined(Status), "invalid_roof_state", "Roof revision status is invalid.");
        Guard.Items(Surfaces, nameof(Surfaces), 100); Guard.Items(Details, nameof(Details), 200);
        Guard.Require(Surfaces.Select(x => x.Id).Distinct().Count() == Surfaces.Count, "duplicate_roof_surface", "Roof surface IDs must be unique within a revision.");
        Guard.Require(Details.Select(x => x.Id).Distinct().Count() == Details.Count, "duplicate_roof_detail", "Roof detail IDs must be unique within a revision.");
        var vertexCount = Surfaces.Sum(x => (long)(x.Geometry?.Vertices?.Count ?? 0) + (x.Geometry?.Openings?.Sum(y => (long)(y?.Vertices?.Count ?? 0)) ?? 0));
        Guard.Require(vertexCount <= 4000, "roof_geometry_budget", "A roof revision supports at most 4,000 vertices across all surfaces and openings.");
        var results = new List<RoofSurfaceSnapshot>();
        foreach (var surface in Surfaces)
        {
            Guard.Id(surface.Id, "Surface Id"); Guard.Text(surface.Name, "Surface name");
            Guard.Require(surface.Geometry is not null, "missing_geometry", "Every surface requires explicit geometry on its actual plane.");
            results.Add(new(surface.Id, surface.Name, RoofWorkflow.CalculateGeometry(surface.Geometry)));
        }
        var ids = Surfaces.Select(x => x.Id).ToHashSet();
        foreach (var detail in Details)
        {
            Guard.Id(detail.Id, "Detail Id"); Guard.OptionalId(detail.SurfaceId, "Detail surface");
            Guard.Require(!detail.SurfaceId.HasValue || ids.Contains(detail.SurfaceId.Value), "unknown_roof_surface", "A detail references an unknown surface.");
            Guard.Require(Enum.IsDefined(detail.Kind) && detail.Count is >= 1 and <= 10_000, "invalid_roof_detail", "Detail kind or count is invalid.");
            Guard.Text(detail.Name, "Detail name"); Guard.Text(detail.Notes, "Detail notes", 1000, false);
            var linear = detail.Kind is RoofDetailKind.Ridge or RoofDetailKind.Eave or RoofDetailKind.Verge or RoofDetailKind.Valley or RoofDetailKind.Hip;
            Guard.Require(linear == detail.LengthM.HasValue, "invalid_roof_detail", "Linear details require an explicit length; point details have a count only.");
            if (detail.LengthM is { } length) Guard.Range(length, 0.000001m, 100_000m, "Detail length");
        }
        return results.AsReadOnly();
    }

    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Building>(BuildingId);
        if (ProjectId is { } project) yield return BusinessRegistry.Reference<Project>(project);
        if (PreviousRevisionId is { } previous) yield return BusinessRegistry.Reference<RoofRevision>(previous);
    }
}

public static class RoofWorkflow
{
    public const string MethodVersion = "1.0.0";

    public static void Publish(RoofRevision roof, RoofRevision? previous, DateTimeOffset now)
    {
        roof.Validate(); Guard.Utc(now, nameof(now));
        Guard.Require(roof.Status == RoofRevisionStatus.Draft && !roof.Archived && roof.Surfaces.Count > 0, "invalid_transition", "Only a non-empty active draft roof can be published.");
        Guard.Require(now >= roof.CreatedAt, "invalid_time", "Publication cannot precede creation.");
        if (roof.PreviousRevisionId is { } previousId)
        {
            Guard.Require(previous is not null, "missing_predecessor", "The previous published roof revision must be provided.");
            previous.Validate();
            Guard.Require(previous.Id == previousId && previous.TenantId == roof.TenantId && previous.BuildingId == roof.BuildingId
                          && previous.Status == RoofRevisionStatus.Published && roof.Version == previous.Version + 1,
                "wrong_predecessor", "The predecessor must be the preceding published version of this tenant's building roof.");
            Guard.Require(now >= previous.PublishedAt, "invalid_time", "A new revision cannot precede its predecessor's publication.");
        }
        else Guard.Require(previous is null, "wrong_predecessor", "A first roof version cannot use a predecessor.");
        var snapshot = BuildSnapshot(roof, roof.ValidateInputs(), now);
        roof.Surfaces = snapshot.Surfaces.Select(CloneSurface).ToList();
        roof.Details = snapshot.Details.Select(CloneDetail).ToList();
        roof.Status = RoofRevisionStatus.Published; roof.PublishedAt = now; roof.UpdatedAt = now; roof.Snapshot = snapshot;
    }

    public static RoofRevision CreateRevision(RoofRevision source, string reason, DateTimeOffset now)
    {
        source.Validate(); Guard.Utc(now, nameof(now)); Guard.Text(reason, nameof(reason), 2000);
        Guard.Require(source.Status == RoofRevisionStatus.Published && source.Version < 1_000_000, "invalid_transition", "A new revision requires a published source within the version budget.");
        Guard.Require(now >= source.PublishedAt, "invalid_time", "A revision cannot precede its published source.");
        return new()
        {
            TenantId = source.TenantId, BuildingId = source.BuildingId, ProjectId = source.ProjectId,
            PreviousRevisionId = source.Id, Version = source.Version + 1, Title = source.Title, RevisionReason = reason,
            CreatedAt = now, UpdatedAt = now, Surfaces = source.Snapshot!.Surfaces.Select(CloneSurface).ToList(),
            Details = source.Snapshot.Details.Select(CloneDetail).ToList()
        };
    }

    internal static RoofSnapshot BuildSnapshot(RoofRevision roof, IReadOnlyList<RoofSurfaceSnapshot> surfaces, DateTimeOffset publishedAt)
    {
        var details = Array.AsReadOnly(roof.Details.Select(x => new RoofDetailSnapshot(x.Id, x.SurfaceId, x.Kind, x.Name, x.LengthM, x.Count, x.Notes)).ToArray());
        var snapshot = new RoofSnapshot(roof.Id, roof.TenantId, roof.BuildingId, roof.ProjectId, roof.PreviousRevisionId, roof.Version,
            roof.Title, roof.RevisionReason, publishedAt, surfaces, details,
            surfaces.Sum(x => x.Geometry.GrossAreaM2), surfaces.Sum(x => x.Geometry.OpeningAreaM2), surfaces.Sum(x => x.Geometry.NetAreaM2),
            MethodVersion, "", false);
        return snapshot with { Digest = ContentDigest.Of(snapshot) };
    }

    internal static PolygonRoofResult CalculateGeometry(PolygonRoofInput input)
    {
        try { return RoofGeometry.Calculate(input); }
        catch (ArgumentException failure) { throw new DomainException("invalid_roof_geometry", failure.Message); }
    }

    private static RoofSurface CloneSurface(RoofSurfaceSnapshot source)
        => new() { Id = source.Id, Name = source.Name, Geometry = CalculateGeometry(source.Geometry.Input).Input };
    private static RoofDetail CloneDetail(RoofDetailSnapshot source)
        => new() { Id = source.Id, SurfaceId = source.SurfaceId, Kind = source.Kind, Name = source.Name, LengthM = source.LengthM, Count = source.Count, Notes = source.Notes };
}

public enum MeasurementStatus { Draft, Frozen }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RectangleArea), "rectangleArea")]
[JsonDerivedType(typeof(TriangleArea), "triangleArea")]
[JsonDerivedType(typeof(Length), "length")]
[JsonDerivedType(typeof(Count), "count")]
[JsonDerivedType(typeof(Volume), "volume")]
[JsonDerivedType(typeof(SurfaceNetArea), "surfaceNetArea")]
public abstract record MeasurementInput;
public sealed record RectangleArea(decimal LengthM, decimal WidthM) : MeasurementInput;
public sealed record TriangleArea(decimal BaseM, decimal HeightM) : MeasurementInput;
public sealed record Length(decimal LengthM) : MeasurementInput;
public sealed record Count(int Quantity) : MeasurementInput;
public sealed record Volume(decimal LengthM, decimal WidthM, decimal HeightM) : MeasurementInput;
public sealed record SurfaceNetArea(Guid SurfaceId) : MeasurementInput;
public sealed record MeasurementDeduction(string Reason, decimal Quantity);

public sealed record MeasurementLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Description { get; set; } = "";
    public string MeasurementSource { get; set; } = "";
    public MeasurementInput Input { get; set; } = null!;
    public int Repetitions { get; set; } = 1;
    public List<MeasurementDeduction> Deductions { get; set; } = [];
}

public sealed record MeasurementLineSnapshot(
    Guid Id, string Description, string MeasurementSource, MeasurementInput Input, int Repetitions,
    IReadOnlyList<MeasurementDeduction> Deductions, decimal GrossQuantity, decimal DeductionQuantity, decimal Quantity,
    string Unit, Guid? SourceSurfaceId, string? SourceGeometryDigest);
public sealed record MeasurementTotal(string Unit, decimal Quantity);
public sealed record MeasurementSnapshot(
    Guid MeasurementId, Guid TenantId, Guid ProjectId, Guid RoofRevisionId, int RoofVersion, string RoofDigest, string Title,
    IReadOnlyList<MeasurementLineSnapshot> Lines, IReadOnlyList<RoofSurfaceSnapshot> SurfaceEvidence,
    IReadOnlyList<MeasurementTotal> Totals, string MethodVersion, string Digest, bool IsNormative);

public sealed class Measurement : TenantEntity
{
    public Guid ProjectId { get; set; }
    public Guid RoofRevisionId { get; set; }
    public string Title { get; set; } = "";
    public MeasurementStatus Status { get; set; }
    public List<MeasurementLine> Lines { get; set; } = [];
    public DateTimeOffset? FrozenAt { get; set; }
    public MeasurementSnapshot? Snapshot { get; set; }

    public override void Validate()
    {
        ValidateInputs();
        if (Status == MeasurementStatus.Draft)
        {
            Guard.Require(FrozenAt is null && Snapshot is null, "invalid_measurement_state", "A draft measurement cannot contain frozen evidence.");
            return;
        }
        Guard.Require(FrozenAt.HasValue && Snapshot is not null && Lines.Count > 0, "missing_measurement_snapshot", "A frozen measurement requires a date, lines and its snapshot.");
        Guard.Utc(FrozenAt.Value, nameof(FrozenAt));
        Guard.Require(FrozenAt.Value >= CreatedAt, "invalid_time", "A measurement cannot be frozen before its creation.");
        var expected = MeasurementWorkflow.BuildSnapshot(this, Snapshot.RoofVersion, Snapshot.RoofDigest, Snapshot.SurfaceEvidence);
        Guard.Require(ContentDigest.Of(expected) == ContentDigest.Of(Snapshot), "measurement_snapshot_changed", "Frozen measurement inputs, deductions and quantities are immutable.");
    }

    internal void ValidateInputs()
    {
        ValidateCommon(); Guard.Id(ProjectId, nameof(ProjectId)); Guard.Id(RoofRevisionId, nameof(RoofRevisionId)); Guard.Text(Title, nameof(Title));
        Guard.Require(Enum.IsDefined(Status), "invalid_measurement_state", "Measurement status is invalid.");
        Guard.Items(Lines, nameof(Lines), 300);
        Guard.Require(Lines.Select(x => x.Id).Distinct().Count() == Lines.Count, "duplicate_measurement_line", "Measurement line IDs must be unique.");
        Guard.Require(Lines.Sum(x => (long)(x.Deductions?.Count ?? 0)) <= 1000, "measurement_budget", "A measurement supports at most 1,000 explicitly listed deductions.");
        foreach (var line in Lines)
        {
            Guard.Id(line.Id, "Line Id"); Guard.Text(line.Description, "Line description"); Guard.Text(line.MeasurementSource, "Measurement source", 500);
            Guard.Require(line.Repetitions is >= 1 and <= 10_000, "invalid_repetitions", "Repetitions must be an integer from 1 to 10,000.");
            Guard.Require(line.Input is not null, "missing_measurement_input", "Every measurement line requires a supported typed input.");
            MeasurementWorkflow.ValidateInput(line.Input);
            Guard.Items(line.Deductions, "Deductions", 100);
            foreach (var deduction in line.Deductions)
            {
                Guard.Text(deduction.Reason, "Deduction reason"); Guard.Range(deduction.Quantity, 0.000001m, MeasurementWorkflow.MaximumQuantity, "Deduction quantity");
                if (line.Input is Count) Guard.Require(decimal.Truncate(deduction.Quantity) == deduction.Quantity, "fractional_count", "Count deductions must be whole pieces.");
            }
        }
    }

    public override IEnumerable<EntityReference> References()
        => [BusinessRegistry.Reference<Project>(ProjectId), BusinessRegistry.Reference<RoofRevision>(RoofRevisionId)];
}

public static class MeasurementWorkflow
{
    public const string MethodVersion = "1.0.0";
    public const decimal MaximumQuantity = 1_000_000_000_000m;

    public static MeasurementSnapshot Preview(Measurement measurement, RoofRevision roof)
    {
        measurement.Validate(); roof.Validate();
        Guard.Require(!measurement.Archived && !roof.Archived && roof.Status == RoofRevisionStatus.Published,
            "roof_not_published", "Measurement requires an active published roof revision.");
        Guard.Require(roof.TenantId == measurement.TenantId && roof.Id == measurement.RoofRevisionId
                      && (!roof.ProjectId.HasValue || roof.ProjectId == measurement.ProjectId),
            "wrong_measurement_roof", "The roof revision must belong to this tenant and measurement project.");
        Guard.Require(measurement.Lines.Count > 0, "empty_measurement", "At least one measurement line is required.");
        var required = measurement.Lines.Select(x => x.Input).OfType<SurfaceNetArea>().Select(x => x.SurfaceId).ToHashSet();
        var evidence = roof.Snapshot!.Surfaces.Where(x => required.Contains(x.Id)).ToArray();
        return BuildSnapshot(measurement, roof.Version, roof.Snapshot.Digest, evidence);
    }

    public static void Freeze(Measurement measurement, RoofRevision roof, DateTimeOffset now)
    {
        Guard.Utc(now, nameof(now));
        Guard.Require(measurement.Status == MeasurementStatus.Draft, "invalid_transition", "Only a draft measurement can be frozen.");
        var snapshot = Preview(measurement, roof);
        Guard.Require(now >= measurement.CreatedAt && now >= roof.PublishedAt, "invalid_time", "Freezing cannot precede the measurement or published roof.");
        measurement.Lines = snapshot.Lines.Select(x => new MeasurementLine
        {
            Id = x.Id, Description = x.Description, MeasurementSource = x.MeasurementSource, Input = x.Input,
            Repetitions = x.Repetitions, Deductions = x.Deductions.ToList()
        }).ToList();
        measurement.Status = MeasurementStatus.Frozen; measurement.FrozenAt = now; measurement.UpdatedAt = now; measurement.Snapshot = snapshot;
    }

    internal static void ValidateInput(MeasurementInput input)
    {
        switch (input)
        {
            case RectangleArea rectangle: Dimension(rectangle.LengthM, "Length"); Dimension(rectangle.WidthM, "Width"); break;
            case TriangleArea triangle: Dimension(triangle.BaseM, "Base"); Dimension(triangle.HeightM, "Perpendicular height"); break;
            case Length length: Dimension(length.LengthM, "Length"); break;
            case Count count: Guard.Require(count.Quantity is >= 1 and <= 1_000_000, "invalid_count", "Count must be an integer from 1 to 1,000,000."); break;
            case Volume volume: Dimension(volume.LengthM, "Length"); Dimension(volume.WidthM, "Width"); Dimension(volume.HeightM, "Height"); break;
            case SurfaceNetArea surface: Guard.Id(surface.SurfaceId, "Surface Id"); break;
            default: throw new DomainException("unsupported_measurement", "This measurement input type is unsupported.");
        }
    }

    internal static MeasurementSnapshot BuildSnapshot(Measurement measurement, int roofVersion, string roofDigest, IReadOnlyList<RoofSurfaceSnapshot> suppliedEvidence)
    {
        Guard.Require(roofVersion is >= 1 and <= 1_000_000 && IsDigest(roofDigest), "invalid_roof_evidence", "A published roof version and digest are required.");
        Guard.Items(suppliedEvidence, "Surface evidence", 100);
        var required = measurement.Lines.Select(x => x.Input).OfType<SurfaceNetArea>().Select(x => x.SurfaceId).ToHashSet();
        Guard.Require(suppliedEvidence.Select(x => x.Id).Distinct().Count() == suppliedEvidence.Count
                      && required.SetEquals(suppliedEvidence.Select(x => x.Id)),
            "unknown_measurement_surface", "Each referenced surface must be present exactly once in the published roof evidence.");
        var vertices = suppliedEvidence.Sum(x => (long)(x.Geometry?.Input?.Vertices?.Count ?? 0) + (x.Geometry?.Input?.Openings?.Sum(y => (long)(y?.Vertices?.Count ?? 0)) ?? 0));
        Guard.Require(vertices <= 4000, "roof_geometry_budget", "Measurement surface evidence exceeds the roof geometry budget.");
        var surfaces = suppliedEvidence.OrderBy(x => x.Id).Select(surface =>
        {
            Guard.Id(surface.Id, "Surface Id"); Guard.Text(surface.Name, "Surface name");
            Guard.Require(surface.Geometry?.Input is not null, "missing_geometry", "Frozen surface geometry is required.");
            var verified = RoofWorkflow.CalculateGeometry(surface.Geometry.Input);
            Guard.Require(ContentDigest.Of(verified) == ContentDigest.Of(surface.Geometry), "surface_evidence_changed", "Frozen surface results do not match their original input.");
            return new RoofSurfaceSnapshot(surface.Id, surface.Name, verified);
        }).ToArray();
        var byId = surfaces.ToDictionary(x => x.Id);
        var lines = measurement.Lines.Select(line => CalculateLine(line, byId)).ToArray();
        var totals = lines.GroupBy(x => x.Unit).OrderBy(x => x.Key, StringComparer.Ordinal).Select(group =>
        {
            var quantity = group.Sum(x => x.Quantity); Guard.Range(quantity, 0, MaximumQuantity, "Measurement unit total");
            return new MeasurementTotal(group.Key, quantity);
        }).ToArray();
        var snapshot = new MeasurementSnapshot(measurement.Id, measurement.TenantId, measurement.ProjectId, measurement.RoofRevisionId,
            roofVersion, roofDigest, measurement.Title, Array.AsReadOnly(lines), Array.AsReadOnly(surfaces), Array.AsReadOnly(totals), MethodVersion, "", false);
        return snapshot with { Digest = ContentDigest.Of(snapshot) };
    }

    private static MeasurementLineSnapshot CalculateLine(MeasurementLine line, IReadOnlyDictionary<Guid, RoofSurfaceSnapshot> surfaces)
    {
        var (raw, unit) = line.Input switch
        {
            RectangleArea rectangle => (rectangle.LengthM * rectangle.WidthM, "m²"),
            TriangleArea triangle => (triangle.BaseM * triangle.HeightM / 2m, "m²"),
            Length length => (length.LengthM, "m"),
            Count count => ((decimal)count.Quantity, "Stk"),
            Volume volume => (volume.LengthM * volume.WidthM * volume.HeightM, "m³"),
            SurfaceNetArea surface => ((decimal)surfaces[surface.SurfaceId].Geometry.NetAreaM2, "m²"),
            _ => throw new DomainException("unsupported_measurement", "This measurement input type is unsupported.")
        };
        var gross = decimal.Round(raw * line.Repetitions, 6, MidpointRounding.AwayFromZero);
        Guard.Range(gross, 0.000001m, MaximumQuantity, "Gross quantity");
        var deducted = line.Deductions.Sum(x => x.Quantity);
        Guard.Require(deducted <= gross, "excessive_deduction", "Explicit deductions cannot exceed a line's gross quantity.");
        var quantity = gross - deducted;
        Guid? surfaceId = line.Input is SurfaceNetArea referenced ? referenced.SurfaceId : null;
        MeasurementInput input = line.Input switch
        {
            RectangleArea x => x with { }, TriangleArea x => x with { }, Length x => x with { }, Count x => x with { },
            Volume x => x with { }, SurfaceNetArea x => x with { },
            _ => throw new DomainException("unsupported_measurement", "This measurement input type is unsupported.")
        };
        return new(line.Id, line.Description, line.MeasurementSource, input, line.Repetitions,
            Array.AsReadOnly(line.Deductions.Select(x => x with { }).ToArray()), gross, deducted, quantity, unit,
            surfaceId, surfaceId.HasValue ? surfaces[surfaceId.Value].Geometry.Provenance.InputSha256 : null);
    }

    private static void Dimension(decimal value, string name) => Guard.Range(value, 0.000001m, 100_000m, name);
    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');
}
