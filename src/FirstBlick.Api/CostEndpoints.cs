using System.Data;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record SaveCostForecastRequest(CostAnalysisInput Input, string ExpectedReportDigest, string Reason);

public static class CostEndpoints
{
    private const int HistoryLimit = 500;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/orders/{id:guid}/cost-analysis", async (Guid id, CostAnalysisInput input, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await using var tx = await c.Db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await c.Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({c.Scope.TenantId.ToString()},0))", ct);
            await Authorize(c, false, ct);
            var order = await c.Current<Order>(id, ct, false);
            var report = await Evaluate(c, order, input, ct);
            await tx.CommitAsync(ct);
            c.Http.Response.Headers.CacheControl = "no-store, private"; c.Http.Response.Headers.Pragma = "no-cache";
            return Results.Ok(new { capturedAt = DateTimeOffset.UtcNow, report,
                scope = "Current recorded sources of this project; cost rates, supplier allocations, source completeness and remaining costs are explicit reviewed inputs. This does not post financial transactions." });
        }).WithTags("reports");

        api.MapPost("/orders/{id:guid}/cost-forecasts", (Guid id, SaveCostForecastRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("cost-forecasts", "costforecast.save", body, async ct =>
            {
                Guard.Require(body.Input is not null, "invalid_forecast_input", "An explicit cost basis is required.");
                Guard.Text(body.Reason, "Forecast justification", 2000);
                await Authorize(c, true, ct);
                var order = await c.Current<Order>(id, ct, false);
                var report = await Evaluate(c, order, body.Input, ct);
                Guard.Require(report.Digest == body.ExpectedReportDigest,
                    "stale_cost_report", "The current source and cost basis differs from the reviewed report. Recalculate before retaining a forecast.");
                var previous = (await History(c, order.Id, ct)).LastOrDefault();
                var saved = new CostForecast
                {
                    TenantId = c.Scope.TenantId, OrderId = order.Id, ProjectId = order.ProjectId,
                    Version = (previous?.Version ?? 0) + 1, PreviousForecastId = previous?.Id,
                    CapturedByUserId = c.Scope.UserId, CapturedAt = DateTimeOffset.UtcNow,
                    Reason = body.Reason.Trim(), Report = report
                };
                saved.Digest = saved.CalculateDigest();
                return await c.SaveNew(saved, "costforecast.save", ct);
            })).Produces<CostForecast>().WithTags("reports");

        api.MapGet("/orders/{id:guid}/cost-forecasts", async (Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await Authorize(c, false, ct);
            var order = await c.Current<Order>(id, ct, false);
            var versions = await History(c, order.Id, ct);
            return Results.Ok(new { items = versions.OrderByDescending(x => x.Version).Select(x => new
            {
                x.Id, x.Version, x.CapturedAt, x.CapturedByUserId, x.PreviousForecastId, x.Reason, x.Digest,
                reportDigest = x.Report.Digest, x.Report.ActualCost, x.Report.ForecastCost,
                x.Report.ForecastMargin, missingCount = x.Report.Missing.Count
            }).ToArray(), total = versions.Count });
        }).WithTags("reports");

        api.MapGet("/cost-forecasts/{id:guid}", async (Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await Authorize(c, false, ct);
            var forecast = await c.Current<CostForecast>(id, ct, false);
            forecast.Validate();
            return Results.Ok(forecast);
        }).Produces<CostForecast>().WithTags("reports");
    }

    private static async Task Authorize(BusinessCommandServices c, bool write, CancellationToken ct)
    {
        await c.Commands.Authorize("cost-forecasts", write, ct);
        Guard.Require(c.Scope.Role is BusinessRole.ManagingDirector or BusinessRole.Office, "forbidden",
            "Individual cost assessments are available only to business management and office staff.");
    }

    private static async Task<List<CostForecast>> History(BusinessCommandServices c, Guid orderId, CancellationToken ct)
    {
        var tenant = c.Scope.TenantId;
        var ids = c.Db.Links.Where(x => x.TenantId == tenant && x.SourceKind == "cost-forecasts"
            && x.TargetKind == "orders" && x.TargetId == orderId).Select(x => x.SourceId);
        var rows = await c.Db.Entities.AsNoTracking()
            .Where(x => x.TenantId == tenant && x.Kind == "cost-forecasts" && !x.Archived && ids.Contains(x.Id))
            .OrderBy(x => x.Id).Take(HistoryLimit + 1).ToListAsync(ct);
        Guard.Require(rows.Count <= HistoryLimit, "forecast_history_budget",
            "The retained forecast history exceeds the supported maximum; it was not truncated.");
        var versions = new List<CostForecast>(rows.Count);
        foreach (var row in rows)
        {
            var value = AppJson.Read<CostForecast>(row.Data);
            Guard.Require(row.TenantId == value.TenantId && row.Kind == "cost-forecasts"
                && row.Id == value.Id && row.Revision == value.Revision && row.Archived == value.Archived
                && value.TenantId == tenant && !value.Archived && value.OrderId == orderId,
                "forecast_source_metadata", "Stored cost forecast identity or order linkage has changed.");
            value.Validate(); versions.Add(value);
        }
        versions.Sort((left, right) => left.Version.CompareTo(right.Version));
        for (var i = 0; i < versions.Count; i++)
            Guard.Require(versions[i].Version == i + 1 && versions[i].PreviousForecastId == (i == 0 ? null : versions[i - 1].Id),
                "forecast_history_changed", "The retained cost forecast versions are incomplete or have an invalid predecessor chain.");
        return versions;
    }

    private static async Task<CostAnalysisResult> Evaluate(BusinessCommandServices c, Order order, CostAnalysisInput input, CancellationToken ct)
    {
        Guard.Require(input is not null, "invalid_forecast_input", "An explicit cost basis is required.");
        var time = await Linked<TimeEntry>("projects", order.ProjectId, c.Db, ct);
        var stock = await Linked<StockMovement>("projects", order.ProjectId, c.Db, ct);
        var changes = await Linked<ChangeOrder>("orders", order.Id, c.Db, ct);
        var purchases = await Linked<PurchaseOrder>("projects", order.ProjectId, c.Db, ct);
        var purchaseIds = purchases.Select(x => x.Id).ToArray();
        Guard.Items(input.SupplierCosts, nameof(input.SupplierCosts), 5000);

        // Bind the current report to immutable, approved project-cost postings. Manual
        // allocations remain previews only and must not override amounts already booked
        // anywhere in the tenant for the same supplier invoice.
        var projectJournal = await Linked<SupplierCostAllocation>("projects", order.ProjectId, c.Db, ct);
        Guard.Require(projectJournal.All(x => x.TenantId == order.TenantId && x.ProjectId == order.ProjectId),
            "cost_allocation_metadata", "Project cost journal references do not match their tenant and project.");
        var projectActive = SupplierCostAllocationRules.Active(projectJournal);
        var postedInvoiceIds = projectActive.Select(x => x.SupplierInvoiceId).Distinct().ToArray();
        var explicitInvoiceIds = input.SupplierCosts.Select(x => x.SupplierInvoiceId).ToArray();
        var linkedInvoiceIds = c.Db.Links.Where(x => x.SourceKind == "supplier-invoices" && x.TargetKind == "purchase-orders"
            && purchaseIds.Contains(x.TargetId)).Select(x => x.SourceId);
        var rows = await c.Db.Entities.Where(x => x.Kind == "supplier-invoices" && !x.Archived
                && (linkedInvoiceIds.Contains(x.Id) || explicitInvoiceIds.Contains(x.Id) || postedInvoiceIds.Contains(x.Id)))
            .OrderBy(x => x.Id).Take(5001).Select(x => x.Data).ToListAsync(ct);
        Guard.Require(rows.Count <= 5000, "cost_report_budget", "This source report exceeds 5,000 supplier documents.");
        var invoices = rows.Select(AppJson.Read<SupplierInvoice>).ToList();
        Guard.Require(invoices.All(x => !x.PurchaseOrderId.HasValue || purchaseIds.Contains(x.PurchaseOrderId.Value)
                || postedInvoiceIds.Contains(x.Id)),
            "wrong_cost_project", "A supplier invoice belongs to another project's purchase order without a posted project allocation.");
        Guard.Require(projectActive.All(x => invoices.Any(i => i.Id == x.SupplierInvoiceId
                && i.TenantId == x.TenantId && i.Status == SupplierInvoiceStatus.Approved
                && i.ApprovedDigest == x.SourceDigest)),
            "cost_allocation_source_changed", "A posted cost allocation does not match an approved supplier invoice.");

        var invoiceIds = invoices.Select(x => x.Id).ToArray();
        var allocationIds = c.Db.Links.Where(x => x.SourceKind == "supplier-cost-allocations"
            && x.TargetKind == "supplier-invoices" && invoiceIds.Contains(x.TargetId)).Select(x => x.SourceId);
        var ledgerRows = await c.Db.Entities.Where(x => x.Kind == "supplier-cost-allocations"
            && !x.Archived && allocationIds.Contains(x.Id)).OrderBy(x => x.Id)
            .Take(5001).Select(x => x.Data).ToListAsync(ct);
        Guard.Require(ledgerRows.Count <= 5000, "cost_report_budget",
            "A supplier document's project distribution exceeds the supported evidence budget.");
        var allJournal = ledgerRows.Select(AppJson.Read<SupplierCostAllocation>).ToList();
        Guard.Require(allJournal.All(x => x.TenantId == order.TenantId && invoiceIds.Contains(x.SupplierInvoiceId)),
            "cost_allocation_metadata", "The supplier allocation journal does not match this business and these sources.");
        var allActive = SupplierCostAllocationRules.Active(allJournal);
        var bookedIds = allActive.Select(x => x.SupplierInvoiceId).ToHashSet();
        Guard.Require(!input.SupplierCosts.Any(x => bookedIds.Contains(x.SupplierInvoiceId)),
            "cost_allocation_posted", "A booked supplier amount cannot be overridden with a manual project-cost estimate.");

        var bookedBases = projectActive.GroupBy(x => x.SupplierInvoiceId).Select(group =>
            new SupplierCostBasis(group.Key, SupplierCostTreatment.ExternalService, group.Sum(x => x.NetAmount),
                "Posted allocation journal " + ContentDigest.Of(group.OrderBy(x => x.Id)
                    .Select(x => new { x.Id, x.PostedDigest }).ToArray()))).ToList();
        var evaluatedInput = input with { SupplierCosts = input.SupplierCosts.Concat(bookedBases).ToList() };
        return ProjectCosting.Calculate(order, changes, time, stock, invoices, evaluatedInput);
    }

    private static async Task<List<T>> Linked<T>(string targetKind, Guid id, FirstBlickDbContext db, CancellationToken ct) where T : TenantEntity
    {
        var kind = BusinessRegistry.KindOf<T>();
        var ids = db.Links.Where(x => x.SourceKind == kind && x.TargetKind == targetKind && x.TargetId == id).Select(x => x.SourceId);
        var rows = await db.Entities.Where(x => x.Kind == kind && !x.Archived && ids.Contains(x.Id)).OrderBy(x => x.Id).Take(5001).Select(x => x.Data).ToListAsync(ct);
        Guard.Require(rows.Count <= 5000, "cost_report_budget", "This source report exceeds 5,000 records in one category.");
        return rows.Select(AppJson.Read<T>).ToList();
    }
}
