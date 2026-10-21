using System.Globalization;

namespace FirstBlick.Core.Business;

public enum CollectionCaseStatus { Active, Disputed, Closed }
public enum CollectionStage { PaymentReminder, FirstReminder, SecondReminder }
public enum CollectionContactChannel { Letter, Email, Phone, Other }
public enum CollectionClosureKind { Settled, ManualHandling }

public sealed record CollectionSource(string Kind, Guid Id, long Revision, string Digest);
public sealed record CollectionInvoice(string Number, string BuyerName, string BuyerAddress, string SellerName,
    string SellerAddress, string Currency, DateOnly DueDate);
public sealed record CollectionLedger(string Version, Guid TenantId, Guid InvoiceId, string InvoiceDigest, DateOnly AsOf,
    CollectionInvoice Invoice, OpenInvoice Balance, IReadOnlyList<CollectionSource> Sources, string Digest)
{
    public static CollectionLedger Capture(Invoice invoice, IReadOnlyList<PaymentAllocation> allocations,
        IReadOnlyList<Invoice> credits, IReadOnlyList<CollectionSource> sources, DateOnly asOf)
    {
        invoice.Validate(); Guard.Require(asOf != default, "required", "The business date is required.");
        Guard.Items(sources, nameof(sources), 1000, 1);
        Guard.Require(!invoice.Archived && allocations.All(x => !x.Archived && x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id) &&
            credits.All(x => !x.Archived && x.TenantId == invoice.TenantId && x.OriginalInvoiceId == invoice.Id && x.Kind == InvoiceKind.CreditNote && x.Status == InvoiceStatus.Issued),
            "collection_source_metadata", "Financial sources must match the selected invoice and business.");
        var expected = new[] { (Kind: "invoices", invoice.Id, invoice.Revision) }
            .Concat(credits.Select(x => (Kind: "invoices", x.Id, x.Revision)))
            .Concat(allocations.Select(x => (Kind: "payment-allocations", x.Id, x.Revision))).ToArray();
        Guard.Require(sources.Count == expected.Length && expected.Distinct().Count() == expected.Length &&
            expected.All(item => sources.Count(source => source.Kind == item.Kind && source.Id == item.Id && source.Revision == item.Revision) == 1),
            "collection_source_metadata", "Financial source identities and revisions must cover the full calculated ledger exactly once.");
        foreach (var allocation in allocations) allocation.Validate();
        var result = new CollectionLedger("1.0.0", invoice.TenantId, invoice.Id, invoice.IssuedDigest, asOf,
            new(invoice.Number, invoice.BuyerName, invoice.BuyerAddress, invoice.SellerName, invoice.SellerAddress, invoice.Currency, invoice.DueDate),
            PaymentWorkflow.Balance(invoice, allocations, credits, asOf), sources.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray(), "");
        result = result with { Digest = CalculateDigest(result) }; result.Validate(); return result;
    }
    public void Validate()
    {
        Guard.Require(Version == "1.0.0" && AsOf != default, "collection_source_metadata", "Unsupported financial snapshot.");
        Guard.Id(TenantId, nameof(TenantId)); Guard.Id(InvoiceId, nameof(InvoiceId)); CollectionWorkflow.RequireDigest(InvoiceDigest);
        Guard.Require(Invoice is not null && Balance is not null, "collection_source_metadata", "Invoice and balance evidence are required.");
        Guard.Currency(Invoice!.Currency); Guard.Text(Invoice.Number, "Invoice number", 80); Guard.Text(Invoice.BuyerName, "Buyer", 240);
        Guard.Items(Sources, nameof(Sources), 1000, 1);
        foreach (var source in Sources)
        {
            Guard.Require(source.Kind is "invoices" or "payment-allocations" && source.Revision > 0, "collection_source_metadata", "Invalid financial source metadata.");
            Guard.Id(source.Id, "Source"); CollectionWorkflow.RequireDigest(source.Digest);
        }
        Guard.Require(Sources.Select(x => (x.Kind, x.Id)).Distinct().Count() == Sources.Count && Sources.Any(x => x.Kind == "invoices" && x.Id == InvoiceId),
            "collection_source_metadata", "Financial sources must be unique and include the original invoice.");
        foreach (var amount in new[] { Balance!.Gross, Balance.Credits, Balance.Paid, Balance.Discount, Balance.Retained, Balance.Outstanding, Balance.Collectible, Balance.CustomerCredit }) Money.Amount(amount, "Balance");
        Guard.Require(Balance.Outstanding == Balance.Collectible + Balance.Retained && Digest == CalculateDigest(this),
            "collection_evidence_changed", "The financial snapshot was changed.");
    }
    private static string CalculateDigest(CollectionLedger value) => ContentDigest.Of(new
    { value.Version, value.TenantId, value.InvoiceId, value.InvoiceDigest, value.AsOf, value.Invoice, value.Balance, value.Sources });
}

public sealed record CollectionStepInput(CollectionStage Stage, DateOnly RespondBy, string Reason, string AdditionalMessage = "");
public sealed record CollectionPreview(string Version, Guid CaseId, long CaseRevision, string CaseHistoryDigest,
    CollectionStepInput Input, CollectionLedger Ledger, string Subject, string Text, string Digest);
public sealed record CollectionContact(CollectionContactChannel Channel, DateTimeOffset OccurredAt, DateOnly OccurredOn,
    string EvidenceReference, Guid RecordedBy, DateTimeOffset RecordedAt);
public sealed record CollectionWithdrawal(string Reason, Guid ActorId, DateTimeOffset At);
public sealed record CollectionStep(Guid Id, CollectionPreview Preview, Guid ApprovedBy, DateTimeOffset ApprovedAt,
    string Digest, CollectionContact? Contact = null, CollectionWithdrawal? Withdrawal = null);
public sealed record CollectionDispute(Guid Id, decimal Amount, string Reason, string EvidenceReference, string LedgerDigest,
    Guid OpenedBy, DateTimeOffset OpenedAt, DateOnly OpenedOn, Guid? ResolvedBy = null, DateTimeOffset? ResolvedAt = null,
    string ResolutionReason = "", string ResolutionEvidence = "");
public sealed record CollectionClosure(Guid Id, CollectionClosureKind Kind, string Reason, string EvidenceReference,
    CollectionLedger Ledger, Guid ClosedBy, DateTimeOffset ClosedAt, Guid? ReopenedBy = null, DateTimeOffset? ReopenedAt = null,
    string ReopeningReason = "", string ReopeningEvidence = "");
public sealed record CollectionEventData(Guid? RelatedId = null, Guid? AssignedToUserId = null, DateOnly? NextReviewOn = null,
    decimal? Amount = null, CollectionClosureKind? ClosureKind = null, string? SnapshotDigest = null);
public sealed record CollectionEvent(Guid Id, string Action, DateTimeOffset At, DateOnly On, Guid ActorId, string Reason,
    string EvidenceReference, CollectionEventData Data, string PreviousDigest, string Digest);

/// <summary>Manual receivable handling. No collection action changes the ledger or sends a message.</summary>
public sealed class CollectionCase : TenantEntity
{
    public Guid InvoiceId { get; set; }
    public string InvoiceDigest { get; set; } = "";
    public Guid AssignedToUserId { get; set; }
    public DateOnly NextReviewOn { get; set; }
    public CollectionCaseStatus Status { get; set; }
    public List<CollectionStep> Steps { get; set; } = [];
    public List<CollectionDispute> Disputes { get; set; } = [];
    public List<CollectionClosure> Closures { get; set; } = [];
    public List<CollectionEvent> Events { get; set; } = [];
    public string HistoryDigest { get; set; } = "";
    public string StateDigest { get; set; } = "";
    public override IEnumerable<EntityReference> References() { yield return BusinessRegistry.Reference<Invoice>(InvoiceId); }
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(InvoiceId, nameof(InvoiceId)); Guard.Id(AssignedToUserId, nameof(AssignedToUserId));
        CollectionWorkflow.RequireDigest(InvoiceDigest); Guard.Require(Enum.IsDefined(Status) && NextReviewOn != default, "invalid_collection_state", "A collection case needs a status and next review date.");
        Guard.Items(Steps, nameof(Steps), 50); Guard.Items(Disputes, nameof(Disputes), 100); Guard.Items(Closures, nameof(Closures), 100); Guard.Items(Events, nameof(Events), 500, 1);
        Guard.Require(Steps.Count(x => x.Contact is null && x.Withdrawal is null) <= 1 && Disputes.Count(x => x.ResolvedAt is null) <= 1 && Closures.Count(x => x.ReopenedAt is null) <= 1,
            "invalid_collection_state", "A case cannot have overlapping pending steps, disputes or closures.");
        Guard.Require((Status == CollectionCaseStatus.Disputed) == Disputes.Any(x => x.ResolvedAt is null) &&
            (Status == CollectionCaseStatus.Closed) == Closures.Any(x => x.ReopenedAt is null), "invalid_collection_state", "Case status and retained history disagree.");
        var previous = new string('0', 64); DateTimeOffset at = default;
        foreach (var item in Events)
        {
            Guard.Id(item.Id, "Event"); Guard.Id(item.ActorId, "Event actor"); Guard.Utc(item.At, "Event time"); Guard.Text(item.Reason, "Reason", 2000);
            Guard.Require(item.On != default && item.At >= at && item.PreviousDigest == previous && item.Digest == CollectionWorkflow.EventDigest(item),
                "collection_evidence_changed", "The retained event chain was changed."); previous = item.Digest; at = item.At;
        }
        Guard.Require(Events.Select(x => x.Id).Distinct().Count() == Events.Count && HistoryDigest == previous && StateDigest == CollectionWorkflow.StateDigest(this),
            "collection_evidence_changed", "The retained collection case was changed.");
        foreach (var step in Steps)
        {
            CollectionWorkflow.CheckLedger(this, step.Preview.Ledger); CollectionWorkflow.ValidatePreview(step.Preview);
            Guard.Require(step.Preview.CaseId == Id && step.Digest == CollectionWorkflow.StepDigest(step) && (step.Contact is null || step.Withdrawal is null),
                "collection_evidence_changed", "An approved step was changed.");
            Guard.Id(step.ApprovedBy, "Approver"); Guard.Utc(step.ApprovedAt, "Approval time");
            if (step.Contact is { } contact)
                Guard.Require(Enum.IsDefined(contact.Channel) && contact.OccurredAt >= step.ApprovedAt && contact.RecordedAt >= contact.OccurredAt,
                    "invalid_collection_time", "Contact evidence must follow its approval.");
        }
        foreach (var closure in Closures) CollectionWorkflow.CheckLedger(this, closure.Ledger);
    }
}

public static class CollectionWorkflow
{
    public static CollectionCase Open(CollectionLedger ledger, Guid owner, DateOnly nextReview, string reason, Guid actor, DateTimeOffset now)
    {
        ledger.Validate(); Identity(actor, now); ReviewDate(nextReview, ledger.AsOf); Guard.Id(owner, "Assigned person"); Guard.Text(reason, "Reason", 2000);
        Guard.Require(ledger.Balance.Outstanding > 0, "collection_not_open", "Only a recorded outstanding invoice can start a collection case.");
        var value = new CollectionCase { TenantId = ledger.TenantId, InvoiceId = ledger.InvoiceId, InvoiceDigest = ledger.InvoiceDigest,
            AssignedToUserId = owner, NextReviewOn = nextReview, CreatedAt = now, UpdatedAt = now };
        AddEvent(value, "opened", reason, "", new(AssignedToUserId: owner, NextReviewOn: nextReview, SnapshotDigest: ledger.Digest), ledger.AsOf, actor, now); return value;
    }

    public static CollectionPreview Preview(CollectionCase value, CollectionLedger ledger, CollectionStepInput input)
    {
        value.Validate(); CheckLedger(value, ledger); Guard.Require(input is not null, "required", "A reviewed step input is required.");
        Guard.Require(value.Status == CollectionCaseStatus.Active && value.Steps.All(x => x.Contact is not null || x.Withdrawal is not null),
            "collection_step_blocked", "Resolve disputes and pending drafts before preparing another reminder.");
        Guard.Require(ledger.Balance.Overdue && ledger.Balance.Collectible > 0, "collection_not_due", "A reminder requires an overdue recorded collectible balance.");
        Guard.Require(Enum.IsDefined(input.Stage), "invalid_collection_stage", "Choose a supported reminder stage.");
        Guard.Require(input.RespondBy > ledger.AsOf && input.RespondBy <= ledger.AsOf.AddYears(1), "invalid_collection_date", "Choose an explicit future response date within one year.");
        Guard.Text(input.Reason, "Reason", 2000); Guard.Text(input.AdditionalMessage, "Additional message", 4000, false);
        var contacted = value.Steps.LastOrDefault(x => x.Contact is not null);
        var next = contacted is null ? CollectionStage.PaymentReminder : (CollectionStage)((int)contacted.Preview.Input.Stage + 1);
        Guard.Require(input.Stage == next && (contacted is null || ledger.AsOf > contacted.Preview.Input.RespondBy),
            "collection_stage_blocked", "The next stage requires the previous recorded contact and expiry of its explicitly selected response date.");
        var label = input.Stage switch { CollectionStage.PaymentReminder => "Zahlungserinnerung", CollectionStage.FirstReminder => "Erste Mahnung", _ => "Zweite Mahnung" };
        var amount = ledger.Balance.Collectible.ToString("F2", CultureInfo.GetCultureInfo("de-DE"));
        var subject = $"{label} zur Rechnung {ledger.Invoice.Number}";
        var text = $"{subject}\n\nGuten Tag {ledger.Invoice.BuyerName},\n\nfür die Rechnung {ledger.Invoice.Number} mit Fälligkeit {ledger.Invoice.DueDate:dd.MM.yyyy} ist nach unseren erfassten Belegen vom {ledger.AsOf:dd.MM.yyyy} ein Betrag von {amount} EUR ohne erfasste Einbehalte offen.\nBitte prüfen Sie den Vorgang und melden Sie sich bis zum {input.RespondBy:dd.MM.yyyy}.\n";
        if (!string.IsNullOrWhiteSpace(input.AdditionalMessage)) text += "\n" + input.AdditionalMessage + "\n";
        text += $"\n{ledger.Invoice.SellerName}";
        var result = new CollectionPreview("1.0.0", value.Id, value.Revision, value.HistoryDigest, input, ledger, subject, text, "");
        return result with { Digest = PreviewDigest(result) };
    }

    public static void Approve(CollectionCase value, CollectionLedger ledger, CollectionStepInput input, string expectedDigest, Guid actor, DateTimeOffset now)
    {
        Identity(actor, now); var preview = Preview(value, ledger, input);
        Guard.Require(expectedDigest == preview.Digest, "collection_preview_changed", "The case, inputs, financial sources or business date changed. Review a fresh preview.");
        var step = new CollectionStep(Guid.NewGuid(), preview, actor, now, ""); step = step with { Digest = StepDigest(step) };
        value.Steps.Add(step); value.NextReviewOn = input.RespondBy;
        AddEvent(value, "step.approved", input.Reason, "", new(RelatedId: step.Id, NextReviewOn: input.RespondBy, SnapshotDigest: step.Digest), ledger.AsOf, actor, now);
    }

    public static void Withdraw(CollectionCase value, Guid stepId, string reason, DateOnly today, Guid actor, DateTimeOffset now)
    {
        value.Validate(); Identity(actor, now); Guard.Text(reason, "Reason", 2000);
        var index = value.Steps.FindIndex(x => x.Id == stepId);
        Guard.Require(index >= 0 && value.Steps[index].Contact is null && value.Steps[index].Withdrawal is null,
            "collection_step_blocked", "Only an uncontacted pending draft can be withdrawn.");
        value.Steps[index] = value.Steps[index] with { Withdrawal = new(reason, actor, now) };
        AddEvent(value, "step.withdrawn", reason, "", new(RelatedId: stepId), today, actor, now);
    }

    public static void RecordContact(CollectionCase value, Guid stepId, string expectedStepDigest, CollectionContactChannel channel,
        DateTimeOffset occurredAt, DateOnly occurredOn, string evidence, DateOnly today, Guid actor, DateTimeOffset now)
    {
        value.Validate(); Identity(actor, now); Guard.Utc(occurredAt, "Contact time"); Guard.Text(evidence, "Contact evidence", 4000);
        Guard.Require(value.Status == CollectionCaseStatus.Active && Enum.IsDefined(channel), "collection_step_blocked", "Contact recording requires an active case and supported channel.");
        var index = value.Steps.FindIndex(x => x.Id == stepId);
        Guard.Require(index >= 0 && value.Steps[index].Digest == expectedStepDigest && value.Steps[index].Contact is null && value.Steps[index].Withdrawal is null,
            "collection_step_blocked", "The selected approved draft no longer permits contact recording.");
        Guard.Require(occurredAt >= value.Steps[index].ApprovedAt && occurredAt <= now && occurredOn != default && occurredOn <= today,
            "invalid_collection_time", "Record an actual contact after approval, never a future contact.");
        var contact = new CollectionContact(channel, occurredAt, occurredOn, evidence, actor, now);
        value.Steps[index] = value.Steps[index] with { Contact = contact };
        AddEvent(value, "contact.recorded", "Manual contact recorded by the acting user", evidence, new(RelatedId: stepId, SnapshotDigest: ContentDigest.Of(contact)), today, actor, now);
    }

    public static void Dispute(CollectionCase value, CollectionLedger ledger, decimal amount, string reason, string evidence,
        DateOnly nextReview, Guid actor, DateTimeOffset now)
    {
        value.Validate(); CheckLedger(value, ledger); Identity(actor, now); ReviewDate(nextReview, ledger.AsOf);
        Guard.Require(value.Status == CollectionCaseStatus.Active, "collection_dispute_blocked", "Only an active case can open a new dispute.");
        Money.Amount(amount, "Disputed amount"); Guard.Require(amount > 0 && amount <= ledger.Balance.Outstanding, "invalid_dispute_amount", "The disputed amount must be positive and no greater than the currently outstanding balance.");
        Guard.Text(reason, "Dispute reason", 2000); Guard.Text(evidence, "Dispute evidence", 4000);
        var pending = value.Steps.SingleOrDefault(x => x.Contact is null && x.Withdrawal is null);
        if (pending is not null) Withdraw(value, pending.Id, "Draft withdrawn because the invoice is disputed: " + reason[..Math.Min(reason.Length, 1800)], ledger.AsOf, actor, now);
        var dispute = new CollectionDispute(Guid.NewGuid(), amount, reason, evidence, ledger.Digest, actor, now, ledger.AsOf);
        value.Disputes.Add(dispute); value.Status = CollectionCaseStatus.Disputed; value.NextReviewOn = nextReview;
        AddEvent(value, "dispute.opened", reason, evidence, new(RelatedId: dispute.Id, Amount: amount, NextReviewOn: nextReview, SnapshotDigest: ledger.Digest), ledger.AsOf, actor, now);
    }

    public static void ResolveDispute(CollectionCase value, string reason, string evidence, DateOnly nextReview, DateOnly today, Guid actor, DateTimeOffset now)
    {
        value.Validate(); Identity(actor, now); ReviewDate(nextReview, today); Guard.Text(reason, "Resolution reason", 2000); Guard.Text(evidence, "Resolution evidence", 4000);
        Guard.Require(value.Status == CollectionCaseStatus.Disputed, "collection_dispute_blocked", "There is no unresolved dispute.");
        var index = value.Disputes.FindIndex(x => x.ResolvedAt is null); var previous = value.Disputes[index];
        value.Disputes[index] = previous with { ResolvedBy = actor, ResolvedAt = now, ResolutionReason = reason, ResolutionEvidence = evidence };
        value.Status = CollectionCaseStatus.Active; value.NextReviewOn = nextReview;
        AddEvent(value, "dispute.resolved", reason, evidence, new(RelatedId: previous.Id, NextReviewOn: nextReview), today, actor, now);
    }

    public static void Review(CollectionCase value, Guid owner, DateOnly nextReview, string reason, DateOnly today, Guid actor, DateTimeOffset now)
    {
        value.Validate(); Identity(actor, now); ReviewDate(nextReview, today); Guard.Id(owner, "Assigned person"); Guard.Text(reason, "Reason", 2000);
        Guard.Require(value.Status != CollectionCaseStatus.Closed, "collection_closed", "Reopen the retained case explicitly before changing responsibility.");
        Guard.Require(value.AssignedToUserId != owner || value.NextReviewOn != nextReview, "collection_unchanged", "Choose a changed responsibility or review date.");
        value.AssignedToUserId = owner; value.NextReviewOn = nextReview;
        AddEvent(value, "review.changed", reason, "", new(AssignedToUserId: owner, NextReviewOn: nextReview), today, actor, now);
    }

    public static void Close(CollectionCase value, CollectionLedger ledger, CollectionClosureKind kind, string reason, string evidence, Guid actor, DateTimeOffset now)
    {
        value.Validate(); CheckLedger(value, ledger); Identity(actor, now); Guard.Text(reason, "Closure reason", 2000);
        Guard.Require(value.Status == CollectionCaseStatus.Active && value.Steps.All(x => x.Contact is not null || x.Withdrawal is not null),
            "collection_close_blocked", "Resolve a dispute and withdraw any pending draft before closing the case.");
        Guard.Require(Enum.IsDefined(kind), "invalid_collection_closure", "Choose a supported closure reason.");
        Guard.Text(evidence, "Closure evidence", 4000, kind == CollectionClosureKind.ManualHandling);
        Guard.Require(kind != CollectionClosureKind.Settled || ledger.Balance.Outstanding == 0 && ledger.Balance.CustomerCredit == 0,
            "collection_not_settled", "The recorded balance is not settled; manual handling requires an explicit retained reason and evidence.");
        var closure = new CollectionClosure(Guid.NewGuid(), kind, reason, evidence, ledger, actor, now);
        value.Closures.Add(closure); value.Status = CollectionCaseStatus.Closed;
        AddEvent(value, "closed", reason, evidence, new(RelatedId: closure.Id, ClosureKind: kind, SnapshotDigest: ledger.Digest), ledger.AsOf, actor, now);
    }

    public static void Reopen(CollectionCase value, CollectionLedger ledger, Guid owner, DateOnly nextReview, string reason, string evidence, Guid actor, DateTimeOffset now)
    {
        value.Validate(); CheckLedger(value, ledger); Identity(actor, now); ReviewDate(nextReview, ledger.AsOf); Guard.Id(owner, "Assigned person");
        Guard.Text(reason, "Reopening reason", 2000); Guard.Text(evidence, "Reopening evidence", 4000);
        Guard.Require(value.Status == CollectionCaseStatus.Closed && ledger.Balance.Outstanding > 0, "collection_reopen_blocked", "Reopening requires a closed case and a current recorded outstanding balance.");
        var index = value.Closures.FindIndex(x => x.ReopenedAt is null); var closure = value.Closures[index];
        value.Closures[index] = closure with { ReopenedBy = actor, ReopenedAt = now, ReopeningReason = reason, ReopeningEvidence = evidence };
        value.Status = CollectionCaseStatus.Active; value.AssignedToUserId = owner; value.NextReviewOn = nextReview;
        AddEvent(value, "reopened", reason, evidence, new(RelatedId: closure.Id, AssignedToUserId: owner, NextReviewOn: nextReview, SnapshotDigest: ledger.Digest), ledger.AsOf, actor, now);
    }

    public static void CheckLedger(CollectionCase value, CollectionLedger ledger)
    {
        ledger.Validate(); Guard.Require(value.TenantId == ledger.TenantId && value.InvoiceId == ledger.InvoiceId && value.InvoiceDigest == ledger.InvoiceDigest,
            "collection_source_metadata", "The retained case and invoice evidence must match.");
    }
    public static void RequireDigest(string digest) => Guard.Require(digest is not null && digest.Length == 64 && digest.All(char.IsAsciiHexDigit), "collection_evidence_changed", "A SHA-256 evidence digest is required.");
    public static string StateDigest(CollectionCase value) => ContentDigest.Of(new
    { value.Id, value.TenantId, value.InvoiceId, value.InvoiceDigest, value.AssignedToUserId, value.NextReviewOn, value.Status, value.Steps, value.Disputes, value.Closures, value.Events, value.HistoryDigest });
    public static string EventDigest(CollectionEvent value) => ContentDigest.Of(new
    { value.Id, value.Action, value.At, value.On, value.ActorId, value.Reason, value.EvidenceReference, value.Data, value.PreviousDigest });
    public static string StepDigest(CollectionStep value) => ContentDigest.Of(new { value.Id, value.Preview, value.ApprovedBy, value.ApprovedAt });
    private static string PreviewDigest(CollectionPreview value) => ContentDigest.Of(new
    { value.Version, value.CaseId, value.CaseRevision, value.CaseHistoryDigest, value.Input, value.Ledger, value.Subject, value.Text });
    public static void ValidatePreview(CollectionPreview value)
    {
        value.Ledger.Validate(); Guard.Require(value.Version == "1.0.0" && value.Digest == PreviewDigest(value), "collection_evidence_changed", "The reviewed text snapshot changed.");
    }
    private static void Identity(Guid actor, DateTimeOffset now) { Guard.Id(actor, "Actor"); Guard.Utc(now, "Action time"); }
    private static void ReviewDate(DateOnly value, DateOnly today) => Guard.Require(value != default && today != default && value >= today && value <= today.AddYears(5), "invalid_collection_date", "Choose an explicit review date from today to five years ahead.");
    private static void AddEvent(CollectionCase value, string action, string reason, string evidence, CollectionEventData data, DateOnly today, Guid actor, DateTimeOffset now)
    {
        Guard.Require(value.Events.Count < 500, "collection_history_budget", "The retained case reached its supported event budget.");
        var item = new CollectionEvent(Guid.NewGuid(), action, now, today, actor, reason, evidence, data,
            value.Events.Count == 0 ? new string('0', 64) : value.HistoryDigest, "");
        item = item with { Digest = EventDigest(item) }; value.Events.Add(item); value.HistoryDigest = item.Digest;
        value.StateDigest = StateDigest(value); value.Validate();
    }
}
