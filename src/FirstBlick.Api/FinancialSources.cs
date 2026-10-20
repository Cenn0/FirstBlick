using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

/// <summary>
/// Bounded, tenant-scoped financial source reads. Relationships are resolved through retained
/// foreign-key links, never by trusting mutable JSON status or a client-supplied tenant identifier.
/// All relevant source rows are verified before an invoice may be issued.
/// </summary>
public static class FinancialSources
{
    private const int InvoiceHistoryLimit = 10_000;
    private const int ChangeHistoryLimit = 10_000;
    private const int AllocationLimit = 10_000;

    public static Task<List<Invoice>> ForOrder(BusinessCommandServices c, Guid orderId, CancellationToken ct) =>
        Related<Invoice>(c, "orders", orderId, InvoiceHistoryLimit, ct);

    public static Task<List<Invoice>> ForOriginalInvoice(BusinessCommandServices c, Guid originalInvoiceId, CancellationToken ct) =>
        Related<Invoice>(c, "invoices", originalInvoiceId, InvoiceHistoryLimit, ct);

    public static Task<List<ChangeOrder>> ChangesForOrder(BusinessCommandServices c, Guid orderId, CancellationToken ct) =>
        Related<ChangeOrder>(c, "orders", orderId, ChangeHistoryLimit, ct);

    public static Task<List<PaymentAllocation>> AllocationsForInvoice(BusinessCommandServices c, Guid invoiceId, CancellationToken ct) =>
        Related<PaymentAllocation>(c, "invoices", invoiceId, AllocationLimit, ct);

    private static async Task<List<T>> Related<T>(BusinessCommandServices c, string targetKind, Guid targetId, int maximum, CancellationToken ct)
        where T : TenantEntity
    {
        var tenantId = c.Scope.TenantId;
        var kind = BusinessRegistry.KindOf<T>();
        // Both tables carry EF tenant filters; the explicit tenant predicates also document
        // and enforce the intended join boundary even if a query-filter configuration changes.
        var sourceIds = c.Db.Links.Where(x => x.TenantId == tenantId && x.SourceKind == kind
            && x.TargetKind == targetKind && x.TargetId == targetId).Select(x => x.SourceId);
        var rows = await c.Db.Entities.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Kind == kind && !x.Archived && sourceIds.Contains(x.Id))
            .OrderBy(x => x.Id).Take(maximum + 1).ToListAsync(ct);

        // Never calculate on a truncated ledger: all preceding documents must contribute.
        Guard.Require(rows.Count <= maximum, "financial_source_budget",
            "The linked financial history exceeds the supported source budget; no partial result was used.");
        var result = new List<T>(rows.Count);
        foreach (var row in rows)
        {
            var value = AppJson.Read<T>(row.Data);
            Guard.Require(row.TenantId == tenantId && value.TenantId == tenantId && row.Kind == kind
                && row.Id == value.Id && row.Revision == value.Revision && !row.Archived && !value.Archived,
                "financial_source_metadata", "The linked financial source metadata does not match its stored body.");
            Guard.Require(value.References().Any(x => x.Kind == targetKind && x.Id == targetId),
                "financial_source_reference", "The linked financial source no longer refers to its retained target.");
            value.Validate();
            result.Add(value);
        }
        return result;
    }
}
