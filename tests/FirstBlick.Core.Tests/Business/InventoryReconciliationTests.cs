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
}
