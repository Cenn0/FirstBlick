using System.Text.Json;
using System.Text.Json.Serialization;
using FirstBlick.Core.Business;
using FirstBlick.Core.Technical;

namespace FirstBlick.Core.Tests.Business;

public sealed class RoofMeasurementTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void Publishing_preserves_independent_plane_geometry_without_inferring_detail_deductions()
    {
        var roof = DraftRoof();
        roof.Surfaces.Add(new() { Name = "Triangle", Geometry = new([new(0, 0), new(10, 0), new(0, 3)], [], "Independent 10 m base and 3 m perpendicular height") });
        roof.Details.Add(new() { Kind = RoofDetailKind.Drain, Name = "Explicit point annotation", Count = 2 });
        RoofWorkflow.Publish(roof, null, Epoch.AddMinutes(1));
        roof.Validate();
        var snapshot = roof.Snapshot!;
        Assert.Equal(RoofRevisionStatus.Published, roof.Status); Assert.Equal(Epoch.AddMinutes(1), roof.PublishedAt);
        Assert.Equal(135d, snapshot.GrossAreaM2, 10); Assert.Equal(6d, snapshot.OpeningAreaM2, 10); Assert.Equal(129d, snapshot.NetAreaM2, 10);
        Assert.False(snapshot.IsNormative); Assert.Equal("1.0.0", snapshot.MethodVersion); Assert.Equal(64, snapshot.Digest.Length);
        Assert.Equal(roof.Id, snapshot.RoofRevisionId); Assert.Equal(roof.BuildingId, snapshot.BuildingId);
        Assert.Equal(2, snapshot.Details.Count); Assert.All(snapshot.Surfaces, surface => Assert.False(surface.Geometry.Provenance.IsNormative));
    }

    [Theory]
    [InlineData("geometry")]
    [InlineData("detail")]
    [InlineData("title")]
    [InlineData("project")]
    [InlineData("result")]
    [InlineData("normative")]
    [InlineData("digest")]
    public void A_published_roof_rejects_mutated_inputs_and_output_evidence(string change)
    {
        var roof = PublishedRoof();
        switch (change)
        {
            case "geometry": roof.Surfaces[0].Geometry = roof.Surfaces[0].Geometry with { Openings = [] }; break;
            case "detail": roof.Details[0].Count = 2; break;
            case "title": roof.Title = "Changed after publication"; break;
            case "project": roof.ProjectId = Guid.NewGuid(); break;
            case "result": roof.Snapshot = roof.Snapshot! with { NetAreaM2 = 120 }; break;
            case "normative": roof.Snapshot = roof.Snapshot! with { IsNormative = true }; break;
            case "digest": roof.Snapshot = roof.Snapshot! with { Digest = new string('0', 64) }; break;
        }
        Assert.Equal("roof_snapshot_changed", Assert.Throws<DomainException>(roof.Validate).Code);
    }

    [Fact]
    public void A_new_revision_copies_published_evidence_and_leaves_its_predecessor_unchanged()
    {
        var roof = PublishedRoof(); var originalDigest = roof.Snapshot!.Digest;
        var next = RoofWorkflow.CreateRevision(roof, "Opening removed after a new survey", Epoch.AddMinutes(2));
        Assert.NotEqual(roof.Id, next.Id); Assert.Equal(roof.Id, next.PreviousRevisionId); Assert.Equal(2, next.Version);
        Assert.Equal(RoofRevisionStatus.Draft, next.Status); Assert.Null(next.Snapshot); Assert.Null(next.PublishedAt);
        Assert.Equal(roof.Surfaces[0].Id, next.Surfaces[0].Id); Assert.NotSame(roof.Surfaces[0], next.Surfaces[0]);
        next.Surfaces[0].Geometry = next.Surfaces[0].Geometry with { Openings = [] };
        next.Details[0].LengthM = 10;
        RoofWorkflow.Publish(next, roof, Epoch.AddMinutes(3)); next.Validate(); roof.Validate();
        Assert.Equal(120d, next.Snapshot!.NetAreaM2, 10); Assert.Equal(114d, roof.Snapshot.NetAreaM2, 10);
        Assert.Equal(originalDigest, roof.Snapshot.Digest); Assert.NotEqual(originalDigest, next.Snapshot.Digest);
        Assert.Equal(12m, roof.Details[0].LengthM);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tenant")]
    [InlineData("building")]
    [InlineData("skipped_version")]
    [InlineData("unpublished")]
    public void Publishing_a_revision_checks_its_actual_predecessor(string change)
    {
        var source = PublishedRoof(); var next = RoofWorkflow.CreateRevision(source, "Independent follow-up survey", Epoch.AddMinutes(2));
        RoofRevision? supplied = source;
        switch (change)
        {
            case "missing": supplied = null; break;
            case "tenant":
            case "building":
                supplied = DraftRoof();
                if (change == "tenant") supplied.BuildingId = source.BuildingId;
                else supplied.TenantId = source.TenantId;
                RoofWorkflow.Publish(supplied, null, Epoch.AddMinutes(1)); next.PreviousRevisionId = supplied.Id; break;
            case "skipped_version": next.Version = 3; break;
            case "unpublished": supplied = DraftRoof(); supplied.TenantId = source.TenantId; supplied.BuildingId = source.BuildingId; next.PreviousRevisionId = supplied.Id; break;
        }
        Assert.Throws<DomainException>(() => RoofWorkflow.Publish(next, supplied, Epoch.AddMinutes(3)));
        Assert.Equal(RoofRevisionStatus.Draft, next.Status); Assert.Null(next.Snapshot);
    }

    [Fact]
    public void Publish_detaches_mutable_caller_collections_and_details()
    {
        var roof = DraftRoof();
        var points = new List<PlanePoint> { new(0, 0), new(12, 0), new(12, 10), new(0, 10) };
        var openings = new List<PolygonOpening> { new("Window", [new(1, 1), new(3, 1), new(3, 4), new(1, 4)]) };
        var originalDetail = roof.Details[0];
        roof.Surfaces[0].Geometry = new(points, openings, "Synthetic tape measure");
        RoofWorkflow.Publish(roof, null, Epoch.AddMinutes(1));
        points[1] = new(100, 0); openings.Clear(); originalDetail.LengthM = 999;
        roof.Validate();
        Assert.Equal(114d, roof.Snapshot!.NetAreaM2, 10); Assert.Equal(12m, roof.Details[0].LengthM);
    }

    [Theory]
    [InlineData("missing_surface")]
    [InlineData("duplicate_surface")]
    [InlineData("self_intersection")]
    [InlineData("non_finite")]
    [InlineData("missing_linear_length")]
    [InlineData("point_length")]
    [InlineData("vertex_budget")]
    public void Roof_drafts_reject_invalid_geometry_and_detail_evidence(string change)
    {
        var roof = DraftRoof();
        switch (change)
        {
            case "missing_surface": roof.Details[0].SurfaceId = Guid.NewGuid(); break;
            case "duplicate_surface": roof.Surfaces.Add(roof.Surfaces[0] with { }); break;
            case "self_intersection": roof.Surfaces[0].Geometry = new([new(0, 0), new(2, 2), new(0, 2), new(2, 0)], [], "Bow-tie rejection reference"); break;
            case "non_finite": roof.Surfaces[0].Geometry = new([new(double.NaN, 0), new(2, 0), new(0, 2)], [], "NaN rejection reference"); break;
            case "missing_linear_length": roof.Details[0].LengthM = null; break;
            case "point_length": roof.Details[0].Kind = RoofDetailKind.Drain; break;
            case "vertex_budget":
                roof.Details.Clear();
                roof.Surfaces = Enumerable.Range(0, 5).Select(index => new RoofSurface
                {
                    Name = "Bounded circle " + index,
                    Geometry = new(Enumerable.Range(0, 1000).Select(i => new PlanePoint(10 * Math.Cos(2 * Math.PI * i / 1000), 10 * Math.Sin(2 * Math.PI * i / 1000))).ToArray(), [], "Synthetic rejection reference")
                }).ToList(); break;
        }
        Assert.Throws<DomainException>(roof.Validate);
    }

    [Fact]
    public void An_empty_draft_cannot_be_published()
    {
        var roof = DraftRoof(); roof.Details.Clear(); roof.Surfaces.Clear(); roof.Validate();
        Assert.Throws<DomainException>(() => RoofWorkflow.Publish(roof, null, Epoch.AddMinutes(1)));
        Assert.Equal(RoofRevisionStatus.Draft, roof.Status);
    }

    [Fact]
    public void Typed_measurements_produce_independent_reference_quantities_and_separate_units()
    {
        var roof = PublishedRoof();
        var measurement = DraftMeasurement(roof,
            new RectangleArea(12, 10), new TriangleArea(4, 3), new Length(7.125m), new Count(5), new Volume(2, 3, 0.1m), new SurfaceNetArea(roof.Surfaces[0].Id));
        measurement.Lines[0].Repetitions = 2; measurement.Lines[0].Deductions.Add(new("Explicitly excluded 2 by 3 metre work item", 6));
        measurement.Lines[3].Repetitions = 3; measurement.Lines[3].Deductions.Add(new("Two pieces outside this measurement", 2));
        var preview = MeasurementWorkflow.Preview(measurement, roof);
        Assert.Equal(new[] { 234m, 6m, 7.125m, 13m, 0.6m, 114m }, preview.Lines.Select(x => x.Quantity));
        Assert.Equal(new[] { "m²", "m²", "m", "Stk", "m³", "m²" }, preview.Lines.Select(x => x.Unit));
        Assert.Equal(240m, preview.Lines[0].GrossQuantity); Assert.Equal(6m, preview.Lines[0].DeductionQuantity);
        Assert.Equal(354m, Assert.Single(preview.Totals, x => x.Unit == "m²").Quantity);
        Assert.Equal(4, preview.Totals.Count); Assert.Equal(roof.Snapshot!.Digest, preview.RoofDigest);
        var surface = Assert.Single(preview.SurfaceEvidence);
        Assert.Equal(roof.Surfaces[0].Id, surface.Id); Assert.Equal(surface.Geometry.Provenance.InputSha256, preview.Lines[5].SourceGeometryDigest);
        Assert.False(preview.IsNormative); Assert.Equal(MeasurementStatus.Draft, measurement.Status); Assert.Null(measurement.Snapshot);
        MeasurementWorkflow.Freeze(measurement, roof, Epoch.AddMinutes(3)); measurement.Validate();
        Assert.Equal(preview.Digest, measurement.Snapshot!.Digest); Assert.Equal(MeasurementStatus.Frozen, measurement.Status);
        Assert.Throws<DomainException>(() => MeasurementWorkflow.Freeze(measurement, roof, Epoch.AddMinutes(4)));
    }

    [Fact]
    public void Measurement_rounding_happens_after_repetitions_and_retains_declared_deductions()
    {
        var roof = PublishedRoof();
        var measurement = DraftMeasurement(roof, new RectangleArea(1.000001m, 0.5m), new TriangleArea(0.000001m, 1m));
        measurement.Lines[0].Repetitions = 2;
        var result = MeasurementWorkflow.Preview(measurement, roof);
        Assert.Equal(1.000001m, result.Lines[0].GrossQuantity);
        Assert.Equal(0.000001m, result.Lines[1].Quantity);
        measurement.Lines[0].Deductions.Add(new("All of this line is explicitly excluded", 1.000001m));
        Assert.Equal(0m, MeasurementWorkflow.Preview(measurement, roof).Lines[0].Quantity);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("zero")]
    [InlineData("precision")]
    [InlineData("quantity_budget")]
    [InlineData("rounds_to_zero")]
    [InlineData("unknown_surface")]
    [InlineData("excessive_deduction")]
    [InlineData("negative_deduction")]
    [InlineData("fractional_count_deduction")]
    [InlineData("empty_source")]
    [InlineData("unknown_type")]
    public void Measurement_input_is_bounded_and_unsupported_or_ambiguous_cases_fail_closed(string change)
    {
        var roof = PublishedRoof();
        MeasurementInput input = change switch
        {
            "negative" => new Length(-1), "zero" => new RectangleArea(0, 2), "precision" => new Length(0.1234567m),
            "quantity_budget" => new Volume(100_000, 100_000, 100_000), "rounds_to_zero" => new Volume(0.000001m, 0.000001m, 0.000001m),
            "unknown_surface" => new SurfaceNetArea(Guid.NewGuid()), "fractional_count_deduction" => new Count(3),
            "unknown_type" => new ArbitraryFormula("outside the allowlist"), _ => new Length(10)
        };
        var measurement = DraftMeasurement(roof, input);
        if (change == "excessive_deduction") measurement.Lines[0].Deductions.AddRange([new("First declared deduction", 6), new("Second declared deduction", 5)]);
        if (change == "negative_deduction") measurement.Lines[0].Deductions.Add(new("Negative deduction rejection", -1));
        if (change == "fractional_count_deduction") measurement.Lines[0].Deductions.Add(new("Fractional piece rejection", 0.5m));
        if (change == "empty_source") measurement.Lines[0].MeasurementSource = "";
        Assert.Throws<DomainException>(() => MeasurementWorkflow.Preview(measurement, roof));
        Assert.Null(measurement.Snapshot); Assert.Equal(MeasurementStatus.Draft, measurement.Status);
    }

    [Theory]
    [InlineData("roof_not_published")]
    [InlineData("wrong_tenant")]
    [InlineData("wrong_project")]
    [InlineData("wrong_roof_id")]
    public void Measurement_requires_the_matching_published_roof_context(string change)
    {
        var roof = change == "roof_not_published" ? DraftRoof() : PublishedRoof();
        var measurement = DraftMeasurement(roof, new RectangleArea(2, 3));
        if (change == "wrong_tenant") measurement.TenantId = Guid.NewGuid();
        if (change == "wrong_project") measurement.ProjectId = Guid.NewGuid();
        if (change == "wrong_roof_id") measurement.RoofRevisionId = Guid.NewGuid();
        Assert.Throws<DomainException>(() => MeasurementWorkflow.Freeze(measurement, roof, Epoch.AddMinutes(3)));
        Assert.Equal(MeasurementStatus.Draft, measurement.Status);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("deduction")]
    [InlineData("result")]
    [InlineData("roof_digest")]
    [InlineData("geometry")]
    [InlineData("normative")]
    public void Frozen_measurement_evidence_rejects_later_mutations(string change)
    {
        var roof = PublishedRoof(); var measurement = DraftMeasurement(roof, new SurfaceNetArea(roof.Surfaces[0].Id));
        MeasurementWorkflow.Freeze(measurement, roof, Epoch.AddMinutes(3));
        var snapshot = measurement.Snapshot!;
        switch (change)
        {
            case "input": measurement.Lines[0].Repetitions = 2; break;
            case "deduction": measurement.Lines[0].Deductions.Add(new("Added after freeze", 1)); break;
            case "result": measurement.Snapshot = snapshot with { Lines = [snapshot.Lines[0] with { Quantity = 999 }] }; break;
            case "roof_digest": measurement.Snapshot = snapshot with { RoofDigest = new string('f', 64) }; break;
            case "geometry": measurement.Snapshot = snapshot with { SurfaceEvidence = [snapshot.SurfaceEvidence[0] with { Geometry = snapshot.SurfaceEvidence[0].Geometry with { NetAreaM2 = 999 } }] }; break;
            case "normative": measurement.Snapshot = snapshot with { IsNormative = true }; break;
        }
        Assert.Throws<DomainException>(measurement.Validate);
    }

    [Fact]
    public void Frozen_measurement_remains_reproducible_after_a_new_roof_revision_and_caller_mutation()
    {
        var roof = PublishedRoof(); var measurement = DraftMeasurement(roof, new SurfaceNetArea(roof.Surfaces[0].Id));
        var inputLine = measurement.Lines[0]; var inputList = measurement.Lines;
        MeasurementWorkflow.Freeze(measurement, roof, Epoch.AddMinutes(3));
        var digest = measurement.Snapshot!.Digest;
        inputLine.Repetitions = 100; inputList.Clear();
        var next = RoofWorkflow.CreateRevision(roof, "Later independent survey", Epoch.AddMinutes(4));
        next.Surfaces[0].Geometry = next.Surfaces[0].Geometry with { Openings = [] };
        RoofWorkflow.Publish(next, roof, Epoch.AddMinutes(5));
        var restored = JsonSerializer.Deserialize<Measurement>(JsonSerializer.Serialize(measurement, Json), Json)!;
        restored.Validate();
        Assert.Equal(digest, restored.Snapshot!.Digest); Assert.Equal(114m, Assert.Single(restored.Snapshot.Lines).Quantity);
        Assert.Equal(120d, next.Snapshot!.NetAreaM2, 10); Assert.Equal(roof.Id, restored.RoofRevisionId);
        Assert.Equal(1, Assert.Single(restored.Lines).Repetitions);
    }

    [Fact]
    public void Typed_input_deserialization_accepts_jsonb_key_order_and_rejects_unknown_metadata_or_fields()
    {
        var input = JsonSerializer.Deserialize<MeasurementInput>("{\"lengthM\":12,\"widthM\":10,\"type\":\"rectangleArea\"}", Json);
        Assert.Equal(new RectangleArea(12, 10), Assert.IsType<RectangleArea>(input));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MeasurementInput>("{\"type\":\"formula\",\"expression\":\"12*10\"}", Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MeasurementInput>("{\"type\":\"length\",\"lengthM\":2,\"unrecognized\":3}", Json));
        var roof = PublishedRoof();
        var restored = JsonSerializer.Deserialize<RoofRevision>(JsonSerializer.Serialize(roof, Json), Json)!;
        restored.Validate(); Assert.Equal(roof.Snapshot!.Digest, restored.Snapshot!.Digest);
    }

    [Fact]
    public void Registry_and_references_keep_roof_and_measurement_records_in_the_tenant_graph()
    {
        var roof = PublishedRoof(); var next = RoofWorkflow.CreateRevision(roof, "Follow-up", Epoch.AddMinutes(2));
        var measurement = DraftMeasurement(roof, new Count(1));
        Assert.Equal("roof-revisions", BusinessRegistry.KindOf<RoofRevision>()); Assert.Equal("measurements", BusinessRegistry.KindOf<Measurement>());
        Assert.Contains(new EntityReference("roof-revisions", roof.Id), next.References());
        Assert.Contains(new EntityReference("buildings", roof.BuildingId), next.References());
        Assert.Contains(new EntityReference("roof-revisions", roof.Id), measurement.References());
        Assert.Contains(new EntityReference("projects", measurement.ProjectId), measurement.References());
    }

    private static RoofRevision DraftRoof()
    {
        var surface = new RoofSurface
        {
            Name = "Actual roof plane",
            Geometry = new([new(0, 0), new(12, 0), new(12, 10), new(0, 10)],
                [new("2 by 3 metre opening", [new(1, 1), new(3, 1), new(3, 4), new(1, 4)])], "Independent synthetic 12 by 10 metre survey")
        };
        return new()
        {
            TenantId = Guid.NewGuid(), BuildingId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Title = "Synthetic roof revision",
            CreatedAt = Epoch, UpdatedAt = Epoch, Surfaces = [surface],
            Details = [new() { SurfaceId = surface.Id, Kind = RoofDetailKind.Ridge, Name = "Descriptive ridge", LengthM = 12 }]
        };
    }

    private static RoofRevision PublishedRoof()
    {
        var roof = DraftRoof(); RoofWorkflow.Publish(roof, null, Epoch.AddMinutes(1)); return roof;
    }

    private static Measurement DraftMeasurement(RoofRevision roof, params MeasurementInput[] inputs)
        => new()
        {
            TenantId = roof.TenantId, ProjectId = roof.ProjectId!.Value, RoofRevisionId = roof.Id, Title = "Synthetic measured work",
            CreatedAt = Epoch.AddMinutes(2), UpdatedAt = Epoch.AddMinutes(2),
            Lines = inputs.Select((input, index) => new MeasurementLine { Description = "Independent reference " + index, MeasurementSource = "Explicit synthetic survey values", Input = input }).ToList()
        };

    private sealed record ArbitraryFormula(string Expression) : MeasurementInput;
}
