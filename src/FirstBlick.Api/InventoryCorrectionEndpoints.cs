using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace FirstBlick.Api;

public sealed record PhysicalStockCountRequest(
    string Location, decimal ExpectedOnHand, decimal CountedOnHand, decimal UnitCost,
    string EvidenceReference, string Reason);
public sealed record StockTransferRequest(
    string FromLocation, string ToLocation, decimal Quantity, decimal UnitCost,
    decimal ExpectedFromOnHand, string Reason);
public sealed record ProjectStockReturnRequest(Guid ConsumptionMovementId, decimal Quantity, string Reason);
public sealed record SupplierStockReturnRequest(Guid LineId, decimal Quantity, string Location,
    string SupplierCreditReference, string Reason);

/// <summary>
/// Explicit correction commands. Every write is tenant-scoped, idempotent, serialised by
/// the command lock, and committed with its immutable stock evidence in a single transaction.
/// </summary>
public static class InventoryCorrectionEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/materials/{id:guid}/stock-count", (Guid id, PhysicalStockCountRequest body,
            [AsParameters] BusinessCommandServices c) =>
            c.Execute("stock-movements", "stock.physical_count", body, async ct =>
            {
                RequireStockManager(c.Scope);
                await c.Current<Material>(id, ct, false);
                var movements = await c.Store.All<StockMovement>(ct);
                var reservations = await c.Store.All<MaterialReservation>(ct);
                var correction = InventoryCorrections.ReconcileCount(c.Scope.TenantId, id,
                    body.Location, body.ExpectedOnHand, body.CountedOnHand, body.UnitCost,
                    body.EvidenceReference, body.Reason, movements, reservations, DateTimeOffset.UtcNow);
                return await c.SaveNew(correction, "stock.physical_count", ct);
            })).Produces<StockMovement>();

        api.MapPost("/materials/{id:guid}/transfer", (Guid id, StockTransferRequest body,
            [AsParameters] BusinessCommandServices c) =>
            c.Execute("stock-movements", "stock.transfer", body, async ct =>
            {
                RequireStockManager(c.Scope);
                await c.Current<Material>(id, ct, false);
                var movements = await c.Store.All<StockMovement>(ct);
                var reservations = await c.Store.All<MaterialReservation>(ct);
                var (outbound, inbound) = InventoryCorrections.Transfer(c.Scope.TenantId, id,
                    body.FromLocation, body.ToLocation, body.Quantity, body.UnitCost,
                    body.ExpectedFromOnHand, body.Reason, Guid.NewGuid(),
                    movements, reservations, DateTimeOffset.UtcNow);
                await c.SaveNew(outbound, "stock.transfer_out", ct);
                await c.SaveNew(inbound, "stock.transfer_in", ct);
                return new { outbound, inbound };
            }));

        api.MapPost("/material-reservations/{id:guid}/return", (Guid id, ProjectStockReturnRequest body,
            [AsParameters] BusinessCommandServices c) =>
            c.Execute("material-reservations", "stock.project_return", body, async ct =>
            {
                RequireStockManager(c.Scope);
                var reservation = await c.Current<MaterialReservation>(id, ct);
                var original = await c.Current<StockMovement>(body.ConsumptionMovementId, ct, false);
                var movements = await c.Store.All<StockMovement>(ct);
                var correction = InventoryCorrections.ReturnConsumption(reservation, original,
                    body.Quantity, body.Reason, movements, DateTimeOffset.UtcNow);
                await c.SaveNew(correction, "stock.project_return", ct);
                await c.Store.Save(reservation, "stock.return_reservation", false, ct);
                return new { reservation, correction };
            }));

        api.MapPost("/purchase-orders/{id:guid}/supplier-return", (Guid id, SupplierStockReturnRequest body,
            [AsParameters] BusinessCommandServices c) =>
            c.Execute("purchase-orders", "stock.supplier_return", body, async ct =>
            {
                RequireStockManager(c.Scope);
                var order = await c.Current<PurchaseOrder>(id, ct);
                var movements = await c.Store.All<StockMovement>(ct);
                var reservations = await c.Store.All<MaterialReservation>(ct);
                var correction = InventoryCorrections.ReturnToSupplier(order, body.LineId,
                    body.Quantity, body.Location, body.SupplierCreditReference, body.Reason,
                    movements, reservations, DateTimeOffset.UtcNow);
                await c.SaveNew(correction, "stock.supplier_return", ct);
                await c.Store.Save(order, "stock.supplier_return_order", false, ct);
                return new { order, correction };
            }));
    }

    private static void RequireStockManager(TenantScope scope) =>
        Guard.Require(scope.Role is BusinessRole.ManagingDirector or BusinessRole.Office or BusinessRole.SiteManager,
            "forbidden", "Only authorised stock managers may change posted inventory.");
}
