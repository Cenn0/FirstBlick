using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class PlanningInventoryTests
{
    [Fact]
    public void Absence_assignment_invariants_are_checked_in_both_creation_orders()
    {
        var assignment = BusinessFixture.Assignment();
        var absence = new Absence { TenantId = BusinessFixture.Tenant, EmployeeId = assignment.EmployeeId, Start = assignment.Start, End = assignment.End, Kind = AbsenceKind.Vacation, Status = AbsenceStatus.Approved };
        Assert.Equal("absence_conflict", Assert.Throws<DomainException>(() => PlanningRules.EnsureAssignment(assignment, [], [absence])).Code);
        Assert.Equal("planning_conflict", Assert.Throws<DomainException>(() => PlanningRules.EnsureAbsence(absence, [assignment], [])).Code);
    }

    [Fact]
    public void Adjacent_windows_are_permitted_and_cross_tenant_windows_are_isolated()
    {
        var first = BusinessFixture.Assignment(); var next = BusinessFixture.Assignment(first.EmployeeId, 12, 16);
        PlanningRules.EnsureAssignment(next, [first], []);
        var otherTenant = BusinessFixture.Assignment(first.EmployeeId); otherTenant.TenantId = Guid.NewGuid();
        PlanningRules.EnsureAssignment(first, [otherTenant], []);
    }

    [Fact]
    public void Equipment_collision_is_rejected_even_with_different_employees()
    {
        var tool = Guid.NewGuid(); var a = BusinessFixture.Assignment(); var b = BusinessFixture.Assignment();
        a.EquipmentIds = [tool]; b.EquipmentIds = [tool];
        Assert.Equal("planning_conflict", Assert.Throws<DomainException>(() => PlanningRules.EnsureAssignment(b, [a], [])).Code);
    }

    [Fact]
    public void Schedule_edit_cannot_shift_into_an_existing_assignment()
    {
        var first = BusinessFixture.Assignment(); var edited = BusinessFixture.Assignment(first.EmployeeId, 12, 16);
        PlanningRules.EnsureAssignment(edited, [first, edited], []);
        edited.Start = BusinessFixture.Now.AddHours(3);
        Assert.Throws<DomainException>(() => PlanningRules.EnsureAssignment(edited, [first, edited], []));
    }

    [Fact]
    public void Time_entry_uses_elapsed_utc_time_and_excludes_nonoverlapping_breaks()
    {
        var entry = new TimeEntry
        {
            TenantId = BusinessFixture.Tenant, EmployeeId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Activity = "Roof repair",
            Start = BusinessFixture.Now, End = BusinessFixture.Now.AddHours(8),
            Breaks = [new(BusinessFixture.Now.AddHours(2), BusinessFixture.Now.AddHours(2.25)), new(BusinessFixture.Now.AddHours(4), BusinessFixture.Now.AddHours(4.5))]
        };
        Assert.Equal(7.25m, entry.NetHours());
        entry.Breaks.Add(new(BusinessFixture.Now.AddHours(4.25), BusinessFixture.Now.AddHours(4.75)));
        Assert.Equal("overlapping_breaks", Assert.Throws<DomainException>(entry.Validate).Code);
    }

    [Fact]
    public void Overnight_entry_across_dst_uses_instants_and_rejects_non_utc_offsets()
    {
        var entry = new TimeEntry
        {
            TenantId = BusinessFixture.Tenant, EmployeeId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Activity = "Night shift",
            Start = new(2026, 10, 24, 22, 0, 0, TimeSpan.Zero), End = new(2026, 10, 25, 7, 0, 0, TimeSpan.Zero)
        };
        Assert.Equal(9m, entry.NetHours());
        entry.Start = entry.Start.ToOffset(TimeSpan.FromHours(2)); Assert.Equal("invalid_utc", Assert.Throws<DomainException>(entry.Validate).Code);
    }

    [Fact]
    public void Time_entry_overlap_is_rejected_across_projects()
    {
        var a = new TimeEntry { TenantId = BusinessFixture.Tenant, EmployeeId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Activity = "A", Start = BusinessFixture.Now, End = BusinessFixture.Now.AddHours(2) };
        var b = new TimeEntry { TenantId = a.TenantId, EmployeeId = a.EmployeeId, ProjectId = Guid.NewGuid(), Activity = "B", Start = a.Start.AddHours(1), End = a.End.AddHours(1) };
        Assert.Equal("time_conflict", Assert.Throws<DomainException>(() => PlanningRules.EnsureTimeEntry(b, [a])).Code);
    }

    [Fact]
    public void Purchasing_partial_receipts_reservations_consumption_and_release_reconcile()
    {
        var material = Guid.NewGuid(); var lineId = Guid.NewGuid();
        var order = new PurchaseOrder
        {
            TenantId = BusinessFixture.Tenant, SupplierId = Guid.NewGuid(), Title = "Roof tiles",
            Lines = [new() { Id = lineId, MaterialId = material, Description = "Tiles", Quantity = 100, UnitNet = 2m }]
        };
        InventoryRules.Order(order, "PO1");
        var first = InventoryRules.Receive(order, lineId, 60, [], "Main", BusinessFixture.Now);
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, order.Status);
        Assert.Equal("over_receipt", Assert.Throws<DomainException>(() => InventoryRules.Receive(order, lineId, 41, [first], "Main", BusinessFixture.Now)).Code);
        var second = InventoryRules.Receive(order, lineId, 40, [first], "Main", BusinessFixture.Now);
        Assert.Equal(PurchaseOrderStatus.Received, order.Status);
        var reservation = new MaterialReservation { TenantId = order.TenantId, MaterialId = material, ProjectId = Guid.NewGuid(), Location = "Main", Quantity = 70 };
        InventoryRules.Reserve(reservation, [first, second], []);
        var consumed = InventoryRules.Consume(reservation, 50, 2m, BusinessFixture.Now);
        InventoryRules.Release(reservation, 10);
        var balance = InventoryRules.Balance(order.TenantId, material, "Main", [first, second, consumed], [reservation]);
        Assert.Equal(50m, balance.OnHand); Assert.Equal(10m, balance.Reserved); Assert.Equal(40m, balance.Available);
        Assert.Equal("reservation_exceeded", Assert.Throws<DomainException>(() => InventoryRules.Consume(reservation, 11, 2m, BusinessFixture.Now)).Code);
    }

    [Fact]
    public void New_reservation_cannot_overbook_existing_stock_or_use_other_tenant_stock()
    {
        var material = Guid.NewGuid();
        var receipt = new StockMovement { TenantId = BusinessFixture.Tenant, MaterialId = material, Location = "Main", Quantity = 10, Kind = StockMovementKind.Receipt, OccurredAt = BusinessFixture.Now };
        var first = new MaterialReservation { TenantId = BusinessFixture.Tenant, MaterialId = material, ProjectId = Guid.NewGuid(), Location = "Main", Quantity = 7 };
        var second = new MaterialReservation { TenantId = BusinessFixture.Tenant, MaterialId = material, ProjectId = Guid.NewGuid(), Location = "Main", Quantity = 4 };
        Assert.Equal("insufficient_stock", Assert.Throws<DomainException>(() => InventoryRules.Reserve(second, [receipt], [first])).Code);
        receipt.TenantId = Guid.NewGuid();
        Assert.Throws<DomainException>(() => InventoryRules.Reserve(first, [receipt], []));
    }

    [Fact]
    public void Ordered_purchase_and_posted_stock_input_mutations_are_detected()
    {
        var order = new PurchaseOrder { TenantId = BusinessFixture.Tenant, SupplierId = Guid.NewGuid(), Title = "Purchase", Lines = [new() { MaterialId = Guid.NewGuid(), Description = "Tile", Quantity = 1, UnitNet = 10 }] };
        InventoryRules.Order(order, "PO1"); var receipt = InventoryRules.Receive(order, order.Lines[0].Id, 1, [], "Main", BusinessFixture.Now);
        receipt.Quantity = 2; Assert.Equal("movement_immutable", Assert.Throws<DomainException>(receipt.Validate).Code);
        order.Lines[0].UnitNet = 1; Assert.Equal("purchase_immutable", Assert.Throws<DomainException>(order.Validate).Code);
    }

    [Fact]
    public void Inventory_count_is_a_signed_immutable_delta_and_never_discards_reserved_stock()
    {
        var materialId = Guid.NewGuid();
        var receipt = new StockMovement
        {
            TenantId = BusinessFixture.Tenant, MaterialId = materialId, Kind = StockMovementKind.Receipt,
            Quantity = 10m, UnitCost = 2m, Location = "A", OccurredAt = BusinessFixture.Now
        };
        receipt.PostedDigest = InventoryRules.MovementDigest(receipt);
        var reservation = new MaterialReservation
        {
            TenantId = receipt.TenantId, MaterialId = materialId, ProjectId = Guid.NewGuid(),
            Location = "A", Quantity = 8m
        };
        var movements = new[] { receipt };
        var reservations = new[] { reservation };
        Assert.Equal("stale_stock_count", Assert.Throws<DomainException>(() => InventoryRules.CountAdjustment(
            receipt.TenantId, materialId, "A", 9m, 8m, 2m, "Signed synthetic stock count",
            movements, reservations, BusinessFixture.Now)).Code);
        Assert.Equal("stock_reserved", Assert.Throws<DomainException>(() => InventoryRules.CountAdjustment(
            receipt.TenantId, materialId, "A", 10m, 7m, 2m, "Signed synthetic stock count",
            movements, reservations, BusinessFixture.Now)).Code);
        var correction = InventoryRules.CountAdjustment(receipt.TenantId, materialId, "A",
            10m, 8m, 2m, "Signed synthetic stock count", movements, reservations, BusinessFixture.Now);
        Assert.Equal(StockMovementKind.Adjustment, correction.Kind);
        Assert.Equal(-2m, correction.Quantity);
        Assert.Equal(64, correction.PostedDigest.Length);
        Assert.Equal(new StockBalance(8m, 8m, 0m),
            InventoryRules.Balance(receipt.TenantId, materialId, "A", [receipt, correction], reservations));
        correction.Reason = "Altered evidence";
        Assert.Equal("movement_immutable", Assert.Throws<DomainException>(correction.Validate).Code);
        Assert.Equal("unchanged_stock_count", Assert.Throws<DomainException>(() => InventoryRules.CountAdjustment(
            receipt.TenantId, materialId, "A", 8m, 8m, 2m, "Synthetic count", [receipt, new StockMovement { TenantId = receipt.TenantId, MaterialId = materialId, Location = "A", Kind = StockMovementKind.Adjustment, Quantity = -2m, UnitCost = 2m, Reason = "Signed synthetic stock count", OccurredAt = BusinessFixture.Now } ], reservations, BusinessFixture.Now)).Code);
    }

    [Fact]
    public void Supplier_returns_reopen_only_the_correct_purchase_line_and_allow_a_replacement_receipt()
    {
        var materialId = Guid.NewGuid();
        var order = new PurchaseOrder { TenantId = BusinessFixture.Tenant, SupplierId = Guid.NewGuid(),
            Title = "Synthetic tile delivery", Lines = [new() { MaterialId = materialId, Description = "Tile", Quantity = 10m, UnitNet = 2m }] };
        InventoryRules.Order(order, "BE-2026-000010");
        var source = InventoryRules.Receive(order, order.Lines[0].Id, 10m, [], "A", BusinessFixture.Now);
        Assert.Equal(PurchaseOrderStatus.Received, order.Status);
        var first = InventoryRules.SupplierReturn(order, source, 3m, "Delivery defect with signed supplier receipt",
            [source], [], BusinessFixture.Now);
        Assert.Equal(StockMovementKind.SupplierReturn, first.Kind);
        Assert.Equal(-3m, first.Quantity);
        Assert.Equal(source.Id.ToString("D"), first.Reference);
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, order.Status);
        Assert.Equal(7m, InventoryRules.ReceivedQuantity(order, order.Lines[0].Id, [source, first]));
        Assert.Equal("supplier_return_exceeded", Assert.Throws<DomainException>(() => InventoryRules.SupplierReturn(
            order, source, 8m, "Duplicate supplier return", [source, first], [], BusinessFixture.Now)).Code);
        var replacement = InventoryRules.Receive(order, order.Lines[0].Id, 3m, [source, first], "A", BusinessFixture.Now);
        Assert.Equal(PurchaseOrderStatus.Received, order.Status);
        Assert.Equal(10m, InventoryRules.ReceivedQuantity(order, order.Lines[0].Id, [source, first, replacement]));
        Assert.Equal(new StockBalance(10m, 0m, 10m),
            InventoryRules.Balance(order.TenantId, materialId, "A", [source, first, replacement], []));
    }

    [Fact]
    public void Supplier_returns_cannot_remove_project_reservations_and_cannot_change_other_tenant_stock()
    {
        var order = new PurchaseOrder { TenantId = BusinessFixture.Tenant, SupplierId = Guid.NewGuid(),
            Title = "Roof tiles", Lines = [new() { MaterialId = Guid.NewGuid(), Description = "Tile", Quantity = 10, UnitNet = 5 }] };
        InventoryRules.Order(order, "PO1");
        var receipt = InventoryRules.Receive(order, order.Lines[0].Id, 10, [], "A", BusinessFixture.Now);
        var reserved = new MaterialReservation { TenantId = order.TenantId, MaterialId = receipt.MaterialId, ProjectId = Guid.NewGuid(), Location = "A", Quantity = 9 };
        Assert.Equal("stock_reserved", Assert.Throws<DomainException>(() => InventoryRules.SupplierReturn(
            order, receipt, 2, "Supplier delivery defect", [receipt], [reserved], BusinessFixture.Now)).Code);
        var other = new PurchaseOrder { TenantId = Guid.NewGuid(), SupplierId = Guid.NewGuid(), Title = "Foreign",
            Lines = [new() { MaterialId = receipt.MaterialId, Description = "Tile", Quantity = 10, UnitNet = 1 }] };
        InventoryRules.Order(other, "P-FOREIGN");
        Assert.Equal("invalid_supplier_return", Assert.Throws<DomainException>(() => InventoryRules.SupplierReturn(
            other, receipt, 1, "Foreign business", [receipt], [], BusinessFixture.Now)).Code);
    }

    [Fact]
    public void Project_returns_reverse_consumption_cost_without_reopening_reserved_units()
    {
        var materialId = Guid.NewGuid();
        var receipt = new StockMovement { TenantId = BusinessFixture.Tenant, MaterialId = materialId,
            Kind = StockMovementKind.Receipt, Location = "A", Quantity = 10m, OccurredAt = BusinessFixture.Now };
        receipt.PostedDigest = InventoryRules.MovementDigest(receipt);
        var reservation = new MaterialReservation { TenantId = receipt.TenantId, MaterialId = materialId,
            ProjectId = Guid.NewGuid(), Location = "A", Quantity = 5m };
        InventoryRules.Reserve(reservation, [receipt], []);
        var consumed = InventoryRules.Consume(reservation, 3m, 12m, BusinessFixture.Now);
        var returned = InventoryRules.ProjectReturn(consumed, 2m, "Unused project stock returned",
            [receipt, consumed], BusinessFixture.Now);
        Assert.Equal(StockMovementKind.Return, returned.Kind);
        Assert.Equal(consumed.ProjectId, returned.ProjectId);
        Assert.Equal(consumed.ReservationId, returned.ReservationId);
        Assert.Equal(consumed.UnitCost, returned.UnitCost);
        Assert.Equal(2m, returned.Quantity);
        Assert.Equal(2m, reservation.Quantity - reservation.ConsumedQuantity);
        Assert.Equal(new StockBalance(9m, 2m, 7m),
            InventoryRules.Balance(receipt.TenantId, materialId, "A", [receipt, consumed, returned], [reservation]));
        Assert.Equal("project_return_exceeded", Assert.Throws<DomainException>(() => InventoryRules.ProjectReturn(
            consumed, 2m, "Duplicate project return", [receipt, consumed, returned], BusinessFixture.Now)).Code);
        Assert.Equal("invalid_project_return", Assert.Throws<DomainException>(() => InventoryRules.ProjectReturn(
            receipt, 1m, "Not a consumption", [receipt], BusinessFixture.Now)).Code);
    }

    [Fact]
    public void Transfer_posts_an_atomic_balanced_pair_without_touching_existing_project_reservations()
    {
        var materialId = Guid.NewGuid();
        var receipt = new StockMovement { TenantId = BusinessFixture.Tenant, MaterialId = materialId,
            Kind = StockMovementKind.Receipt, Location = "A", Quantity = 10m, OccurredAt = BusinessFixture.Now };
        receipt.PostedDigest = InventoryRules.MovementDigest(receipt);
        var reserved = new MaterialReservation { TenantId = receipt.TenantId, MaterialId = materialId,
            ProjectId = Guid.NewGuid(), Location = "A", Quantity = 4m };
        var (outgoing, incoming) = InventoryRules.Transfer(receipt.TenantId, materialId, "A", "B",
            6m, 1m, "Stock transfer on delivery note", [receipt], [reserved], BusinessFixture.Now);
        Assert.Equal(-outgoing.Quantity, incoming.Quantity);
        Assert.Equal(outgoing.Reference, incoming.Reference);
        Assert.Equal(StockMovementKind.TransferOut, outgoing.Kind);
        Assert.Equal(StockMovementKind.TransferIn, incoming.Kind);
        Assert.Equal(new StockBalance(4m, 4m, 0m),
            InventoryRules.Balance(receipt.TenantId, materialId, "A", [receipt, outgoing, incoming], [reserved]));
        Assert.Equal(new StockBalance(6m, 0m, 6m),
            InventoryRules.Balance(receipt.TenantId, materialId, "B", [receipt, outgoing, incoming], [reserved]));
        Assert.Equal("stock_reserved", Assert.Throws<DomainException>(() => InventoryRules.Transfer(
            receipt.TenantId, materialId, "A", "B", 7m, 1m, "Excess transfer",
            [receipt], [reserved], BusinessFixture.Now)).Code);
        Assert.Equal("same_stock_location", Assert.Throws<DomainException>(() => InventoryRules.Transfer(
            receipt.TenantId, materialId, "A", "a", 1m, 1m, "Invalid same warehouse",
            [receipt], [reserved], BusinessFixture.Now)).Code);
    }
}
