using System.Text.Json.Serialization;

namespace FirstBlick.Core.Business;

public sealed class Supplier : TenantEntity
{
    public string Name { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Address { get; set; } = "";
    public string AccountReference { get; set; } = "";
    public override void Validate()
    {
        ValidateCommon(); Guard.Text(Name, nameof(Name)); Guard.Text(ContactName, nameof(ContactName), required: false); Guard.Email(Email);
        Guard.Text(Phone, nameof(Phone), 80, false); Guard.Text(Address, nameof(Address), 2000, false); Guard.Text(AccountReference, nameof(AccountReference), 120, false);
    }
}

public sealed class Material : TenantEntity
{
    public string Name { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Unit { get; set; } = "Stk";
    public decimal ReorderLevel { get; set; }
    public Guid? PreferredSupplierId { get; set; }
    public override void Validate()
    {
        ValidateCommon(); Guard.Text(Name, nameof(Name)); Guard.Text(Sku, nameof(Sku), 100, false); Guard.Text(Unit, nameof(Unit), 32);
        Guard.Range(ReorderLevel, 0, 1_000_000m, nameof(ReorderLevel)); Guard.OptionalId(PreferredSupplierId, nameof(PreferredSupplierId));
    }
    public override IEnumerable<EntityReference> References() => PreferredSupplierId is { } id ? [BusinessRegistry.Reference<Supplier>(id)] : [];
}

public sealed record PurchaseLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MaterialId { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitNet { get; set; }
    public decimal VatPercent { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? MaterialDemandId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QuantityEvidence? QuantityEvidence { get; set; }
    public void Validate()
    {
        Guard.Id(Id, nameof(Id)); Guard.Id(MaterialId, nameof(MaterialId)); Guard.Text(Description, nameof(Description), 2000);
        Guard.Range(Quantity, 0.000001m, 1_000_000m, nameof(Quantity)); Guard.Range(UnitNet, 0, 1_000_000m, nameof(UnitNet)); Guard.Range(VatPercent, 0, 100, nameof(VatPercent));
        Money.Amount(Money.Round(Quantity * UnitNet), "Purchase line net");
        Guard.OptionalId(MaterialDemandId, nameof(MaterialDemandId));
        Guard.Require(MaterialDemandId.HasValue == (QuantityEvidence is not null), "invalid_demand_reference", "A demand purchase line must retain both its demand and quantity evidence.");
        QuantityLinks.EnsureTarget(QuantityEvidence, Quantity);
    }
}

public enum PurchaseOrderStatus { Draft, Ordered, PartiallyReceived, Received, Cancelled }
public sealed class PurchaseOrder : TenantEntity
{
    public Guid SupplierId { get; set; }
    public Guid? ProjectId { get; set; }
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string Currency { get; set; } = "EUR";
    public DateOnly? ExpectedOn { get; set; }
    public PurchaseOrderStatus Status { get; set; }
    public List<PurchaseLine> Lines { get; set; } = [];
    public string OrderedDigest { get; set; } = "";
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(SupplierId, nameof(SupplierId)); Guard.OptionalId(ProjectId, nameof(ProjectId));
        Guard.Text(Number, nameof(Number), 80, Status != PurchaseOrderStatus.Draft); Guard.Text(Title, nameof(Title)); Guard.Currency(Currency);
        Guard.Items(Lines, nameof(Lines), 1000, 1); foreach (var line in Lines) line.Validate();
        Guard.Require(Enum.IsDefined(Status), "invalid_status", "Purchase order status is invalid.");
        Guard.Require(Lines.Select(x => x.Id).Distinct().Count() == Lines.Count, "duplicate_line", "Purchase lines must have unique identifiers.");
        if (Status != PurchaseOrderStatus.Draft)
            Guard.Require(OrderedDigest == InventoryRules.PurchaseDigest(this), "purchase_immutable", "Ordered quantities and prices are immutable.");
    }
    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Supplier>(SupplierId);
        if (ProjectId is { } project) yield return BusinessRegistry.Reference<Project>(project);
        foreach (var id in Lines.Select(x => x.MaterialId).Distinct()) yield return BusinessRegistry.Reference<Material>(id);
        foreach (var id in Lines.Where(x => x.MaterialDemandId.HasValue).Select(x => x.MaterialDemandId!.Value).Distinct()) yield return BusinessRegistry.Reference<MaterialDemand>(id);
    }
}

public enum StockMovementKind { Receipt, Consumption, Return, Adjustment, TransferIn, TransferOut, SupplierReturn }
public sealed class StockMovement : TenantEntity
{
    public Guid MaterialId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? PurchaseOrderId { get; set; }
    public Guid? PurchaseLineId { get; set; }
    public Guid? ReservationId { get; set; }
    public string Location { get; set; } = "Hauptlager";
    public StockMovementKind Kind { get; set; }
    /// <summary>Positive receipts/returns/inbound transfers; negative consumption/outbound transfers; signed adjustments.</summary>
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Reference { get; set; } = "";
    public string Reason { get; set; } = "";
    public string PostedDigest { get; set; } = "";
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(MaterialId, nameof(MaterialId)); Guard.OptionalId(ProjectId, nameof(ProjectId));
        Guard.OptionalId(PurchaseOrderId, nameof(PurchaseOrderId)); Guard.OptionalId(PurchaseLineId, nameof(PurchaseLineId)); Guard.OptionalId(ReservationId, nameof(ReservationId));
        Guard.Require(PurchaseOrderId.HasValue == PurchaseLineId.HasValue, "invalid_purchase_reference", "A purchase movement requires both order and line reference.");
        Guard.Text(Location, nameof(Location), 120); Guard.Utc(OccurredAt, nameof(OccurredAt)); Guard.Text(Reference, nameof(Reference), 240, false);
        Guard.Text(Reason, nameof(Reason), 2000, Kind == StockMovementKind.Adjustment); Guard.Range(Quantity, -1_000_000m, 1_000_000m, nameof(Quantity));
        Guard.Range(UnitCost, 0, 1_000_000m, nameof(UnitCost)); Guard.Require(Quantity != 0, "zero_movement", "A stock movement cannot be zero.");
        Guard.Require(Enum.IsDefined(Kind), "invalid_kind", "Stock movement kind is invalid.");
        Guard.Require(Kind switch
        {
            StockMovementKind.Receipt or StockMovementKind.Return or StockMovementKind.TransferIn => Quantity > 0,
            StockMovementKind.Consumption or StockMovementKind.TransferOut or StockMovementKind.SupplierReturn => Quantity < 0,
            StockMovementKind.Adjustment => true, _ => false
        }, "invalid_quantity_direction", "The quantity sign does not match the movement kind.");
        if (!string.IsNullOrEmpty(PostedDigest)) Guard.Require(PostedDigest == InventoryRules.MovementDigest(this), "movement_immutable", "Posted stock movements are immutable; post a compensating movement.");
    }
    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Material>(MaterialId);
        if (ProjectId is { } project) yield return BusinessRegistry.Reference<Project>(project);
        if (PurchaseOrderId is { } purchase) yield return BusinessRegistry.Reference<PurchaseOrder>(purchase);
        if (ReservationId is { } reservation) yield return BusinessRegistry.Reference<MaterialReservation>(reservation);
    }
}

public sealed class MaterialReservation : TenantEntity
{
    public Guid MaterialId { get; set; }
    public Guid ProjectId { get; set; }
    public string Location { get; set; } = "Hauptlager";
    public decimal Quantity { get; set; }
    public decimal ConsumedQuantity { get; set; }
    public decimal ReleasedQuantity { get; set; }
    public DateOnly? RequiredOn { get; set; }
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(MaterialId, nameof(MaterialId)); Guard.Id(ProjectId, nameof(ProjectId)); Guard.Text(Location, nameof(Location), 120);
        Guard.Range(Quantity, 0.000001m, 1_000_000m, nameof(Quantity)); Guard.Range(ConsumedQuantity, 0, Quantity, nameof(ConsumedQuantity));
        Guard.Range(ReleasedQuantity, 0, Quantity, nameof(ReleasedQuantity));
        Guard.Require(ConsumedQuantity + ReleasedQuantity <= Quantity, "reservation_exceeded", "Consumption and releases exceed the reserved quantity.");
    }
    public override IEnumerable<EntityReference> References() => [BusinessRegistry.Reference<Material>(MaterialId), BusinessRegistry.Reference<Project>(ProjectId)];
}

public enum EquipmentKind { Tool, Machine, Vehicle }
public sealed class Equipment : TenantEntity
{
    public string Name { get; set; } = "";
    public string InventoryNumber { get; set; } = "";
    public EquipmentKind Kind { get; set; }
    public string Registration { get; set; } = "";
    public string Location { get; set; } = "";
    public DateOnly? NextInspectionOn { get; set; }
    public bool Available { get; set; } = true;
    public override void Validate()
    {
        ValidateCommon(); Guard.Text(Name, nameof(Name)); Guard.Text(InventoryNumber, nameof(InventoryNumber), 100);
        Guard.Text(Registration, nameof(Registration), 80, false); Guard.Text(Location, nameof(Location), 240, false);
        Guard.Require(Enum.IsDefined(Kind), "invalid_kind", "Equipment kind is invalid.");
    }
}

public sealed record StockBalance(decimal OnHand, decimal Reserved, decimal Available);
public static class InventoryRules
{
    public static string PurchaseDigest(PurchaseOrder order) => ContentDigest.Of(new { order.TenantId, order.Id, order.SupplierId, order.ProjectId, order.Number, order.Title, order.Currency, order.Lines });
    public static string MovementDigest(StockMovement movement) => ContentDigest.Of(new
    {
        movement.TenantId, movement.Id, movement.MaterialId, movement.ProjectId, movement.PurchaseOrderId, movement.PurchaseLineId,
        movement.ReservationId, movement.Location, movement.Kind, movement.Quantity, movement.UnitCost, movement.OccurredAt, movement.Reference, movement.Reason
    });

    public static void Order(PurchaseOrder order, string number)
    {
        order.Validate(); Guard.Text(number, nameof(number), 80); Guard.Require(order.Status == PurchaseOrderStatus.Draft, "invalid_transition", "Only draft purchase orders can be ordered.");
        order.Number = number; order.OrderedDigest = PurchaseDigest(order); order.Status = PurchaseOrderStatus.Ordered;
    }

    public static StockMovement Receive(PurchaseOrder order, Guid lineId, decimal quantity, IReadOnlyCollection<StockMovement> movements, string location, DateTimeOffset now)
    {
        order.Validate(); Guard.Range(quantity, 0.000001m, 1_000_000m, nameof(quantity));
        Guard.Require(order.Status is PurchaseOrderStatus.Ordered or PurchaseOrderStatus.PartiallyReceived, "invalid_transition", "Only outstanding purchase orders can receive goods.");
        var line = order.Lines.SingleOrDefault(x => x.Id == lineId);
        Guard.Require(line is not null, "missing_line", "The purchase line does not exist.");
        var already = movements.Where(x => !x.Archived && x.TenantId == order.TenantId && x.PurchaseOrderId == order.Id && x.PurchaseLineId == lineId && x.Kind is StockMovementKind.Receipt or StockMovementKind.SupplierReturn).Sum(x => x.Quantity);
        Guard.Require(already + quantity <= line!.Quantity, "over_receipt", "The receipt exceeds the ordered quantity. Resolve the purchasing discrepancy first.");
        var receipt = new StockMovement
        {
            TenantId = order.TenantId, MaterialId = line.MaterialId, PurchaseOrderId = order.Id, PurchaseLineId = line.Id,
            ProjectId = order.ProjectId, Location = location, Kind = StockMovementKind.Receipt, Quantity = quantity, UnitCost = line.UnitNet,
            OccurredAt = now, Reference = order.Number
        };
        receipt.Validate(); receipt.PostedDigest = MovementDigest(receipt);
        order.Status = order.Lines.All(x => movements.Where(m => !m.Archived && m.TenantId == order.TenantId && m.PurchaseOrderId == order.Id && m.PurchaseLineId == x.Id && m.Kind is StockMovementKind.Receipt or StockMovementKind.SupplierReturn).Sum(m => m.Quantity)
            + (x.Id == lineId ? quantity : 0) >= x.Quantity) ? PurchaseOrderStatus.Received : PurchaseOrderStatus.PartiallyReceived;
        return receipt;
    }

    public static StockBalance Balance(Guid tenantId, Guid materialId, string location, IEnumerable<StockMovement> movements, IEnumerable<MaterialReservation> reservations)
    {
        var stock = movements.Where(x => x.TenantId == tenantId && x.MaterialId == materialId && x.Location == location).ToList();
        var reserved = reservations.Where(x => x.TenantId == tenantId && x.MaterialId == materialId && x.Location == location).ToList();
        foreach (var movement in stock) movement.Validate(); foreach (var reservation in reserved) reservation.Validate();
        var onHand = stock.Sum(x => x.Quantity); var remaining = reserved.Sum(x => x.Quantity - x.ConsumedQuantity - x.ReleasedQuantity);
        return new(onHand, remaining, onHand - remaining);
    }

    public static void Reserve(MaterialReservation reservation, IEnumerable<StockMovement> movements, IEnumerable<MaterialReservation> existing)
    {
        reservation.Validate(); Guard.Require(reservation.ConsumedQuantity == 0 && reservation.ReleasedQuantity == 0, "invalid_reservation", "A new reservation starts without consumption or releases.");
        var balance = Balance(reservation.TenantId, reservation.MaterialId, reservation.Location, movements, existing.Where(x => x.Id != reservation.Id));
        Guard.Require(reservation.Quantity <= balance.Available, "insufficient_stock", "The reservation exceeds available stock.");
    }

    public static StockMovement Consume(MaterialReservation reservation, decimal quantity, decimal unitCost, DateTimeOffset now)
    {
        reservation.Validate(); Guard.Range(quantity, 0.000001m, 1_000_000m, nameof(quantity));
        Guard.Require(quantity <= reservation.Quantity - reservation.ConsumedQuantity - reservation.ReleasedQuantity, "reservation_exceeded", "Consumption exceeds the remaining reservation.");
        var movement = new StockMovement
        {
            TenantId = reservation.TenantId, MaterialId = reservation.MaterialId, ProjectId = reservation.ProjectId, ReservationId = reservation.Id,
            Location = reservation.Location, Kind = StockMovementKind.Consumption, Quantity = -quantity, UnitCost = unitCost, OccurredAt = now
        };
        movement.Validate(); movement.PostedDigest = MovementDigest(movement); reservation.ConsumedQuantity += quantity;
        return movement;
    }

    public static void Release(MaterialReservation reservation, decimal quantity)
    {
        reservation.Validate(); Guard.Range(quantity, 0.000001m, 1_000_000m, nameof(quantity));
        Guard.Require(quantity <= reservation.Quantity - reservation.ConsumedQuantity - reservation.ReleasedQuantity, "reservation_exceeded", "Release exceeds the remaining reservation.");
        reservation.ReleasedQuantity += quantity;
    }
}
