using FirstBlick.Core.Business;
using System.Text.Json;

namespace FirstBlick.Core.Tests.Business;

public sealed class InventoryTransferChainTests
{
    [Fact]
    public void Three_hops_retain_the_original_receipt_value_and_auditable_path()
    {
        var ledger = new Ledger();
        var first = ledger.Transfer(ledger.Receipt, "B", 8m);
        var second = ledger.Transfer(first, "C", 6m);
        var third = ledger.Transfer(second, "D", 4m);
        var returned = ledger.Return(3m, third);
        var trace = InventoryTransferChain.Inspect(third, ledger.Movements);
        Assert.Equal(ledger.Receipt.Id, trace.OriginalReceiptId);
        Assert.Equal(new[] { "B", "C", "D" }, trace.Transfers.Select(x => x.ToLocation));
        Assert.Equal(1m, trace.EvidenceRemaining);
        Assert.Equal(5m, returned.UnitCost);
        Assert.Equal("D", returned.Location);
        Assert.Equal(7m, InventoryRules.ReceivedQuantity(ledger.Order, ledger.Order.Lines[0].Id, ledger.Movements));
        Assert.Equal(2m, InventoryTransferChain.Remaining(ledger.Receipt, ledger.Movements));
        Assert.Equal(2m, InventoryTransferChain.Remaining(first, ledger.Movements));
        Assert.Equal(2m, InventoryTransferChain.Remaining(second, ledger.Movements));
        Assert.Equal(7m, ledger.Movements.Sum(x => x.Quantity));
        Assert.Equal(ledger.Receipt.Id.ToString("D"), returned.Reference);
        Assert.Equal(third.Id, returned.ReturnTransferId);
        Assert.Contains(third.References(), x => x.Id == second.Id);
    }

    [Fact]
    public void Other_stock_cannot_expand_a_receipts_allocation_or_bypass_it_by_a_direct_return()
    {
        var ledger = new Ledger();
        ledger.Transfer(ledger.Receipt, "B", 6m);
        ledger.AddOtherStock("A", 20m);
        Assert.Equal("transfer_source_exceeded", Assert.Throws<DomainException>(() => ledger.Transfer(ledger.Receipt, "C", 5m)).Code);
        Assert.Equal("supplier_return_source_exceeded", Assert.Throws<DomainException>(() => ledger.Return(5m)).Code);
        Assert.Equal(4m, InventoryTransferChain.Remaining(ledger.Receipt, ledger.Movements));
        Assert.Equal(4, ledger.Movements.Count);
    }

    [Fact]
    public void Split_branches_and_partial_returns_consume_each_source_only_once()
    {
        var ledger = new Ledger();
        var first = ledger.Transfer(ledger.Receipt, "B", 6m);
        var c = ledger.Transfer(first, "C", 4m);
        var d = ledger.Transfer(first, "D", 2m);
        ledger.AddOtherStock("B", 10m);
        Assert.Equal("supplier_return_transfer_exceeded", Assert.Throws<DomainException>(() => ledger.Return(1m, first)).Code);
        ledger.Return(3m, c);
        ledger.AddOtherStock("C", 10m);
        Assert.Equal("transfer_source_exceeded", Assert.Throws<DomainException>(() => ledger.Transfer(c, "E", 2m)).Code);
        ledger.Return(2m, d);
        Assert.Equal(4m, InventoryTransferChain.Remaining(ledger.Receipt, ledger.Movements));
        Assert.Equal(0m, InventoryTransferChain.Remaining(first, ledger.Movements));
        Assert.Equal(1m, InventoryTransferChain.Remaining(c, ledger.Movements));
        Assert.Equal(0m, InventoryTransferChain.Remaining(d, ledger.Movements));
        Assert.Equal(5m, InventoryRules.ReceivedQuantity(ledger.Order, ledger.Order.Lines[0].Id, ledger.Movements));
    }

    [Fact]
    public void Historic_unlinked_returns_are_debited_before_new_linked_allocations()
    {
        var ledger = new Ledger();
        var (outgoing, incoming) = InventoryRules.Transfer(ledger.Order.TenantId, ledger.Receipt.MaterialId,
            "A", "B", 6m, 5m, 10m, "Historic direct transfer", ledger.Movements, [], BusinessFixture.Now);
        ledger.Movements.AddRange([outgoing, incoming]);
        ledger.Return(2m, incoming);
        ledger.AddOtherStock("A", 20m);
        ledger.Transfer(ledger.Receipt, "C", 5m);
        Assert.Equal(3m, InventoryTransferChain.Remaining(ledger.Receipt, ledger.Movements));
        Assert.Equal("supplier_return_source_exceeded", Assert.Throws<DomainException>(() => ledger.Return(4m, incoming)).Code);
        Assert.Equal("transfer_source_exceeded", Assert.Throws<DomainException>(() => ledger.Transfer(ledger.Receipt, "D", 4m)).Code);
        Assert.Equal("invalid_transfer_source", Assert.Throws<DomainException>(() => ledger.Transfer(incoming, "D", 1m)).Code);
    }

    [Fact]
    public void A_chain_cannot_be_used_for_a_different_receipt_even_of_the_same_purchase_line()
    {
        var ledger = new Ledger();
        var first = ledger.Transfer(ledger.Receipt, "B", 3m);
        var other = new StockMovement { TenantId = ledger.Order.TenantId, MaterialId = ledger.Receipt.MaterialId,
            Kind = StockMovementKind.Receipt, Location = "A", Quantity = 2m, UnitCost = 5m,
            PurchaseOrderId = ledger.Order.Id, PurchaseLineId = ledger.Order.Lines[0].Id, OccurredAt = BusinessFixture.Now };
        other.PostedDigest = InventoryRules.MovementDigest(other);
        ledger.Movements.Add(other);
        Assert.Equal("invalid_return_transfer", Assert.Throws<DomainException>(() => InventoryRules.SupplierReturn(
            ledger.Order, other, 1m, "Wrong original receipt", ledger.Movements, [], BusinessFixture.Now.AddDays(1), first.Id)).Code);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("material")]
    [InlineData("location")]
    [InlineData("value")]
    public void A_linked_transfer_rejects_foreign_or_incompatible_source_data(string mismatch)
    {
        var ledger = new Ledger();
        var error = Assert.Throws<DomainException>(() => InventoryRules.Transfer(
            mismatch == "tenant" ? Guid.NewGuid() : ledger.Order.TenantId,
            mismatch == "material" ? Guid.NewGuid() : ledger.Receipt.MaterialId,
            mismatch == "location" ? "Other" : "A", "B", 1m,
            mismatch == "value" ? 7m : 5m, 10m, "Source mismatch", ledger.Movements, [], BusinessFixture.Now, ledger.Receipt.Id));
        Assert.Equal("invalid_transfer_source", error.Code);
    }

    [Theory]
    [InlineData("missing-outgoing")]
    [InlineData("wrong-source")]
    [InlineData("wrong-quantity")]
    [InlineData("duplicate-incoming")]
    public void Incomplete_or_mismatched_pairs_cannot_be_followed(string mismatch)
    {
        var ledger = new Ledger();
        var incoming = ledger.Transfer(ledger.Receipt, "B", 4m);
        var outgoing = ledger.Movements.Single(x => x.Kind == StockMovementKind.TransferOut);
        if (mismatch == "missing-outgoing") ledger.Movements.Remove(outgoing);
        if (mismatch == "wrong-source") outgoing.TransferSourceId = Guid.NewGuid();
        if (mismatch == "wrong-quantity") outgoing.Quantity = -3m;
        outgoing.PostedDigest = InventoryRules.MovementDigest(outgoing);
        if (mismatch == "duplicate-incoming")
        {
            var duplicate = JsonSerializer.Deserialize<StockMovement>(JsonSerializer.Serialize(incoming))!;
            duplicate.Id = Guid.NewGuid(); duplicate.PostedDigest = InventoryRules.MovementDigest(duplicate);
            ledger.Movements.Add(duplicate);
        }
        Assert.Equal("invalid_transfer_chain", Assert.Throws<DomainException>(() => InventoryTransferChain.Inspect(incoming, ledger.Movements)).Code);
    }

    [Fact]
    public void Cycles_are_rejected_deterministically()
    {
        var ledger = new Ledger();
        var first = ledger.Transfer(ledger.Receipt, "B", 4m);
        var second = ledger.Transfer(first, "A", 4m);
        foreach (var entry in ledger.Movements.Where(x => x.Kind is StockMovementKind.TransferIn or StockMovementKind.TransferOut))
        {
            entry.OccurredAt = BusinessFixture.Now;
            if (entry.Reference == first.Reference) entry.TransferSourceId = second.Id;
            entry.PostedDigest = InventoryRules.MovementDigest(entry);
        }
        var failure = Assert.Throws<DomainException>(() => InventoryTransferChain.Inspect(second, ledger.Movements));
        Assert.Equal("invalid_transfer_chain", failure.Code);
        Assert.Contains("cycle", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_chain_stops_at_32_transfers_without_recursion_or_partial_posting()
    {
        var ledger = new Ledger();
        var latest = ledger.Receipt;
        for (var index = 1; index <= InventoryTransferChain.MaximumTransfers; index++) latest = ledger.Transfer(latest, $"Warehouse {index}", 1m);
        Assert.Equal(32, InventoryTransferChain.Inspect(latest, ledger.Movements).Transfers.Count);
        Assert.Equal("transfer_chain_too_deep", Assert.Throws<DomainException>(() => ledger.Transfer(latest, "Too far", 1m)).Code);
        Assert.Equal(65, ledger.Movements.Count);
    }

    [Fact]
    public void Linked_returns_respect_destination_reservations_and_do_not_change_the_order_on_failure()
    {
        var ledger = new Ledger();
        var first = ledger.Transfer(ledger.Receipt, "B", 4m);
        var last = ledger.Transfer(first, "C", 4m);
        var reservation = new MaterialReservation { TenantId = ledger.Order.TenantId, MaterialId = ledger.Receipt.MaterialId,
            ProjectId = Guid.NewGuid(), Location = "C", Quantity = 4m };
        Assert.Equal("stock_reserved", Assert.Throws<DomainException>(() => InventoryRules.SupplierReturn(
            ledger.Order, ledger.Receipt, 1m, "Reserved destination", ledger.Movements, [reservation], BusinessFixture.Now.AddDays(1), last.Id)).Code);
        Assert.Equal(PurchaseOrderStatus.Received, ledger.Order.Status);
        Assert.Equal(5, ledger.Movements.Count);
    }

    [Fact]
    public void New_source_reference_is_digest_protected_but_legacy_digest_bytes_are_unchanged()
    {
        var ledger = new Ledger();
        var source = ledger.Receipt;
        var legacyDigest = ContentDigest.Of(new { source.TenantId, source.Id, source.MaterialId, source.ProjectId,
            source.PurchaseOrderId, source.PurchaseLineId, source.ReservationId, source.Location, source.Kind,
            source.Quantity, source.UnitCost, source.OccurredAt, source.Reference, source.Reason });
        Assert.Equal(legacyDigest, InventoryRules.MovementDigest(source));
        Assert.DoesNotContain("TransferSourceId", JsonSerializer.Serialize(source), StringComparison.Ordinal);
        var transferred = ledger.Transfer(source, "B", 1m);
        transferred.TransferSourceId = Guid.NewGuid();
        Assert.Equal("movement_immutable", Assert.Throws<DomainException>(transferred.Validate).Code);
    }

    private sealed class Ledger
    {
        public PurchaseOrder Order { get; }
        public StockMovement Receipt { get; }
        public List<StockMovement> Movements { get; } = [];
        public Ledger()
        {
            Order = new PurchaseOrder { TenantId = BusinessFixture.Tenant, SupplierId = Guid.NewGuid(), Title = "Synthetic transfer chain",
                Lines = [new() { MaterialId = Guid.NewGuid(), Description = "Synthetic roof tiles", Quantity = 10m, UnitNet = 5m }] };
            InventoryRules.Order(Order, "TEST-CHAIN");
            Receipt = InventoryRules.Receive(Order, Order.Lines[0].Id, 10m, [], "A", BusinessFixture.Now);
            Movements.Add(Receipt);
        }
        public StockMovement Transfer(StockMovement source, string target, decimal quantity)
        {
            var balance = InventoryRules.Balance(Order.TenantId, Receipt.MaterialId, source.Location, Movements, []);
            var pair = InventoryRules.Transfer(Order.TenantId, Receipt.MaterialId, source.Location, target, quantity, 5m,
                balance.OnHand, "Explicit synthetic receipt allocation", Movements, [], BusinessFixture.Now.AddMinutes(Movements.Count), source.Id);
            Movements.AddRange([pair.Out, pair.In]);
            return pair.In;
        }
        public StockMovement Return(decimal quantity, StockMovement? source = null)
        {
            var returned = InventoryRules.SupplierReturn(Order, Receipt, quantity, "Recorded synthetic supplier return",
                Movements, [], BusinessFixture.Now.AddDays(1), source?.Id);
            Movements.Add(returned); return returned;
        }
        public void AddOtherStock(string location, decimal quantity)
        {
            var unrelated = new StockMovement { TenantId = Order.TenantId, MaterialId = Receipt.MaterialId,
                Kind = StockMovementKind.Receipt, Location = location, Quantity = quantity, UnitCost = 5m, OccurredAt = BusinessFixture.Now };
            unrelated.PostedDigest = InventoryRules.MovementDigest(unrelated);
            Movements.Add(unrelated);
        }
    }
}
