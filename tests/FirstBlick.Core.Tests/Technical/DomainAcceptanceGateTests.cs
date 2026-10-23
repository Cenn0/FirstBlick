using FirstBlick.Core.Business;
using FirstBlick.Core.Technical;
using FirstBlick.Core.Tests.Business;

namespace FirstBlick.Core.Tests.Technical;

/// <summary>
/// DP-08-02/03 and DP-11-02/03/06/07/08: synthetic, independently specified
/// acceptance oracles. A correct number is not evidence of a real-world
/// structural, hydraulic, tax or normative approval.
/// </summary>
public sealed class DomainAcceptanceGateTests
{
    private static readonly DateOnly Day = new(2026, 10, 23);

    [Theory]
    [InlineData("WIND-01", 4d, 2d, -1d, 1d, .5d, -2.5d, -10d, 2.5d)]
    [InlineData("WIND-02", 6d, 1.5d, 1.2d, 1d, -.2d, 2d, 12d, 0d)]
    [InlineData("WIND-03", 2d, 1d, 0d, 1d, 0d, 0d, 0d, 0d)]
    public void Wind_cases_preserve_directional_force_and_suction(string caseId, double area, double externalPeak,
        double externalCoefficient, double internalPeak, double internalCoefficient,
        double expectedNet, double expectedForce, double expectedSuction)
    {
        var result = RoofWind.Calculate(new($"Synthetic reference {caseId}",
            [new("Zone", area, externalPeak, externalCoefficient, internalPeak, internalCoefficient,
                "Independent scenario inputs")]));
        var actual = Assert.Single(result.Zones);
        Assert.Equal(expectedNet, actual.NetInwardPressureKNm2, 10);
        Assert.Equal(expectedForce, actual.NormalForceKN, 10);
        Assert.Equal(expectedSuction, actual.OutwardSuctionKNm2, 10);
        Assert.False(result.StructuralDesignApproved);
        Assert.False(result.Provenance.IsNormative);
    }

    [Fact]
    public void Snow_case_uses_two_nonoverlapping_horizontal_zones_and_keeps_zero_shape_zero()
    {
        // Synthetic independent products: 2 * .8 * .9 = 1.44 kN/m2;
        // forces = 50 * 1.44 = 72 kN, 25 * .72 = 18 kN, 10 * 0 = 0 kN.
        var result = RoofSnow.Calculate(new("Synthetic simultaneous snow load", 2, .8, .9,
            [new("Roof A", 50, 1, "Synthetic plan projection"),
             new("Roof B", 25, .5, "Synthetic plan projection"),
             new("Roof C", 10, 0, "Synthetic plan projection")]));
        Assert.Equal(72d, result.Zones[0].VerticalForceKN, 10);
        Assert.Equal(18d, result.Zones[1].VerticalForceKN, 10);
        Assert.Equal(0d, result.Zones[2].VerticalForceKN, 10);
        Assert.Equal(90d, result.TotalVerticalForceKN, 10);
        Assert.False(result.StructuralDesignApproved);
        Assert.False(result.Provenance.IsNormative);
    }

    [Theory]
    [InlineData("DRAIN-01", EmergencyDrainageScenario.ResidualAfterPrimaryDesignFlow, 1.5d, 3.5d, .5d, 0d)]
    [InlineData("DRAIN-02", EmergencyDrainageScenario.PrimaryUnavailable, 0d, 5d, .5d, 1d)]
    public void Drainage_cases_use_scenario_bound_credit_without_inventing_hydraulic_approval(
        string caseId, EmergencyDrainageScenario scenario, double expectedPrimaryCredit,
        double expectedEmergencyDemand, double expectedPrimaryDeficit, double expectedEmergencyDeficit)
    {
        // 100 m2 = .01 ha; 200 L/(s ha) -> 2 L/s; 500 L/(s ha) -> 5 L/s.
        var result = RoofDrainage.Calculate(new($"Synthetic rainfall reference {caseId}", 200, 500, scenario,
            [new("Independent catchment", 100, 1, 1.5, 4, "Synthetic effective plan area",
                "Synthetic verified-capacity assumption")]));
        var actual = Assert.Single(result.Catchments);
        Assert.Equal(2d, actual.PrimaryDemandLs, 10);
        Assert.Equal(5d, actual.StormInflowLs, 10);
        Assert.Equal(expectedPrimaryCredit, actual.CreditedPrimaryFlowLs, 10);
        Assert.Equal(expectedEmergencyDemand, actual.EmergencyDemandLs, 10);
        Assert.Equal(expectedPrimaryDeficit, actual.PrimaryDeficitLs, 10);
        Assert.Equal(expectedEmergencyDeficit, actual.EmergencyDeficitLs, 10);
        Assert.False(result.HydraulicDesignApproved);
        Assert.False(result.Provenance.IsNormative);
    }

    [Fact]
    public void A_large_capacity_on_one_catchment_never_eliminates_a_deficit_on_another()
    {
        var result = RoofDrainage.Calculate(new("Synthetic disjoint runoff areas", 200, 500,
            EmergencyDrainageScenario.ResidualAfterPrimaryDesignFlow,
            [new("Oversized local outlet", 100, 1, 100, 100, "Area A", "Capacities A"),
             new("Separate insufficient outlets", 200, .8, 10, 3, "Area B", "Capacities B")]));
        Assert.Equal(2, result.Catchments.Count);
        Assert.Equal(2d, result.Catchments[0].CreditedPrimaryFlowLs, 10);
        Assert.Equal(3d, result.Catchments[0].EmergencyDemandLs, 10);
        Assert.Equal(3.2d, result.Catchments[1].CreditedPrimaryFlowLs, 10);
        Assert.Equal(6.8d, result.Catchments[1].EmergencyDemandLs, 10);
        Assert.Equal(3.8d, result.Catchments[1].EmergencyDeficitLs, 10);
        Assert.False(result.HydraulicDesignApproved);
    }

    [Theory]
    [InlineData("THERM-01", .1d, 2.17d)]
    [InlineData("THERM-02", .2d, 4.17d)]
    public void Homogeneous_layer_has_hand_specified_series_resistance_and_heat_flow(
        string caseId, double thickness, double expectedResistance)
    {
        // lambda = .05 W/(m K), Rsi+Rse = .17 m2 K/W, delta-T = 30 K.
        var result = ThermalCalculator.Calculate(new(20, -10, .13, .04,
            [new($"Synthetic material {caseId}", thickness, .05, "Declared synthetic conductivity")]));
        Assert.Equal(expectedResistance, result.TotalResistanceM2KW, 11);
        Assert.Equal(1d / expectedResistance, result.UValueWm2K, 11);
        Assert.Equal(30d / expectedResistance, result.HeatFluxWm2, 10);
        Assert.False(result.Provenance.IsNormative);
    }

    [Fact]
    public void Finance_case_reconciles_partials_a_specific_credit_and_final_invoice_to_frozen_contract_cents()
    {
        // Contract: 3 * EUR 0.07 = EUR 0.21; 19% tax -> EUR 0.04.
        // Independent expected journal [net cents, tax cents]:
        // P1 [7,1], P2 [7,2], C1 [-7,-1], F1 [14,2].
        var order = BusinessFixture.Order(.07m, 3m, 19m);
        var first = Issue(order, InvoiceKind.Progress, 1m, [], "P1");
        var second = Issue(order, InvoiceKind.Progress, 1m, [first], "P2");
        var originalDigest = first.IssuedDigest;
        var credit = InvoiceWorkflow.DraftCreditNote(first, "Full correction of first advance");
        credit.DueDate = Day;
        credit.Lines[0].Quantity = 1m;
        InvoiceWorkflow.IssueCreditNote(credit, first, [], "C1", Day);
        var final = Issue(order, InvoiceKind.Final, 2m, [first, second, credit], "F1");
        Assert.Equal((.07m, .01m), (first.Snapshot!.Net, first.Snapshot.Tax));
        Assert.Equal((.07m, .02m), (second.Snapshot!.Net, second.Snapshot.Tax));
        Assert.Equal((.07m, .01m), (credit.Snapshot!.Net, credit.Snapshot.Tax));
        Assert.Equal((.14m, .02m, .16m), (final.Snapshot!.Net, final.Snapshot.Tax, final.Snapshot.Gross));
        Assert.Equal((.21m, .04m, .25m),
            (first.Snapshot.Net + second.Snapshot.Net - credit.Snapshot.Net + final.Snapshot.Net,
             first.Snapshot.Tax + second.Snapshot.Tax - credit.Snapshot.Tax + final.Snapshot.Tax,
             first.Snapshot.Gross + second.Snapshot.Gross - credit.Snapshot.Gross + final.Snapshot.Gross));
        Assert.Equal(order.Contract!.Net, .21m);
        Assert.Equal(order.Contract.Tax, .04m);
        Assert.Equal(originalDigest, first.IssuedDigest);
        Assert.Equal(originalDigest, credit.Calculation!.Sources.Single().DocumentDigest);
        first.Validate(); second.Validate(); credit.Validate(); final.Validate();
    }

    [Fact]
    public void Missing_sources_or_duplicate_zone_names_fail_closed_before_any_approval()
    {
        Assert.Throws<DomainException>(() => RoofWind.Calculate(new("Synthetic wind", [
            new("Same", 1, 1, 1, 1, 1, "A"),
            new(" same ", 1, 1, 1, 1, 1, "B")
        ])));
        Assert.ThrowsAny<ArgumentException>(() => RoofSnow.Calculate(new(" ", 1, 1, 1,
            [new("Roof", 10, 1, "Synthetic")])));
        Assert.ThrowsAny<ArgumentException>(() => RoofDrainage.Calculate(new("Synthetic rain", 200, 500,
            EmergencyDrainageScenario.PrimaryUnavailable,
            [new("Catchment", 100, 1, 1, 1, " ", "Synthetic capacity")])));
    }

    private static Invoice Issue(Order order, InvoiceKind kind, decimal quantity, Invoice[] previous, string number)
    {
        var invoice = BusinessFixture.Draft(order, kind);
        invoice.Lines = [invoice.Lines[0] with { Quantity = quantity }];
        InvoiceWorkflow.IssueForOrder(invoice, order, previous, [], number, Day);
        return invoice;
    }
}
