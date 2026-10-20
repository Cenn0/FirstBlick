using System.Text.Json;
using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class InvoiceCumulativeRoundingTests
{
    private static readonly DateOnly Day = new(2026, 10, 20);

    [Fact]
    public void Final_invoice_carries_the_contract_tax_cent_after_a_partial_invoice()
    {
        var order = BusinessFixture.Order(.01m, 3m, 19m);
        var progress = Issue(order, InvoiceKind.Progress, 1m, [], "P1");
        var final = Issue(order, InvoiceKind.Final, 2m, [progress], "F1");
        Assert.Equal(.01m, progress.Snapshot!.Gross);
        Assert.Equal((.02m, .01m, .03m), (final.Snapshot!.Net, final.Snapshot.Tax, final.Snapshot.Gross));
        Assert.Equal(order.Contract!.Gross, progress.Snapshot.Gross + final.Snapshot.Gross);
        Assert.Equal((1m, 3m, 0m, .01m), (final.Calculation!.Lines[0].BeforeQuantity,
            final.Calculation.Lines[0].AfterQuantity, final.Calculation.Lines[0].BeforeTax, final.Calculation.Lines[0].AfterTax));
        final.Validate();
    }

    [Fact]
    public void Three_partial_quantities_allocate_tax_cents_as_1_2_1_not_1_1_1()
    {
        var order = BusinessFixture.Order(.07m, 3m, 19m);
        var first = Issue(order, InvoiceKind.Progress, 1, [], "P1");
        var second = Issue(order, InvoiceKind.Progress, 1, [first], "P2");
        var final = Issue(order, InvoiceKind.Final, 1, [first, second], "F1");
        Assert.Equal(new[] { .01m, .02m, .01m }, new[] { first.Snapshot!.Tax, second.Snapshot!.Tax, final.Snapshot!.Tax });
        Assert.Equal((.21m, .04m, .25m), (first.Snapshot.Net + second.Snapshot.Net + final.Snapshot.Net,
            first.Snapshot.Tax + second.Snapshot.Tax + final.Snapshot.Tax, first.Snapshot.Gross + second.Snapshot.Gross + final.Snapshot.Gross));
    }

    [Fact]
    public void Fractional_quantities_carry_both_net_and_tax_without_repricing_prior_invoice()
    {
        var order = BusinessFixture.Order(.03m, 1m, 19m);
        var progress = Issue(order, InvoiceKind.Progress, .5m, [], "P1"); var digest = progress.IssuedDigest;
        var final = Issue(order, InvoiceKind.Final, .5m, [progress], "F1");
        Assert.Equal((.02m, 0m), (progress.Snapshot!.Net, progress.Snapshot.Tax));
        Assert.Equal((.01m, .01m), (final.Snapshot!.Net, final.Snapshot.Tax));
        Assert.Equal(.03m, final.Snapshot.Lines[0].UnitNet);
        Assert.Equal(digest, progress.IssuedDigest); progress.Validate(); final.Validate();
    }

    [Fact]
    public void Full_quantities_across_two_corrections_reverse_the_original_tax_cent()
    {
        var order = BusinessFixture.Order(.01m, 3m, 19m);
        var original = Issue(order, InvoiceKind.Final, 3m, [], "I1");
        var first = Credit(original, 1m, [], "C1");
        var remainder = Credit(original, 2m, [first], "C2");
        Assert.Equal(original.Snapshot!.Net, first.Snapshot!.Net + remainder.Snapshot!.Net);
        Assert.Equal(original.Snapshot.Tax, first.Snapshot.Tax + remainder.Snapshot.Tax);
        Assert.Equal(original.Snapshot.Gross, first.Snapshot.Gross + remainder.Snapshot.Gross);
        Assert.Equal(InvoiceCalculationBasis.OriginalInvoice, remainder.Calculation!.Basis);
        remainder.Validate();
    }

    [Fact]
    public void Corrections_of_a_tax_adjusted_partial_invoice_use_its_actual_amounts()
    {
        var order = BusinessFixture.Order(.01m, 3m, 19m);
        var progress = Issue(order, InvoiceKind.Progress, 1m, [], "P1");
        var original = Issue(order, InvoiceKind.Final, 2m, [progress], "F1");
        var first = Credit(original, 1m, [], "C1");
        var second = Credit(original, 1m, [first], "C2");
        Assert.Equal((.01m, .01m), (first.Snapshot!.Net, first.Snapshot.Tax));
        Assert.Equal((.01m, 0m), (second.Snapshot!.Net, second.Snapshot.Tax));
        Assert.Equal(original.Snapshot!.Gross, first.Snapshot.Gross + second.Snapshot.Gross);
        Assert.Equal(original.IssuedDigest, second.Calculation!.Sources.Single().DocumentDigest);
        second.Validate();
    }

    [Fact]
    public void Rebilling_after_complete_correction_uses_zero_actual_previous_balance()
    {
        var order = BusinessFixture.Order(.01m, 3m, 19m);
        var progress = Issue(order, InvoiceKind.Progress, 1m, [], "P1");
        var credit = Credit(progress, 1m, [], "C1");
        var final = Issue(order, InvoiceKind.Final, 3m, [progress, credit], "F1");
        Assert.Equal((.03m, .01m), (final.Snapshot!.Net, final.Snapshot.Tax));
        Assert.Equal((0m, 0m, 0m), (final.Calculation!.PreviousBalances.Single().Quantity,
            final.Calculation.PreviousBalances.Single().Net, final.Calculation.PreviousBalances.Single().Tax));
    }

    [Fact]
    public void Equal_source_positions_retain_their_separate_original_rounding()
    {
        var quote = BusinessFixture.Quote(.03m, 1m, 19m); quote.Lines.Add(quote.Lines[0] with { });
        var order = Accepted(quote);
        var first = Issue(order, InvoiceKind.Progress, 1.5m, [], "P1");
        var final = Issue(order, InvoiceKind.Final, .5m, [first], "F1");
        Assert.Equal((.06m, .02m), (order.Contract!.Net, order.Contract.Tax));
        Assert.Equal((.05m, .01m), (first.Snapshot!.Net, first.Snapshot.Tax));
        Assert.Equal((.01m, .01m), (final.Snapshot!.Net, final.Snapshot.Tax));
        Assert.Equal(2, final.Calculation!.Lines.Single().Positions.Count);
        Assert.Equal(new[] { 0, 1 }, final.Calculation.Lines.Single().Positions.Select(x => x.PositionIndex));
    }

    [Fact]
    public void An_accepted_change_retains_its_own_rounding_and_source_identity()
    {
        var order = BusinessFixture.Order(.03m, 1m, 19m);
        var change = new ChangeOrder { TenantId = order.TenantId, OrderId = order.Id, Title = "Additional position", Reason = "Agreed addition",
            Lines = [order.Contract!.Lines.Single().Inputs with { }] };
        CommercialWorkflow.AcceptChange(change, BusinessFixture.Now);
        var draft = BusinessFixture.Draft(order); draft.Lines[0].Quantity = 2m;
        InvoiceWorkflow.IssueForOrder(draft, order, [], [change], "F1", Day);
        Assert.Equal((.06m, .02m), (draft.Snapshot!.Net, draft.Snapshot.Tax));
        Assert.Equal(new[] { "orders", "change-orders" }, draft.Calculation!.Sources.Select(x => x.Kind));
        Assert.Contains(new EntityReference("change-orders", change.Id), draft.References()); draft.Validate();
    }

    [Fact]
    public void Distinct_tax_rates_are_never_offset_against_each_other()
    {
        var quote = BusinessFixture.Quote(.03m, 1m, 19m);
        quote.Lines.Add(quote.Lines[0] with { VatPercent = 7m }); var order = Accepted(quote);
        var first = BusinessFixture.Draft(order, InvoiceKind.Progress); foreach (var line in first.Lines) line.Quantity = .5m;
        InvoiceWorkflow.IssueForOrder(first, order, [], [], "P1", Day);
        var final = BusinessFixture.Draft(order); foreach (var line in final.Lines) line.Quantity = .5m;
        InvoiceWorkflow.IssueForOrder(final, order, [first], [], "F1", Day);
        Assert.Equal(new[] { .01m, 0m }, final.Snapshot!.Lines.Select(x => x.Tax));
        Assert.Equal(new[] { .01m, .01m }, final.Snapshot.Lines.Select(x => x.Net));
        Assert.Equal(2, final.Calculation!.PreviousBalances.Count);
    }

    [Fact]
    public void Legacy_invoice_is_preserved_and_new_final_invoice_accounts_for_its_actual_cents()
    {
        var order = BusinessFixture.Order(.03m, 1m, 19m);
        var legacy = BusinessFixture.Draft(order, InvoiceKind.Progress); legacy.Lines[0].Quantity = .5m;
        InvoiceWorkflow.Issue(legacy, "LEGACY", Day);
        var digest = legacy.IssuedDigest;
        var final = Issue(order, InvoiceKind.Final, .5m, [legacy], "F1");
        Assert.Null(legacy.Calculation); Assert.Equal(digest, InvoiceWorkflow.Digest(legacy)); legacy.Validate();
        Assert.Equal((.01m, .01m), (final.Snapshot!.Net, final.Snapshot.Tax));
        Assert.Contains(legacy.Id, final.References().Where(x => x.Kind == "invoices").Select(x => x.Id));
    }

    [Fact]
    public void Multiple_legacy_partial_invoices_settle_to_the_original_contract_cents()
    {
        var order = BusinessFixture.Order(.03m, 1m, 19m);
        var first = BusinessFixture.Draft(order, InvoiceKind.Progress); first.Lines[0].Quantity = .4m; InvoiceWorkflow.Issue(first, "P1", Day);
        var second = BusinessFixture.Draft(order, InvoiceKind.Progress); second.Lines[0].Quantity = .4m; InvoiceWorkflow.Issue(second, "P2", Day);
        // Each historical tax is zero and each net is .01; final .2 supplies .01 net and .01 tax.
        var final = Issue(order, InvoiceKind.Final, .2m, [first, second], "F1");
        Assert.Equal((.01m, .01m), (final.Snapshot!.Net, final.Snapshot.Tax));
        Assert.Equal(.02m, final.Calculation!.Lines.Single().BeforeNet);
        Assert.Equal(.02m, final.Calculation.Lines.Single().ExpectedBeforeNet);
    }

    [Fact]
    public void Prior_rounding_difference_is_visible_and_not_silently_rewritten()
    {
        var order = BusinessFixture.Order(.01m, 3m, 19m);
        var legacy = BusinessFixture.Draft(order, InvoiceKind.Progress);
        legacy.Lines = [legacy.Lines[0] with { Quantity = .5m }, legacy.Lines[0] with { Quantity = .5m }];
        InvoiceWorkflow.Issue(legacy, "P1", Day); // .02 actual net, whereas one cumulative unit is .01.
        var final = Issue(order, InvoiceKind.Final, 2m, [legacy], "F1");
        Assert.Equal((.01m, .01m), (final.Snapshot!.Net, final.Snapshot.Tax));
        Assert.Equal((.02m, .01m), (final.Calculation!.Lines.Single().BeforeNet, final.Calculation.Lines.Single().ExpectedBeforeNet));
        Assert.Equal(.02m, legacy.Snapshot!.Net); legacy.Validate();
    }

    [Fact]
    public void Previously_overbilled_legacy_cents_are_rejected_without_negative_adjustment()
    {
        var order = BusinessFixture.Order(.01m, 1m, 0m); var legacy = BusinessFixture.Draft(order, InvoiceKind.Progress);
        legacy.Lines = [legacy.Lines[0] with { Quantity = .5m }, legacy.Lines[0] with { Quantity = .5m }];
        InvoiceWorkflow.Issue(legacy, "P1", Day);
        var draft = BusinessFixture.Draft(order, InvoiceKind.Progress); draft.Lines[0].Quantity = .1m;
        Assert.Equal("invoice_rounding_conflict", Assert.Throws<DomainException>(() => InvoiceWorkflow.IssueForOrder(draft, order, [legacy], [], "P2", Day)).Code);
        Assert.Equal(InvoiceStatus.Draft, draft.Status); Assert.Null(draft.Snapshot); Assert.Null(draft.Calculation);
    }

    [Fact]
    public void Zero_cent_remaining_invoice_is_explicitly_rejected_without_mutation()
    {
        var order = BusinessFixture.Order(.01m, 1m, 0m); var first = Issue(order, InvoiceKind.Progress, .5m, [], "P1");
        var final = BusinessFixture.Draft(order); final.Lines[0].Quantity = .5m;
        Assert.Equal("zero_invoice", Assert.Throws<DomainException>(() => InvoiceWorkflow.IssueForOrder(final, order, [first], [], "F1", Day)).Code);
        Assert.Equal(InvoiceStatus.Draft, final.Status); Assert.Null(final.Calculation); Assert.Null(final.Snapshot); Assert.Equal("", final.IssuedDigest);
    }

    [Fact]
    public void Altered_source_or_previous_balance_invalidates_the_frozen_calculation()
    {
        var order = BusinessFixture.Order(.03m, 1m, 19m); var progress = Issue(order, InvoiceKind.Progress, .5m, [], "P1");
        var final = Issue(order, InvoiceKind.Final, .5m, [progress], "F1");
        var calculation = final.Calculation!;
        final.Calculation = calculation with { PreviousBalances = [calculation.PreviousBalances[0] with { Net = 0m }] };
        Assert.Equal("invoice_calculation_changed", Assert.Throws<DomainException>(final.Validate).Code);
        final.Calculation = calculation;
        final.Calculation.Sources[0].Snapshot.Lines[0].Inputs.Description = "Changed source";
        Assert.Equal("invoice_calculation_changed", Assert.Throws<DomainException>(final.Validate).Code);
    }

    [Fact]
    public void Source_snapshot_is_independent_and_rehashed_wrong_output_is_still_rejected()
    {
        var order = BusinessFixture.Order(.03m, 1m, 19m); var original = Issue(order, InvoiceKind.Final, 1m, [], "I1");
        order.Contract!.Lines[0].Inputs.UnitMaterialCost = 900m;
        original.Validate(); Assert.Equal(.03m, original.Calculation!.Sources[0].Snapshot.Lines[0].UnitNet);
        var calculation = original.Calculation;
        calculation = calculation with { Lines = [calculation.Lines[0] with { AfterTax = 0m }], Digest = "" };
        original.Calculation = calculation with { Digest = ContentDigest.Of(calculation) };
        original.IssuedDigest = InvoiceWorkflow.Digest(original);
        Assert.Equal("invoice_calculation_changed", Assert.Throws<DomainException>(original.Validate).Code);
    }

    [Fact]
    public void Duplicate_history_and_duplicate_changes_cannot_inflate_the_calculation_basis()
    {
        var order = BusinessFixture.Order(100m, 2m, 0m); var prior = Issue(order, InvoiceKind.Progress, 1m, [], "P1");
        var final = BusinessFixture.Draft(order); final.Lines[0].Quantity = 1m;
        Assert.Equal("duplicate_invoice", Assert.Throws<DomainException>(() => InvoiceWorkflow.IssueForOrder(final, order, [prior, prior], [], "F1", Day)).Code);
        var correction = InvoiceWorkflow.DraftCreditNote(prior, "Duplicate regression"); correction.DueDate = Day;
        var firstCredit = Credit(prior, .5m, [], "C1");
        Assert.Equal("duplicate_invoice", Assert.Throws<DomainException>(() => InvoiceWorkflow.IssueCreditNote(correction, prior, [firstCredit, firstCredit], "C2", Day)).Code);
        var change = new ChangeOrder { TenantId = order.TenantId, OrderId = order.Id, Title = "Additional work", Reason = "Duplicate source regression",
            Lines = [order.Contract!.Lines[0].Inputs with { }] };
        CommercialWorkflow.AcceptChange(change, BusinessFixture.Now);
        Assert.Equal("duplicate_change", Assert.Throws<DomainException>(() => InvoiceWorkflow.IssueForOrder(final, order, [prior], [change, change], "F1", Day)).Code);
    }

    [Fact]
    public void A_credit_without_its_original_cannot_reduce_the_order_billed_balance()
    {
        var order = BusinessFixture.Order(); var original = Issue(order, InvoiceKind.Final, 1m, [], "I1");
        var credit = Credit(original, 1m, [], "C1"); var draft = BusinessFixture.Draft(order);
        Assert.Equal("missing_original_invoice", Assert.Throws<DomainException>(() => InvoiceWorkflow.IssueForOrder(draft, order, [credit], [], "I2", Day)).Code);
    }

    [Fact]
    public void Drafts_cannot_supply_server_generated_calculation_evidence()
    {
        var order = BusinessFixture.Order(); var original = Issue(order, InvoiceKind.Final, 1m, [], "I1");
        var draft = BusinessFixture.Draft(order); draft.Calculation = original.Calculation;
        Assert.Equal("command_required", Assert.Throws<DomainException>(draft.Validate).Code);
    }

    [Fact]
    public void Repeated_equal_positions_cannot_expand_a_small_input_into_unbounded_evidence()
    {
        var quote = BusinessFixture.Quote(.03m, 1m, 19m);
        quote.Lines = Enumerable.Range(0, 101).Select(_ => quote.Lines[0] with { }).ToList();
        var order = Accepted(quote); var draft = BusinessFixture.Draft(order);
        Assert.Equal("invoice_evidence_budget", Assert.Throws<DomainException>(() => InvoiceWorkflow.IssueForOrder(draft, order, [], [], "I1", Day)).Code);
        Assert.Null(draft.Calculation); Assert.Null(draft.Snapshot);
    }

    [Fact]
    public void Json_round_trip_preserves_all_decimal_evidence_and_rejects_removed_calculation()
    {
        var order = BusinessFixture.Order(.03m, 1m, 19m); var original = Issue(order, InvoiceKind.Final, 1m, [], "I1");
        var json = JsonSerializer.Serialize(original); var reloaded = JsonSerializer.Deserialize<Invoice>(json)!;
        reloaded.Validate(); Assert.Equal(original.IssuedDigest, reloaded.IssuedDigest);
        Assert.Equal(original.Calculation!.Digest, reloaded.Calculation!.Digest);
        reloaded.Calculation = null;
        Assert.Equal("invoice_immutable", Assert.Throws<DomainException>(reloaded.Validate).Code);
    }

    private static Order Accepted(Quote quote)
    {
        CommercialWorkflow.IssueQuote(quote, "Q1");
        CommercialWorkflow.AcceptQuote(quote, quote.Snapshot!.Digest, BusinessFixture.Now, Day);
        return CommercialWorkflow.CreateOrder(quote, "O1");
    }

    private static Invoice Issue(Order order, InvoiceKind kind, decimal quantity, Invoice[] previous, string number)
    {
        var invoice = BusinessFixture.Draft(order, kind); invoice.Lines = [invoice.Lines[0] with { Quantity = quantity }];
        InvoiceWorkflow.IssueForOrder(invoice, order, previous, [], number, Day); return invoice;
    }

    private static Invoice Credit(Invoice original, decimal quantity, Invoice[] previous, string number)
    {
        var credit = InvoiceWorkflow.DraftCreditNote(original, "Synthetic correction"); credit.DueDate = Day; credit.Lines[0].Quantity = quantity;
        InvoiceWorkflow.IssueCreditNote(credit, original, previous, number, Day); return credit;
    }
}
