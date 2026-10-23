using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;

namespace FirstBlick.Api;

/// <summary>
/// Bounded, tenant-scoped financial source reads. Relationships are resolved through retained
/// foreign-key links, never by trusting mutable JSON status or a client-supplied tenant identifier.
/// All relevant source rows are verified before an invoice may be issued.
/// </summary>
public static class FinancialSources
{
    public static Task<List<BankTransaction>> ImportedBankRows(BusinessCommandServices c, string accountReference, IEnumerable<string> externalIds, CancellationToken ct) =>
        EntitySources.Read<BankTransaction>(c.Db, c.Scope.TenantId, ct, errorPrefix: "financial_source", uniqueKeys: externalIds.Distinct(StringComparer.Ordinal)
            .Select(id => EntityProjection.UniqueKey(new BankTransaction { AccountReference = accountReference, ExternalId = id })!).ToArray());

    public static Task<List<Invoice>> ForOrder(BusinessCommandServices c, Guid orderId, CancellationToken ct) =>
        Related<Invoice>(c, "orders", orderId, ct);

    public static Task<List<Invoice>> ForOriginalInvoice(BusinessCommandServices c, Guid originalInvoiceId, CancellationToken ct) =>
        Related<Invoice>(c, "invoices", originalInvoiceId, ct);

    public static Task<List<ChangeOrder>> ChangesForOrder(BusinessCommandServices c, Guid orderId, CancellationToken ct) =>
        Related<ChangeOrder>(c, "orders", orderId, ct);

    public static Task<List<PaymentAllocation>> AllocationsForInvoice(BusinessCommandServices c, Guid invoiceId, CancellationToken ct) =>
        Related<PaymentAllocation>(c, "invoices", invoiceId, ct);

    public static Task<List<SupplierInvoice>> CreditsForSupplierInvoice(BusinessCommandServices c, Guid invoiceId, CancellationToken ct) =>
        Related<SupplierInvoice>(c, "supplier-invoices", invoiceId, ct);

    public static Task<List<SupplierPaymentAllocation>> AllocationsForSupplierInvoice(BusinessCommandServices c, Guid invoiceId, CancellationToken ct) =>
        Related<SupplierPaymentAllocation>(c, "supplier-invoices", invoiceId, ct);

    public static Task<List<PaymentAllocation>> CustomerBankSources(BusinessCommandServices c, Guid bankId, CancellationToken ct, Guid? invoiceId = null) =>
        Read<PaymentAllocation>(c, invoiceId is { } id
            ? [BusinessRegistry.Reference<BankTransaction>(bankId), BusinessRegistry.Reference<Invoice>(id)]
            : [BusinessRegistry.Reference<BankTransaction>(bankId)], ct);

    public static Task<List<SupplierPaymentAllocation>> SupplierBankSources(BusinessCommandServices c, Guid bankId, CancellationToken ct, Guid? invoiceId = null) =>
        Read<SupplierPaymentAllocation>(c, invoiceId is { } id
            ? [BusinessRegistry.Reference<BankTransaction>(bankId), BusinessRegistry.Reference<SupplierInvoice>(id)]
            : [BusinessRegistry.Reference<BankTransaction>(bankId)], ct);

    private static Task<List<T>> Related<T>(BusinessCommandServices c, string targetKind, Guid targetId, CancellationToken ct)
        where T : TenantEntity => Read<T>(c, [new(targetKind, targetId)], ct);

    private static async Task<List<T>> Read<T>(BusinessCommandServices c, EntityReference[] targets, CancellationToken ct)
        where T : TenantEntity
    {
        var result = await EntitySources.Read<T>(c.Db, c.Scope.TenantId, ct, targets, errorPrefix: "financial_source");
        foreach (var value in result) value.Validate();
        return result;
    }
}
