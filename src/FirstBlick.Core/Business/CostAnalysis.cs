namespace FirstBlick.Core.Business;

public sealed record TimeCostBasis(Guid TimeEntryId, decimal HourlyCost, string Evidence);
public enum SupplierCostTreatment { StockValuation, ExternalService }
public sealed record SupplierCostBasis(Guid SupplierInvoiceId, SupplierCostTreatment Treatment, decimal ProjectNet, string Evidence);
public sealed record ZeroStockCostBasis(Guid StockMovementId, string Evidence);
public sealed record CostAnalysisInput(IReadOnlyList<TimeCostBasis> TimeCosts, IReadOnlyList<SupplierCostBasis> SupplierCosts,
    IReadOnlyList<ZeroStockCostBasis> ZeroStockCosts, decimal? RemainingForecastCost, string ForecastEvidence, string RecordedCostsCoverage);
public sealed record CostSource(string Kind, Guid Id, long Revision, string Digest, string Title, decimal? NetCost, string Basis, string? Missing);
public sealed record ContractCostSource(string Kind, Guid Id, long Revision, string Digest, string Title, decimal ContractNet, decimal PlannedCost);
public sealed record CostAnalysisResult(Guid OrderId, Guid ProjectId, decimal ContractNet, decimal PlannedCost, decimal KnownActualCost,
    decimal? ActualCost, decimal? ForecastCost, decimal? ForecastMargin, decimal? MarginPercent, decimal? MarkupPercent,
    IReadOnlyList<ContractCostSource> ContractSources, IReadOnlyList<CostSource> Sources, IReadOnlyList<string> Missing, CostAnalysisInput ReviewedInputs, string MethodVersion, string Digest);

/// <summary>Reproducible cost report from source records and named cost evidence. No wage, tax or overhead rate is inferred.</summary>
public static class ProjectCosting
{
    public const string Version = "1.0.0";
    public static CostAnalysisResult Calculate(Order order, IReadOnlyList<ChangeOrder> changes, IReadOnlyList<TimeEntry> time,
        IReadOnlyList<StockMovement> movements, IReadOnlyList<SupplierInvoice> supplierInvoices, CostAnalysisInput input)
    {
        order.Validate(); Guard.Items(changes, nameof(changes), 5000); Guard.Items(time, nameof(time), 5000);
        Guard.Items(movements, nameof(movements), 5000); Guard.Items(supplierInvoices, nameof(supplierInvoices), 5000);
        Guard.Items(input.TimeCosts, nameof(input.TimeCosts), 5000); Guard.Items(input.SupplierCosts, nameof(input.SupplierCosts), 5000); Guard.Items(input.ZeroStockCosts, nameof(input.ZeroStockCosts), 5000);
        Guard.Text(input.RecordedCostsCoverage, nameof(input.RecordedCostsCoverage), 2000, false);
        Guard.Text(input.ForecastEvidence, nameof(input.ForecastEvidence), 2000, input.RemainingForecastCost.HasValue);
        if (input.RemainingForecastCost is { } remaining) Money.Amount(remaining, nameof(input.RemainingForecastCost));
        Unique(input.TimeCosts.Select(x => x.TimeEntryId)); Unique(input.SupplierCosts.Select(x => x.SupplierInvoiceId)); Unique(input.ZeroStockCosts.Select(x => x.StockMovementId));
        Guard.Require(time.All(x => x.TenantId == order.TenantId && x.ProjectId == order.ProjectId)
            && movements.All(x => x.TenantId == order.TenantId && x.ProjectId == order.ProjectId)
            && supplierInvoices.All(x => x.TenantId == order.TenantId), "wrong_cost_project", "Every cost source must belong to this business and the reviewed project context.");
        Guard.Require(input.TimeCosts.All(x => time.Any(t => t.Id == x.TimeEntryId)) && input.SupplierCosts.All(x => supplierInvoices.Any(i => i.Id == x.SupplierInvoiceId))
            && input.ZeroStockCosts.All(x => movements.Any(m => m.Id == x.StockMovementId && m.UnitCost == 0 && m.Kind is StockMovementKind.Consumption or StockMovementKind.Return)),
            "unknown_cost_source", "A cost valuation references an unavailable source or an inapplicable zero-cost movement.");
        foreach (var basis in input.TimeCosts) { Guard.Range(basis.HourlyCost, 0, 100_000, "Hourly cost"); Guard.Text(basis.Evidence, "Time cost evidence", 2000); }
        foreach (var basis in input.ZeroStockCosts) Guard.Text(basis.Evidence, "Zero material cost evidence", 2000);
        foreach (var basis in input.SupplierCosts)
        {
            Guard.Require(Enum.IsDefined(basis.Treatment), "invalid_cost_treatment", "The supplier-cost treatment is unknown.");
            Money.Amount(basis.ProjectNet, "Project net cost"); Guard.Text(basis.Evidence, "Supplier allocation evidence", 2000);
            var invoice = supplierInvoices.Single(x => x.Id == basis.SupplierInvoiceId);
            Guard.Require(basis.ProjectNet <= invoice.Net && (basis.Treatment != SupplierCostTreatment.StockValuation || basis.ProjectNet == 0),
                "cost_allocation_exceeded", "The allocation cannot exceed the source net amount. Material already valued in stock must not be counted again.");
        }
        var sources = new List<CostSource>(); var missing = new List<string>();
        foreach (var entry in time.OrderBy(x => x.Start).ThenBy(x => x.Id))
        {
            entry.Validate(); var basis = input.TimeCosts.SingleOrDefault(x => x.TimeEntryId == entry.Id);
            var problem = !entry.Approved ? "time_not_approved" : basis is null ? "hourly_cost_missing" : null;
            var ticks = (entry.End - entry.Start).Ticks - entry.Breaks.Sum(x => (x.End - x.Start).Ticks);
            decimal? cost = problem is null ? Money.Round((decimal)ticks / TimeSpan.TicksPerHour * basis!.HourlyCost) : null;
            sources.Add(new("time-entries", entry.Id, entry.Revision, ContentDigest.Of(entry), entry.Activity, cost, basis?.Evidence ?? "", problem));
        }
        foreach (var movement in movements.Where(x => x.Kind is StockMovementKind.Consumption or StockMovementKind.Return).OrderBy(x => x.OccurredAt).ThenBy(x => x.Id))
        {
            movement.Validate(); var zero = input.ZeroStockCosts.SingleOrDefault(x => x.StockMovementId == movement.Id);
            var problem = movement.PostedDigest == "" ? "movement_not_posted" : movement.UnitCost == 0 && zero is null ? "material_cost_missing" : null;
            decimal? cost = problem is null ? Money.Round(-movement.Quantity * movement.UnitCost) : null;
            sources.Add(new("stock-movements", movement.Id, movement.Revision, ContentDigest.Of(movement), movement.Reference, cost,
                zero?.Evidence ?? "Recorded unit cost of posted consumption/return; receipts are not counted again", problem));
        }
        foreach (var invoice in supplierInvoices.OrderBy(x => x.InvoiceDate).ThenBy(x => x.Id))
        {
            invoice.Validate(); var basis = input.SupplierCosts.SingleOrDefault(x => x.SupplierInvoiceId == invoice.Id);
            var problem = invoice.Status != SupplierInvoiceStatus.Approved ? "supplier_invoice_not_approved" : basis is null ? "supplier_cost_treatment_missing" : null;
            decimal? cost = problem is null ? basis!.ProjectNet * (invoice.Kind == SupplierInvoiceKind.CreditNote ? -1 : 1) : null;
            sources.Add(new("supplier-invoices", invoice.Id, invoice.Revision, ContentDigest.Of(invoice), invoice.Number, cost, basis?.Evidence ?? "", problem));
        }
        if (string.IsNullOrWhiteSpace(input.RecordedCostsCoverage)) missing.Add("recorded_cost_coverage_not_confirmed");
        missing.AddRange(sources.Where(x => x.Missing is not null).Select(x => $"{x.Kind}/{x.Id}:{x.Missing}"));
        var known = sources.Sum(x => x.NetCost ?? 0m); Money.Amount(known, "Known costs", true);
        if (known < 0) missing.Add("negative_project_cost_requires_reconciliation");
        decimal? actual = missing.Count == 0 ? known : null;
        var baseline = CommercialWorkflow.CompareCosts(order, changes, 0, 0);
        var contractSources = new List<ContractCostSource> { new("orders", order.Id, order.Revision, ContentDigest.Of(order), order.Title, order.Contract!.Net, Planned(order.Contract)) };
        contractSources.AddRange(changes.Where(x => x.TenantId == order.TenantId && x.OrderId == order.Id && x.Status == ChangeOrderStatus.Accepted).OrderBy(x => x.Id)
            .Select(x => new ContractCostSource("change-orders", x.Id, x.Revision, ContentDigest.Of(x), x.Title, x.Snapshot!.Net, Planned(x.Snapshot))));
        decimal? forecast = actual is { } actualCost && input.RemainingForecastCost is { } forecastRemainder ? actualCost + forecastRemainder : null;
        if (!input.RemainingForecastCost.HasValue) missing.Add("remaining_cost_forecast_missing");
        decimal? margin = forecast.HasValue ? baseline.ContractNet - forecast.Value : null;
        decimal? marginPercent = margin.HasValue && baseline.ContractNet != 0 ? decimal.Round(margin.Value / baseline.ContractNet * 100, 6, MidpointRounding.AwayFromZero) : null;
        decimal? markupPercent = margin.HasValue && forecast > 0 ? decimal.Round(margin.Value / forecast.Value * 100, 6, MidpointRounding.AwayFromZero) : null;
        var result = new CostAnalysisResult(order.Id, order.ProjectId, baseline.ContractNet, baseline.PlannedCost, known, actual, forecast, margin,
            marginPercent, markupPercent, contractSources, sources, missing, input, Version, "");
        return result with { Digest = ContentDigest.Of(result) };
    }
    private static decimal Planned(QuoteSnapshot snapshot) => snapshot.Lines.Sum(x => Money.Round(x.Inputs.Quantity * x.UnitCost));
    private static void Unique(IEnumerable<Guid> ids)
    { var list = ids.ToList(); foreach (var id in list) Guard.Id(id, "Cost source"); Guard.Require(list.Distinct().Count() == list.Count, "duplicate_cost_source", "A source may be valued only once in a report."); }
}
