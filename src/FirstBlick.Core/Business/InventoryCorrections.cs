namespace FirstBlick.Core.Business;

/// <summary>
/// Auditable corrections against the posted material ledger. These operations are designed
/// for execution under the existing tenant-wide PostgreSQL command lock.
/// </summary>
public static class InventoryCorrections
{
    private static StockMovement Post(Guid tenantId, Guid materialId, string location, StockMovementKind kind,
        decimal quantity, decimal unitCost, string reference, string reason, DateTimeOffset now,
        Guid? projectId = null, Guid? orderId = null, Guid? lineId = null, Guid? reservationId = null)
    {
        Guard.Id(tenantId, nameof(tenantId));
        Guard.Id(materialId, nameof(materialId));
        Guard.Text(location, nameof(location), 120);
        Guard.Text(reference, nameof(reference), 240);
        Guard.Text(reason, nameof(reason), 2000);
        Guard.Utc(now, nameof(now));
        Guard.Range(unitCost, 0, 1_000_000m, nameof(unitCost));
        var result = new StockMovement
        {
            TenantId = tenantId, MaterialId = materialId, ProjectId = projectId,
            PurchaseOrderId = orderId, PurchaseLineId = lineId, ReservationId = reservationId,
            Location = location, Kind = kind, Quantity = quantity, UnitCost = unitCost,
            OccurredAt = now, Reference = reference.Trim(), Reason = reason.Trim()
        };
        result.Validate();
        result.PostedDigest = InventoryRules.MovementDigest(result);
        return result;
    }

    /// <summary>Count is a physical quantity, never a blind signed correction; a stale observation is rejected.</summary>
    public static StockMovement ReconcileCount(Guid tenantId, Guid materialId, string location,
        decimal expectedOnHand, decimal countedOnHand, decimal unitCost, string evidenceReference,
        string reason, IReadOnlyCollection<StockMovement> movements,
        IReadOnlyCollection<MaterialReservation> reservations, DateTimeOffset now)
    {
        Guard.Range(expectedOnHand, 0, 1_000_000m, nameof(expectedOnHand));
        Guard.Range(countedOnHand, 0, 1_000_000m, nameof(countedOnHand));
        var balance = InventoryRules.Balance(tenantId, materialId, location, movements, reservations);
        Guard.Require(balance.OnHand == expectedOnHand, "stock_count_stale",
            "The material balance changed since the physical count was reviewed.");
        Guard.Require(countedOnHand >= balance.Reserved, "reserved_stock",
            "A physical count cannot discard material already reserved for projects. Resolve reservations first.");
        var delta = countedOnHand - balance.OnHand;
        Guard.Require(delta != 0, "no_stock_difference", "The counted quantity equals the book quantity.");
        return Post(tenantId, materialId, location, StockMovementKind.Adjustment, delta,
            unitCost, evidenceReference, reason, now);
    }

    /// <summary>Two opposite movements share a correlation reference and are posted in one database transaction.</summary>
    public static (StockMovement Outbound, StockMovement Inbound) Transfer(Guid tenantId, Guid materialId,
        string fromLocation, string toLocation, decimal quantity, decimal unitCost, decimal expectedFromOnHand,
        string reason, Guid transferId, IReadOnlyCollection<StockMovement> movements,
        IReadOnlyCollection<MaterialReservation> reservations, DateTimeOffset now)
    {
        Guard.Text(fromLocation, nameof(fromLocation), 120);
        Guard.Text(toLocation, nameof(toLocation), 120);
        Guard.Require(!string.Equals(fromLocation, toLocation, StringComparison.Ordinal),
            "same_location", "A transfer requires two different locations.");
        Guard.Id(transferId, nameof(transferId));
        Guard.Range(quantity, 0.000001m, 1_000_000m, nameof(quantity));
        Guard.Range(expectedFromOnHand, 0, 1_000_000m, nameof(expectedFromOnHand));
        var from = InventoryRules.Balance(tenantId, materialId, fromLocation, movements, reservations);
        Guard.Require(from.OnHand == expectedFromOnHand, "stock_count_stale",
            "The source balance changed; refresh and review the transfer.");
        Guard.Require(from.Available >= quantity, "insufficient_stock",
            "The transfer would move reserved or unavailable stock.");
        var reference = transferId.ToString("N");
        var outbound = Post(tenantId, materialId, fromLocation, StockMovementKind.TransferOut,
            -quantity, unitCost, reference, reason, now);
        var inbound = Post(tenantId, materialId, toLocation, StockMovementKind.TransferIn,
            quantity, unitCost, reference, reason, now);
        return (outbound, inbound);
    }

    /// <summary>Returns one original project consumption at its ORIGINAL valuation; no double credit is possible.</summary>
    public static StockMovement ReturnConsumption(MaterialReservation reservation, StockMovement original,
        decimal quantity, string reason, IReadOnlyCollection<StockMovement> movements, DateTimeOffset now)
    {
        reservation.Validate();
        original.Validate();
        Guard.Require(!original.Archived && !reservation.Archived
            && original.PostedDigest.Length == 64
            && original.Kind == StockMovementKind.Consumption
            && original.TenantId == reservation.TenantId && original.MaterialId == reservation.MaterialId
            && original.ProjectId == reservation.ProjectId && original.ReservationId == reservation.Id
            && original.Location == reservation.Location,
            "invalid_return_source", "Only an original posted consumption of this reservation can be returned.");
        Guard.Range(quantity, 0.000001m, 1_000_000m, nameof(quantity));
        var reference = original.Id.ToString("N");
        var alreadyReturned = movements.Where(m => !m.Archived && m.TenantId == reservation.TenantId
            && m.Kind == StockMovementKind.Return && m.ReservationId == reservation.Id
            && m.Reference == reference).Sum(m => m.Quantity);
        Guard.Require(quantity <= -original.Quantity - alreadyReturned
            && quantity <= reservation.ConsumedQuantity,
            "over_return", "The returned quantity exceeds the remaining original project consumption.");
        var correction = Post(reservation.TenantId, reservation.MaterialId, reservation.Location,
            StockMovementKind.Return, quantity, original.UnitCost, reference, reason, now,
            projectId: reservation.ProjectId, reservationId: reservation.Id);
        // Returned material becomes freely available again, without recreating a phantom reservation.
        reservation.ConsumedQuantity -= quantity;
        reservation.ReleasedQuantity += quantity;
        reservation.Validate();
        return correction;
    }

    /// <summary>Physical supplier returns reverse accepted receipt volume; financial refunds are separate.</summary>
    public static StockMovement ReturnToSupplier(PurchaseOrder order, Guid lineId, decimal quantity,
        string location, string reference, string reason,
        IReadOnlyCollection<StockMovement> movements, IReadOnlyCollection<MaterialReservation> reservations,
        DateTimeOffset now)
    {
        order.Validate();
        Guard.Require(order.Status is PurchaseOrderStatus.Received or PurchaseOrderStatus.PartiallyReceived,
            "invalid_transition", "Only an order with received goods can be returned to its supplier.");
        var line = order.Lines.SingleOrDefault(x => x.Id == lineId);
        Guard.Require(line is not null, "missing_line", "The purchase line does not exist.");
        Guard.Range(quantity, 0.000001m, 1_000_000m, nameof(quantity));
        var purchaseMovements = movements.Where(m => !m.Archived && m.TenantId == order.TenantId
            && m.PurchaseOrderId == order.Id && m.PurchaseLineId == lineId
            && m.Kind is StockMovementKind.Receipt or StockMovementKind.SupplierReturn).ToArray();
        foreach (var movement in purchaseMovements) movement.Validate();
        var receivedNet = purchaseMovements.Sum(m => m.Quantity);
        Guard.Require(quantity <= receivedNet, "over_supplier_return",
            "The supplier return exceeds the net quantity received for this purchase line.");
        var stock = InventoryRules.Balance(order.TenantId, line!.MaterialId, location, movements, reservations);
        Guard.Require(stock.Available >= quantity, "insufficient_stock",
            "The supplier return would withdraw unavailable or project-reserved stock.");
        var result = Post(order.TenantId, line.MaterialId, location, StockMovementKind.SupplierReturn,
            -quantity, line.UnitNet, reference, reason, now,
            projectId: order.ProjectId, orderId: order.Id, lineId: line.Id);
        order.Status = order.Lines.All(x =>
            movements.Where(m => !m.Archived && m.TenantId == order.TenantId
                && m.PurchaseOrderId == order.Id && m.PurchaseLineId == x.Id
                && m.Kind is StockMovementKind.Receipt or StockMovementKind.SupplierReturn).Sum(m => m.Quantity)
            - (x.Id == lineId ? quantity : 0) >= x.Quantity)
            ? PurchaseOrderStatus.Received : PurchaseOrderStatus.PartiallyReceived;
        order.Validate();
        return result;
    }
}
