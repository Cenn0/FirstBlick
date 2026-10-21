using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class CommercialTests
{
    [Fact]
    public void Frozen_prices_capture_effective_labor_material_markup_and_discount()
    {
        var input = new QuoteLine { Description = "Test", Quantity = 3m, UnitMaterialCost = 10m, UnitLaborHours = .25m, HourlyRate = 60m, MarkupPercent = 20m, DiscountPercent = 10m, VatPercent = 19m };
        var result = Pricing.Freeze([input]);
        Assert.Equal(25m, result.Lines[0].UnitCost); Assert.Equal(27m, result.Lines[0].UnitNet);
        Assert.Equal(81m, result.Net); Assert.Equal(15.39m, result.Tax); Assert.Equal(96.39m, result.Gross);
        input.HourlyRate = 200m; Assert.Equal(60m, result.Lines[0].Inputs.HourlyRate);
        result.Validate();
    }

    [Fact]
    public void Rounding_policy_is_unit_then_line_then_line_tax_away_from_zero()
    {
        var result = Pricing.Freeze([new() { Description = "Precision", Quantity = 3, UnitMaterialCost = 10.005m, VatPercent = 19 }]);
        Assert.Equal(10.01m, result.Lines[0].UnitNet); Assert.Equal(30.03m, result.Net); Assert.Equal(5.71m, result.Tax); Assert.Equal(35.74m, result.Gross);
        Assert.Equal(-1.01m, Money.Round(-1.005m));
    }

    [Fact]
    public void Each_tax_line_is_rounded_and_no_implicit_tax_rate_is_added()
    {
        var result = Pricing.Freeze([new() { Description = "Zero rate", Quantity = 1, UnitMaterialCost = 10 }, new() { Description = "Input rate", Quantity = 1, UnitMaterialCost = 10, VatPercent = 7 }]);
        Assert.Equal(.7m, result.Tax); Assert.Equal(20.7m, result.Gross);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1000001")]
    [InlineData("0.0000001")]
    public void Invalid_or_excess_precision_quantity_is_rejected(string quantity)
    {
        var line = new QuoteLine { Description = "Invalid", Quantity = decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture) };
        Assert.Throws<DomainException>(() => Pricing.Freeze([line]));
    }

    [Fact]
    public void Accepted_quote_status_survives_sending_and_delivery_uncertainty()
    {
        var quote = BusinessFixture.Quote(); CommercialWorkflow.IssueQuote(quote, "Q1");
        CommercialWorkflow.AcceptQuote(quote, quote.Snapshot!.Digest, BusinessFixture.Now, new(2026, 10, 20));
        var attempt = CommercialWorkflow.RequestDelivery(quote, CommunicationChannel.Email, "buyer@example.invalid", BusinessFixture.Now);
        CommercialWorkflow.BeginDelivery(attempt); CommercialWorkflow.MarkDeliveryUncertain(attempt);
        Assert.Equal(QuoteStatus.Accepted, quote.Status);
        Assert.Equal("unresolved_delivery", Assert.Throws<DomainException>(() => CommercialWorkflow.RequestDelivery(quote, CommunicationChannel.Email, "buyer@example.invalid", BusinessFixture.Now)).Code);
        CommercialWorkflow.ResolveDelivery(attempt, true, "Provider lookup confirmed delivery", "provider-1", BusinessFixture.Now.AddMinutes(1));
        Assert.Equal(QuoteStatus.Accepted, quote.Status); Assert.Equal(DeliveryState.Sent, attempt.State);
        quote.Validate();
    }

    [Fact]
    public void Altering_an_issued_input_invalidates_the_frozen_quote()
    {
        var quote = BusinessFixture.Quote(); CommercialWorkflow.IssueQuote(quote, "Q1"); quote.Lines[0].HourlyRate = 100;
        Assert.Equal("snapshot_changed", Assert.Throws<DomainException>(quote.Validate).Code);
    }

    [Fact]
    public void Altering_a_snapshot_output_is_detected()
    {
        var quote = BusinessFixture.Quote(); CommercialWorkflow.IssueQuote(quote, "Q1");
        quote.Snapshot = quote.Snapshot! with { Gross = 1m };
        Assert.Equal("snapshot_changed", Assert.Throws<DomainException>(quote.Validate).Code);
    }

    [Fact]
    public void Acceptance_checks_digest_expiry_and_contractual_state()
    {
        var quote = BusinessFixture.Quote(); quote.ValidUntil = new(2026, 10, 19); CommercialWorkflow.IssueQuote(quote, "Q1");
        Assert.Equal("stale_content", Assert.Throws<DomainException>(() => CommercialWorkflow.AcceptQuote(quote, "stale", BusinessFixture.Now, new(2026, 10, 20))).Code);
        Assert.Equal("quote_expired", Assert.Throws<DomainException>(() => CommercialWorkflow.AcceptQuote(quote, quote.Snapshot!.Digest, BusinessFixture.Now, new(2026, 10, 20))).Code);
        Assert.Equal(QuoteStatus.Issued, quote.Status);
        Assert.Equal("quote_not_accepted", Assert.Throws<DomainException>(() => CommercialWorkflow.CreateOrder(quote, "O1")).Code);
    }

    [Fact]
    public void Order_snapshot_is_independent_of_the_source_object()
    {
        var quote = BusinessFixture.Quote(); CommercialWorkflow.IssueQuote(quote, "Q1");
        CommercialWorkflow.AcceptQuote(quote, quote.Snapshot!.Digest, BusinessFixture.Now, new(2026, 10, 20));
        var order = CommercialWorkflow.CreateOrder(quote, "O1");
        quote.Snapshot.Lines[0].Inputs.UnitMaterialCost = 1000m;
        Assert.Equal(100m, order.Contract!.Lines[0].Inputs.UnitMaterialCost); order.Validate();
    }

    [Fact]
    public void Invoice_rejects_post_issue_mutation_including_customer_address()
    {
        var invoice = BusinessFixture.Invoice(); invoice.BuyerAddress = "Replaced address";
        Assert.Equal("invoice_immutable", Assert.Throws<DomainException>(invoice.Validate).Code);
    }

    [Fact]
    public void Progress_and_final_invoices_cannot_bill_the_order_twice()
    {
        var order = BusinessFixture.Order(100, vat: 0); var progress = BusinessFixture.Draft(order, InvoiceKind.Progress);
        progress.Lines[0].Quantity = .4m; InvoiceWorkflow.IssueForOrder(progress, order, [], [], "P1", new(2026, 10, 20));
        var final = BusinessFixture.Draft(order);
        Assert.Equal("order_overbilled", Assert.Throws<DomainException>(() => InvoiceWorkflow.IssueForOrder(final, order, [progress], [], "F1", new(2026, 10, 20))).Code);
        final.Lines[0].Quantity = .5m;
        Assert.Equal("incomplete_final_invoice", Assert.Throws<DomainException>(() => InvoiceWorkflow.IssueForOrder(final, order, [progress], [], "F1", new(2026, 10, 20))).Code);
        final.Lines[0].Quantity = .6m; InvoiceWorkflow.IssueForOrder(final, order, [progress], [], "F1", new(2026, 10, 20));
        Assert.Equal(60m, final.Snapshot!.Gross);
    }

    [Fact]
    public void Cumulative_credit_notes_are_limited_to_original_invoice()
    {
        var original = BusinessFixture.Invoice(100, vat: 0);
        var first = InvoiceWorkflow.DraftCreditNote(original, "Partial correction"); first.Lines[0].Quantity = .6m; first.DueDate = new(2026, 10, 20);
        InvoiceWorkflow.IssueCreditNote(first, original, [], "C1", new(2026, 10, 20));
        var second = InvoiceWorkflow.DraftCreditNote(original, "Second correction"); second.DueDate = new(2026, 10, 20); second.Lines[0].Quantity = .5m;
        Assert.Equal("excess_credit", Assert.Throws<DomainException>(() => InvoiceWorkflow.IssueCreditNote(second, original, [first], "C2", new(2026, 10, 20))).Code);
        Assert.Equal(InvoiceStatus.Draft, second.Status);
    }
}
