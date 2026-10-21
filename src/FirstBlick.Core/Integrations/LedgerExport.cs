using FirstBlick.Core.Business;

namespace FirstBlick.Core.Integrations;

public sealed record LedgerExportSources(IReadOnlyCollection<Invoice> Receivables, IReadOnlyCollection<SupplierInvoice> Payables,
    IReadOnlyCollection<BankTransaction> Bank, IReadOnlyCollection<PaymentAllocation> CustomerAllocations, IReadOnlyCollection<SupplierPaymentAllocation> SupplierAllocations)
{
    public void Validate(Guid tenantId)
    {
        Guard.Require((long)Receivables.Count + Payables.Count + Bank.Count + CustomerAllocations.Count + SupplierAllocations.Count <= ExportContract.MaximumSources,
            "export_budget", "The financial export and its linked evidence exceed 10,000 records.");
        ExportContract.ValidateSources(Receivables, tenantId); ExportContract.ValidateSources(Payables, tenantId); ExportContract.ValidateSources(Bank, tenantId);
        ExportContract.ValidateSources(CustomerAllocations, tenantId); ExportContract.ValidateSources(SupplierAllocations, tenantId);
        Guard.Require(Receivables.All(x => x.Status == InvoiceStatus.Issued) && Payables.All(x => x.Status == SupplierInvoiceStatus.Approved)
            && Bank.All(x => x.ImportDigest.Length > 0) && CustomerAllocations.All(x => x.PostedDigest.Length > 0) && SupplierAllocations.All(x => x.PostedDigest.Length > 0),
            "export_evidence", "Financial exports require issued, approved or sealed ledger evidence.");
    }
}

/// <summary>Own versioned reconciliation files. No DATEV account mapping, tax filing or historical-cutoff accounting is inferred.</summary>
public static class LedgerExport
{
    public const string AccountingSchema = "firstblick.accounting/1.0.0";
    public const string OpenItemsSchema = "firstblick.open-items/1.0.0";

    public static ExportBundle Accounting(ExportContext context, LedgerExportSources data)
    {
        context.Validate(); data.Validate(context.TenantId);
        var selection = Select(context.Window, data, false);
        return ExportContract.Bundle(AccountingSchema, context,
            "Invoice dates, bank booking dates, allocation/reversal/retention-release timestamps inside the inclusive local date window. Include every connected posted invoice, credit, bank entry and allocation as current reconciliation context; selection columns distinguish seed records from linked context.",
            Tables(context, data, selection));
    }

    public static ExportBundle OpenItems(ExportContext context, LedgerExportSources data)
    {
        context.Validate(); data.Validate(context.TenantId); var selection = Select(context.Window, data, true);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(context.CapturedAt, context.Window.Zone).DateTime);
        IEnumerable<IReadOnlyList<CsvCell>> Receivables()
        {
            foreach (var invoice in data.Receivables.Where(x => selection.SeedInvoices.Contains(x.Id)).OrderBy(x => x.IssueDate).ThenBy(x => x.Id))
            {
                var balance = PaymentWorkflow.Balance(invoice, data.CustomerAllocations, data.Receivables, today);
                if (balance.Outstanding == 0 && balance.CustomerCredit == 0) continue;
                var allocations = data.CustomerAllocations.Where(x => x.InvoiceId == invoice.Id).OrderBy(x => x.Id).ToArray();
                var credits = data.Receivables.Where(x => x.OriginalInvoiceId == invoice.Id).OrderBy(x => x.Id).ToArray();
                var digest = ContentDigest.Of(new { invoice = invoice.IssuedDigest, allocationStates = allocations.Select(ContentDigest.Of).ToArray(), credits = credits.Select(x => x.IssuedDigest).ToArray(), today });
                yield return ExportContract.Row(context, CsvCell.Id(invoice.Id), CsvCell.Text(invoice.Number), CsvCell.Id(invoice.CustomerId), CsvCell.Text(invoice.BuyerName),
                    CsvCell.Date(invoice.IssueDate), CsvCell.Date(invoice.DueDate), CsvCell.Date(today), CsvCell.Money(balance.Gross), CsvCell.Money(balance.Credits), CsvCell.Money(balance.Paid),
                    CsvCell.Money(balance.Discount), CsvCell.Money(balance.Retained), CsvCell.Money(balance.Outstanding), CsvCell.Money(balance.Collectible), CsvCell.Money(balance.CustomerCredit), CsvCell.Flag(balance.Overdue),
                    CsvCell.Code(invoice.IssuedDigest), CsvCell.Code(digest));
            }
        }
        IEnumerable<IReadOnlyList<CsvCell>> Payables()
        {
            foreach (var invoice in data.Payables.Where(x => selection.SeedPayables.Contains(x.Id)).OrderBy(x => x.InvoiceDate).ThenBy(x => x.Id))
            {
                var balance = PayablesWorkflow.Balance(invoice, data.SupplierAllocations, data.Payables, today);
                if (balance.Outstanding == 0 && balance.SupplierCredit == 0) continue;
                var allocations = data.SupplierAllocations.Where(x => x.SupplierInvoiceId == invoice.Id).OrderBy(x => x.Id).ToArray();
                var credits = data.Payables.Where(x => x.OriginalInvoiceId == invoice.Id).OrderBy(x => x.Id).ToArray();
                var digest = ContentDigest.Of(new { invoice = invoice.ApprovedDigest, allocationStates = allocations.Select(ContentDigest.Of).ToArray(), credits = credits.Select(x => x.ApprovedDigest).ToArray(), today });
                yield return ExportContract.Row(context, CsvCell.Id(invoice.Id), CsvCell.Text(invoice.Number), CsvCell.Id(invoice.SupplierId), CsvCell.Text(invoice.SupplierName),
                    CsvCell.Date(invoice.InvoiceDate), CsvCell.Date(invoice.DueDate), CsvCell.Date(today), CsvCell.Money(balance.Gross), CsvCell.Money(balance.Credits), CsvCell.Money(balance.Paid),
                    CsvCell.Money(balance.Discount), CsvCell.Money(balance.Outstanding), CsvCell.Money(balance.SupplierCredit), CsvCell.Flag(balance.Overdue), CsvCell.Code(invoice.ApprovedDigest), CsvCell.Code(digest));
            }
        }
        var files = Tables(context, data, selection).ToList();
        files.Add(ExportContract.Table(context, "open_receivables.csv", OpenItemsSchema,
            ["invoice_id", "number", "customer_id", "buyer_name", "issue_date", "due_date", "overdue_comparison_date_current", "gross_eur", "credits_eur", "paid_eur", "discount_eur",
                "retained_eur", "outstanding_eur", "collectible_eur", "customer_credit_eur", "overdue", "issued_digest", "balance_evidence_digest"], Receivables()));
        files.Add(ExportContract.Table(context, "open_payables.csv", OpenItemsSchema,
            ["supplier_invoice_id", "number", "supplier_id", "supplier_name", "invoice_date", "due_date", "overdue_comparison_date_current", "gross_eur", "credits_eur", "paid_eur",
                "discount_eur", "outstanding_eur", "supplier_credit_eur", "overdue", "approved_digest", "balance_evidence_digest"], Payables()));
        return ExportContract.Bundle(OpenItemsSchema, context,
            "The date window filters original invoice dates. Balances use the current complete posted state at capture, including later credits, refunds, reversals and retention releases. Settled originals are omitted from open_* tables; linked CSV evidence may include them. This is not a historical as-of balance.", files);
    }

    private static IEnumerable<CsvArtifact> Tables(ExportContext c, LedgerExportSources data, Selection s)
    {
        var invoices = data.Receivables.Where(x => s.Invoices.Contains(x.Id)).OrderBy(x => x.IssueDate).ThenBy(x => x.Id).ToArray();
        var payables = data.Payables.Where(x => s.Payables.Contains(x.Id)).OrderBy(x => x.InvoiceDate).ThenBy(x => x.Id).ToArray();
        var banks = data.Bank.Where(x => s.Banks.Contains(x.Id)).OrderBy(x => x.BookedOn).ThenBy(x => x.Id).ToArray();
        var allocations = data.CustomerAllocations.Where(x => s.Allocations.Contains(x.Id)).OrderBy(x => x.AllocatedAt).ThenBy(x => x.Id).ToArray();
        var supplierAllocations = data.SupplierAllocations.Where(x => s.SupplierAllocations.Contains(x.Id)).OrderBy(x => x.AllocatedAt).ThenBy(x => x.Id).ToArray();
        CsvCell SelectionLabel(bool seed) => CsvCell.Code(seed ? "window" : "context");
        yield return ExportContract.Table(c, "receivables.csv", AccountingSchema,
            ["selection", "invoice_id", "revision", "document_kind", "effect_sign", "number", "customer_id", "order_id", "original_invoice_id", "issue_date", "due_date", "service_from", "service_until",
                "title", "seller_name", "seller_address", "seller_tax_id", "buyer_name", "buyer_address", "currency", "net_eur", "tax_eur", "gross_eur", "correction_reason", "issued_digest", "pricing_digest", "record_digest", "archived"],
            invoices.Select(x => ExportContract.Row(c, SelectionLabel(s.SeedInvoices.Contains(x.Id)), CsvCell.Id(x.Id), CsvCell.Integer(x.Revision), CsvCell.Code(x.Kind.ToString()), CsvCell.Integer(x.Kind == InvoiceKind.CreditNote ? -1 : 1),
                CsvCell.Text(x.Number), CsvCell.Id(x.CustomerId), CsvCell.Id(x.OrderId), CsvCell.Id(x.OriginalInvoiceId), CsvCell.Date(x.IssueDate), CsvCell.Date(x.DueDate), CsvCell.Date(x.ServiceFrom), CsvCell.Date(x.ServiceUntil),
                CsvCell.Text(x.Title), CsvCell.Text(x.SellerName), CsvCell.Text(x.SellerAddress), CsvCell.Text(x.SellerTaxId), CsvCell.Text(x.BuyerName), CsvCell.Text(x.BuyerAddress), CsvCell.Code(x.Currency),
                CsvCell.Money(x.Snapshot!.Net), CsvCell.Money(x.Snapshot.Tax), CsvCell.Money(x.Snapshot.Gross), CsvCell.Text(x.CorrectionReason), CsvCell.Code(x.IssuedDigest), CsvCell.Code(x.Snapshot.Digest), CsvCell.Code(ContentDigest.Of(x)), CsvCell.Flag(x.Archived))));
        yield return ExportContract.Table(c, "receivable_lines.csv", AccountingSchema,
            ["invoice_id", "issued_digest", "line_number", "catalog_item_id", "description", "unit", "quantity", "unit_material_cost_eur", "unit_labor_hours", "hourly_rate_eur", "markup_percent", "discount_percent", "vat_percent",
                "calculated_unit_cost_eur", "unit_net_eur", "net_eur", "tax_eur", "gross_eur", "effect_sign"],
            invoices.SelectMany(x => x.Snapshot!.Lines.Select((line, index) => ExportContract.Row(c, CsvCell.Id(x.Id), CsvCell.Code(x.IssuedDigest), CsvCell.Integer(index + 1), CsvCell.Id(line.Inputs.CatalogItemId), CsvCell.Text(line.Inputs.Description),
                CsvCell.Text(line.Inputs.Unit), CsvCell.Number(line.Inputs.Quantity), CsvCell.Number(line.Inputs.UnitMaterialCost), CsvCell.Number(line.Inputs.UnitLaborHours), CsvCell.Number(line.Inputs.HourlyRate), CsvCell.Number(line.Inputs.MarkupPercent),
                CsvCell.Number(line.Inputs.DiscountPercent), CsvCell.Number(line.Inputs.VatPercent), CsvCell.Number(line.UnitCost), CsvCell.Money(line.UnitNet), CsvCell.Money(line.Net), CsvCell.Money(line.Tax), CsvCell.Money(line.Gross), CsvCell.Integer(x.Kind == InvoiceKind.CreditNote ? -1 : 1)))));
        yield return ExportContract.Table(c, "payables.csv", AccountingSchema,
            ["selection", "supplier_invoice_id", "revision", "document_kind", "effect_sign", "number", "supplier_id", "purchase_order_id", "original_invoice_id", "invoice_date", "due_date", "title", "supplier_name", "supplier_address", "currency",
                "net_eur", "tax_eur", "gross_eur", "approved_by", "approved_at_utc", "approved_digest", "record_digest", "archived"],
            payables.Select(x => ExportContract.Row(c, SelectionLabel(s.SeedPayables.Contains(x.Id)), CsvCell.Id(x.Id), CsvCell.Integer(x.Revision), CsvCell.Code(x.Kind.ToString()), CsvCell.Integer(x.Kind == SupplierInvoiceKind.CreditNote ? -1 : 1), CsvCell.Text(x.Number),
                CsvCell.Id(x.SupplierId), CsvCell.Id(x.PurchaseOrderId), CsvCell.Id(x.OriginalInvoiceId), CsvCell.Date(x.InvoiceDate), CsvCell.Date(x.DueDate), CsvCell.Text(x.Title), CsvCell.Text(x.SupplierName), CsvCell.Text(x.SupplierAddress), CsvCell.Code(x.Currency),
                CsvCell.Money(x.Net), CsvCell.Money(x.Tax), CsvCell.Money(x.Gross), CsvCell.Id(x.ApprovedBy), CsvCell.Instant(x.ApprovedAt), CsvCell.Code(x.ApprovedDigest), CsvCell.Code(ContentDigest.Of(x)), CsvCell.Flag(x.Archived))));
        yield return ExportContract.Table(c, "bank_transactions.csv", AccountingSchema,
            ["selection", "bank_transaction_id", "revision", "account_reference", "external_id", "booked_on", "amount_eur", "currency", "counterparty", "reference", "import_digest", "record_digest", "archived"],
            banks.Select(x => ExportContract.Row(c, SelectionLabel(s.SeedBanks.Contains(x.Id)), CsvCell.Id(x.Id), CsvCell.Integer(x.Revision), CsvCell.Text(x.AccountReference), CsvCell.Text(x.ExternalId), CsvCell.Date(x.BookedOn), CsvCell.Money(x.Amount), CsvCell.Code(x.Currency),
                CsvCell.Text(x.Counterparty), CsvCell.Text(x.Reference), CsvCell.Code(x.ImportDigest), CsvCell.Code(ContentDigest.Of(x)), CsvCell.Flag(x.Archived))));
        yield return ExportContract.Table(c, "customer_allocations.csv", AccountingSchema,
            ["selection", "allocation_id", "revision", "invoice_id", "bank_transaction_id", "direction", "cash_eur", "discount_eur", "retained_eur", "allocated_by", "allocated_at_utc", "allocation_in_window", "reversed_by", "reversed_at_utc", "reversal_in_window",
                "return_bank_transaction_id", "reason", "reversal_reason", "posted_digest", "record_digest"],
            allocations.Select(x => ExportContract.Row(c, SelectionLabel(s.SeedAllocations.Contains(x.Id)), CsvCell.Id(x.Id), CsvCell.Integer(x.Revision), CsvCell.Id(x.InvoiceId), CsvCell.Id(x.BankTransactionId), CsvCell.Code(x.Direction.ToString()), CsvCell.Money(x.Amount),
                CsvCell.Money(x.DiscountAmount), CsvCell.Money(x.RetainedAmount), CsvCell.Id(x.AllocatedBy), CsvCell.Instant(x.AllocatedAt), CsvCell.Flag(c.Window.Includes(x.AllocatedAt)), CsvCell.Id(x.ReversedBy), CsvCell.Instant(x.ReversedAt),
                CsvCell.Flag(x.ReversedAt.HasValue && c.Window.Includes(x.ReversedAt.Value)), CsvCell.Id(x.ReturnBankTransactionId), CsvCell.Text(x.Reason), CsvCell.Text(x.ReversalReason), CsvCell.Code(x.PostedDigest), CsvCell.Code(ContentDigest.Of(x)))));
        yield return ExportContract.Table(c, "retention_releases.csv", AccountingSchema,
            ["allocation_id", "invoice_id", "release_index", "amount_eur", "released_by", "released_at_utc", "release_in_window", "reason", "posted_allocation_digest", "event_digest"],
            allocations.SelectMany(x => x.RetentionReleases.Select((release, index) => ExportContract.Row(c, CsvCell.Id(x.Id), CsvCell.Id(x.InvoiceId), CsvCell.Integer(index + 1), CsvCell.Money(release.Amount), CsvCell.Id(release.ActorId), CsvCell.Instant(release.ReleasedAt),
                CsvCell.Flag(c.Window.Includes(release.ReleasedAt)), CsvCell.Text(release.Reason), CsvCell.Code(x.PostedDigest), CsvCell.Code(ContentDigest.Of(new { x.Id, index = index + 1, release }))))));
        yield return ExportContract.Table(c, "supplier_allocations.csv", AccountingSchema,
            ["selection", "allocation_id", "revision", "supplier_invoice_id", "bank_transaction_id", "direction", "cash_eur", "discount_eur", "allocated_by", "allocated_at_utc", "allocation_in_window", "reversed_by", "reversed_at_utc", "reversal_in_window",
                "return_bank_transaction_id", "reason", "reversal_reason", "posted_digest", "record_digest"],
            supplierAllocations.Select(x => ExportContract.Row(c, SelectionLabel(s.SeedSupplierAllocations.Contains(x.Id)), CsvCell.Id(x.Id), CsvCell.Integer(x.Revision), CsvCell.Id(x.SupplierInvoiceId), CsvCell.Id(x.BankTransactionId), CsvCell.Code(x.Direction.ToString()), CsvCell.Money(x.Amount),
                CsvCell.Money(x.DiscountAmount), CsvCell.Id(x.AllocatedBy), CsvCell.Instant(x.AllocatedAt), CsvCell.Flag(c.Window.Includes(x.AllocatedAt)), CsvCell.Id(x.ReversedBy), CsvCell.Instant(x.ReversedAt), CsvCell.Flag(x.ReversedAt.HasValue && c.Window.Includes(x.ReversedAt.Value)),
                CsvCell.Id(x.ReturnBankTransactionId), CsvCell.Text(x.Reason), CsvCell.Text(x.ReversalReason), CsvCell.Code(x.PostedDigest), CsvCell.Code(ContentDigest.Of(x)))));
    }

    private sealed class Selection
    {
        public HashSet<Guid> Invoices { get; } = []; public HashSet<Guid> Payables { get; } = []; public HashSet<Guid> Banks { get; } = [];
        public HashSet<Guid> Allocations { get; } = []; public HashSet<Guid> SupplierAllocations { get; } = [];
        public HashSet<Guid> SeedInvoices { get; } = []; public HashSet<Guid> SeedPayables { get; } = []; public HashSet<Guid> SeedBanks { get; } = [];
        public HashSet<Guid> SeedAllocations { get; } = []; public HashSet<Guid> SeedSupplierAllocations { get; } = [];
    }
    private static Selection Select(ExportWindow window, LedgerExportSources data, bool openItems)
    {
        var s = new Selection(); var queue = new Queue<(char Kind, Guid Id)>();
        var invoices = data.Receivables.ToDictionary(x => x.Id); var payables = data.Payables.ToDictionary(x => x.Id); var banks = data.Bank.ToDictionary(x => x.Id);
        var allocations = data.CustomerAllocations.ToDictionary(x => x.Id); var supplierAllocations = data.SupplierAllocations.ToDictionary(x => x.Id);
        var invoiceCredits = data.Receivables.Where(x => x.OriginalInvoiceId.HasValue).ToLookup(x => x.OriginalInvoiceId!.Value);
        var payableCredits = data.Payables.Where(x => x.OriginalInvoiceId.HasValue).ToLookup(x => x.OriginalInvoiceId!.Value);
        var invoiceAllocations = data.CustomerAllocations.ToLookup(x => x.InvoiceId); var payableAllocations = data.SupplierAllocations.ToLookup(x => x.SupplierInvoiceId);
        var bankAllocations = data.CustomerAllocations.SelectMany(x => (x.ReturnBankTransactionId is { } returned ? new[] { x.BankTransactionId, returned } : [x.BankTransactionId]).Select(id => (id, x))).ToLookup(x => x.id, x => x.x);
        var bankSupplierAllocations = data.SupplierAllocations.SelectMany(x => (x.ReturnBankTransactionId is { } returned ? new[] { x.BankTransactionId, returned } : [x.BankTransactionId]).Select(id => (id, x))).ToLookup(x => x.id, x => x.x);
        void Add(char kind, Guid id, bool seed = false)
        {
            var set = kind switch { 'i' => s.Invoices, 'p' => s.Payables, 'b' => s.Banks, 'a' => s.Allocations, _ => s.SupplierAllocations };
            if (seed) (kind switch { 'i' => s.SeedInvoices, 'p' => s.SeedPayables, 'b' => s.SeedBanks, 'a' => s.SeedAllocations, _ => s.SeedSupplierAllocations }).Add(id);
            if (set.Add(id)) queue.Enqueue((kind, id));
        }
        foreach (var x in data.Receivables.Where(x => window.Includes(x.IssueDate!.Value) && (!openItems || x.Kind != InvoiceKind.CreditNote))) Add('i', x.Id, true);
        foreach (var x in data.Payables.Where(x => window.Includes(x.InvoiceDate) && (!openItems || x.Kind != SupplierInvoiceKind.CreditNote))) Add('p', x.Id, true);
        if (!openItems)
        {
            foreach (var x in data.Bank.Where(x => window.Includes(x.BookedOn))) Add('b', x.Id, true);
            foreach (var x in data.CustomerAllocations.Where(x => window.Includes(x.AllocatedAt) || (x.ReversedAt.HasValue && window.Includes(x.ReversedAt.Value)) || x.RetentionReleases.Any(r => window.Includes(r.ReleasedAt)))) Add('a', x.Id, true);
            foreach (var x in data.SupplierAllocations.Where(x => window.Includes(x.AllocatedAt) || (x.ReversedAt.HasValue && window.Includes(x.ReversedAt.Value)))) Add('s', x.Id, true);
        }
        while (queue.TryDequeue(out var item))
        {
            switch (item.Kind)
            {
                case 'i':
                    Guard.Require(invoices.TryGetValue(item.Id, out var invoice), "export_reference", "Linked receivable evidence is missing.");
                    if (invoice.OriginalInvoiceId is { } original) Add('i', original);
                    foreach (var credit in invoiceCredits[item.Id]) Add('i', credit.Id);
                    foreach (var allocation in invoiceAllocations[item.Id]) Add('a', allocation.Id); break;
                case 'p':
                    Guard.Require(payables.TryGetValue(item.Id, out var payable), "export_reference", "Linked payable evidence is missing.");
                    if (payable.OriginalInvoiceId is { } originalPayable) Add('p', originalPayable);
                    foreach (var credit in payableCredits[item.Id]) Add('p', credit.Id);
                    foreach (var allocation in payableAllocations[item.Id]) Add('s', allocation.Id); break;
                case 'b':
                    Guard.Require(banks.ContainsKey(item.Id), "export_reference", "Linked sealed bank evidence is missing.");
                    foreach (var allocation in bankAllocations[item.Id]) Add('a', allocation.Id);
                    foreach (var allocation in bankSupplierAllocations[item.Id]) Add('s', allocation.Id); break;
                case 'a':
                    var customer = allocations[item.Id]; Add('i', customer.InvoiceId); Add('b', customer.BankTransactionId);
                    if (customer.ReturnBankTransactionId is { } returned) Add('b', returned); break;
                case 's':
                    var supplier = supplierAllocations[item.Id]; Add('p', supplier.SupplierInvoiceId); Add('b', supplier.BankTransactionId);
                    if (supplier.ReturnBankTransactionId is { } returnedSupplier) Add('b', returnedSupplier); break;
            }
        }
        return s;
    }
}
