using FirstBlick.Core.Business;
using FirstBlick.Core.Technical;
using Xunit;
using Xunit.Abstractions;

namespace FirstBlick.Core.Tests.Technical;

public sealed class Iso13788BalanceTests(ITestOutputHelper output)
{
    // Independently encoded numerical data. No standard text or figures are redistributed.
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
            "Independently transcribed numerical reference; not a proposed real construction",
            "ISO 13788:2012 Annex C.1 boundary values; equivalent thermal temperature explicitly matches this reference example"));
    private static MoistureResistanceLayer Layer(string name, double r, double sd, MoistureMaterialKind kind = MoistureMaterialKind.HomogeneousLayer)
        => new(name, r, sd, kind, "Independent numerical fixture; not manufacturer data");

    // Independent closed-form oracles do not call the production pressure-envelope or event solver.
    private static double Ps(double t) => t < 0 ? 610.5 * Math.Exp(21.875 * t / (265.5 + t)) : 610.5 * Math.Exp(17.269 * t / (237.3 + t));
    private static double SinglePlaneRate(MoistureDesignMonth m)
    {
        var q = (m.InsideTemperatureC - m.OutsideAirTemperatureC) / 3.265;
        var pc = Ps(m.OutsideAirTemperatureC + q * .09);
        return 2e-10 * ((Ps(m.InsideTemperatureC) * m.InsideRelativeHumidityPercent / 100 - pc) / 1015.12
            - (pc - Ps(m.OutsideAirTemperatureC) * m.OutsideRelativeHumidityPercent / 100) / 5000);
    }
    private static void VerifySinglePlaneClosedForm(Iso13788BalanceResult result)
    {
        var water = 0d;
        foreach (var month in result.Months)
        {
            var climate = result.Input.Months.Single(x => x.Month == month.Month);
            var signedChange = SinglePlaneRate(climate) * DateTime.DaysInMonth(2001, month.Month) * 86400;
            var expectedCondensation = Math.Max(0, signedChange);
            var expectedEvaporation = Math.Min(water, Math.Max(0, -signedChange));
            water += expectedCondensation - expectedEvaporation;
            Assert.InRange(month.CondensedKgM2 - expectedCondensation, -1e-12, 1e-12);
            Assert.InRange(month.EvaporatedKgM2 - expectedEvaporation, -1e-12, 1e-12);
            Assert.InRange(month.EndStoredMassKgM2 - water, -1e-12, 1e-12);
            Assert.InRange(month.MassConservationErrorKgM2, -1e-12, 1e-12);
        }
        Assert.InRange(result.ResidualCondensateKgM2 - water, -1e-12, 1e-12);
    }

    [Fact]
    public void Single_plane_season_matches_independent_flux_and_exact_drying_event_equations()
    {
        var result = Iso13788Balance.Calculate(Reference()); VerifySinglePlaneClosedForm(result);
        Assert.Equal(11, result.StartingMonth);
        Assert.Equal(AnnualMoistureClassification.CondensateFullyReevaporates, result.Classification);
        var peak = Assert.Single(result.InterfaceMaxima);
        Assert.Equal(3, peak.Month); Assert.Equal(0, peak.EndMassKgM2);
        Assert.Equal(0, result.Months.Single(x => x.Month == 7).EndStoredMassKgM2);
        Assert.True(result.Months.Single(x => x.Month == 7).Subperiods > 1);
        Assert.InRange(result.TotalCondensedKgM2 - result.TotalEvaporatedKgM2, -1e-12, 1e-12);
        // Informative C.3 is printed at 1e-5 kg/m2, not exact input/output precision.
        // The exact equation check above is the acceptance oracle, not a silently fitted table number.
        const double printedPeak = .00055;
        output.WriteLine($"C.3 printed peak={printedPeak:R}; exact-boundary peak={peak.MaximumMassKgM2:R}; difference={peak.MaximumMassKgM2 - printedPeak:R}");
        Assert.InRange(Math.Abs(peak.MaximumMassKgM2 - printedPeak), 0, 5 * .000005);
        Assert.True(result.StandardProcedure.ImplementsSpecifiedProcedure);
        Assert.False(result.StandardProcedure.ConstructionComplianceAssessed);
        Assert.False(result.StandardProcedure.Din4108Part3Assessment);
    }

    [Fact]
    public void High_humidity_retains_the_year_end_inventory_and_matches_independent_flux_equations()
    {
        var result = Iso13788Balance.Calculate(Reference(high: true)); VerifySinglePlaneClosedForm(result);
        Assert.Equal(10, result.StartingMonth); Assert.Equal(AnnualMoistureClassification.ResidualCondensate, result.Classification);
        var peak = Assert.Single(result.InterfaceMaxima); Assert.Equal(4, peak.Month);
        output.WriteLine($"C.3 printed residual=0.00090; exact-boundary residual={result.ResidualCondensateKgM2:R}; printed peak=0.00127; exact-boundary peak={peak.MaximumMassKgM2:R}");
        Assert.InRange(Math.Abs(result.ResidualCondensateKgM2 - .00090), 0, 12 * .000005);
        Assert.InRange(Math.Abs(peak.MaximumMassKgM2 - .00127), 0, 7 * .000005);
    }

    [Fact]
    public void Literal_Annex_C4_inputs_do_not_reproduce_C5_and_the_discrepancy_is_not_hidden_by_changing_physics()
    {
        var basis = Reference(true);
        var input = basis with { Layers = [basis.Layers[0], Layer("Inner insulation", 3, 15), basis.Layers[1], basis.Layers[2], basis.Layers[3]] };
        var december = input.Months.Single(x => x.Month == 12);
        var temperature = 20 - (20 - 1) * 3.175 / 6.265;
        var insidePressure = Ps(20) * .51;
        Assert.True(insidePressure < Ps(temperature));
        Assert.Equal(51, december.InsideRelativeHumidityPercent);
        var result = Iso13788Balance.Calculate(input);
        var innerNode = result.Grid.Single(x => x.LayerIndex == 1 && x.LayerFraction == 1).Index;
        Assert.DoesNotContain(result.InterfaceMaxima, x => x.NodeIndex == innerNode);
        output.WriteLine($"Unresolved informative-reference discrepancy: literal C.4 December inner interface T={temperature:R} C, saturation={Ps(temperature):R} Pa, indoor vapour={insidePressure:R} Pa. C.5's stated inner-plane condensation is not reproduced. This test is a discrepancy diagnostic, not a passed C.5 conformance reference.");
    }

    [Fact]
    public void Two_condensation_planes_match_independent_four_node_slope_equations_over_the_entire_year()
    {
        var input = Reference() with { TrialMonth = 1, Layers = [Layer("A", 2, .1), Layer("B", 2, .1), Layer("C", .05, 100)],
            Months = Enumerable.Range(1, 12).Select(m => new MoistureDesignMonth(m, 20, -10, 80, 80, -10)).ToArray() };
        var result = Iso13788Balance.Calculate(input);
        var p0 = Ps(20) * .8; var p1 = Ps(20 - 30 * 2.1 / 4.19);
        var p2 = Ps(20 - 30 * 4.1 / 4.19); var p3 = Ps(-10) * .8;
        var g1 = 2e-10 * ((p0 - p1) / .1 - (p1 - p2) / .1);
        var g2 = 2e-10 * ((p1 - p2) / .1 - (p2 - p3) / 100);
        Assert.True(g1 > 0 && g2 > 0); Assert.Equal(2, result.InterfaceMaxima.Count);
        foreach (var month in result.Months)
        {
            var duration = month.Days * 86400d;
            Assert.InRange(month.WetInterfaces.Single(x => x.NodeIndex == 1).CondensedKgM2 - g1 * duration, -1e-9, 1e-9);
            Assert.InRange(month.WetInterfaces.Single(x => x.NodeIndex == 2).CondensedKgM2 - g2 * duration, -1e-9, 1e-9);
            Assert.Equal(0, month.EvaporatedKgM2);
        }
        Assert.InRange(result.ResidualCondensateKgM2 - (g1 + g2) * 365 * 86400, -1e-8, 1e-8);
    }

    [Fact]
    public void Distinct_wet_planes_can_evaporate_and_condense_simultaneously_then_dry_without_negative_storage()
    {
        var input = Reference() with { TrialMonth = 1, Layers = [Layer("A", 2, .1), Layer("B", 2, .1), Layer("C", .05, 100)],
            Months = Enumerable.Range(1, 12).Select(m => m == 1 ? new MoistureDesignMonth(m, 20, -10, 80, 80, -10)
                : m == 2 ? new MoistureDesignMonth(m, 20, 10, 30, 50, 10)
                : new MoistureDesignMonth(m, 20, 20, 0, 0, 20)).ToArray() };
        var result = Iso13788Balance.Calculate(input);
        var february = result.Months.Single(x => x.Month == 2);
        Assert.True(february.CondensedKgM2 > 0 && february.EvaporatedKgM2 > 0);
        Assert.Contains(february.WetInterfaces, x => x.CondensedKgM2 > 0);
        Assert.Contains(february.WetInterfaces, x => x.EvaporatedKgM2 > 0);
        Assert.Equal(AnnualMoistureClassification.CondensateFullyReevaporates, result.Classification);
        Assert.All(result.Months.SelectMany(x => x.WetInterfaces), x => Assert.True(x.EndMassKgM2 >= 0));
        Assert.InRange(result.TotalCondensedKgM2 - result.TotalEvaporatedKgM2, -1e-8, 1e-8);
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
    public void Annex_D_drying_tracks_redistribution_and_matches_an_independent_midmonth_drying_solution()
    {
        var input = Reference(true) with { Layers = [Layer("Liner", .06, .15), Layer("Unventilated cavity", .160, .01, MoistureMaterialKind.UnventilatedAirLayer),
            Layer("Deck", .146, .19), Layer("Vapour check", 0, 75, MoistureMaterialKind.Membrane), Layer("Insulation", 5, .2), Layer("Membrane", .008, 12)] };
        var result = Iso13788Balance.Dry(new(input, 4, 9, "Independent excess-water comparison; annual baseline contains no accumulation"));
        Assert.Equal(1, result.InitialExcessMassKgM2); Assert.Equal(DryingClassification.DriedWithRedistribution, result.Classification);
        Assert.Equal(83, result.DryingTimeMonths); Assert.Equal(0, result.ResidualMassKgM2);
        // September has initially wet insulation midpoint and newly condensing outer face.
        // After the midpoint empties, solve the remaining seconds with only the outer face wet.
        var pi = Ps(22.5) * .65; var pe = Ps(15) * .79;
        var pm = Ps(22.5 - 7.5 * 2.966 / 5.514);
        var po = Ps(22.5 - 7.5 * 5.466 / 5.514);
        var gm = 2e-10 * ((pi - pm) / 75.45 - (pm - po) / .1);
        var go = 2e-10 * ((pm - po) / .1 - (po - pe) / 12);
        var timeToDryMidpoint = 1 / -gm;
        var laterOuterRate = 2e-10 * ((pi - po) / 75.55 - (po - pe) / 12);
        var independentEnd = go * timeToDryMidpoint + laterOuterRate * (30 * 86400 - timeToDryMidpoint);
        Assert.InRange(timeToDryMidpoint, 0, 30 * 86400);
        Assert.InRange(result.Months[0].EndStoredMassKgM2 - independentEnd, -1e-10, 1e-10);
        Assert.Equal(0, result.Months[0].WetInterfaces.Single(x => x.NodeIndex == result.InitiallyWettedNode).EndMassKgM2);
        Assert.Contains(result.Months[0].WetInterfaces, x => x.NodeIndex != result.InitiallyWettedNode && x.EndMassKgM2 > .9);
        Assert.True(result.Months[0].Subperiods > 1);
        output.WriteLine($"Annex D elapsed months={result.DryingTimeMonths}; exact event-split first-month water={result.Months[0].EndStoredMassKgM2:R}; whole-month table value is not substituted for the exact within-month inventory.");
        Assert.All(result.Months, m => Assert.InRange(m.MassConservationErrorKgM2, -1e-8, 1e-8));
    }
    [Fact]
    public void A_failed_annual_baseline_cannot_be_presented_as_an_applicable_excess_water_drying_test()
        => Assert.Throws<DomainException>(() => Iso13788Balance.Dry(new(Reference(true), 2, 9, "Reviewed synthetic input")));
}
