namespace FirstBlick.Core.Business;

public enum SupplierInvoiceKind { Invoice, CreditNote }
public enum SupplierInvoiceStatus { Draft, Approved, Rejected }
public sealed class SupplierInvoice : TenantEntity
{
    public Guid SupplierId { get; set; }
    public Guid? PurchaseOrderId { get; set; }
    public Guid? OriginalInvoiceId { get; set; }
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string SupplierName { get; set; } = "";
    public string SupplierAddress { get; set; } = "";
    public string Currency { get; set; } = "EUR";
    public DateOnly InvoiceDate { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal Net { get; set; }
    public decimal Tax { get; set; }
    public decimal Gross { get; set; }
    public SupplierInvoiceKind Kind { get; set; }
    public SupplierInvoiceStatus Status { get; set; }
    public string ReviewNote { get; set; } = "";
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string ApprovedDigest { get; set; } = "";
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(SupplierId, nameof(SupplierId)); Guard.OptionalId(PurchaseOrderId, nameof(PurchaseOrderId));
        Guard.OptionalId(OriginalInvoiceId, nameof(OriginalInvoiceId)); Guard.Text(Number, nameof(Number), 80); Guard.Text(Title, nameof(Title));
        Guard.Text(SupplierName, nameof(SupplierName), 240, Status == SupplierInvoiceStatus.Approved);
        Guard.Text(SupplierAddress, nameof(SupplierAddress), 2000, false); Guard.Currency(Currency);
        Guard.Require(InvoiceDate != default && DueDate >= InvoiceDate, "invalid_due_date", "Supplier invoice dates are invalid.");
        Money.Amount(Net, nameof(Net)); Money.Amount(Tax, nameof(Tax)); Money.Amount(Gross, nameof(Gross));
        Guard.Require(Gross > 0 && Net + Tax == Gross, "invalid_invoice_totals", "Supplier invoice net and tax must equal its positive gross amount.");
        Guard.Require(Enum.IsDefined(Kind) && Enum.IsDefined(Status), "invalid_status", "Supplier invoice kind or status is invalid.");
        Guard.Require((Kind == SupplierInvoiceKind.CreditNote) == OriginalInvoiceId.HasValue, "invalid_correction", "A supplier credit note must reference its original invoice.");
        Guard.Text(ReviewNote, nameof(ReviewNote), 4000, Status == SupplierInvoiceStatus.Rejected);
        Guard.Require(ApprovedBy.HasValue == ApprovedAt.HasValue && (Status == SupplierInvoiceStatus.Approved) == ApprovedAt.HasValue,
            "invalid_approval", "Approval requires both actor and timestamp, and only approved records may contain them.");
        if (Status == SupplierInvoiceStatus.Approved)
        {
            Guard.Id(ApprovedBy!.Value, nameof(ApprovedBy)); Guard.Utc(ApprovedAt!.Value, nameof(ApprovedAt));
            Guard.Require(ApprovedDigest == PayablesWorkflow.InvoiceDigest(this), "supplier_invoice_immutable", "Approved supplier invoice content is immutable.");
        }
    }
    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Supplier>(SupplierId);
        if (PurchaseOrderId is { } purchase) yield return BusinessRegistry.Reference<PurchaseOrder>(purchase);
        if (OriginalInvoiceId is { } original) yield return BusinessRegistry.Reference<SupplierInvoice>(original);
    }
}

public enum SupplierPaymentDirection { Payment, Refund }
public sealed class SupplierPaymentAllocation : TenantEntity
{
    public Guid SupplierInvoiceId { get; set; }
    public Guid BankTransactionId { get; set; }
    public SupplierPaymentDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public decimal DiscountAmount { get; set; }
    public string Reason { get; set; } = "";
    public Guid AllocatedBy { get; set; }
    public DateTimeOffset AllocatedAt { get; set; }
    public Guid? ReversedBy { get; set; }
    public DateTimeOffset? ReversedAt { get; set; }
    public string ReversalReason { get; set; } = "";
    public Guid? ReturnBankTransactionId { get; set; }
    public string PostedDigest { get; set; } = "";
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(SupplierInvoiceId, nameof(SupplierInvoiceId)); Guard.Id(BankTransactionId, nameof(BankTransactionId));
        Guard.Id(AllocatedBy, nameof(AllocatedBy)); Guard.Utc(AllocatedAt, nameof(AllocatedAt));
        Money.Amount(Amount, nameof(Amount)); Money.Amount(DiscountAmount, nameof(DiscountAmount));
        Guard.Require(Amount > 0 && Enum.IsDefined(Direction), "invalid_allocation", "Payment requires a positive amount and valid direction.");
        Guard.Require(Direction != SupplierPaymentDirection.Refund || DiscountAmount == 0, "invalid_refund", "A supplier refund cannot create a discount.");
        Guard.Text(Reason, nameof(Reason), 2000, DiscountAmount > 0 || Direction == SupplierPaymentDirection.Refund);
        Guard.Text(ReversalReason, nameof(ReversalReason), 2000, ReversedAt.HasValue);
        Guard.Require(ReversedAt.HasValue == ReversedBy.HasValue, "invalid_reversal", "Reversal requires an actor and timestamp.");
        Guard.OptionalId(ReturnBankTransactionId, nameof(ReturnBankTransactionId));
        Guard.Require(!ReturnBankTransactionId.HasValue || ReversedAt.HasValue, "invalid_reversal", "A bank return must reverse its allocation.");
        if (ReversedAt is { } at)
        {
            Guard.Utc(at, nameof(ReversedAt)); Guard.Id(ReversedBy!.Value, nameof(ReversedBy));
            Guard.Require(at >= AllocatedAt, "invalid_time", "Reversal cannot precede allocation.");
        }
        if (!string.IsNullOrEmpty(PostedDigest)) Guard.Require(PostedDigest == PayablesWorkflow.AllocationDigest(this), "allocation_immutable", "Posted supplier payment inputs are immutable.");
    }
    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<SupplierInvoice>(SupplierInvoiceId); yield return BusinessRegistry.Reference<BankTransaction>(BankTransactionId);
        if (ReturnBankTransactionId is { } id) yield return BusinessRegistry.Reference<BankTransaction>(id);
    }
}

public sealed record PayableBalance(decimal Gross, decimal Credits, decimal Paid, decimal Discount, decimal Outstanding, decimal SupplierCredit, bool Overdue);
public static class PayablesWorkflow
{
    public static string InvoiceDigest(SupplierInvoice invoice) => ContentDigest.Of(new
    {
        invoice.TenantId, invoice.Id, invoice.SupplierId, invoice.PurchaseOrderId, invoice.OriginalInvoiceId, invoice.Number, invoice.Title,
        invoice.SupplierName, invoice.SupplierAddress, invoice.Currency, invoice.InvoiceDate, invoice.DueDate, invoice.Net, invoice.Tax, invoice.Gross, invoice.Kind
    });
    public static string AllocationDigest(SupplierPaymentAllocation allocation) => ContentDigest.Of(new
    {
        allocation.TenantId, allocation.Id, allocation.SupplierInvoiceId, allocation.BankTransactionId, allocation.Direction,
        allocation.Amount, allocation.DiscountAmount, allocation.Reason, allocation.AllocatedBy, allocation.AllocatedAt
    });

    public static void Approve(SupplierInvoice invoice, Supplier supplier, Guid creatorId, Guid approverId, DateTimeOffset now, bool requireIndependentApproval)
    {
        Guard.Require(invoice.Kind != SupplierInvoiceKind.CreditNote, "credit_check_required", "Supplier credit notes require checking their original and existing credits.");
        ApproveCore(invoice, supplier, creatorId, approverId, now, requireIndependentApproval);
    }

    public static void ApproveCredit(SupplierInvoice credit, SupplierInvoice original, Supplier supplier, IReadOnlyCollection<SupplierInvoice> existingCredits,
        Guid creatorId, Guid approverId, DateTimeOffset now, bool requireIndependentApproval)
    {
        credit.Validate(); original.Validate();
        Guard.Require(credit.Kind == SupplierInvoiceKind.CreditNote && original.Kind == SupplierInvoiceKind.Invoice && original.Status == SupplierInvoiceStatus.Approved &&
            credit.OriginalInvoiceId == original.Id && credit.TenantId == original.TenantId && credit.SupplierId == original.SupplierId,
            "invalid_correction", "Supplier credit note and approved original invoice must match.");
        var matching = existingCredits.Where(x => x.TenantId == original.TenantId && x.OriginalInvoiceId == original.Id && x.Status == SupplierInvoiceStatus.Approved).ToList();
        foreach (var item in matching) item.Validate();
        Guard.Require(matching.Sum(x => x.Gross) + credit.Gross <= original.Gross, "excess_credit", "Approved supplier credits cannot exceed the original invoice.");
        Guard.Require(matching.Sum(x => x.Net) + credit.Net <= original.Net && matching.Sum(x => x.Tax) + credit.Tax <= original.Tax,
            "excess_credit", "Supplier credit net and tax totals cannot exceed the original components.");
        ApproveCore(credit, supplier, creatorId, approverId, now, requireIndependentApproval);
    }

    private static void ApproveCore(SupplierInvoice invoice, Supplier supplier, Guid creatorId, Guid approverId, DateTimeOffset now, bool requireIndependentApproval)
    {
        invoice.Validate(); supplier.Validate(); Guard.Id(creatorId, nameof(creatorId)); Guard.Id(approverId, nameof(approverId)); Guard.Utc(now, nameof(now));
        Guard.Require(invoice.Status == SupplierInvoiceStatus.Draft, "invalid_transition", "Only a draft supplier invoice can be approved.");
        Guard.Require(invoice.SupplierId == supplier.Id && invoice.TenantId == supplier.TenantId, "supplier_mismatch", "The invoice supplier does not match.");
        Guard.Require(!requireIndependentApproval || creatorId != approverId, "self_approval", "This policy requires a second person to approve the supplier invoice.");
        invoice.SupplierName = supplier.Name; invoice.SupplierAddress = supplier.Address;
        invoice.ApprovedBy = approverId; invoice.ApprovedAt = now; invoice.ApprovedDigest = InvoiceDigest(invoice); invoice.Status = SupplierInvoiceStatus.Approved;
    }

    public static PayableBalance Balance(SupplierInvoice invoice, IEnumerable<SupplierPaymentAllocation> allocations, IEnumerable<SupplierInvoice> credits, DateOnly asOf)
    {
        invoice.Validate(); Guard.Require(invoice.Status == SupplierInvoiceStatus.Approved && invoice.Kind == SupplierInvoiceKind.Invoice, "invoice_not_approved", "Payables require an approved supplier invoice.");
        var active = allocations.Where(x => x.TenantId == invoice.TenantId && x.SupplierInvoiceId == invoice.Id && !x.ReversedAt.HasValue).ToList();
        var matchingCredits = credits.Where(x => x.TenantId == invoice.TenantId && x.OriginalInvoiceId == invoice.Id && x.Status == SupplierInvoiceStatus.Approved).ToList();
        foreach (var item in active) item.Validate(); foreach (var credit in matchingCredits) credit.Validate();
        var creditGross = matchingCredits.Sum(x => x.Gross); var paid = active.Sum(x => x.Direction == SupplierPaymentDirection.Payment ? x.Amount : -x.Amount);
        var discount = Math.Min(active.Sum(x => x.DiscountAmount), Math.Max(0, invoice.Gross - creditGross));
        Guard.Require(creditGross <= invoice.Gross && paid >= 0, "overallocated", "Supplier credits or refunds exceed the original invoice or payments.");
        var raw = invoice.Gross - creditGross - paid - discount; var outstanding = Math.Max(0, raw);
        return new(invoice.Gross, creditGross, paid, discount, outstanding, Math.Max(0, -raw), asOf > invoice.DueDate && outstanding > 0);
    }

    /// <summary>Records reconciliation only. This method does not initiate or authorize a bank transfer.</summary>
    public static SupplierPaymentAllocation Allocate(SupplierInvoice invoice, BankTransaction bank, IReadOnlyCollection<SupplierPaymentAllocation> supplierAllocations,
        IReadOnlyCollection<PaymentAllocation> customerAllocations, IReadOnlyCollection<SupplierInvoice> credits,
        decimal amount, decimal discount, string reason, SupplierPaymentDirection direction, Guid actorId, DateTimeOffset now)
    {
        invoice.Validate(); bank.Validate();
        Guard.Require(invoice.TenantId == bank.TenantId && invoice.Currency == bank.Currency && !string.IsNullOrEmpty(bank.ImportDigest), "payment_mismatch", "A sealed bank transaction from the same tenant is required.");
        Guard.Require(direction == SupplierPaymentDirection.Payment ? bank.Amount < 0 : bank.Amount > 0, "payment_mismatch", "The bank amount direction does not match this supplier allocation.");
        var allocation = new SupplierPaymentAllocation
        {
            TenantId = invoice.TenantId, SupplierInvoiceId = invoice.Id, BankTransactionId = bank.Id, Direction = direction,
            Amount = amount, DiscountAmount = discount, Reason = reason, AllocatedBy = actorId, AllocatedAt = now
        };
        allocation.Validate();
        Guard.Require(PaymentWorkflow.UsedBankAmount(bank, customerAllocations, supplierAllocations) + amount <= Math.Abs(bank.Amount), "bank_overallocated", "Customer, supplier and return allocations exceed the bank amount.");
        var balance = Balance(invoice, supplierAllocations, credits, DateOnly.FromDateTime(now.UtcDateTime));
        Guard.Require(direction == SupplierPaymentDirection.Payment ? amount + discount <= balance.Outstanding : amount <= balance.SupplierCredit,
            "supplier_overallocated", "This allocation exceeds the outstanding supplier invoice or supplier credit.");
        allocation.PostedDigest = AllocationDigest(allocation); return allocation;
    }

    public static void Reverse(SupplierPaymentAllocation allocation, string reason, Guid actorId, DateTimeOffset now)
    {
        allocation.Validate(); Guard.Require(!allocation.ReversedAt.HasValue, "already_reversed", "This allocation was already reversed.");
        Guard.Text(reason, nameof(reason), 2000); Guard.Id(actorId, nameof(actorId)); Guard.Utc(now, nameof(now));
        Guard.Require(now >= allocation.AllocatedAt, "invalid_time", "Reversal cannot precede allocation.");
        allocation.ReversedAt = now; allocation.ReversedBy = actorId; allocation.ReversalReason = reason;
    }

    public static void ReverseForReturn(SupplierPaymentAllocation allocation, BankTransaction bank, IReadOnlyCollection<SupplierPaymentAllocation> supplierAllocations,
        IReadOnlyCollection<PaymentAllocation> customerAllocations, string reason, Guid actorId, DateTimeOffset now)
    {
        allocation.Validate(); bank.Validate();
        Guard.Require(allocation.Direction == SupplierPaymentDirection.Payment && allocation.TenantId == bank.TenantId && bank.Amount > 0 && !string.IsNullOrEmpty(bank.ImportDigest),
            "invalid_return", "A returned outgoing supplier payment requires a sealed incoming return transaction.");
        Guard.Require(PaymentWorkflow.UsedBankAmount(bank, customerAllocations, supplierAllocations) + allocation.Amount <= bank.Amount, "bank_overallocated", "Return allocations exceed the imported bank amount.");
        Reverse(allocation, reason, actorId, now); allocation.ReturnBankTransactionId = bank.Id;
    }
}
