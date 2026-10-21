namespace FirstBlick.Core.Business;

public sealed record StockTransferProvenance(StockMovement Receipt, IReadOnlyList<StockMovement> Transfers);
public sealed record StockTransferHop(Guid IncomingId, Guid SourceId, string FromLocation, string ToLocation,
    decimal Quantity, DateTimeOffset OccurredAt);
public sealed record StockTransferTrace(Guid SourceMovementId, Guid OriginalReceiptId, string OriginalLocation,
    string SourceLocation, decimal UnitCost, decimal EvidenceRemaining, IReadOnlyList<StockTransferHop> Transfers);

/// <summary>
/// Explicit allocation of receipt quantities to warehouse branches, not a claim that
/// indistinguishable physical units have been identified by a batch or serial number.
/// Unlinked historic transfers cannot be retroactively used as linked chain sources.
/// </summary>
public static class InventoryTransferChain
{
    public const int MaximumTransfers = 32;

    public static StockTransferProvenance Resolve(StockMovement source, IReadOnlyCollection<StockMovement> movements,
        DateTimeOffset? latest = null)
    {
        var visited = new HashSet<Guid>();
        var reversed = new List<StockMovement>();
        var current = source;
        while (true)
        {
            Posted(current);
            Guard.Require(visited.Add(current.Id), "invalid_transfer_chain", "The warehouse chain contains a cycle.");
            Guard.Require(current.TenantId == source.TenantId && current.MaterialId == source.MaterialId
                && current.UnitCost == source.UnitCost && (!latest.HasValue || current.OccurredAt <= latest.Value),
                "invalid_transfer_chain", "Every chain entry must retain the receipt's tenant, material, value and chronological order.");
            if (current.Kind == StockMovementKind.Receipt)
            {
                Guard.Require(current.PurchaseOrderId.HasValue && current.PurchaseLineId.HasValue,
                    "invalid_transfer_source", "A linked warehouse chain requires an original purchase receipt.");
                reversed.Reverse();
                return new(current, reversed);
            }
            Guard.Require(current.Kind == StockMovementKind.TransferIn && current.TransferSourceId.HasValue,
                "invalid_transfer_source", "Select a purchase receipt or an already source-linked incoming transfer.");
            Guard.Require(reversed.Count < MaximumTransfers, "transfer_chain_too_deep",
                "A receipt chain supports at most 32 warehouse transfers.");
            var outgoing = Pair(current, movements);
            var parent = Find(current.TenantId, current.TransferSourceId!.Value, movements);
            Guard.Require(parent.Location == outgoing.Location && parent.OccurredAt <= current.OccurredAt,
                "invalid_transfer_chain", "A transfer must start at its source receipt's warehouse and cannot predate it.");
            reversed.Add(current);
            current = parent;
        }
    }

    public static StockTransferTrace Inspect(StockMovement source, IReadOnlyCollection<StockMovement> movements)
    {
        var chain = Resolve(source, movements);
        return new(source.Id, chain.Receipt.Id, chain.Receipt.Location, source.Location, source.UnitCost,
            Remaining(source, movements, chain), chain.Transfers.Select(x => new StockTransferHop(
                x.Id, x.TransferSourceId!.Value, Pair(x, movements).Location, x.Location, x.Quantity, x.OccurredAt)).ToArray());
    }

    public static void EnsureTransferSource(Guid tenantId, Guid materialId, Guid sourceId, string fromLocation,
        decimal unitCost, decimal quantity, IReadOnlyCollection<StockMovement> movements, DateTimeOffset now)
    {
        Guard.Id(sourceId, nameof(sourceId));
        var source = Find(tenantId, sourceId, movements);
        var chain = Resolve(source, movements, now);
        Guard.Require(source.MaterialId == materialId && source.Location == fromLocation && source.UnitCost == unitCost,
            "invalid_transfer_source", "Source receipt, warehouse, material and unit cost must agree with the requested transfer.");
        Guard.Require(chain.Transfers.Count < MaximumTransfers, "transfer_chain_too_deep",
            "A receipt chain supports at most 32 warehouse transfers.");
        Guard.Require(quantity <= Remaining(source, movements, chain), "transfer_source_exceeded",
            "This quantity is already allocated to another warehouse branch or supplier return.");
    }

    public static decimal Remaining(StockMovement source, IReadOnlyCollection<StockMovement> movements,
        StockTransferProvenance? chain = null)
    {
        chain ??= Resolve(source, movements);
        // Recheck all ancestor capacities. A downstream return must not legitimize
        // an already over-allocated parent, even when the destination has other stock.
        decimal remaining = 0;
        foreach (var node in new[] { chain.Receipt }.Concat(chain.Transfers))
        {
            var children = movements.Where(x => x.TenantId == source.TenantId && x.TransferSourceId == node.Id).ToArray();
            decimal allocated = 0;
            foreach (var child in children.Where(x => x.Kind == StockMovementKind.TransferIn))
            {
                var outgoing = Pair(child, movements);
                Guard.Require(outgoing.Location == node.Location && child.MaterialId == node.MaterialId
                    && child.UnitCost == node.UnitCost && child.OccurredAt >= node.OccurredAt,
                    "invalid_transfer_chain", "A child transfer does not match its declared source receipt.");
                allocated += child.Quantity;
            }
            Guard.Require(children.Length == 2 * children.Count(x => x.Kind == StockMovementKind.TransferIn),
                "invalid_transfer_chain", "Every allocated transfer requires exactly one posted incoming and outgoing entry.");
            var returned = DirectReturns(node, chain.Receipt, movements);
            remaining = node.Quantity - allocated - returned;
            Guard.Require(remaining >= 0, "invalid_transfer_chain", "The warehouse chain over-allocates a source quantity.");
        }
        return remaining;
    }

    private static decimal DirectReturns(StockMovement node, StockMovement original, IReadOnlyCollection<StockMovement> movements)
    {
        decimal returned = 0;
        foreach (var movement in movements.Where(x => x.TenantId == node.TenantId && x.Kind == StockMovementKind.SupplierReturn))
        {
            var referencesOriginal = movement.Reference == original.Id.ToString("D");
            var returnSource = node;
            if (node.Kind == StockMovementKind.Receipt)
            {
                if (!referencesOriginal) continue;
                if (movement.ReturnTransferId is { } transferId)
                {
                    var transfer = Find(node.TenantId, transferId, movements);
                    returnSource = transfer;
                    // A linked descendant was already allocated to its parent when
                    // transferred. Its return must not debit the root a second time.
                    if (transfer.TransferSourceId.HasValue)
                    {
                        Guard.Require(Resolve(transfer, movements).Receipt.Id == original.Id,
                            "invalid_transfer_chain", "The supplier return refers to a different original receipt.");
                        continue;
                    }
                }
            }
            else if (movement.ReturnTransferId != node.Id) continue;
            Posted(movement);
            Guard.Require(referencesOriginal && movement.MaterialId == original.MaterialId
                && movement.UnitCost == original.UnitCost && movement.PurchaseOrderId == original.PurchaseOrderId
                && movement.PurchaseLineId == original.PurchaseLineId && movement.ProjectId == original.ProjectId
                && movement.Location == returnSource.Location && movement.OccurredAt >= returnSource.OccurredAt,
                "invalid_transfer_chain", "A supplier return does not match its allocated purchase receipt.");
            returned -= movement.Quantity;
        }
        return returned;
    }

    private static StockMovement Pair(StockMovement incoming, IReadOnlyCollection<StockMovement> movements)
    {
        Posted(incoming);
        Guard.Require(incoming.Kind == StockMovementKind.TransferIn && incoming.TransferSourceId.HasValue
            && Guid.TryParse(incoming.Reference, out var correlation) && correlation != Guid.Empty,
            "invalid_transfer_chain", "A linked transfer requires a valid pair reference and source receipt.");
        var pair = movements.Where(x => x.TenantId == incoming.TenantId && x.Reference == incoming.Reference
            && x.Kind is StockMovementKind.TransferIn or StockMovementKind.TransferOut).ToArray();
        Guard.Require(pair.Length == 2 && pair.Count(x => x.Kind == StockMovementKind.TransferOut) == 1
            && pair.Count(x => x.Id == incoming.Id) == 1,
            "invalid_transfer_chain", "A transfer must have one unique posted incoming/outgoing pair.");
        var outgoing = pair.Single(x => x.Kind == StockMovementKind.TransferOut);
        Posted(outgoing);
        Guard.Require(outgoing.MaterialId == incoming.MaterialId && outgoing.UnitCost == incoming.UnitCost
            && outgoing.TransferSourceId == incoming.TransferSourceId && outgoing.OccurredAt == incoming.OccurredAt
            && outgoing.Quantity == -incoming.Quantity && outgoing.Location != incoming.Location,
            "invalid_transfer_chain", "Transfer pair quantities, source, material, value, date and warehouses do not agree.");
        return outgoing;
    }

    private static StockMovement Find(Guid tenantId, Guid id, IReadOnlyCollection<StockMovement> movements)
    {
        var found = movements.Where(x => x.TenantId == tenantId && x.Id == id).ToArray();
        Guard.Require(found.Length == 1, "invalid_transfer_source", "The source movement is unavailable in this business.");
        return found[0];
    }

    private static void Posted(StockMovement movement)
    {
        movement.Validate();
        Guard.Require(!movement.Archived && movement.PostedDigest is { Length: 64 },
            "invalid_transfer_source", "Warehouse provenance requires active, immutable posted movements.");
    }
}
