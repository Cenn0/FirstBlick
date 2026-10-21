using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class InventoryReconciliationTests
{
    [Fact]
    public void Transfer_rejects_a_stale_review_even_when_current_stock_would_cover_the_quantity()
    {
        var source = Receipt();
        var error = Assert.Throws<DomainException>(() => InventoryRules.Transfer(source.TenantId,
            source.MaterialId, "A", "B", 1m, 5m, 11m, "Transfer reviewed before a consumption",
            [source], [], BusinessFixture.Now));
        Assert.Equal("stale_stock_count", error.Code);
        Assert.Equal(10m, source.Quantity);
    }

    [Fact]
    public void Physical_count_requires_a_separate_nonempty_evidence_reference()
    {
        var source = Receipt();
        var error = Assert.Throws<DomainException>(() => InventoryRules.CountAdjustment(source.TenantId,
            source.MaterialId, "A", 10m, 9m, 5m, "Count checked by storekeeper", " ",
            [source], [], BusinessFixture.Now));
        Assert.Equal("required", error.Code);
    }

    [Fact]
    public void Returning_one_consumption_preserves_other_consumptions_and_releases_without_reserving_again()
    {
        var source = Receipt();
        var reservation = new MaterialReservation { TenantId = source.TenantId, MaterialId = source.MaterialId,
            ProjectId = Guid.NewGuid(), Location = "A", Quantity = 8m };
        var first = InventoryRules.Consume(reservation, 3m, 5m, BusinessFixture.Now);
        var second = InventoryRules.Consume(reservation, 2m, 7m, BusinessFixture.Now);
        var originalDigest = first.PostedDigest;
        var returned = InventoryRules.ProjectReturn(reservation, first, 3m, "Original three units returned",
            [source, first, second], BusinessFixture.Now);
        Assert.Equal(2m, reservation.ConsumedQuantity);
        Assert.Equal(3m, reservation.ReleasedQuantity);
        Assert.Equal(5m, returned.UnitCost);
        Assert.Equal(originalDigest, first.PostedDigest);
        Assert.Equal(new StockBalance(8m, 3m, 5m), InventoryRules.Balance(source.TenantId,
            source.MaterialId, "A", [source, first, second, returned], [reservation]));
        Assert.Equal("project_return_exceeded", Assert.Throws<DomainException>(() =>
            InventoryRules.ProjectReturn(reservation, first, 1m, "Already returned source",
                [source, first, second, returned], BusinessFixture.Now)).Code);
        Assert.Equal(2m, reservation.ConsumedQuantity);
        Assert.Equal(3m, reservation.ReleasedQuantity);
    }

    [Fact]
    public void Return_rejects_a_different_reservation_without_modifying_its_counters()
    {
        var source = Receipt();
        var reservation = new MaterialReservation { TenantId = source.TenantId, MaterialId = source.MaterialId,
            ProjectId = Guid.NewGuid(), Location = "A", Quantity = 5m };
        var consumption = InventoryRules.Consume(reservation, 3m, 5m, BusinessFixture.Now);
        var other = new MaterialReservation { TenantId = source.TenantId, MaterialId = source.MaterialId,
            ProjectId = reservation.ProjectId, Location = "A", Quantity = 5m, ConsumedQuantity = 3m };
        Assert.Equal("invalid_project_return", Assert.Throws<DomainException>(() =>
            InventoryRules.ProjectReturn(other, consumption, 1m, "Wrong reservation",
                [source, consumption], BusinessFixture.Now)).Code);
        Assert.Equal(3m, other.ConsumedQuantity);
        Assert.Equal(0m, other.ReleasedQuantity);
    }

    private static StockMovement Receipt()
    {
        var receipt = new StockMovement { TenantId = BusinessFixture.Tenant, MaterialId = Guid.NewGuid(),
            Kind = StockMovementKind.Receipt, Location = "A", Quantity = 10m, UnitCost = 5m,
            OccurredAt = BusinessFixture.Now };
        receipt.PostedDigest = InventoryRules.MovementDigest(receipt);
        return receipt;
    }

    [Fact]
    public void Supplier_return_from_a_direct_transfer_reduces_the_destination_and_retains_both_source_references()
    {
        var order = Purchase();
        var receipt = InventoryRules.Receive(order, order.Lines[0].Id, 10m, [], "A", BusinessFixture.Now);
        var receiptDigest = receipt.PostedDigest;
        var (outgoing, incoming) = InventoryRules.Transfer(order.TenantId, receipt.MaterialId,
            "A", "B", 4m, 5m, 10m, "Direct transfer", [receipt], [], BusinessFixture.Now);
        var returned = InventoryRules.SupplierReturn(order, receipt, 2m, "Return from B",
            [receipt, outgoing, incoming], [], BusinessFixture.Now, incoming.Id);
        Assert.Equal("B", returned.Location);
        Assert.Equal(receipt.Id.ToString("D"), returned.Reference);
        Assert.Equal(incoming.Id, returned.ReturnTransferId);
        Assert.Contains(returned.References(), reference => reference.Id == incoming.Id);
        Assert.Equal(8m, InventoryRules.ReceivedQuantity(order, order.Lines[0].Id, [receipt, outgoing, incoming, returned]));
        Assert.Equal(new StockBalance(6m, 0m, 6m), InventoryRules.Balance(order.TenantId,
            receipt.MaterialId, "A", [receipt, outgoing, incoming, returned], []));
        Assert.Equal(new StockBalance(2m, 0m, 2m), InventoryRules.Balance(order.TenantId,
            receipt.MaterialId, "B", [receipt, outgoing, incoming, returned], []));
        Assert.Equal(receiptDigest, InventoryRules.MovementDigest(receipt));
        returned.ReturnTransferId = null;
        Assert.Equal("movement_immutable", Assert.Throws<DomainException>(returned.Validate).Code);
    }

    [Fact]
    public void Other_stock_at_the_destination_cannot_expand_the_returnable_transfer_quantity()
    {
        var order = Purchase();
        var receipt = InventoryRules.Receive(order, order.Lines[0].Id, 10m, [], "A", BusinessFixture.Now);
        var (outgoing, incoming) = InventoryRules.Transfer(order.TenantId, receipt.MaterialId,
            "A", "B", 4m, 5m, 10m, "Direct transfer", [receipt], [], BusinessFixture.Now);
        var first = InventoryRules.SupplierReturn(order, receipt, 3m, "First partial return",
            [receipt, outgoing, incoming], [], BusinessFixture.Now, incoming.Id);
        var otherStock = new StockMovement { TenantId = order.TenantId, MaterialId = receipt.MaterialId,
            Kind = StockMovementKind.Receipt, Quantity = 10m, Location = "B", UnitCost = 5m, OccurredAt = BusinessFixture.Now };
        otherStock.PostedDigest = InventoryRules.MovementDigest(otherStock);
        Assert.Equal("supplier_return_transfer_exceeded", Assert.Throws<DomainException>(() =>
            InventoryRules.SupplierReturn(order, receipt, 2m, "Cannot borrow the other stock's provenance",
                [receipt, outgoing, incoming, first, otherStock], [], BusinessFixture.Now, incoming.Id)).Code);
    }

    [Theory]
    [InlineData("different-origin")]
    [InlineData("different-value")]
    [InlineData("missing-outbound")]
    [InlineData("foreign-tenant")]
    public void Supplier_return_rejects_unmatched_or_unrelated_transfer_evidence(string error)
    {
        var order = Purchase();
        var receipt = InventoryRules.Receive(order, order.Lines[0].Id, 10m, [], "A", BusinessFixture.Now);
        var (outgoing, incoming) = InventoryRules.Transfer(order.TenantId, receipt.MaterialId,
            "A", "B", 4m, 5m, 10m, "Direct transfer", [receipt], [], BusinessFixture.Now);
        if (error == "different-origin") outgoing.Location = "Other";
        if (error == "different-value") incoming.UnitCost = outgoing.UnitCost = 6m;
        if (error == "foreign-tenant") incoming.TenantId = outgoing.TenantId = Guid.NewGuid();
        outgoing.PostedDigest = InventoryRules.MovementDigest(outgoing);
        incoming.PostedDigest = InventoryRules.MovementDigest(incoming);
        StockMovement[] movements = error == "missing-outbound" ? [receipt, incoming] : [receipt, outgoing, incoming];
        Assert.Equal("invalid_return_transfer", Assert.Throws<DomainException>(() =>
            InventoryRules.SupplierReturn(order, receipt, 1m, "Unmatched transfer",
                movements, [], BusinessFixture.Now, incoming.Id)).Code);
    }

    private static PurchaseOrder Purchase()
    {
        var order = new PurchaseOrder { TenantId = BusinessFixture.Tenant, SupplierId = Guid.NewGuid(),
            Title = "Synthetic supplier return", Lines = [new() { MaterialId = Guid.NewGuid(), Description = "Tiles", Quantity = 10m, UnitNet = 5m }] };
        InventoryRules.Order(order, "TEST-RETURN");
        return order;
    }
}
