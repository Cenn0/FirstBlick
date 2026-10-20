namespace FirstBlick.Core.Business;

public enum InvoiceKind { Final, Progress, CreditNote }
public enum InvoiceStatus { Draft, Issued }
public sealed class Invoice : TenantEntity
{
    public Guid CustomerId { get; set; }
    public Guid OrderId { get; set; }
    public Guid? OriginalInvoiceId { get; set; }
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string SellerName { get; set; } = "";
    public string SellerAddress { get; set; } = "";
    public string SellerTaxId { get; set; } = "";
    public string BuyerName { get; set; } = "";
    public string BuyerAddress { get; set; } = "";
    public string CorrectionReason { get; set; } = "";
    public string Currency { get; set; } = "EUR";
    public DateOnly? IssueDate { get; set; }
    public DateOnly DueDate { get; set; }
    public DateOnly ServiceFrom { get; set; }
    public DateOnly ServiceUntil { get; set; }
    public InvoiceKind Kind { get; set; }
    public InvoiceStatus Status { get; set; }
    public List<QuoteLine> Lines { get; set; } = [];
    public QuoteSnapshot? Snapshot { get; set; }
    public string IssuedDigest { get; set; } = "";

    public override void Validate()
    {
        ValidateCommon(); Guard.Id(CustomerId, nameof(CustomerId)); Guard.Id(OrderId, nameof(OrderId));
        Guard.OptionalId(OriginalInvoiceId, nameof(OriginalInvoiceId)); Guard.Text(Title, nameof(Title)); Guard.Currency(Currency);
        Guard.Require(Enum.IsDefined(Kind) && Enum.IsDefined(Status), "invalid_status", "Invoice kind or status is invalid.");
        Guard.Items(Lines, nameof(Lines), 1000, 1); foreach (var line in Lines) line.Validate();
        Guard.Text(Number, nameof(Number), 80, Status == InvoiceStatus.Issued);
        Guard.Text(SellerName, nameof(SellerName), 240, Status == InvoiceStatus.Issued); Guard.Text(SellerAddress, nameof(SellerAddress), 2000, Status == InvoiceStatus.Issued);
        Guard.Text(SellerTaxId, nameof(SellerTaxId), 80, false); Guard.Text(BuyerName, nameof(BuyerName), 240, Status == InvoiceStatus.Issued);
        Guard.Text(BuyerAddress, nameof(BuyerAddress), 2000, Status == InvoiceStatus.Issued); Guard.Text(CorrectionReason, nameof(CorrectionReason), 4000, Kind == InvoiceKind.CreditNote);
        Guard.Require((Kind == InvoiceKind.CreditNote) == OriginalInvoiceId.HasValue, "invalid_correction", "A credit note must reference its original invoice; other invoices must not.");
        if (Status == InvoiceStatus.Issued)
        {
            Guard.Require(IssueDate.HasValue && DueDate >= IssueDate.Value, "invalid_due_date", "The due date must not precede the issue date.");
            Guard.Require(ServiceFrom != default && ServiceUntil >= ServiceFrom, "invalid_service_period", "A valid service period is required.");
            Guard.Require(Snapshot is not null, "missing_snapshot", "Issued invoices require a frozen snapshot."); Snapshot!.Validate();
            Guard.Require(Pricing.Freeze(Lines).Digest == Snapshot.Digest && InvoiceWorkflow.Digest(this) == IssuedDigest,
                "invoice_immutable", "Issued invoice content is immutable. Use a referenced correction.");
        }
    }
    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Customer>(CustomerId); yield return BusinessRegistry.Reference<Order>(OrderId);
        if (OriginalInvoiceId is { } id) yield return BusinessRegistry.Reference<Invoice>(id);
    }
}

public static class InvoiceWorkflow
{
    public static Invoice DraftFromOrder(Order order, Customer customer, InvoiceKind kind = InvoiceKind.Final)
    {
        order.Validate(); customer.Validate();
        Guard.Require(order.TenantId == customer.TenantId && order.CustomerId == customer.Id, "reference_mismatch", "The customer does not own this order.");
        Guard.Require(kind is InvoiceKind.Final or InvoiceKind.Progress, "invalid_invoice_kind", "Use the credit-note workflow for a correction.");
        Guard.Require(order.Status != OrderStatus.Cancelled, "invalid_transition", "Cancelled orders cannot be invoiced.");
        return new()
        {
            TenantId = order.TenantId, CustomerId = customer.Id, OrderId = order.Id, Title = order.Title, Kind = kind,
            BuyerName = customer.Name, BuyerAddress = customer.Address,
            Lines = order.Contract!.Lines.Select(x => x.Inputs with { }).ToList()
        };
    }

    public static Invoice DraftCreditNote(Invoice original, string reason)
    {
        original.Validate(); Guard.Text(reason, nameof(reason), 4000);
        Guard.Require(original.Status == InvoiceStatus.Issued && original.Kind != InvoiceKind.CreditNote, "invalid_correction", "A credit note must reference an issued non-credit invoice.");
        return new()
        {
            TenantId = original.TenantId, CustomerId = original.CustomerId, OrderId = original.OrderId, OriginalInvoiceId = original.Id,
            Title = "Correction: " + original.Number, Kind = InvoiceKind.CreditNote, CorrectionReason = reason,
            SellerName = original.SellerName, SellerAddress = original.SellerAddress, SellerTaxId = original.SellerTaxId,
            BuyerName = original.BuyerName, BuyerAddress = original.BuyerAddress, Currency = original.Currency,
            ServiceFrom = original.ServiceFrom, ServiceUntil = original.ServiceUntil,
            Lines = original.Snapshot!.Lines.Select(x => x.Inputs with { }).ToList()
        };
    }

    public static void Issue(Invoice invoice, string number, DateOnly issueDate)
    {
        Guard.Require(invoice.Kind != InvoiceKind.CreditNote, "credit_check_required", "Credit notes require the original invoice and existing corrections to be checked.");
        IssueCore(invoice, number, issueDate);
    }

    /// <summary>Pass all issued order invoices and accepted changes from a transaction locked for this order.</summary>
    public static void IssueForOrder(Invoice invoice, Order order, IReadOnlyCollection<Invoice> existingInvoices,
        IReadOnlyCollection<ChangeOrder> changes, string number, DateOnly issueDate)
    {
        invoice.Validate(); order.Validate(); Guard.Items(existingInvoices, nameof(existingInvoices), 100_000); Guard.Items(changes, nameof(changes), 10_000);
        Guard.Require(invoice.TenantId == order.TenantId && invoice.OrderId == order.Id && invoice.CustomerId == order.CustomerId &&
            invoice.Kind != InvoiceKind.CreditNote && order.Status != OrderStatus.Cancelled, "invoice_order_mismatch", "Invoice and active order must match.");
        var issued = existingInvoices.Where(x => x.TenantId == order.TenantId && x.OrderId == order.Id && x.Id != invoice.Id && x.Status == InvoiceStatus.Issued).ToList();
        var accepted = changes.Where(x => x.TenantId == order.TenantId && x.OrderId == order.Id && x.Status == ChangeOrderStatus.Accepted).ToList();
        foreach (var item in issued) item.Validate(); foreach (var change in accepted) change.Validate();
        var contractGross = order.Contract!.Gross + accepted.Sum(x => x.Snapshot!.Gross);
        var billed = issued.Sum(x => x.Kind == InvoiceKind.CreditNote ? -x.Snapshot!.Gross : x.Snapshot!.Gross);
        var resulting = billed + Pricing.Freeze(invoice.Lines).Gross;
        Guard.Require(resulting <= contractGross, "order_overbilled", "Cumulative invoices exceed the accepted order and accepted changes.");
        Guard.Require(invoice.Kind != InvoiceKind.Final || resulting == contractGross, "incomplete_final_invoice", "The final invoice must settle the remaining accepted contract amount.");
        var contracted = order.Contract.Lines.Select(x => x.Inputs).Concat(accepted.SelectMany(x => x.Snapshot!.Lines.Select(y => y.Inputs)))
            .GroupBy(LineKey).ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity), StringComparer.Ordinal);
        var cumulative = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var previous in issued)
            foreach (var line in previous.Snapshot!.Lines.Select(x => x.Inputs))
                cumulative[LineKey(line)] = cumulative.GetValueOrDefault(LineKey(line)) + (previous.Kind == InvoiceKind.CreditNote ? -line.Quantity : line.Quantity);
        foreach (var line in invoice.Lines) cumulative[LineKey(line)] = cumulative.GetValueOrDefault(LineKey(line)) + line.Quantity;
        Guard.Require(cumulative.All(x => contracted.TryGetValue(x.Key, out var quantity) && x.Value >= 0 && x.Value <= quantity),
            "uncontracted_line", "Invoice quantities and effective unit prices must derive from the accepted order and changes.");
        Guard.Require(invoice.Kind != InvoiceKind.Final || contracted.All(x => cumulative.GetValueOrDefault(x.Key) == x.Value),
            "incomplete_final_invoice", "Final invoice quantities must settle every accepted contract position.");
        IssueCore(invoice, number, issueDate);
    }

    /// <summary>The caller must serialize issuance per original invoice and pass all existing corrections from the same transaction.</summary>
    public static void IssueCreditNote(Invoice credit, Invoice original, IReadOnlyCollection<Invoice> existingCredits, string number, DateOnly issueDate)
    {
        original.Validate(); credit.Validate(); Guard.Items(existingCredits, nameof(existingCredits), 10_000);
        Guard.Require(credit.Kind == InvoiceKind.CreditNote && original.Kind != InvoiceKind.CreditNote && original.Status == InvoiceStatus.Issued &&
            credit.OriginalInvoiceId == original.Id && credit.TenantId == original.TenantId && credit.OrderId == original.OrderId && credit.CustomerId == original.CustomerId,
            "invalid_correction", "Credit note and original invoice do not match.");
        var matching = existingCredits.Where(x => x.TenantId == original.TenantId && x.OriginalInvoiceId == original.Id && x.Status == InvoiceStatus.Issued).ToList();
        foreach (var existing in matching) existing.Validate();
        var amount = Pricing.Freeze(credit.Lines).Gross;
        Guard.Require(matching.Sum(x => x.Snapshot!.Gross) + amount <= original.Snapshot!.Gross, "excess_credit", "Cumulative credit notes cannot exceed the original invoice.");
        var originalQuantities = original.Snapshot.Lines.Select(x => x.Inputs).GroupBy(LineKey).ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity), StringComparer.Ordinal);
        var credited = matching.SelectMany(x => x.Snapshot!.Lines.Select(y => y.Inputs)).Concat(credit.Lines).GroupBy(LineKey);
        Guard.Require(credited.All(x => originalQuantities.TryGetValue(x.Key, out var originalQuantity) && x.Sum(y => y.Quantity) <= originalQuantity),
            "invalid_credit_lines", "Credit positions must retain the original effective prices and tax rates and cannot exceed their original quantities.");
        IssueCore(credit, number, issueDate);
    }

    private static string LineKey(QuoteLine line) => ContentDigest.Of(line with { Quantity = 1m });

    private static void IssueCore(Invoice invoice, string number, DateOnly issueDate)
    {
        invoice.Validate(); Guard.Require(invoice.Status == InvoiceStatus.Draft, "invoice_immutable", "Only a draft invoice can be issued.");
        Guard.Text(number, nameof(number), 80); Guard.Require(issueDate != default, "required", "Issue date is required.");
        Guard.Require(invoice.DueDate >= issueDate, "invalid_due_date", "Due date cannot precede issue date.");
        Guard.Require(invoice.ServiceFrom != default && invoice.ServiceUntil >= invoice.ServiceFrom, "invalid_service_period", "A valid service period is required.");
        Guard.Text(invoice.SellerName, "SellerName"); Guard.Text(invoice.SellerAddress, "SellerAddress", 2000);
        Guard.Text(invoice.BuyerName, "BuyerName"); Guard.Text(invoice.BuyerAddress, "BuyerAddress", 2000);
        var snapshot = Pricing.Freeze(invoice.Lines); Guard.Require(snapshot.Gross > 0, "zero_invoice", "An issued invoice must have a positive absolute amount.");
        invoice.Number = number; invoice.IssueDate = issueDate; invoice.Snapshot = snapshot; invoice.Status = InvoiceStatus.Issued;
        invoice.IssuedDigest = Digest(invoice);
    }

    public static string Digest(Invoice invoice) => ContentDigest.Of(new
    {
        invoice.TenantId, invoice.Id, invoice.CustomerId, invoice.OrderId, invoice.OriginalInvoiceId,
        invoice.Number, invoice.Title, invoice.SellerName, invoice.SellerAddress, invoice.SellerTaxId,
        invoice.BuyerName, invoice.BuyerAddress, invoice.CorrectionReason, invoice.Currency,
        invoice.IssueDate, invoice.DueDate, invoice.ServiceFrom, invoice.ServiceUntil, invoice.Kind, invoice.Snapshot
    });
}

public sealed class BankTransaction : TenantEntity
{
    public string AccountReference { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Counterparty { get; set; } = "";
    public string Reference { get; set; } = "";
    public DateOnly BookedOn { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string ImportDigest { get; set; } = "";
    public override void Validate()
    {
        ValidateCommon(); Guard.Text(AccountReference, nameof(AccountReference), 120); Guard.Text(ExternalId, nameof(ExternalId), 240);
        Guard.Text(Counterparty, nameof(Counterparty), 240, false); Guard.Text(Reference, nameof(Reference), 2000, false);
        Guard.Require(BookedOn != default, "required", "Booking date is required."); Money.Amount(Amount, nameof(Amount), true);
        Guard.Require(Amount != 0, "zero_transaction", "Bank transactions cannot have a zero amount."); Guard.Currency(Currency);
        if (!string.IsNullOrEmpty(ImportDigest)) Guard.Require(ImportDigest == PaymentWorkflow.BankDigest(this), "bank_immutable", "Imported bank data is immutable.");
    }
}

public enum PaymentDirection { Receipt, Refund }
public sealed record RetentionRelease(decimal Amount, string Reason, Guid ActorId, DateTimeOffset ReleasedAt);
public sealed class PaymentAllocation : TenantEntity
{
    public Guid InvoiceId { get; set; }
    public Guid BankTransactionId { get; set; }
    public decimal Amount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal RetainedAmount { get; set; }
    public PaymentDirection Direction { get; set; }
    public List<RetentionRelease> RetentionReleases { get; set; } = [];
    public DateTimeOffset AllocatedAt { get; set; }
    public Guid AllocatedBy { get; set; }
    public DateTimeOffset? ReversedAt { get; set; }
    public Guid? ReversedBy { get; set; }
    public string Reason { get; set; } = "";
    public string ReversalReason { get; set; } = "";
    public Guid? ReturnBankTransactionId { get; set; }
    public string PostedDigest { get; set; } = "";
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(InvoiceId, nameof(InvoiceId)); Guard.Id(BankTransactionId, nameof(BankTransactionId));
        Guard.Id(AllocatedBy, nameof(AllocatedBy)); Guard.Utc(AllocatedAt, nameof(AllocatedAt));
        Money.Amount(Amount, nameof(Amount)); Guard.Require(Amount > 0, "zero_allocation", "A payment allocation must have a positive cash amount.");
        Money.Amount(DiscountAmount, nameof(DiscountAmount)); Money.Amount(RetainedAmount, nameof(RetainedAmount));
        Guard.Require(Enum.IsDefined(Direction), "invalid_direction", "Payment direction is invalid.");
        Guard.Require(Direction != PaymentDirection.Refund || (DiscountAmount == 0 && RetainedAmount == 0), "invalid_refund", "Refunds cannot create discounts or retainage.");
        Guard.Items(RetentionReleases, nameof(RetentionReleases), 1000);
        foreach (var release in RetentionReleases)
        {
            Money.Amount(release.Amount, "Retention release"); Guard.Require(release.Amount > 0, "zero_release", "A retention release must be positive.");
            Guard.Text(release.Reason, "Release reason", 2000); Guard.Id(release.ActorId, "Release actor"); Guard.Utc(release.ReleasedAt, "ReleasedAt");
            Guard.Require(release.ReleasedAt >= AllocatedAt, "invalid_time", "Retention release cannot precede allocation.");
        }
        Guard.Require(RetentionReleases.Sum(x => x.Amount) <= RetainedAmount, "excess_retention_release", "Retention releases exceed the original retainage.");
        Guard.Text(Reason, nameof(Reason), 2000, DiscountAmount > 0 || RetainedAmount > 0);
        Guard.Text(ReversalReason, nameof(ReversalReason), 2000, ReversedAt.HasValue);
        Guard.Require(ReversedAt.HasValue == ReversedBy.HasValue, "invalid_reversal", "Reversal requires both actor and timestamp.");
        Guard.OptionalId(ReturnBankTransactionId, nameof(ReturnBankTransactionId));
        Guard.Require(!ReturnBankTransactionId.HasValue || ReversedAt.HasValue, "invalid_reversal", "A returned payment must reverse its allocation.");
        if (ReversedAt is { } at)
        {
            Guard.Utc(at, nameof(ReversedAt)); Guard.Id(ReversedBy!.Value, nameof(ReversedBy));
            Guard.Require(at >= AllocatedAt, "invalid_time", "Reversal cannot precede allocation.");
        }
        if (!string.IsNullOrEmpty(PostedDigest)) Guard.Require(PostedDigest == PaymentWorkflow.AllocationDigest(this), "allocation_immutable", "Cash, discount and retention allocation inputs are immutable.");
    }
    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Invoice>(InvoiceId); yield return BusinessRegistry.Reference<BankTransaction>(BankTransactionId);
        if (ReturnBankTransactionId is { } id) yield return BusinessRegistry.Reference<BankTransaction>(id);
    }
}

public sealed record OpenInvoice(decimal Gross, decimal Credits, decimal Paid, decimal Discount, decimal Retained, decimal Outstanding, decimal Collectible, bool Overdue, decimal CustomerCredit);
public static class PaymentWorkflow
{
    /// <summary>Returns reserve consumption on both original and returned bank entries. A chargeback does not make the original receipt reusable.</summary>
    public static decimal UsedBankAmount(BankTransaction bank, IEnumerable<PaymentAllocation> customerAllocations, IEnumerable<SupplierPaymentAllocation>? supplierAllocations = null)
    {
        var used = customerAllocations.Where(x => x.TenantId == bank.TenantId &&
            ((x.BankTransactionId == bank.Id && (!x.ReversedAt.HasValue || x.ReturnBankTransactionId.HasValue)) ||
             (x.ReturnBankTransactionId == bank.Id && x.ReversedAt.HasValue))).Sum(x => x.Amount);
        used += (supplierAllocations ?? []).Where(x => x.TenantId == bank.TenantId &&
            ((x.BankTransactionId == bank.Id && (!x.ReversedAt.HasValue || x.ReturnBankTransactionId.HasValue)) ||
             (x.ReturnBankTransactionId == bank.Id && x.ReversedAt.HasValue))).Sum(x => x.Amount);
        return used;
    }
    public static string AllocationDigest(PaymentAllocation allocation) => ContentDigest.Of(new
    {
        allocation.TenantId, allocation.Id, allocation.InvoiceId, allocation.BankTransactionId, allocation.Direction,
        allocation.Amount, allocation.DiscountAmount, allocation.RetainedAmount, allocation.AllocatedAt, allocation.AllocatedBy, allocation.Reason
    });
    public static string BankDigest(BankTransaction bank) => ContentDigest.Of(new
    {
        bank.TenantId, bank.AccountReference, bank.ExternalId, bank.Counterparty, bank.Reference, bank.BookedOn, bank.Amount, bank.Currency
    });
    public static void SealImport(BankTransaction bank)
    {
        bank.Validate(); Guard.Require(string.IsNullOrEmpty(bank.ImportDigest), "bank_immutable", "This transaction was already sealed.");
        bank.ImportDigest = BankDigest(bank);
    }

    public static OpenInvoice Balance(Invoice invoice, IEnumerable<PaymentAllocation> allocations, IEnumerable<Invoice> credits, DateOnly asOf)
    {
        invoice.Validate(); Guard.Require(invoice.Status == InvoiceStatus.Issued && invoice.Kind != InvoiceKind.CreditNote, "invalid_invoice", "Open balances require an issued receivable invoice.");
        var active = allocations.Where(x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && !x.ReversedAt.HasValue).ToList();
        foreach (var allocation in active) allocation.Validate();
        var issuedCredits = credits.Where(x => x.TenantId == invoice.TenantId && x.OriginalInvoiceId == invoice.Id && x.Status == InvoiceStatus.Issued).ToList();
        foreach (var credit in issuedCredits) credit.Validate();
        var gross = invoice.Snapshot!.Gross; var creditAmount = issuedCredits.Sum(x => x.Snapshot!.Gross);
        var paid = active.Sum(x => x.Direction == PaymentDirection.Receipt ? x.Amount : -x.Amount);
        // A commercial discount cannot exceed the amount still billed after corrections.
        var discount = Math.Min(active.Sum(x => x.DiscountAmount), Math.Max(0, gross - creditAmount));
        var rawBalance = gross - creditAmount - paid - discount; var outstanding = Math.Max(0, rawBalance);
        // A credit note issued after payment creates a customer credit; it does not invalidate the historical payment.
        var retained = Math.Min(outstanding, active.Sum(x => x.RetainedAmount - x.RetentionReleases.Sum(y => y.Amount)));
        Guard.Require(creditAmount <= gross && paid >= 0, "overallocated", "Stored corrections or refunds exceed the original receivable and receipts.");
        var collectible = outstanding - retained;
        return new(gross, creditAmount, paid, discount, retained, outstanding, collectible, asOf > invoice.DueDate && collectible > 0, Math.Max(0, -rawBalance));
    }

    /// <summary>Serialize bank and invoice updates and pass all bank/invoice allocations from the same transaction.</summary>
    public static PaymentAllocation Allocate(Invoice invoice, BankTransaction bank, IReadOnlyCollection<PaymentAllocation> allAllocations,
        IReadOnlyCollection<Invoice> credits, decimal amount, decimal discount, decimal retained, string reason, Guid actorId, DateTimeOffset now,
        IReadOnlyCollection<SupplierPaymentAllocation>? supplierAllocations = null)
    {
        invoice.Validate(); bank.Validate(); Guard.Items(allAllocations, nameof(allAllocations), 100_000); Guard.Items(credits, nameof(credits), 100_000);
        Guard.Require(bank.TenantId == invoice.TenantId && bank.Currency == invoice.Currency && bank.Amount > 0 && !string.IsNullOrEmpty(bank.ImportDigest),
            "payment_mismatch", "Allocate a sealed incoming EUR transaction from the same tenant.");
        var candidate = new PaymentAllocation
        {
            TenantId = invoice.TenantId, InvoiceId = invoice.Id, BankTransactionId = bank.Id, Amount = amount, DiscountAmount = discount,
            RetainedAmount = retained, Reason = reason, AllocatedBy = actorId, AllocatedAt = now
        };
        candidate.Validate();
        var used = UsedBankAmount(bank, allAllocations, supplierAllocations);
        Guard.Require(used + amount <= bank.Amount, "bank_overallocated", "Allocations cannot exceed the imported bank amount.");
        var balance = Balance(invoice, allAllocations, credits, DateOnly.FromDateTime(now.UtcDateTime));
        Guard.Require(amount + discount + retained <= balance.Collectible, "invoice_overallocated", "Payment, discount and additional retainage exceed the collectible amount.");
        candidate.PostedDigest = AllocationDigest(candidate);
        return candidate;
    }

    public static PaymentAllocation AllocateRefund(Invoice invoice, BankTransaction bank, IReadOnlyCollection<PaymentAllocation> allAllocations,
        IReadOnlyCollection<Invoice> credits, decimal amount, string reason, Guid actorId, DateTimeOffset now,
        IReadOnlyCollection<SupplierPaymentAllocation>? supplierAllocations = null)
    {
        invoice.Validate(); bank.Validate(); Guard.Text(reason, nameof(reason), 2000);
        Guard.Require(bank.TenantId == invoice.TenantId && bank.Currency == invoice.Currency && bank.Amount < 0 && !string.IsNullOrEmpty(bank.ImportDigest),
            "payment_mismatch", "A refund requires a sealed outgoing transaction from the same tenant.");
        var candidate = new PaymentAllocation
        {
            TenantId = invoice.TenantId, InvoiceId = invoice.Id, BankTransactionId = bank.Id, Amount = amount, Direction = PaymentDirection.Refund,
            Reason = reason, AllocatedBy = actorId, AllocatedAt = now
        };
        candidate.Validate();
        var used = UsedBankAmount(bank, allAllocations, supplierAllocations);
        Guard.Require(used + amount <= -bank.Amount, "bank_overallocated", "Refund allocations exceed the imported outgoing transaction.");
        var balance = Balance(invoice, allAllocations, credits, DateOnly.FromDateTime(now.UtcDateTime));
        Guard.Require(amount <= balance.CustomerCredit, "excess_refund", "The refund exceeds the customer credit.");
        candidate.PostedDigest = AllocationDigest(candidate); return candidate;
    }

    public static void ReleaseRetention(PaymentAllocation allocation, decimal amount, string reason, Guid actorId, DateTimeOffset now)
    {
        allocation.Validate(); Guard.Require(!allocation.ReversedAt.HasValue, "already_reversed", "A reversed allocation has no active retainage.");
        Money.Amount(amount, nameof(amount)); Guard.Require(amount > 0, "zero_release", "The release amount must be positive.");
        Guard.Text(reason, nameof(reason), 2000); Guard.Id(actorId, nameof(actorId)); Guard.Utc(now, nameof(now));
        Guard.Require(now >= allocation.AllocatedAt, "invalid_time", "Release cannot precede allocation.");
        Guard.Require(allocation.RetentionReleases.Count < 1000 && amount <= allocation.RetainedAmount - allocation.RetentionReleases.Sum(x => x.Amount),
            "excess_retention_release", "Retention release exceeds the remaining retainage or event budget.");
        allocation.RetentionReleases.Add(new(amount, reason, actorId, now));
    }

    public static void Reverse(PaymentAllocation allocation, string reason, Guid actorId, DateTimeOffset now)
    {
        allocation.Validate(); Guard.Require(!allocation.ReversedAt.HasValue, "already_reversed", "This allocation was already reversed.");
        Guard.Text(reason, nameof(reason), 2000); Guard.Id(actorId, nameof(actorId)); Guard.Utc(now, nameof(now));
        Guard.Require(now >= allocation.AllocatedAt, "invalid_time", "Reversal cannot precede allocation.");
        allocation.ReversedAt = now; allocation.ReversedBy = actorId; allocation.ReversalReason = reason;
    }

    public static void ReverseForReturn(PaymentAllocation allocation, BankTransaction returnedTransaction, IReadOnlyCollection<PaymentAllocation> allAllocations,
        string reason, Guid actorId, DateTimeOffset now, IReadOnlyCollection<SupplierPaymentAllocation>? supplierAllocations = null)
    {
        allocation.Validate(); returnedTransaction.Validate();
        Guard.Require(allocation.Direction == PaymentDirection.Receipt && allocation.TenantId == returnedTransaction.TenantId &&
            returnedTransaction.Amount < 0 && !string.IsNullOrEmpty(returnedTransaction.ImportDigest), "invalid_return", "A returned receipt requires a sealed outgoing bank transaction from the same tenant.");
        var used = UsedBankAmount(returnedTransaction, allAllocations, supplierAllocations);
        Guard.Require(used + allocation.Amount <= -returnedTransaction.Amount, "bank_overallocated", "Returned allocations exceed the imported return transaction.");
        Reverse(allocation, reason, actorId, now); allocation.ReturnBankTransactionId = returnedTransaction.Id;
    }
}
