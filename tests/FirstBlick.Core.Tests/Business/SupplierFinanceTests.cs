using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class SupplierFinanceTests
{
    [Fact]
    public void Configured_independent_approval_rejects_self_approval_without_changing_bill()
    {
        var (supplier, bill) = DraftBill();
        Assert.Equal("self_approval", Assert.Throws<DomainException>(() => PayablesWorkflow.Approve(bill, supplier, BusinessFixture.Actor, BusinessFixture.Actor, BusinessFixture.Now, true)).Code);
        Assert.Equal(SupplierInvoiceStatus.Draft, bill.Status); Assert.Null(bill.ApprovedBy);
        PayablesWorkflow.Approve(bill, supplier, Guid.NewGuid(), BusinessFixture.Actor, BusinessFixture.Now, true);
        Assert.Equal(SupplierInvoiceStatus.Approved, bill.Status); Assert.Equal(supplier.Name, bill.SupplierName);
        bill.Number = "Changed"; Assert.Equal("supplier_invoice_immutable", Assert.Throws<DomainException>(bill.Validate).Code);
    }

    [Fact]
    public void Supplier_payment_records_outgoing_bank_data_and_rejects_wrong_direction()
    {
        var bill = ApprovedBill(); var bank = BusinessFixture.Bank(-119m);
        Assert.Equal("payment_mismatch", Assert.Throws<DomainException>(() => PayablesWorkflow.Allocate(bill, BusinessFixture.Bank(119m), [], [], [], 119m, 0, "", SupplierPaymentDirection.Payment, BusinessFixture.Actor, BusinessFixture.Now)).Code);
        var payment = PayablesWorkflow.Allocate(bill, bank, [], [], [], 60m, 0, "", SupplierPaymentDirection.Payment, BusinessFixture.Actor, BusinessFixture.Now);
        Assert.Equal(59m, PayablesWorkflow.Balance(bill, [payment], [], new(2026, 11, 4)).Outstanding);
        PayablesWorkflow.Reverse(payment, "Wrong bill", BusinessFixture.Actor, BusinessFixture.Now.AddMinutes(1));
        Assert.Equal(119m, PayablesWorkflow.Balance(bill, [payment], [], new(2026, 11, 4)).Outstanding);
    }

    [Fact]
    public void Customer_refunds_and_supplier_payments_share_one_bank_capacity()
    {
        var customerInvoice = BusinessFixture.Invoice();
        var receipt = PaymentWorkflow.Allocate(customerInvoice, BusinessFixture.Bank(119m), [], [], 119m, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now);
        var credit = InvoiceWorkflow.DraftCreditNote(customerInvoice, "Cancellation"); credit.DueDate = new(2026, 10, 20);
        InvoiceWorkflow.IssueCreditNote(credit, customerInvoice, [], "C1", new(2026, 10, 20));
        var bank = BusinessFixture.Bank(-200m, "outgoing-shared");
        var refund = PaymentWorkflow.AllocateRefund(customerInvoice, bank, [receipt], [credit], 119m, "Refund", BusinessFixture.Actor, BusinessFixture.Now);
        var bill = ApprovedBill();
        Assert.Equal("bank_overallocated", Assert.Throws<DomainException>(() => PayablesWorkflow.Allocate(bill, bank, [], [receipt, refund], [], 82m, 0, "", SupplierPaymentDirection.Payment, BusinessFixture.Actor, BusinessFixture.Now)).Code);
        var payment = PayablesWorkflow.Allocate(bill, bank, [], [receipt, refund], [], 81m, 0, "", SupplierPaymentDirection.Payment, BusinessFixture.Actor, BusinessFixture.Now);
        Assert.Equal(200m, PaymentWorkflow.UsedBankAmount(bank, [receipt, refund], [payment]));
    }

    [Fact]
    public void Shared_bank_capacity_also_holds_when_supplier_payment_was_recorded_first()
    {
        var bank = BusinessFixture.Bank(-200m); var bill = ApprovedBill();
        var supplierPayment = PayablesWorkflow.Allocate(bill, bank, [], [], [], 119m, 0, "", SupplierPaymentDirection.Payment, BusinessFixture.Actor, BusinessFixture.Now);
        var invoice = BusinessFixture.Invoice();
        var receipt = PaymentWorkflow.Allocate(invoice, BusinessFixture.Bank(119m, "incoming"), [], [], 119m, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now);
        var credit = InvoiceWorkflow.DraftCreditNote(invoice, "Cancelled"); credit.DueDate = new(2026, 10, 20);
        InvoiceWorkflow.IssueCreditNote(credit, invoice, [], "C1", new(2026, 10, 20));
        Assert.Equal("bank_overallocated", Assert.Throws<DomainException>(() => PaymentWorkflow.AllocateRefund(invoice, bank, [receipt], [credit], 82m, "Refund", BusinessFixture.Actor, BusinessFixture.Now, [supplierPayment])).Code);
    }

    [Fact]
    public void Supplier_credit_after_discounted_payment_creates_refundable_cash_only()
    {
        var (supplier, bill) = DraftBill(); PayablesWorkflow.Approve(bill, supplier, Guid.NewGuid(), BusinessFixture.Actor, BusinessFixture.Now, true);
        var payment = PayablesWorkflow.Allocate(bill, BusinessFixture.Bank(-117m), [], [], [], 117m, 2m, "Agreed discount", SupplierPaymentDirection.Payment, BusinessFixture.Actor, BusinessFixture.Now);
        var credit = new SupplierInvoice
        {
            TenantId = bill.TenantId, SupplierId = supplier.Id, OriginalInvoiceId = bill.Id, Number = "SC1", Title = "Supplier credit", Kind = SupplierInvoiceKind.CreditNote,
            InvoiceDate = bill.InvoiceDate, DueDate = bill.DueDate, Net = bill.Net, Tax = bill.Tax, Gross = bill.Gross
        };
        PayablesWorkflow.ApproveCredit(credit, bill, supplier, [], Guid.NewGuid(), BusinessFixture.Actor, BusinessFixture.Now, true);
        Assert.Equal(117m, PayablesWorkflow.Balance(bill, [payment], [credit], new(2026, 10, 20)).SupplierCredit);
        var refund = PayablesWorkflow.Allocate(bill, BusinessFixture.Bank(117m, "supplier-refund"), [payment], [], [credit], 117m, 0, "Refund", SupplierPaymentDirection.Refund, BusinessFixture.Actor, BusinessFixture.Now);
        Assert.Equal(0m, PayablesWorkflow.Balance(bill, [payment, refund], [credit], new(2026, 10, 20)).SupplierCredit);
    }

    [Fact]
    public void Returned_supplier_payment_reserves_both_bank_entries_against_reuse()
    {
        var bill = ApprovedBill(); var outgoing = BusinessFixture.Bank(-119m);
        var payment = PayablesWorkflow.Allocate(bill, outgoing, [], [], [], 119m, 0, "", SupplierPaymentDirection.Payment, BusinessFixture.Actor, BusinessFixture.Now);
        var returned = BusinessFixture.Bank(119m, "returned-transfer");
        PayablesWorkflow.ReverseForReturn(payment, returned, [payment], [], "Bank rejected transfer", BusinessFixture.Actor, BusinessFixture.Now.AddDays(1));
        Assert.Equal(119m, PayablesWorkflow.Balance(bill, [payment], [], new(2026, 10, 21)).Outstanding);
        Assert.Equal(119m, PaymentWorkflow.UsedBankAmount(outgoing, [], [payment]));
        Assert.Equal(119m, PaymentWorkflow.UsedBankAmount(returned, [], [payment]));
        Assert.Equal("bank_overallocated", Assert.Throws<DomainException>(() => PaymentWorkflow.Allocate(BusinessFixture.Invoice(), returned, [], [], 119m, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now, [payment])).Code);
    }

    [Fact]
    public void Actual_return_does_not_make_the_original_customer_receipt_reallocatable()
    {
        var invoice = BusinessFixture.Invoice(); var bank = BusinessFixture.Bank(119m);
        var allocation = PaymentWorkflow.Allocate(invoice, bank, [], [], 119m, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now);
        PaymentWorkflow.ReverseForReturn(allocation, BusinessFixture.Bank(-119m, "returned"), [allocation], "Chargeback", BusinessFixture.Actor, BusinessFixture.Now.AddDays(1));
        Assert.Equal("bank_overallocated", Assert.Throws<DomainException>(() => PaymentWorkflow.Allocate(invoice, bank, [allocation], [], 119m, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now.AddDays(1))).Code);
    }

    [Fact]
    public void Invoice_and_credit_positions_cannot_swap_effective_prices_or_tax_rates()
    {
        var order = BusinessFixture.Order(); var invoice = BusinessFixture.Draft(order);
        invoice.Lines[0].UnitMaterialCost = 50m; invoice.Lines[0].Quantity = 2m;
        Assert.Equal("uncontracted_line", Assert.Throws<DomainException>(() => InvoiceWorkflow.IssueForOrder(invoice, order, [], [], "I1", new(2026, 10, 20))).Code);
        var original = BusinessFixture.Invoice(); var credit = InvoiceWorkflow.DraftCreditNote(original, "Correction"); credit.DueDate = new(2026, 10, 20);
        credit.Lines[0].VatPercent = 0;
        Assert.Equal("invalid_credit_lines", Assert.Throws<DomainException>(() => InvoiceWorkflow.IssueCreditNote(credit, original, [], "C1", new(2026, 10, 20))).Code);
    }

    private static SupplierInvoice ApprovedBill()
    {
        var (supplier, bill) = DraftBill(); PayablesWorkflow.Approve(bill, supplier, Guid.NewGuid(), BusinessFixture.Actor, BusinessFixture.Now, true); return bill;
    }
    private static (Supplier Supplier, SupplierInvoice Invoice) DraftBill()
    {
        var supplier = new Supplier { TenantId = BusinessFixture.Tenant, Name = "Example Supplier", Address = "Supplier street" };
        var bill = new SupplierInvoice
        {
            TenantId = BusinessFixture.Tenant, SupplierId = supplier.Id, Number = "SUP-1", Title = "Roofing materials",
            InvoiceDate = new(2026, 10, 20), DueDate = new(2026, 11, 3), Net = 100m, Tax = 19m, Gross = 119m
        };
        return (supplier, bill);
    }
}
