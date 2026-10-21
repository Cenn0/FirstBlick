namespace FirstBlick.Core.Business;

/// <summary>
/// One immutable allocation journal entry. A correction is an additional reversal entry;
/// posted entries are never edited and are not financial supplier payments.
/// </summary>
public sealed class SupplierCostAllocation : TenantEntity
{
    public Guid SupplierInvoiceId { get; set; }
    public Guid ProjectId { get; set; }
    public decimal NetAmount { get; set; }
    public Guid PostedBy { get; set; }
    public DateTimeOffset PostedAt { get; set; }
    public string SourceDigest { get; set; } = "";
    public string Reason { get; set; } = "";
    public Guid? ReversesAllocationId { get; set; }
    public string PostedDigest { get; set; } = "";

    public string CalculateDigest() => ContentDigest.Of(new
    {
        TenantId, Id, SupplierInvoiceId, ProjectId, NetAmount,
        PostedBy, PostedAt, SourceDigest, Reason, ReversesAllocationId
    });

    public override void Validate()
    {
        ValidateCommon();
        Guard.Id(SupplierInvoiceId, nameof(SupplierInvoiceId)); Guard.Id(ProjectId, nameof(ProjectId));
        Guard.Id(PostedBy, nameof(PostedBy)); Guard.Utc(PostedAt, nameof(PostedAt));
        Guard.OptionalId(ReversesAllocationId, nameof(ReversesAllocationId));
        Money.Amount(NetAmount, nameof(NetAmount));
        Guard.Require(NetAmount > 0 && decimal.Round(NetAmount, 2) == NetAmount,
            "invalid_allocation_amount", "Posted project amounts must be positive whole cents.");
        Guard.Text(Reason, nameof(Reason), 2000);
        Guard.Require(SourceDigest.Length == 64 && SourceDigest.All(Uri.IsHexDigit),
            "invalid_source_digest", "The allocation requires a frozen approved supplier source digest.");
        Guard.Require(PostedDigest == CalculateDigest(), "cost_allocation_changed",
            "A recorded supplier cost allocation cannot be modified.");
    }

    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<SupplierInvoice>(SupplierInvoiceId);
        yield return BusinessRegistry.Reference<Project>(ProjectId);
        if (ReversesAllocationId is { } prior)
            yield return BusinessRegistry.Reference<SupplierCostAllocation>(prior);
    }
}

public static class SupplierCostAllocationRules
{
    public const int MaximumJournalEntries = 5000;

    /// <summary>Returns posted allocations without their separately retained reversal entries.</summary>
    public static List<SupplierCostAllocation> Active(IReadOnlyList<SupplierCostAllocation> journal)
    {
        Guard.Items(journal, nameof(journal), MaximumJournalEntries);
        foreach (var entry in journal) entry.Validate();
        Guard.Require(journal.Select(x => x.Id).Distinct().Count() == journal.Count,
            "duplicate_allocation", "Cost journal entries cannot be duplicated.");
        var posted = journal.Where(x => x.ReversesAllocationId is null).ToDictionary(x => x.Id);
        var reversed = new HashSet<Guid>();
        foreach (var undo in journal.Where(x => x.ReversesAllocationId is not null))
        {
            var target = undo.ReversesAllocationId!.Value;
            Guard.Require(posted.TryGetValue(target, out var original)
                && reversed.Add(target)
                && undo.TenantId == original.TenantId
                && undo.SupplierInvoiceId == original.SupplierInvoiceId
                && undo.ProjectId == original.ProjectId
                && undo.NetAmount == original.NetAmount
                && undo.SourceDigest == original.SourceDigest
                && undo.PostedAt >= original.PostedAt,
                "invalid_allocation_reversal", "Each reversal must match exactly one previous posted allocation.");
        }
        return posted.Values.Where(x => !reversed.Contains(x.Id)).OrderBy(x => x.PostedAt).ThenBy(x => x.Id).ToList();
    }

    public static SupplierCostAllocation Post(SupplierInvoice invoice, Project project,
        IReadOnlyList<SupplierCostAllocation> journal, decimal amount, string reason,
        Guid actor, DateTimeOffset at)
    {
        invoice.Validate(); project.Validate();
        Guard.Require(invoice.Status == SupplierInvoiceStatus.Approved
            && invoice.TenantId == project.TenantId && invoice.Net > 0,
            "invalid_cost_source", "A project allocation requires a positive approved invoice in the same business.");
        Guard.Id(actor, nameof(actor)); Guard.Utc(at, nameof(at)); Guard.Text(reason, nameof(reason), 2000);
        Money.Amount(amount, nameof(amount));
        Guard.Require(amount > 0 && decimal.Round(amount, 2) == amount,
            "invalid_allocation_amount", "Allocation must be a positive whole-cent amount.");
        Guard.Require(journal.All(x => x.TenantId == invoice.TenantId && x.SupplierInvoiceId == invoice.Id),
            "wrong_allocation_source", "The journal belongs to another supplier invoice.");
        var active = Active(journal);
        Guard.Require(journal.Count < MaximumJournalEntries && active.Sum(x => x.NetAmount) + amount <= invoice.Net,
            "cost_allocation_exceeded", "Project allocations exceed the supplier document's approved net amount.");
        var value = new SupplierCostAllocation
        {
            TenantId = invoice.TenantId, SupplierInvoiceId = invoice.Id, ProjectId = project.Id,
            NetAmount = amount, SourceDigest = invoice.ApprovedDigest, PostedBy = actor, PostedAt = at,
            Reason = reason.Trim()
        };
        value.PostedDigest = value.CalculateDigest();
        value.Validate();
        return value;
    }

    public static SupplierCostAllocation Reverse(SupplierCostAllocation original,
        IReadOnlyList<SupplierCostAllocation> journal, string reason, Guid actor, DateTimeOffset at)
    {
        original.Validate(); Guard.Require(original.ReversesAllocationId is null,
            "invalid_allocation_reversal", "Only a posted cost allocation can be reversed.");
        Guard.Id(actor, nameof(actor)); Guard.Utc(at, nameof(at)); Guard.Text(reason, nameof(reason), 2000);
        Guard.Require(journal.All(x => x.TenantId == original.TenantId && x.SupplierInvoiceId == original.SupplierInvoiceId),
            "wrong_allocation_source", "The reversal journal belongs to another supplier invoice.");
        Guard.Require(journal.Count < MaximumJournalEntries && Active(journal).Any(x => x.Id == original.Id),
            "allocation_already_reversed", "This cost allocation has already been reversed or is no longer active.");
        Guard.Require(at >= original.PostedAt, "invalid_allocation_reversal", "The reversal cannot precede its posting.");
        var reversal = new SupplierCostAllocation
        {
            TenantId = original.TenantId, SupplierInvoiceId = original.SupplierInvoiceId, ProjectId = original.ProjectId,
            NetAmount = original.NetAmount, SourceDigest = original.SourceDigest, PostedBy = actor, PostedAt = at,
            Reason = reason.Trim(), ReversesAllocationId = original.Id
        };
        reversal.PostedDigest = reversal.CalculateDigest(); reversal.Validate();
        return reversal;
    }

    /// <summary>Credits must not allocate more costs away from a project than remain allocated on the original.</summary>
    public static void ValidateCreditProjectBalance(Guid projectId, decimal additionalCredit,
        IReadOnlyList<SupplierCostAllocation> originalJournal,
        IReadOnlyList<SupplierCostAllocation> creditJournals)
    {
        var original = Active(originalJournal).Where(x => x.ProjectId == projectId).Sum(x => x.NetAmount);
        var credited = Active(creditJournals).Where(x => x.ProjectId == projectId).Sum(x => x.NetAmount);
        Guard.Require(credited + additionalCredit <= original, "credit_allocation_exceeded",
            "Supplier credit allocations cannot exceed the remaining posted project cost.");
    }
}
