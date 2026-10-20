using FirstBlick.Core.Business;
using FirstBlick.Core.Technical;
using Xunit;

namespace FirstBlick.Core.Tests.Technical;

public sealed class Iso13788BalanceTests
{
    // Numerical reference data only, not a copy of the standard's prose or figures.
    // ISO 13788:2012 informative Annex C.1/C.2/C.3 and D provide independent expected results.
    public static IReadOnlyList<MoistureDesignMonth> ReferenceClimate(bool high)
    {
        double[] outside = [-1, 0, 4, 9, 14, 18, 19, 19, 15, 10, 5, 1];
        double[] exteriorRh = [85, 84, 78, 72, 68, 69, 73, 75, 79, 83, 88, 88];
        double[] inside = [20, 20, 20, 20, 22, 24, 24.5, 24.5, 22.5, 20, 20, 20];
        double[] interiorRh = [39, 40, 44, 49, 54, 58, 59, 59, 55, 50, 45, 41];
        return Enumerable.Range(0, 12).Select(i => new MoistureDesignMonth(i + 1, inside[i], outside[i], interiorRh[i] + (high ? 10 : 0), exteriorRh[i], outside[i])).ToArray();
    }
    public static Iso13788BalanceInput Reference(bool high = false) => new(2001, 10, MoistureHeatFlow.Upward,
        MoistureExteriorBoundary.OutsideAir, MoistureClimateTreatment.SuppliedEquivalentRoofTemperature, false,
        [Layer("Liner", .075, .12), Layer("Vapour check", 0, 1000, MoistureMaterialKind.Membrane), Layer("Insulation", 3, 15), Layer("Weatherproof layer", .05, 5000)],
        ReferenceClimate(high), new(true, true, true, true, true, true,
            "Independent reconstruction of the Annex C numeric example; not a proposed real construction",
            "ISO 13788:2012 Annex C.1 reference boundary values; equivalent thermal temperature explicitly matches this reference example"));
    private static MoistureResistanceLayer Layer(string name, double r, double sd, MoistureMaterialKind kind = MoistureMaterialKind.HomogeneousLayer)
        => new(name, r, sd, kind, "Independent normative-reference fixture; not manufacturer data");

    [Fact]
    public void Single_interface_reference_reproduces_seasonal_condensation_and_complete_re_evaporation()
    {
        var result = Iso13788Balance.Calculate(Reference());
        Assert.Equal(11, result.StartingMonth);
        Assert.Equal(AnnualMoistureClassification.CondensateFullyReevaporates, result.Classification);
        var peak = Assert.Single(result.InterfaceMaxima);
        Assert.InRange(peak.MaximumMassKgM2, .00054, .00056);
        Assert.Equal(3, peak.Month); Assert.Equal(0, peak.EndMassKgM2);
        Assert.Equal(0, result.Months.Single(x => x.Month == 7).EndStoredMassKgM2);
        Assert.InRange(result.TotalCondensedKgM2 - result.TotalEvaporatedKgM2, -1e-10, 1e-10);
        Assert.True(result.Months.Single(x => x.Month == 7).Subperiods > 1);
        Assert.True(result.StandardProcedure.ImplementsSpecifiedProcedure);
        Assert.False(result.StandardProcedure.ConstructionComplianceAssessed);
        Assert.False(result.StandardProcedure.Din4108Part3Assessment);
    }
    [Fact]
    public void High_occupancy_reference_keeps_the_end_of_year_water_instead_of_resetting_each_month()
    {
        var result = Iso13788Balance.Calculate(Reference(high: true));
        Assert.Equal(10, result.StartingMonth); Assert.Equal(AnnualMoistureClassification.ResidualCondensate, result.Classification);
        Assert.InRange(result.ResidualCondensateKgM2, .00088, .00092);
        var peak = Assert.Single(result.InterfaceMaxima); Assert.Equal(4, peak.Month);
        Assert.InRange(peak.MaximumMassKgM2, .00125, .00129);
        Assert.InRange(result.TotalCondensedKgM2 - result.TotalEvaporatedKgM2 - result.ResidualCondensateKgM2, -1e-10, 1e-10);
    }
    [Fact]
    public void Two_interface_reference_accounts_for_condensation_and_evaporation_at_different_planes()
    {
        var basis = Reference(true);
        var input = basis with { Layers = [basis.Layers[0], Layer("Inner insulation", 3, 15), basis.Layers[1], basis.Layers[2], basis.Layers[3]] };
        var result = Iso13788Balance.Calculate(input);
        Assert.Equal(12, result.StartingMonth); Assert.Equal(2, result.InterfaceMaxima.Count);
        var innerNode = result.Grid.Single(x => x.LayerIndex == 1 && x.LayerFraction == 1).Index;
        var inner = result.InterfaceMaxima.Single(x => x.NodeIndex == innerNode);
        Assert.InRange(inner.MaximumMassKgM2, .1775, .1786); Assert.Equal(3, inner.Month);
        Assert.Equal(0, inner.EndMassKgM2);
        var april = result.Months.Single(x => x.Month == 4);
        Assert.True(april.CondensedKgM2 > 0 && april.EvaporatedKgM2 > 0);
        Assert.Equal(AnnualMoistureClassification.ResidualCondensate, result.Classification);
        Assert.All(result.Months, month => Assert.InRange(month.MassConservationErrorKgM2, -1e-9, 1e-9));
    }
    [Fact]
    public void An_uncondensing_construction_is_not_reported_as_a_full_building_compliance_approval()
    {
        var input = Reference() with { Layers = [Layer("Single layer", .5, 1)], Months = Enumerable.Range(1, 12).Select(m => new MoistureDesignMonth(m, 20, 20, 40, 40, 20)).ToArray() };
        var result = Iso13788Balance.Calculate(input);
        Assert.Equal(AnnualMoistureClassification.NoInterstitialCondensation, result.Classification);
        Assert.Empty(result.InterfaceMaxima); Assert.Equal(0, result.ResidualCondensateKgM2);
        Assert.False(result.StandardProcedure.ConstructionComplianceAssessed);
    }
    [Fact]
    public void Roof_radiation_changes_heat_boundary_but_not_outdoor_vapour_pressure()
    {
        var air = Reference() with { ClimateTreatment = MoistureClimateTreatment.WallAir,
            Months = ReferenceClimate(false).Select(x => x with { EquivalentOutsideTemperatureC = null }).ToArray() };
        var a = Iso13788Balance.Calculate(air); var roof = Iso13788Balance.Calculate(air with { ClimateTreatment = MoistureClimateTreatment.RoofMinusTwoKelvin });
        foreach (var month in a.Months)
        {
            var adjusted = roof.Months.Single(x => x.Month == month.Month);
            Assert.Equal(month.OutsideVapourPressurePa, adjusted.OutsideVapourPressurePa);
            Assert.Equal(month.OutsideThermalTemperatureC - 2, adjusted.OutsideThermalTemperatureC);
        }
    }
    [Fact]
    public void Ventilated_cavity_boundary_uses_the_directional_internal_surface_resistance()
    {
        var basis = Reference();
        var result = Iso13788Balance.Calculate(basis with { Layers = basis.Layers.Take(3).ToArray(),
            ExteriorBoundary = MoistureExteriorBoundary.WellVentilatedCavity, ClimateTreatment = MoistureClimateTreatment.WallAir,
            Months = basis.Months.Select(x => x with { EquivalentOutsideTemperatureC = null }).ToArray() });
        Assert.All(result.Months, m => { Assert.Equal(.1, m.InsideSurfaceResistanceM2KW); Assert.Equal(.1, m.OutsideSurfaceResistanceM2KW); });
    }
    [Fact]
    public void Layer_subdivision_is_explicit_and_creates_real_calculation_interfaces()
    {
        var result = Iso13788Balance.Calculate(Reference() with { SubdivideThermalLayers = true });
        Assert.Equal(12, result.Grid.Count(x => x.LayerIndex == 2));
        Assert.All(result.Months, m => Assert.InRange(m.MassConservationErrorKgM2, -1e-9, 1e-9));
        Assert.NotEqual(Iso13788Balance.Calculate(Reference()).Provenance.InputSha256, result.Provenance.InputSha256);
    }
    [Fact]
    public void Supplied_collections_are_detached_and_reordered_climates_have_the_same_normalized_evidence()
    {
        var input = Reference(); var layers = input.Layers.ToArray(); var months = input.Months.Reverse().ToArray();
        var result = Iso13788Balance.Calculate(input with { Layers = layers, Months = months });
        Assert.Equal(Iso13788Balance.Calculate(input).Provenance.InputSha256, result.Provenance.InputSha256);
        layers[0] = layers[0] with { SdM = 999 }; months[0] = months[0] with { InsideTemperatureC = 50 };
        Assert.Equal(.12, result.Input.Layers[0].SdM); Assert.Equal(1, result.Input.Months[0].Month);
    }
    [Fact]
    public void Inapplicable_transports_and_invalid_or_missing_design_inputs_fail_closed()
    {
        var input = Reference();
        Assert.Throws<DomainException>(() => Iso13788Balance.Calculate(input with { Applicability = input.Applicability with { NoAirflowThroughAssembly = false } }));
        Assert.Throws<DomainException>(() => Iso13788Balance.Calculate(input with { Applicability = input.Applicability with { NoRainAbsorption = false } }));
        Assert.ThrowsAny<ArgumentException>(() => Iso13788Balance.Calculate(input with { Layers = [Layer("Bad", double.NaN, 1)] }));
        Assert.ThrowsAny<ArgumentException>(() => Iso13788Balance.Calculate(input with { Layers = [Layer("Bad", 1, 0)] }));
        Assert.Throws<DomainException>(() => Iso13788Balance.Calculate(input with { Layers = [Layer("Air", .16, .1, MoistureMaterialKind.UnventilatedAirLayer)] }));
        Assert.ThrowsAny<ArgumentException>(() => Iso13788Balance.Calculate(input with { Months = input.Months.Take(11).ToArray() }));
        Assert.Throws<DomainException>(() => Iso13788Balance.Calculate(input with { Months = input.Months.Select(x => x with { Month = 1 }).ToArray() }));
        Assert.Throws<DomainException>(() => Iso13788Balance.Calculate(input with { Months = input.Months.Select(x => x with { EquivalentOutsideTemperatureC = null }).ToArray() }));
    }
    [Fact]
    public void Annex_D_drying_tracks_redistribution_and_does_not_claim_the_initial_plane_drying_as_total_drying()
    {
        var input = Reference(true) with { Layers = [Layer("Liner", .06, .15), Layer("Unventilated cavity", .160, .01, MoistureMaterialKind.UnventilatedAirLayer),
            Layer("Deck", .146, .19), Layer("Vapour check", 0, 75, MoistureMaterialKind.Membrane), Layer("Insulation", 5, .2), Layer("Membrane", .008, 12)] };
        var result = Iso13788Balance.Dry(new(input, 4, 9, "Reference comparison; baseline contains no interstitial accumulation"));
        Assert.Equal(1, result.InitialExcessMassKgM2); Assert.Equal(DryingClassification.DriedWithRedistribution, result.Classification);
        Assert.Equal(83, result.DryingTimeMonths); Assert.Equal(0, result.ResidualMassKgM2);
        Assert.InRange(result.Months[0].EndStoredMassKgM2, .990, .993);
        Assert.Equal(0, result.Months[0].WetInterfaces.Single(x => x.NodeIndex == result.InitiallyWettedNode).EndMassKgM2);
        Assert.True(result.Months[0].WetInterfaces.Any(x => x.NodeIndex != result.InitiallyWettedNode && x.EndMassKgM2 > .9));
        Assert.All(result.Months, m => Assert.InRange(m.MassConservationErrorKgM2, -1e-8, 1e-8));
    }
    [Fact]
    public void A_failed_annual_baseline_cannot_be_presented_as_an_applicable_excess_water_drying_test()
        => Assert.Throws<DomainException>(() => Iso13788Balance.Dry(new(Reference(true), 2, 9, "Reviewed synthetic input")));
}
