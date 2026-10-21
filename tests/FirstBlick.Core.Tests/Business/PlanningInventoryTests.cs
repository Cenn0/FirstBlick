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
}
