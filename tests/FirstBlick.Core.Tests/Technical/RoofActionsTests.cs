using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using FirstBlick.Core.Business;
using FirstBlick.Core.Technical;

namespace FirstBlick.Core.Tests.Technical;

public sealed class RoofActionsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static RoofDrainageInput Drainage() => new("Synthetic rainfall pair, same duration and site", 300, 600,
        EmergencyDrainageScenario.ResidualAfterPrimaryDesignFlow,
        [new("Lower roof", 200, .8, 4, 6, "Synthetic effective plan area", "Synthetic capacities at stated heads")]);
    private static RoofWindInput Wind() => new("Synthetic simultaneous wind case",
        [new("Corner", 12, .8, -1.5, .6, .2, "Synthetic peak pressures and local coefficients")]);
    private static RoofSnowInput Snow() => new("Synthetic simultaneous snow arrangement", 1.8, .8, 1,
        [new("Left", 50, .8, "Synthetic projection and shape"), new("Right", 30, 1.2, "Synthetic projection and shape")]);

    [Fact]
    public void Drainage_converts_hectares_and_caps_primary_credit_without_storm_retention()
    {
        var result = RoofDrainage.Calculate(Drainage());
        var row = Assert.Single(result.Catchments);
        Assert.Equal(4.8, row.PrimaryDemandLs, 12);
        Assert.Equal(12, row.StormInflowLs);
        Assert.Equal(4, row.CreditedPrimaryFlowLs);
        Assert.Equal(8, row.EmergencyDemandLs);
        Assert.Equal(.8, row.PrimaryDeficitLs, 12);
        Assert.Equal(2, row.EmergencyDeficitLs);
        Assert.False(result.HydraulicDesignApproved);
        Assert.False(result.Provenance.IsNormative);
    }

    [Fact]
    public void Extra_capacity_never_invents_extra_primary_credit_or_replaces_independent_catchment_capacity()
    {
        var input = Drainage();
        var result = RoofDrainage.Calculate(input with { Catchments = [
            input.Catchments[0] with { PrimaryCapacityLs = 1000, EmergencyCapacityLs = 1000 },
            input.Catchments[0] with { Name = "Independent", PrimaryCapacityLs = 0, EmergencyCapacityLs = 0 }
        ] });
        Assert.Equal(4.8, result.Catchments[0].CreditedPrimaryFlowLs, 12);
        Assert.Equal(12, result.Catchments[1].EmergencyDeficitLs);
        Assert.Equal(4.8, result.Catchments[1].PrimaryDeficitLs, 12);
    }

    [Fact]
    public void Primary_outage_requires_the_entire_storm_flow_even_with_large_primary_capacity()
    {
        var result = RoofDrainage.Calculate(Drainage() with { EmergencyScenario = EmergencyDrainageScenario.PrimaryUnavailable });
        Assert.Equal(0, result.Catchments[0].CreditedPrimaryFlowLs);
        Assert.Equal(12, result.Catchments[0].EmergencyDemandLs);
        Assert.Equal(6, result.Catchments[0].EmergencyDeficitLs);
    }

    [Fact]
    public void Wind_retains_distinct_external_internal_pressures_and_local_force_sign()
    {
        var row = Assert.Single(RoofWind.Calculate(Wind()).Zones);
        Assert.Equal(-1.2, row.ExternalPressureKNm2, 12);
        Assert.Equal(.12, row.InternalPressureKNm2, 12);
        Assert.Equal(-1.32, row.NetInwardPressureKNm2, 12);
        Assert.Equal(-15.84, row.NormalForceKN, 12);
        Assert.Equal(1.32, row.OutwardSuctionKNm2, 12);
        var inward = Assert.Single(RoofWind.Calculate(Wind() with {
            Zones = [Wind().Zones[0] with { ExternalCoefficient = 1, InternalCoefficient = -.3 }] }).Zones);
        Assert.Equal(.98, inward.NetInwardPressureKNm2, 12);
        Assert.Equal(0, inward.OutwardSuctionKNm2);
    }

    [Fact]
    public void Snow_uses_horizontal_projection_and_sums_only_the_supplied_simultaneous_case()
    {
        // Independent products: .8 * .8 * 1 * 1.8 * 50 = 57.6 kN;
        // 1.2 * .8 * 1 * 1.8 * 30 = 51.84 kN.
        var result = RoofSnow.Calculate(Snow());
        Assert.Equal(1.152, result.Zones[0].VerticalLoadKNm2, 12);
        Assert.Equal(57.6, result.Zones[0].VerticalForceKN, 12);
        Assert.Equal(51.84, result.Zones[1].VerticalForceKN, 12);
        Assert.Equal(109.44, result.TotalVerticalForceKN, 12);
        Assert.False(result.StructuralDesignApproved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1000001)]
    public void Invalid_or_nonfinite_areas_are_rejected_in_every_method(double area)
    {
        Assert.ThrowsAny<ArgumentException>(() => RoofDrainage.Calculate(Drainage() with {
            Catchments = [Drainage().Catchments[0] with { EffectiveAreaM2 = area }] }));
        Assert.ThrowsAny<ArgumentException>(() => RoofWind.Calculate(Wind() with {
            Zones = [Wind().Zones[0] with { LoadedAreaM2 = area }] }));
        Assert.ThrowsAny<ArgumentException>(() => RoofSnow.Calculate(Snow() with {
            Zones = [Snow().Zones[0] with { HorizontalAreaM2 = area }] }));
    }

    [Fact]
    public void Invalid_scenarios_coefficients_sources_budgets_and_duplicate_names_fail_closed()
    {
        Assert.Throws<DomainException>(() => RoofDrainage.Calculate(Drainage() with { EmergencyScenario = (EmergencyDrainageScenario)99 }));
        Assert.ThrowsAny<ArgumentException>(() => RoofDrainage.Calculate(Drainage() with { StormRainfallLsHa = 200 }));
        Assert.ThrowsAny<ArgumentException>(() => RoofDrainage.Calculate(Drainage() with { Catchments = [Drainage().Catchments[0] with { DesignRunoffCoefficient = 1.01 }] }));
        Assert.ThrowsAny<ArgumentException>(() => RoofWind.Calculate(Wind() with { Zones = [Wind().Zones[0] with { InternalCoefficient = double.NaN }] }));
        Assert.ThrowsAny<ArgumentException>(() => RoofSnow.Calculate(Snow() with { ExposureCoefficient = 0 }));
        Assert.ThrowsAny<ArgumentException>(() => RoofSnow.Calculate(Snow() with { CaseSource = "invalid\nsource" }));
        Assert.ThrowsAny<ArgumentException>(() => RoofSnow.Calculate(Snow() with { Zones = [] }));
        Assert.ThrowsAny<ArgumentException>(() => RoofSnow.Calculate(Snow() with { Zones = Enumerable.Range(0, 33).Select(i => new SnowLoadZone($"Zone {i}", 10, .8, "Source")).ToArray() }));
        Assert.Throws<DomainException>(() => RoofSnow.Calculate(Snow() with { Zones = [Snow().Zones[0], Snow().Zones[0] with { Name = " LEFT " }] }));
        Assert.ThrowsAny<ArgumentException>(() => RoofWind.Calculate(Wind() with { Zones = [null!] }));
        Assert.ThrowsAny<ArgumentException>(() => RoofDrainage.Calculate(Drainage() with { Catchments = null! }));
    }

    [Fact]
    public void Caller_mutation_cannot_change_retained_input_result_or_provenance()
    {
        var zones = new List<SnowLoadZone>(Snow().Zones);
        var result = RoofSnow.Calculate(Snow() with { Zones = zones });
        var digest = result.Provenance.InputSha256;
        zones[0] = zones[0] with { ShapeCoefficient = 9 };
        zones.Clear();
        Assert.Equal(2, result.Input.Zones.Count);
        Assert.Equal(109.44, result.TotalVerticalForceKN, 12);
        Assert.Equal(digest, RoofSnow.Calculate(result.Input).Provenance.InputSha256);
        Assert.NotEqual(digest, RoofSnow.Calculate(result.Input with { CaseSource = "New source revision" }).Provenance.InputSha256);
    }

    [Theory]
    [InlineData("roof.drainage")]
    [InlineData("roof.wind")]
    [InlineData("roof.snow")]
    public void Project_records_roundtrip_recompute_and_reject_result_or_source_tampering(string method)
    {
        object input = method switch { "roof.drainage" => Drainage(), "roof.wind" => Wind(), _ => Snow() };
        var record = TechnicalRecords.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            "Synthetic load evidence", method, JsonSerializer.SerializeToElement(input, input.GetType(), Json), Guid.NewGuid(), DateTimeOffset.UtcNow);
        var restored = JsonSerializer.Deserialize<TechnicalRecord>(JsonSerializer.Serialize(record, Json), Json)!;
        restored.Validate();
        Assert.Equal(record.Digest, restored.Digest);
        restored.Title += " changed";
        Assert.Throws<DomainException>(restored.Validate);
        restored = JsonSerializer.Deserialize<TechnicalRecord>(JsonSerializer.Serialize(record, Json), Json)!;
        restored.Result = JsonSerializer.SerializeToElement(new { fabricated = true });
        Assert.Throws<DomainException>(restored.Validate);
    }

    [Theory]
    [InlineData("roof.drainage")]
    [InlineData("roof.wind")]
    [InlineData("roof.snow")]
    public void Every_JSON_input_property_is_required_even_when_zero_is_a_valid_value(string method)
    {
        object input = method switch { "roof.drainage" => Drainage(), "roof.wind" => Wind(), _ => Snow() };
        var root = JsonNode.Parse(JsonSerializer.Serialize(input, input.GetType(), Json))!.AsObject();
        foreach (var property in root.ToArray())
        {
            var incomplete = root.DeepClone().AsObject();
            incomplete.Remove(property.Key);
            Assert.Throws<JsonException>(() => TechnicalRecords.Evaluate(method, JsonSerializer.SerializeToElement(incomplete)));
        }
        var rowsKey = method == "roof.drainage" ? "catchments" : "zones";
        foreach (var property in root[rowsKey]![0]!.AsObject().ToArray())
        {
            var incomplete = root.DeepClone();
            incomplete[rowsKey]![0]!.AsObject().Remove(property.Key);
            Assert.Throws<JsonException>(() => TechnicalRecords.Evaluate(method, JsonSerializer.SerializeToElement(incomplete)));
        }
    }
}
