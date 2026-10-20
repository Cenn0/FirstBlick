using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class CollectionTests
{
    private static readonly DateOnly Today = new(2026, 11, 10);
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Actor = BusinessFixture.Actor;

    [Fact]
    public void Manual_preview_freezes_invoice_parties_and_exact_collectible_balance_without_adding_fees()
    {
        var invoice = BusinessFixture.Invoice();
        var payment = PaymentWorkflow.Allocate(invoice, BusinessFixture.Bank(60), [], [], 60, 2, 10, "Explicit discount and retention", Actor, Now);
        var ledger = Ledger(invoice, [payment]); var value = Open(ledger); var previous = value.StateDigest;
        var preview = CollectionWorkflow.Preview(value, ledger, Input());
        Assert.Equal(119m, preview.Ledger.Balance.Gross); Assert.Equal(57m, preview.Ledger.Balance.Outstanding);
        Assert.Equal(47m, preview.Ledger.Balance.Collectible); Assert.Contains("47,00 EUR", preview.Text);
        Assert.Contains(invoice.Number, preview.Subject); Assert.Contains(invoice.BuyerName, preview.Text);
        Assert.Equal(previous, value.StateDigest); Assert.Single(value.Events); Assert.Empty(value.Steps);
        CollectionWorkflow.ValidatePreview(preview);
    }

    [Fact]
    public void Approval_is_bound_to_sources_input_case_history_and_business_date()
    {
        var invoice = BusinessFixture.Invoice(); var ledger = Ledger(invoice); var value = Open(ledger); var input = Input();
        var preview = CollectionWorkflow.Preview(value, ledger, input);
        var payment = PaymentWorkflow.Allocate(invoice, BusinessFixture.Bank(10), [], [], 10, 0, 0, "", Actor, Now);
        AssertCode("collection_preview_changed", () => CollectionWorkflow.Approve(value, Ledger(invoice, [payment]), input, preview.Digest, Actor, Now));
        AssertCode("collection_preview_changed", () => CollectionWorkflow.Approve(value, ledger, input with { AdditionalMessage = "Changed text" }, preview.Digest, Actor, Now));
        AssertCode("collection_preview_changed", () => CollectionWorkflow.Approve(value, Ledger(invoice, asOf: Today.AddDays(1)), input, preview.Digest, Actor, Now));
        CollectionWorkflow.Review(value, Guid.NewGuid(), Today.AddDays(3), "New responsible reviewer", Today, Actor, Now);
        AssertCode("collection_preview_changed", () => CollectionWorkflow.Approve(value, ledger, input, preview.Digest, Actor, Now));
        Assert.Empty(value.Steps);
    }

    [Fact]
    public void Same_balance_with_changed_source_revision_invalidates_the_previously_reviewed_preview()
    {
        var invoice = BusinessFixture.Invoice(); var ledger = Ledger(invoice); var value = Open(ledger); var preview = CollectionWorkflow.Preview(value, ledger, Input());
        invoice.Revision++; var newLedger = Ledger(invoice);
        Assert.Equal(ledger.Balance, newLedger.Balance); Assert.NotEqual(ledger.Digest, newLedger.Digest);
        AssertCode("collection_preview_changed", () => CollectionWorkflow.Approve(value, newLedger, Input(), preview.Digest, Actor, Now));
    }

    [Fact]
    public void A_partial_dispute_withdraws_an_uncontacted_draft_and_pauses_the_whole_case()
    {
        var ledger = Ledger(BusinessFixture.Invoice()); var value = Open(ledger); Approve(value, ledger);
        CollectionWorkflow.Dispute(value, ledger, 10.25m, "Area in dispute", "Saved customer letter", Today.AddDays(4), Actor, Now.AddMinutes(1));
        Assert.Equal(CollectionCaseStatus.Disputed, value.Status); Assert.Equal(10.25m, Assert.Single(value.Disputes).Amount);
        Assert.NotNull(value.Steps[0].Withdrawal); Assert.Null(value.Steps[0].Contact);
        Assert.Contains(value.Events, x => x.Action == "step.withdrawn");
        AssertCode("collection_step_blocked", () => CollectionWorkflow.Preview(value, ledger, Input()));
        CollectionWorkflow.ResolveDispute(value, "Documented agreement", "Meeting note 17", Today.AddDays(5), Today, Actor, Now.AddMinutes(2));
        Assert.Equal(CollectionCaseStatus.Active, value.Status); Assert.NotNull(value.Disputes[0].ResolvedAt); value.Validate();
        Assert.Equal(119m, ledger.Balance.Outstanding); Assert.Equal(119m, CollectionWorkflow.Preview(value, ledger, Input()).Ledger.Balance.Collectible);
    }

    [Fact]
    public void Retained_only_and_not_due_balances_cannot_be_used_for_a_reminder()
    {
        var invoice = BusinessFixture.Invoice(); var payment = PaymentWorkflow.Allocate(invoice, BusinessFixture.Bank(100), [], [], 100, 0, 19, "Agreed retention", Actor, Now);
        var held = Ledger(invoice, [payment]); var value = Open(held);
        AssertCode("collection_not_due", () => CollectionWorkflow.Preview(value, held, Input()));
        var early = Ledger(invoice, asOf: invoice.DueDate); var earlyCase = CollectionWorkflow.Open(early, Actor, Today, "Review invoice", Actor, Now);
        AssertCode("collection_not_due", () => CollectionWorkflow.Preview(earlyCase, early, Input()));
    }

    [Fact]
    public void Next_stage_requires_actual_contact_and_expiry_of_the_reviewed_response_date()
    {
        var invoice = BusinessFixture.Invoice(); var ledger = Ledger(invoice); var value = Open(ledger); Approve(value, ledger);
        AssertCode("collection_step_blocked", () => CollectionWorkflow.Preview(value, ledger, Input() with { Stage = CollectionStage.FirstReminder }));
        var step = value.Steps.Single();
        CollectionWorkflow.RecordContact(value, step.Id, step.Digest, CollectionContactChannel.Email, Now.AddMinutes(1), Today, "Saved sent mail", Today, Actor, Now.AddMinutes(2));
        AssertCode("collection_stage_blocked", () => CollectionWorkflow.Preview(value, ledger, Input() with { Stage = CollectionStage.FirstReminder }));
        var later = Ledger(invoice, asOf: Input().RespondBy.AddDays(1));
        var next = CollectionWorkflow.Preview(value, later, new(CollectionStage.FirstReminder, later.AsOf.AddDays(5), "Earlier response period elapsed"));
        Assert.Equal(CollectionStage.FirstReminder, next.Input.Stage); Assert.Equal(step.Digest, value.Steps[0].Digest); value.Validate();
    }

    [Fact]
    public void Contact_needs_the_approved_text_actual_utc_time_and_explicit_evidence()
    {
        var ledger = Ledger(BusinessFixture.Invoice()); var value = Open(ledger); Approve(value, ledger); var step = value.Steps.Single();
        AssertCode("collection_step_blocked", () => CollectionWorkflow.RecordContact(value, step.Id, new string('0', 64), CollectionContactChannel.Email, Now, Today, "Saved contact", Today, Actor, Now));
        AssertCode("invalid_collection_time", () => CollectionWorkflow.RecordContact(value, step.Id, step.Digest, CollectionContactChannel.Email, Now.AddMinutes(1), Today, "Saved contact", Today, Actor, Now));
        AssertCode("invalid_utc", () => CollectionWorkflow.RecordContact(value, step.Id, step.Digest, CollectionContactChannel.Email, Now.ToOffset(TimeSpan.FromHours(1)), Today, "Saved contact", Today, Actor, Now));
        AssertCode("required", () => CollectionWorkflow.RecordContact(value, step.Id, step.Digest, CollectionContactChannel.Email, Now, Today, "", Today, Actor, Now));
        Assert.Null(value.Steps[0].Contact);
    }

    [Fact]
    public void Manual_closure_and_reopen_preserve_all_financial_evidence()
    {
        var ledger = Ledger(BusinessFixture.Invoice()); var value = Open(ledger);
        AssertCode("collection_not_settled", () => CollectionWorkflow.Close(value, ledger, CollectionClosureKind.Settled, "Claim paid", "", Actor, Now));
        AssertCode("required", () => CollectionWorkflow.Close(value, ledger, CollectionClosureKind.ManualHandling, "Follow separately", "", Actor, Now));
        CollectionWorkflow.Close(value, ledger, CollectionClosureKind.ManualHandling, "Owner handles directly", "External case note 4", Actor, Now);
        Assert.Equal(CollectionCaseStatus.Closed, value.Status); Assert.Equal(119m, Assert.Single(value.Closures).Ledger.Balance.Outstanding);
        CollectionWorkflow.Reopen(value, ledger, Actor, Today.AddDays(1), "Resume documented follow up", "Owner request 5", Actor, Now.AddMinutes(1));
        Assert.Equal(CollectionCaseStatus.Active, value.Status); Assert.NotNull(value.Closures[0].ReopenedAt); Assert.Equal(3, value.Events.Count); value.Validate();
    }

    [Fact]
    public void Settled_closure_uses_current_sources_and_a_returned_payment_requires_explicit_reopen()
    {
        var invoice = BusinessFixture.Invoice(); var value = Open(Ledger(invoice));
        var payment = PaymentWorkflow.Allocate(invoice, BusinessFixture.Bank(119), [], [], 119, 0, 0, "", Actor, Now);
        var settled = Ledger(invoice, [payment]); CollectionWorkflow.Close(value, settled, CollectionClosureKind.Settled, "Recorded payment covers invoice", "Bank receipt", Actor, Now);
        PaymentWorkflow.Reverse(payment, "Payment assigned to wrong invoice", Actor, Now.AddMinutes(1));
        var open = Ledger(invoice, [payment]); Assert.Equal(119m, open.Balance.Outstanding); Assert.Equal(CollectionCaseStatus.Closed, value.Status);
        CollectionWorkflow.Reopen(value, open, Actor, Today.AddDays(1), "Allocation reversed", "Reversal evidence", Actor, Now.AddMinutes(2));
        Assert.Equal(0m, value.Closures[0].Ledger.Balance.Outstanding); Assert.Equal(CollectionCaseStatus.Active, value.Status);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("assignment")]
    [InlineData("event")]
    [InlineData("dispute")]
    public void Retained_case_mutation_is_detected(string field)
    {
        var ledger = Ledger(BusinessFixture.Invoice()); var value = Open(ledger); Approve(value, ledger);
        switch (field)
        {
            case "text": value.Steps[0] = value.Steps[0] with { Preview = value.Steps[0].Preview with { Text = "Changed amount" } }; break;
            case "assignment": value.AssignedToUserId = Guid.NewGuid(); break;
            case "event": value.Events[0] = value.Events[0] with { Reason = "Hidden rewrite" }; break;
            case "dispute":
                CollectionWorkflow.Dispute(value, ledger, 1, "Source in dispute", "Letter", Today.AddDays(1), Actor, Now);
                value.Disputes[0] = value.Disputes[0] with { Amount = 2 }; break;
        }
        AssertCode("collection_evidence_changed", value.Validate);
    }

    [Fact]
    public void Foreign_duplicate_and_over_budget_financial_sources_are_rejected()
    {
        var invoice = BusinessFixture.Invoice(); var ledger = Ledger(invoice);
        var foreign = BusinessFixture.Invoice(); foreign.TenantId = Guid.NewGuid();
        AssertCode("collection_source_metadata", () => CollectionWorkflow.CheckLedger(Open(ledger), Ledger(BusinessFixture.Invoice())));
        var source = Source(invoice);
        AssertCode("collection_source_metadata", () => CollectionLedger.Capture(invoice, [], [], [source, source], Today));
        Assert.Throws<DomainException>(() => CollectionLedger.Capture(invoice, [], [], Enumerable.Repeat(source, 1001).ToArray(), Today));
        var corrupted = ledger with { Balance = ledger.Balance with { Collectible = 1 } };
        AssertCode("collection_evidence_changed", corrupted.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(119.01)]
    [InlineData(0.001)]
    public void Invalid_disputed_amount_never_opens_a_case_hold(decimal amount)
    {
        var ledger = Ledger(BusinessFixture.Invoice()); var value = Open(ledger);
        Assert.Throws<DomainException>(() => CollectionWorkflow.Dispute(value, ledger, amount, "Dispute", "Letter", Today.AddDays(1), Actor, Now));
        Assert.Equal(CollectionCaseStatus.Active, value.Status); Assert.Empty(value.Disputes);
    }

    private static CollectionLedger Ledger(Invoice invoice, IReadOnlyList<PaymentAllocation>? allocations = null, IReadOnlyList<Invoice>? credits = null, DateOnly? asOf = null)
    {
        allocations ??= []; credits ??= [];
        var sources = new[] { Source(invoice) }.Concat(credits.Select(Source)).Concat(allocations.Select(x => new CollectionSource("payment-allocations", x.Id, x.Revision, ContentDigest.Of(x)))).ToArray();
        return CollectionLedger.Capture(invoice, allocations, credits, sources, asOf ?? Today);
    }
    private static CollectionSource Source(Invoice invoice) => new("invoices", invoice.Id, invoice.Revision, ContentDigest.Of(invoice));
    private static CollectionCase Open(CollectionLedger ledger) => CollectionWorkflow.Open(ledger, Actor, Today.AddDays(3), "Invoice independently reviewed", Actor, Now);
    private static CollectionStepInput Input() => new(CollectionStage.PaymentReminder, Today.AddDays(7), "Documents and ledger reviewed", "Please confirm your review.");
    private static void Approve(CollectionCase value, CollectionLedger ledger)
    {
        var input = Input(); var preview = CollectionWorkflow.Preview(value, ledger, input);
        CollectionWorkflow.Approve(value, ledger, input, preview.Digest, Actor, Now);
    }
    private static void AssertCode(string code, Action action) => Assert.Equal(code, Assert.Throws<DomainException>(action).Code);
}
