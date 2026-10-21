using FirstBlick.Core.Business;
using static FirstBlick.Core.Tests.Business.BusinessFixture;

namespace FirstBlick.Core.Tests.Business;

public sealed class CostAnalysisTests
{
    [Fact]
    public void Source_costs_include_net_time_consumption_returns_and_explicit_subcontract_allocations_once()
    {
        var order = Order(1000); var time = Time(order); var consumed = Movement(order, -10, 8); var returned = Movement(order, 2, 8, StockMovementKind.Return);
        var receipt = Movement(order, 50, 8, StockMovementKind.Receipt); var supplier = SupplierBill(200);
        var change = new ChangeOrder { TenantId = Tenant, OrderId = order.Id, Title = "Additional work", Reason = "Reviewed scope extension", Lines = [new() { Description = "Extra", UnitMaterialCost = 100 }] };
        CommercialWorkflow.AcceptChange(change, DateTimeOffset.UtcNow);
        var input = new CostAnalysisInput([new(time.Id, 40, "Recorded project labor cost basis")], [new(supplier.Id, SupplierCostTreatment.ExternalService, 100, "Explicit 50% net allocation")], [], 100, "Reviewed remaining work", "All recorded costs through this review reconciled");
        var result = ProjectCosting.Calculate(order, [change], [time], [consumed, returned, receipt], [supplier], input);
        Assert.Equal(1100m, result.ContractNet); Assert.Equal(1100m, result.PlannedCost);
        Assert.Equal(464m, result.KnownActualCost); Assert.Equal(464m, result.ActualCost); Assert.Equal(564m, result.ForecastCost); Assert.Equal(536m, result.ForecastMargin);
        Assert.Equal(48.727273m, result.MarginPercent); Assert.Equal(95.035461m, result.MarkupPercent);
        Assert.Equal(300m, Assert.Single(result.Sources, x => x.Kind == "time-entries").NetCost); Assert.DoesNotContain(result.Sources, x => x.Id == receipt.Id);
        Assert.Equal(4, result.Sources.Count); Assert.Empty(result.Missing); Assert.Equal(64, result.Digest.Length);
        Assert.Equal(2, result.ContractSources.Count); Assert.Equal(result.ContractNet, result.ContractSources.Sum(x => x.ContractNet));
        Assert.Equal(result.PlannedCost, result.ContractSources.Sum(x => x.PlannedCost));
        Assert.Contains(result.ContractSources, x => x.Id == change.Id && x.Revision == change.Revision && x.Digest == ContentDigest.Of(change));
    }

    [Fact]
    public void Missing_labor_material_classification_and_forecast_stay_unknown_instead_of_zero_cost()
    {
        var order = Order(); var time = Time(order); var consumed = Movement(order, -2, 0); var supplier = SupplierBill(100);
        var result = ProjectCosting.Calculate(order, [], [time], [consumed], [supplier], Empty());
        Assert.Equal(0, result.KnownActualCost); Assert.Null(result.ActualCost); Assert.Null(result.ForecastCost); Assert.Null(result.ForecastMargin); Assert.Null(result.MarginPercent);
        Assert.Contains(result.Missing, x => x.Contains("hourly_cost_missing")); Assert.Contains(result.Missing, x => x.Contains("material_cost_missing"));
        Assert.Contains(result.Missing, x => x.Contains("supplier_cost_treatment_missing")); Assert.Contains("remaining_cost_forecast_missing", result.Missing);
        Assert.All(result.Sources, x => Assert.Null(x.NetCost));
    }

    [Fact]
    public void Explicit_zero_cost_and_stock_classification_do_not_double_count_supplier_materials()
    {
        var order = Order(); var movement = Movement(order, -2, 0); var invoice = SupplierBill(100);
        var input = Empty() with { ZeroStockCosts = [new(movement.Id, "Documented no-charge replacement")], SupplierCosts = [new(invoice.Id, SupplierCostTreatment.StockValuation, 0, "Material cost is represented only by posted project consumption")],
            RemainingForecastCost = 0, ForecastEvidence = "No remaining work", RecordedCostsCoverage = "All actual sources reconciled" };
        var result = ProjectCosting.Calculate(order, [], [], [movement], [invoice], input);
        Assert.Equal(0m, result.ActualCost); Assert.Equal(100m, result.ForecastMargin); Assert.Equal(100m, result.MarginPercent); Assert.Null(result.MarkupPercent);
        Assert.Equal(2, result.Sources.Count); Assert.Empty(result.Missing);
    }

    [Fact]
    public void Unapproved_time_is_not_made_cost_complete_by_supplying_an_hourly_rate()
    {
        var order = Order(); var time = Time(order); time.Approved = false;
        var input = Empty() with { TimeCosts = [new(time.Id, 40, "Recorded hourly cost")], RecordedCostsCoverage = "Reviewed source set" };
        var result = ProjectCosting.Calculate(order, [], [time], [], [], input);
        Assert.Null(result.ActualCost); Assert.Null(Assert.Single(result.Sources).NetCost); Assert.Contains(result.Missing, x => x.Contains("time_not_approved"));
    }

    [Fact]
    public void Time_cost_uses_exact_elapsed_ticks_before_final_money_rounding()
    {
        var order = Order(); var time = Time(order); time.End = time.Start.AddTicks(1800); time.Breaks = [];
        var input = Empty() with { TimeCosts = [new(time.Id, 100000m, "Explicit test hourly basis")], RecordedCostsCoverage = "All recorded sources reviewed" };
        var result = ProjectCosting.Calculate(order, [], [time], [], [], input);
        Assert.Equal(.01m, result.ActualCost); // 0.00000005 hours × 100,000 = EUR 0.005, rounded once away from zero.
    }

    [Fact]
    public void Duplicate_unknown_foreign_or_excessive_source_valuations_are_rejected()
    {
        var order = Order(); var time = Time(order); var invoice = SupplierBill(100);
        Assert.Equal("duplicate_cost_source", Assert.Throws<DomainException>(() => ProjectCosting.Calculate(order, [], [time], [], [], Empty() with { TimeCosts = [new(time.Id, 40, "A"), new(time.Id, 50, "B")] })).Code);
        Assert.Equal("unknown_cost_source", Assert.Throws<DomainException>(() => ProjectCosting.Calculate(order, [], [], [], [], Empty() with { TimeCosts = [new(Guid.NewGuid(), 40, "Unknown")] })).Code);
        Assert.Equal("cost_allocation_exceeded", Assert.Throws<DomainException>(() => ProjectCosting.Calculate(order, [], [], [], [invoice], Empty() with { SupplierCosts = [new(invoice.Id, SupplierCostTreatment.ExternalService, 101, "Too much")] })).Code);
        Assert.Equal("cost_allocation_exceeded", Assert.Throws<DomainException>(() => ProjectCosting.Calculate(order, [], [], [], [invoice], Empty() with { SupplierCosts = [new(invoice.Id, SupplierCostTreatment.StockValuation, 100, "Double count")] })).Code);
        time.ProjectId = Guid.NewGuid();
        Assert.Equal("wrong_cost_project", Assert.Throws<DomainException>(() => ProjectCosting.Calculate(order, [], [time], [], [], Empty())).Code);
    }

    [Fact]
    public void A_coverage_declaration_and_forecast_are_separate_from_valued_source_totals()
    {
        var order = Order(); var material = Movement(order, -5, 8);
        var unknownCoverage = ProjectCosting.Calculate(order, [], [], [material], [], Empty() with { RemainingForecastCost = 20, ForecastEvidence = "Remaining material forecast" });
        Assert.Equal(40, unknownCoverage.KnownActualCost); Assert.Null(unknownCoverage.ActualCost); Assert.Null(unknownCoverage.ForecastCost);
        var known = ProjectCosting.Calculate(order, [], [], [material], [], Empty() with { RecordedCostsCoverage = "Recorded source set reconciled" });
        Assert.Equal(40m, known.ActualCost); Assert.Null(known.ForecastCost); Assert.Null(known.ForecastMargin); Assert.Contains("remaining_cost_forecast_missing", known.Missing);
    }

    internal static CostAnalysisInput Empty() => new([], [], [], null, "", "");
    private static TimeEntry Time(Order order) => new() { TenantId = Tenant, ProjectId = order.ProjectId, EmployeeId = Guid.NewGuid(), Activity = "Synthetic roofing work", Start = Now, End = Now.AddHours(8), Approved = true, Breaks = [new(Now.AddHours(4), Now.AddHours(4.5))] };
    private static StockMovement Movement(Order order, decimal quantity, decimal unitCost, StockMovementKind kind = StockMovementKind.Consumption)
    {
        var movement = new StockMovement { TenantId = Tenant, ProjectId = order.ProjectId, MaterialId = Guid.NewGuid(), Quantity = quantity, UnitCost = unitCost, Kind = kind, OccurredAt = Now, Reference = "Synthetic stock source" };
        movement.PostedDigest = InventoryRules.MovementDigest(movement); return movement;
    }
    private static SupplierInvoice SupplierBill(decimal net)
    {
        var invoice = new SupplierInvoice { TenantId = Tenant, SupplierId = Guid.NewGuid(), Number = "Synthetic source", Title = "Reviewed supplier work", SupplierName = "Synthetic supplier", InvoiceDate = new(2026, 10, 1), DueDate = new(2026, 10, 31),
            Net = net, Tax = Money.Round(net * .19m), Gross = net + Money.Round(net * .19m), Status = SupplierInvoiceStatus.Approved, ApprovedBy = Actor, ApprovedAt = Now };
        invoice.ApprovedDigest = PayablesWorkflow.InvoiceDigest(invoice); invoice.Validate(); return invoice;
    }
}
