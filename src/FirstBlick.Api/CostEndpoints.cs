using System.Data;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public static class CostEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/orders/{id:guid}/cost-analysis", async (Guid id, CostAnalysisInput input, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            // These source-level reports contain individual work-cost assumptions; the general order role is insufficient.
            await using var tx = await c.Db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            // Serialize against business commands, then read current authorization after any lock wait.
            await c.Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({c.Scope.TenantId.ToString()},0))", ct);
            await c.Commands.Authorize("orders", false, ct);
            Guard.Require(c.Scope.Role is BusinessRole.ManagingDirector or BusinessRole.Office, "forbidden", "Source-level cost analysis requires business management or office access.");
            var order = await c.Current<Order>(id, ct, false);
            var time = await Linked<TimeEntry>("projects", order.ProjectId, c.Db, ct);
            var stock = await Linked<StockMovement>("projects", order.ProjectId, c.Db, ct);
            var changes = await Linked<ChangeOrder>("orders", order.Id, c.Db, ct);
            var purchases = await Linked<PurchaseOrder>("projects", order.ProjectId, c.Db, ct); var purchaseIds = purchases.Select(x => x.Id).ToArray();
            Guard.Items(input.SupplierCosts, nameof(input.SupplierCosts), 5000);
            var explicitInvoiceIds = input.SupplierCosts.Select(x => x.SupplierInvoiceId).ToArray();
            var linkedInvoiceIds = c.Db.Links.Where(x => x.SourceKind == "supplier-invoices" && x.TargetKind == "purchase-orders" && purchaseIds.Contains(x.TargetId)).Select(x => x.SourceId);
            var rows = await c.Db.Entities.Where(x => x.Kind == "supplier-invoices" && !x.Archived && (linkedInvoiceIds.Contains(x.Id) || explicitInvoiceIds.Contains(x.Id)))
                .OrderBy(x => x.Id).Take(5001).Select(x => x.Data).ToListAsync(ct);
            Guard.Require(rows.Count <= 5000, "cost_report_budget", "This source report exceeds 5,000 supplier documents.");
            var invoices = rows.Select(AppJson.Read<SupplierInvoice>).ToList();
            Guard.Require(invoices.All(x => !x.PurchaseOrderId.HasValue || purchaseIds.Contains(x.PurchaseOrderId.Value)), "wrong_cost_project", "A selected supplier invoice belongs to another project's purchase order.");
            var result = ProjectCosting.Calculate(order, changes, time, stock, invoices, input);
            await tx.CommitAsync(ct);
            c.Http.Response.Headers.CacheControl = "no-store, private"; c.Http.Response.Headers.Pragma = "no-cache";
            return Results.Ok(new { capturedAt = DateTimeOffset.UtcNow, report = result,
                scope = "Current recorded sources of this project; cost rates, supplier allocations, source completeness and remaining costs are explicit reviewed inputs. This does not post financial transactions." });
        }).WithTags("reports");
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
