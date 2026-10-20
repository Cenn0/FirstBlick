using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class SupplierCostAllocationTests
{
    [Fact]
    public void Concurrent_project_amounts_are_capped_to_approved_invoice_net_and_reversals_remain_in_history()
    {
        var (invoice, project, actor) = Sample(100m);
        var other = new Project { TenantId = invoice.TenantId, CustomerId = Guid.NewGuid(),
            BuildingId = Guid.NewGuid(), Name = "Second synthetic project" };
        var first = SupplierCostAllocationRules.Post(invoice, project, [], 75m, "Approved invoice to project one", actor, DateTimeOffset.UtcNow);
        var second = SupplierCostAllocationRules.Post(invoice, other, [first], 25m, "Approved remainder to project two", actor, DateTimeOffset.UtcNow);
        Assert.Equal(100m, SupplierCostAllocationRules.Active([first, second]).Sum(x => x.NetAmount));
        Assert.Equal("cost_allocation_exceeded", Assert.Throws<DomainException>(() =>
            SupplierCostAllocationRules.Post(invoice, project, [first, second], 0.01m, "Too much", actor, DateTimeOffset.UtcNow)).Code);
        var reversal = SupplierCostAllocationRules.Reverse(first, [first, second], "Wrong project", actor, DateTimeOffset.UtcNow);
        Assert.Equal(first.Id, reversal.ReversesAllocationId);
        var active = SupplierCostAllocationRules.Active([first, second, reversal]);
        Assert.Single(active); Assert.Equal(second.Id, active[0].Id);
        Assert.Equal("allocation_already_reversed", Assert.Throws<DomainException>(() =>
            SupplierCostAllocationRules.Reverse(first, [first, second, reversal], "Again", actor, DateTimeOffset.UtcNow)).Code);
        var replacement = SupplierCostAllocationRules.Post(invoice, other, [first, second, reversal], 75m, "Replace original allocation", actor, DateTimeOffset.UtcNow);
        Assert.Equal(100m, SupplierCostAllocationRules.Active([first, second, reversal, replacement]).Sum(x => x.NetAmount));
    }

    [Fact]
    public void Credit_cannot_reduce_one_project_more_than_its_remaining_original_allocation()
    {
        var (invoice, project, actor) = Sample(100m);
        var original = SupplierCostAllocationRules.Post(invoice, project, [], 60m, "Original subcontractor cost", actor, DateTimeOffset.UtcNow);
        var credit = new SupplierInvoice { TenantId = invoice.TenantId, SupplierId = invoice.SupplierId,
            OriginalInvoiceId = invoice.Id, Kind = SupplierInvoiceKind.CreditNote,
            Number = "SYN-CREDIT", Title = "Supplier correction", InvoiceDate = new DateOnly(2026, 10, 20),
            DueDate = new DateOnly(2026, 11, 1), Net = 50m, Tax = 9.5m, Gross = 59.5m };
        var supplier = new Supplier { TenantId = invoice.TenantId, Name = "Synthetic supplier" };
        PayablesWorkflow.ApproveCredit(credit, invoice, supplier, [], Guid.NewGuid(), actor, DateTimeOffset.UtcNow, false);
        var booked = SupplierCostAllocationRules.Post(credit, project, [], 40m, "Reviewed supplier credit", actor, DateTimeOffset.UtcNow);
        SupplierCostAllocationRules.ValidateCreditProjectBalance(project.Id, 40m, [original], []);
        Assert.Equal("credit_allocation_exceeded", Assert.Throws<DomainException>(() =>
            SupplierCostAllocationRules.ValidateCreditProjectBalance(project.Id, 21m, [original], [booked])).Code);
        SupplierCostAllocationRules.ValidateCreditProjectBalance(project.Id, 20m, [original], [booked]);
    }

    [Fact]
    public void Forged_reversals_and_modified_journal_contents_are_rejected()
    {
        var (invoice, project, actor) = Sample(100m);
        var source = SupplierCostAllocationRules.Post(invoice, project, [], 30m, "Original posting", actor, DateTimeOffset.UtcNow);
        source.NetAmount = 31m;
        Assert.Equal("cost_allocation_changed", Assert.Throws<DomainException>(() => source.Validate()).Code);
        source.NetAmount = 30m;
        var undo = SupplierCostAllocationRules.Reverse(source, [source], "Correction", actor, DateTimeOffset.UtcNow);
        undo.ProjectId = Guid.NewGuid(); undo.PostedDigest = undo.CalculateDigest();
        Assert.Equal("invalid_allocation_reversal", Assert.Throws<DomainException>(() =>
            SupplierCostAllocationRules.Active([source, undo])).Code);
        Assert.Equal("invalid_allocation_amount", Assert.Throws<DomainException>(() =>
            SupplierCostAllocationRules.Post(invoice, project, [source], 0.001m, "Fractional cent", actor, DateTimeOffset.UtcNow)).Code);
    }

    private static (SupplierInvoice Invoice, Project Project, Guid Actor) Sample(decimal net)
    {
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        var supplier = new Supplier { TenantId = tenant, Name = "Synthetic supplier" };
        var invoice = new SupplierInvoice
        {
            TenantId = tenant, SupplierId = supplier.Id, Number = "SYN-BILL",
            Title = "Synthetic service", InvoiceDate = new DateOnly(2026, 10, 20),
            DueDate = new DateOnly(2026, 11, 1), Net = net, Tax = Money.Round(net * 0.19m),
            Gross = net + Money.Round(net * 0.19m)
        };
        PayablesWorkflow.Approve(invoice, supplier, Guid.NewGuid(), actor, DateTimeOffset.UtcNow, false);
        var project = new Project { TenantId = tenant, CustomerId = Guid.NewGuid(),
            BuildingId = Guid.NewGuid(), Name = "Synthetic project" };
        return (invoice, project, actor);
    }
}
