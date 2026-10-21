using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class InventoryCorrectionTests
{
    private static (PurchaseOrder Order, StockMovement Receipt) Received(decimal count = 10m)
    {
        var order = new PurchaseOrder
        {
            TenantId = BusinessFixture.Tenant, SupplierId = Guid.NewGuid(), Title = "Tiles",
            Lines = [new() { MaterialId = Guid.NewGuid(), Description = "Tiles", Quantity = count, UnitNet = 2m }]
        };
        InventoryRules.Order(order, "BE-01");
        var receipt = InventoryRules.Receive(order, order.Lines[0].Id, count, [], "Main", BusinessFixture.Now);
        Assert.Equal(PurchaseOrderStatus.Received, order.Status);
        return (order, receipt);
    }

    [Fact]
    public void Physical_count_posts_only_a_reviewed_difference_and_preserves_reserved_stock()
    {
        var (order, receipt) = Received();
        var material = receipt.MaterialId;
        var reservation = new MaterialReservation
        {
            TenantId = order.TenantId, MaterialId = material, ProjectId = Guid.NewGuid(),
            Location = "Main", Quantity = 4m
        };
        InventoryRules.Reserve(reservation, [receipt], []);
        var correction = InventoryCorrections.ReconcileCount(order.TenantId, material, "Main",
            10m, 8m, 2m, "COUNT-2026-01", "Breakage counted and independently checked",
            [receipt], [reservation], BusinessFixture.Now.AddMinutes(1));
        Assert.Equal(-2m, correction.Quantity);
        Assert.Equal(StockMovementKind.Adjustment, correction.Kind);
        Assert.Equal(64, correction.PostedDigest.Length);
        correction.Validate();
        Assert.Equal(new StockBalance(8m, 4m, 4m),
            InventoryRules.Balance(order.TenantId, material, "Main", [receipt, correction], [reservation]));
        Assert.Equal("stock_count_stale", Assert.Throws<DomainException>(() =>
            InventoryCorrections.ReconcileCount(order.TenantId, material, "Main", 10m, 7m, 2m,
                "COUNT-2026-01", "Double posting attempted", [receipt, correction], [reservation],
                BusinessFixture.Now.AddMinutes(2))).Code);
        Assert.Equal("reserved_stock", Assert.Throws<DomainException>(() =>
            InventoryCorrections.ReconcileCount(order.TenantId, material, "Main", 8m, 3m, 2m,
                "COUNT-2026-02", "Count below assigned stock", [receipt, correction], [reservation],
                BusinessFixture.Now.AddMinutes(2))).Code);
        Assert.Equal("no_stock_difference", Assert.Throws<DomainException>(() =>
            InventoryCorrections.ReconcileCount(order.TenantId, material, "Main", 8m, 8m, 2m,
                "COUNT-2026-02", "No difference", [receipt, correction], [reservation],
                BusinessFixture.Now.AddMinutes(2))).Code);
    }

    [Fact]
    public void Transfer_is_mass_balanced_and_cannot_use_the_reserved_part_of_stock()
    {
        var (order, receipt) = Received();
        var reservation = new MaterialReservation
        {
            TenantId = order.TenantId, MaterialId = receipt.MaterialId, ProjectId = Guid.NewGuid(),
            Location = "Main", Quantity = 7m
        };
        InventoryRules.Reserve(reservation, [receipt], []);
        var result = InventoryCorrections.Transfer(order.TenantId, receipt.MaterialId, "Main", "Site",
            3m, 2m, 10m, "Delivery vehicle checked", Guid.NewGuid(),
            [receipt], [reservation], BusinessFixture.Now.AddMinutes(1));
        Assert.Equal(-3m, result.Outbound.Quantity);
        Assert.Equal(3m, result.Inbound.Quantity);
        Assert.Equal(result.Outbound.Reference, result.Inbound.Reference);
        Assert.NotEqual(result.Outbound.Id, result.Inbound.Id);
        var ledger = new[] { receipt, result.Outbound, result.Inbound };
        Assert.Equal(new StockBalance(7m, 7m, 0m),
            InventoryRules.Balance(order.TenantId, receipt.MaterialId, "Main", ledger, [reservation]));
        Assert.Equal(new StockBalance(3m, 0m, 3m),
            InventoryRules.Balance(order.TenantId, receipt.MaterialId, "Site", ledger, [reservation]));
        Assert.Equal("insufficient_stock", Assert.Throws<DomainException>(() =>
            InventoryCorrections.Transfer(order.TenantId, receipt.MaterialId, "Main", "Site",
                4m, 2m, 10m, "Reserved stock", Guid.NewGuid(), [receipt], [reservation],
                BusinessFixture.Now)).Code);
        Assert.Equal("same_location", Assert.Throws<DomainException>(() =>
            InventoryCorrections.Transfer(order.TenantId, receipt.MaterialId, "Main", "Main",
                1m, 2m, 10m, "Invalid transfer", Guid.NewGuid(), [receipt], [reservation],
                BusinessFixture.Now)).Code);
    }

    [Fact]
    public void Project_return_restores_stock_at_original_cost_and_prevents_double_credit()
    {
        var (order, receipt) = Received();
        var reservation = new MaterialReservation
        {
            TenantId = order.TenantId, MaterialId = receipt.MaterialId, ProjectId = Guid.NewGuid(),
            Location = "Main", Quantity = 6m
        };
        InventoryRules.Reserve(reservation, [receipt], []);
        var consumption = InventoryRules.Consume(reservation, 4m, 2m, BusinessFixture.Now.AddMinutes(1));
        InventoryRules.Release(reservation, 1m);
        var first = InventoryCorrections.ReturnConsumption(reservation, consumption,
            2m, "Unused goods returned from job", [consumption], BusinessFixture.Now.AddMinutes(2));
        Assert.Equal(StockMovementKind.Return, first.Kind);
        Assert.Equal(consumption.UnitCost, first.UnitCost);
        Assert.Equal(consumption.Id.ToString("N"), first.Reference);
        Assert.Equal(2m, reservation.ConsumedQuantity);
        Assert.Equal(3m, reservation.ReleasedQuantity);
        Assert.Equal(new StockBalance(8m, 1m, 7m),
            InventoryRules.Balance(order.TenantId, receipt.MaterialId, "Main",
                [receipt, consumption, first], [reservation]));
        Assert.Equal("over_return", Assert.Throws<DomainException>(() =>
            InventoryCorrections.ReturnConsumption(reservation, consumption,
                3m, "Duplicate credit", [consumption, first], BusinessFixture.Now.AddMinutes(3))).Code);
    }

    [Fact]
    public void Supplier_return_reopens_received_order_without_erasing_receipt_provenance()
    {
        var (order, receipt) = Received();
        var correction = InventoryCorrections.ReturnToSupplier(order, order.Lines[0].Id,
            3m, "Main", "RMA-001", "Damaged tiles returned with signed acceptance",
            [receipt], [], BusinessFixture.Now.AddMinutes(2));
        Assert.Equal(StockMovementKind.SupplierReturn, correction.Kind);
        Assert.Equal(-3m, correction.Quantity);
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, order.Status);
        Assert.Equal(new StockBalance(7m, 0m, 7m),
            InventoryRules.Balance(order.TenantId, receipt.MaterialId, "Main", [receipt, correction], []));
        Assert.Equal("over_supplier_return", Assert.Throws<DomainException>(() =>
            InventoryCorrections.ReturnToSupplier(order, order.Lines[0].Id, 8m, "Main",
                "RMA-002", "Over-return", [receipt, correction], [],
                BusinessFixture.Now.AddMinutes(3))).Code);
        var replacement = InventoryRules.Receive(order, order.Lines[0].Id, 3m,
            [receipt, correction], "Main", BusinessFixture.Now.AddMinutes(4));
        Assert.Equal(StockMovementKind.Receipt, replacement.Kind);
        Assert.Equal(PurchaseOrderStatus.Received, order.Status);
    }

    [Fact]
    public void Supplier_return_cannot_withdraw_material_committed_to_projects_or_from_another_tenant()
    {
        var (order, receipt) = Received();
        var reserved = new MaterialReservation
        {
            TenantId = order.TenantId, MaterialId = receipt.MaterialId, ProjectId = Guid.NewGuid(),
            Location = "Main", Quantity = 9m
        };
        InventoryRules.Reserve(reserved, [receipt], []);
        Assert.Equal("insufficient_stock", Assert.Throws<DomainException>(() =>
            InventoryCorrections.ReturnToSupplier(order, order.Lines[0].Id, 2m, "Main",
                "RMA-03", "Stock committed to another job", [receipt], [reserved],
                BusinessFixture.Now.AddMinutes(1))).Code);
        var crossTenant = new StockMovement
        {
            TenantId = Guid.NewGuid(), MaterialId = receipt.MaterialId, Location = "Main",
            Kind = StockMovementKind.Receipt, Quantity = 100m, OccurredAt = BusinessFixture.Now
        };
        Assert.Equal("insufficient_stock", Assert.Throws<DomainException>(() =>
            InventoryCorrections.ReturnToSupplier(order, order.Lines[0].Id, 2m, "Main",
                "RMA-04", "Cross tenant stock must not count", [receipt, crossTenant], [reserved],
                BusinessFixture.Now.AddMinutes(1))).Code);
    }
}
