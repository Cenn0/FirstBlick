using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class LedgerTests
{
    [Fact]
    public void A_partial_payment_remains_open_and_can_be_reversed_without_losing_history()
    {
        var invoice = BusinessFixture.Invoice(); var bank = BusinessFixture.Bank(60m);
        var allocation = PaymentWorkflow.Allocate(invoice, bank, [], [], 60m, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now);
        var balance = PaymentWorkflow.Balance(invoice, [allocation], [], new(2026, 11, 4));
        Assert.Equal(59m, balance.Outstanding); Assert.True(balance.Overdue);
        PaymentWorkflow.Reverse(allocation, "Wrong invoice selected", BusinessFixture.Actor, BusinessFixture.Now.AddMinutes(1));
        Assert.Equal(119m, PaymentWorkflow.Balance(invoice, [allocation], [], new(2026, 11, 4)).Outstanding);
        Assert.Equal(60m, allocation.Amount); Assert.NotNull(allocation.ReversedAt);
        Assert.Equal("already_reversed", Assert.Throws<DomainException>(() => PaymentWorkflow.Reverse(allocation, "Again", BusinessFixture.Actor, BusinessFixture.Now.AddMinutes(2))).Code);
    }

    [Fact]
    public void Combined_payment_cannot_be_allocated_twice_across_invoices()
    {
        var first = BusinessFixture.Invoice(); var second = BusinessFixture.Invoice(); var bank = BusinessFixture.Bank(200m);
        var a = PaymentWorkflow.Allocate(first, bank, [], [], 119m, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now);
        Assert.Equal("bank_overallocated", Assert.Throws<DomainException>(() => PaymentWorkflow.Allocate(second, bank, [a], [], 82m, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now)).Code);
        var b = PaymentWorkflow.Allocate(second, bank, [a], [], 81m, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now);
        Assert.Equal(38m, PaymentWorkflow.Balance(second, [a, b], [], new(2026, 10, 20)).Outstanding);
    }

    [Fact]
    public void Retainage_is_outstanding_but_not_collectible_until_released()
    {
        var invoice = BusinessFixture.Invoice(); var bank = BusinessFixture.Bank(100m);
        var a = PaymentWorkflow.Allocate(invoice, bank, [], [], 100m, 0, 19m, "Retention agreed", BusinessFixture.Actor, BusinessFixture.Now);
        var held = PaymentWorkflow.Balance(invoice, [a], [], new(2026, 11, 4));
        Assert.Equal(19m, held.Outstanding); Assert.Equal(0m, held.Collectible); Assert.False(held.Overdue);
        PaymentWorkflow.ReleaseRetention(a, 10m, "Partial retention released", BusinessFixture.Actor, BusinessFixture.Now.AddDays(1));
        Assert.Equal(10m, PaymentWorkflow.Balance(invoice, [a], [], new(2026, 11, 4)).Collectible);
        var b = PaymentWorkflow.Allocate(invoice, BusinessFixture.Bank(10m, "second"), [a], [], 10m, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now.AddDays(1));
        var remaining = PaymentWorkflow.Balance(invoice, [a, b], [], new(2026, 11, 4));
        Assert.Equal(9m, remaining.Outstanding); Assert.Equal(9m, remaining.Retained);
    }

    [Fact]
    public void Discount_requires_reason_and_does_not_create_extra_cash_on_full_correction()
    {
        var invoice = BusinessFixture.Invoice(); var bank = BusinessFixture.Bank(117m);
        Assert.Throws<DomainException>(() => PaymentWorkflow.Allocate(invoice, bank, [], [], 117m, 2m, 0, "", BusinessFixture.Actor, BusinessFixture.Now));
        var receipt = PaymentWorkflow.Allocate(invoice, bank, [], [], 117m, 2m, 0, "Agreed discount", BusinessFixture.Actor, BusinessFixture.Now);
        Assert.Equal(0m, PaymentWorkflow.Balance(invoice, [receipt], [], new(2026, 10, 20)).Outstanding);
        var credit = InvoiceWorkflow.DraftCreditNote(invoice, "Full cancellation"); credit.DueDate = new(2026, 10, 20);
        InvoiceWorkflow.IssueCreditNote(credit, invoice, [], "C1", new(2026, 10, 20));
        Assert.Equal(117m, PaymentWorkflow.Balance(invoice, [receipt], [credit], new(2026, 10, 20)).CustomerCredit);
        var refund = PaymentWorkflow.AllocateRefund(invoice, BusinessFixture.Bank(-117m, "refund-1"), [receipt], [credit], 117m, "Refund of cancelled invoice", BusinessFixture.Actor, BusinessFixture.Now);
        var balance = PaymentWorkflow.Balance(invoice, [receipt, refund], [credit], new(2026, 10, 20));
        Assert.Equal(0m, balance.CustomerCredit); Assert.Equal(0m, balance.Outstanding); Assert.Equal(0m, balance.Paid);
    }

    [Fact]
    public void Refund_without_customer_credit_is_rejected()
    {
        var invoice = BusinessFixture.Invoice();
        Assert.Equal("excess_refund", Assert.Throws<DomainException>(() => PaymentWorkflow.AllocateRefund(invoice, BusinessFixture.Bank(-10m), [], [], 10m, "Refund", BusinessFixture.Actor, BusinessFixture.Now)).Code);
    }

    [Fact]
    public void Returned_payment_reopens_invoice_and_cannot_reuse_same_return_bank_money()
    {
        var invoice = BusinessFixture.Invoice(100, 0); var bank = BusinessFixture.Bank(100);
        var a = PaymentWorkflow.Allocate(invoice, bank, [], [], 50, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now);
        var b = PaymentWorkflow.Allocate(invoice, bank, [a], [], 50, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now);
        var returned = BusinessFixture.Bank(-50, "return-1");
        PaymentWorkflow.ReverseForReturn(a, returned, [a, b], "Bank returned payment", BusinessFixture.Actor, BusinessFixture.Now.AddDays(1));
        Assert.Equal(50m, PaymentWorkflow.Balance(invoice, [a, b], [], new(2026, 10, 21)).Outstanding);
        Assert.Equal(returned.Id, a.ReturnBankTransactionId);
        Assert.Equal("bank_overallocated", Assert.Throws<DomainException>(() => PaymentWorkflow.ReverseForReturn(b, returned, [a, b], "Second return", BusinessFixture.Actor, BusinessFixture.Now.AddDays(1))).Code);
    }

    [Fact]
    public void Mutation_of_imported_bank_amount_is_detected()
    {
        var bank = BusinessFixture.Bank(100m); bank.Amount = 200m;
        Assert.Equal("bank_immutable", Assert.Throws<DomainException>(bank.Validate).Code);
    }

    [Fact]
    public void Mutation_of_posted_allocation_amount_is_detected()
    {
        var invoice = BusinessFixture.Invoice();
        var allocation = PaymentWorkflow.Allocate(invoice, BusinessFixture.Bank(100m), [], [], 50, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now);
        allocation.Amount = 60;
        Assert.Equal("allocation_immutable", Assert.Throws<DomainException>(allocation.Validate).Code);
    }

    [Fact]
    public void Financial_inputs_reject_subcent_cash_and_unsealed_or_wrong_direction_bank_entries()
    {
        var invoice = BusinessFixture.Invoice();
        Assert.Equal("invalid_money_precision", Assert.Throws<DomainException>(() => PaymentWorkflow.Allocate(invoice, BusinessFixture.Bank(100m), [], [], 1.001m, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now)).Code);
        var bank = BusinessFixture.Bank(100m); bank.ImportDigest = "";
        Assert.Equal("payment_mismatch", Assert.Throws<DomainException>(() => PaymentWorkflow.Allocate(invoice, bank, [], [], 50, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now)).Code);
        Assert.Throws<DomainException>(() => PaymentWorkflow.Allocate(invoice, BusinessFixture.Bank(-100m), [], [], 50, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now));
    }
}
