using System.Net;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class InventoryCorrectionApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private async Task<(BusinessTenant Tenant, ApiTestUser User, HttpClient Client, Material Material,
        PurchaseOrder Order, StockMovement Receipt)> Setup()
    {
        var tenant = await fixture.CreateTenant();
        var user = await fixture.CreateUser(tenant.Id);
        var client = await fixture.Login(user);
        var supplier = await fixture.Save(new Supplier { TenantId = tenant.Id, Name = "Synthetic tiles merchant" }, user.UserId);
        var material = await fixture.Save(new Material { TenantId = tenant.Id, Name = "Roof tiles", Unit = "Stk" }, user.UserId);
        var order = new PurchaseOrder { TenantId = tenant.Id, SupplierId = supplier.Id, Title = "Roof tiles",
            Lines = [new() { MaterialId = material.Id, Description = "Roof tiles", Quantity = 10m, UnitNet = 2m }] };
        InventoryRules.Order(order, "BE-TEST-01");
        var receipt = InventoryRules.Receive(order, order.Lines[0].Id, 10m, [], "Main", DateTimeOffset.UtcNow);
        await fixture.Save(order, user.UserId);
        await fixture.Save(receipt, user.UserId);
        return (tenant, user, client, material, order, receipt);
    }

    [PostgresApiFact]
    public async Task Physical_count_transfer_and_supplier_return_are_idempotent_atomic_and_scope_checked()
    {
        var g = await Setup();
        var countUrl = $"/api/v1/materials/{g.Material.Id}/stock-count";
        var count = new { location = "Main", expectedOnHand = 10m, countedOnHand = 8m, unitCost = 2m,
            evidenceReference = "COUNT-1", reason = "Independent physical count of damaged tiles" };
        var key = Guid.NewGuid().ToString("N");
        var movement = await Ok<StockMovement>(Send(g.Client, HttpMethod.Post, countUrl, count, key: key));
        Assert.Equal(-2m, movement.Quantity);
        Assert.Equal(StockMovementKind.Adjustment, movement.Kind);
        movement.Validate();
        var replay = await Send(g.Client, HttpMethod.Post, countUrl, count, key: key);
        Assert.True(replay.IsSuccessStatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(movement.Id, (await Ok<StockMovement>(Task.FromResult(replay))).Id);
        await Error(Send(g.Client, HttpMethod.Post, countUrl, count),
            HttpStatusCode.BadRequest, "stock_count_stale");
        await Error(Send(g.Client, HttpMethod.Put, $"/api/v1/stock-movements/{movement.Id}", movement, 1),
            HttpStatusCode.BadRequest, "command_required");

        var transferUrl = $"/api/v1/materials/{g.Material.Id}/transfer";
        var transfer = await Ok<System.Text.Json.JsonElement>(Send(g.Client, HttpMethod.Post, transferUrl,
            new { fromLocation = "Main", toLocation = "Site", quantity = 3m, unitCost = 2m,
                expectedFromOnHand = 8m, reason = "Verified material delivery to second warehouse" }));
        var outbound = AppJson.Read<StockMovement>(transfer.GetProperty("outbound").GetRawText());
        var inbound = AppJson.Read<StockMovement>(transfer.GetProperty("inbound").GetRawText());
        Assert.Equal(-outbound.Quantity, inbound.Quantity);
        Assert.Equal(outbound.Reference, inbound.Reference);
        Assert.Equal(StockMovementKind.TransferOut, outbound.Kind);
        Assert.Equal(StockMovementKind.TransferIn, inbound.Kind);
        Assert.Equal(new StockBalance(5m, 0m, 5m),
            await Ok<StockBalance>(g.Client.GetAsync($"/api/v1/materials/{g.Material.Id}/stock?location=Main")));
        Assert.Equal(new StockBalance(3m, 0m, 3m),
            await Ok<StockBalance>(g.Client.GetAsync($"/api/v1/materials/{g.Material.Id}/stock?location=Site")));
        await Error(Send(g.Client, HttpMethod.Post, transferUrl,
            new { fromLocation = "Main", toLocation = "Site", quantity = 1m, unitCost = 2m,
                expectedFromOnHand = 8m, reason = "Stale warehouse valuation" }),
            HttpStatusCode.BadRequest, "stock_count_stale");

        var returned = await Ok<System.Text.Json.JsonElement>(Send(g.Client, HttpMethod.Post,
            $"/api/v1/purchase-orders/{g.Order.Id}/supplier-return",
            new { lineId = g.Order.Lines[0].Id, quantity = 2m, location = "Main",
                supplierCreditReference = "RMA-2026-01", reason = "Supplier accepted broken tiles" },
            g.Order.Revision));
        var afterReturn = AppJson.Read<PurchaseOrder>(returned.GetProperty("order").GetRawText());
        var supplierMovement = AppJson.Read<StockMovement>(returned.GetProperty("correction").GetRawText());
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, afterReturn.Status);
        Assert.Equal(StockMovementKind.SupplierReturn, supplierMovement.Kind);
        Assert.Equal(new StockBalance(3m, 0m, 3m),
            await Ok<StockBalance>(g.Client.GetAsync($"/api/v1/materials/{g.Material.Id}/stock?location=Main")));
        await Error(Send(g.Client, HttpMethod.Post,
            $"/api/v1/purchase-orders/{g.Order.Id}/supplier-return",
            new { lineId = g.Order.Lines[0].Id, quantity = 1m, location = "Main",
                supplierCreditReference = "RMA-2026-02", reason = "Stale order revision" }, g.Order.Revision),
            HttpStatusCode.Conflict, "revision_conflict");
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(5, await db.Entities.IgnoreQueryFilters().CountAsync(x =>
                x.TenantId == g.Tenant.Id && x.Kind == "stock-movements"));
            Assert.True(await db.Audit.IgnoreQueryFilters().AnyAsync(x =>
                x.TenantId == g.Tenant.Id && x.Action == "stock.supplier_return"));
        });

        var accountant = await fixture.Login(await fixture.CreateUser(g.Tenant.Id, BusinessRole.Accounting));
        await Error(Send(accountant, HttpMethod.Post, countUrl,
            new { location = "Main", expectedOnHand = 3m, countedOnHand = 2m,
                unitCost = 2m, evidenceReference = "COUNT-UNAUTHORIZED", reason = "Not a stock manager" }),
            HttpStatusCode.Forbidden, "forbidden");
        var other = await fixture.CreateTenant();
        var otherClient = await fixture.Login(await fixture.CreateUser(other.Id));
        await Error(Send(otherClient, HttpMethod.Post, transferUrl,
            new { fromLocation = "Main", toLocation = "Site", quantity = 1m,
                unitCost = 2m, expectedFromOnHand = 3m, reason = "Cross tenant" }),
            HttpStatusCode.NotFound, "not_found");
    }

    [PostgresApiFact]
    public async Task Project_return_preserves_original_cost_and_cannot_exceed_one_consumption()
    {
        var g = await Setup();
        var customer = await fixture.Save(new Customer { TenantId = g.Tenant.Id, Name = "Synthetic owner" }, g.User.UserId);
        var building = await fixture.Save(new Building { TenantId = g.Tenant.Id, CustomerId = customer.Id,
            Name = "Garage", Address = "Synthetic Street 1" }, g.User.UserId);
        var project = await fixture.Save(new Project { TenantId = g.Tenant.Id, CustomerId = customer.Id,
            BuildingId = building.Id, Name = "Roof repair" }, g.User.UserId);
        var reservation = await Ok<MaterialReservation>(Send(g.Client, HttpMethod.Post,
            $"/api/v1/materials/{g.Material.Id}/reserve",
            new { projectId = project.Id, quantity = 4m, location = "Main" }, g.Material.Revision));
        var consumption = await Ok<System.Text.Json.JsonElement>(Send(g.Client, HttpMethod.Post,
            $"/api/v1/material-reservations/{reservation.Id}/consume",
            new { quantity = 3m, unitCost = 2m }, reservation.Revision));
        var used = AppJson.Read<StockMovement>(consumption.GetProperty("movement").GetRawText());
        var updated = AppJson.Read<MaterialReservation>(consumption.GetProperty("reservation").GetRawText());
        Assert.Equal(3m, updated.ConsumedQuantity);
        var returnRoute = $"/api/v1/material-reservations/{reservation.Id}/return";
        var result = await Ok<System.Text.Json.JsonElement>(Send(g.Client, HttpMethod.Post,
            returnRoute, new { consumptionMovementId = used.Id, quantity = 2m,
                reason = "Unused material returned from project" }, updated.Revision));
        var returned = AppJson.Read<StockMovement>(result.GetProperty("correction").GetRawText());
        var afterReturn = AppJson.Read<MaterialReservation>(result.GetProperty("reservation").GetRawText());
        Assert.Equal(2m, returned.Quantity);
        Assert.Equal(used.UnitCost, returned.UnitCost);
        Assert.Equal(used.Id.ToString("N"), returned.Reference);
        Assert.Equal(1m, afterReturn.ConsumedQuantity);
        Assert.Equal(2m, afterReturn.ReleasedQuantity);
        Assert.Equal(new StockBalance(9m, 1m, 8m),
            await Ok<StockBalance>(g.Client.GetAsync($"/api/v1/materials/{g.Material.Id}/stock?location=Main")));
        await Error(Send(g.Client, HttpMethod.Post, returnRoute,
            new { consumptionMovementId = used.Id, quantity = 2m,
                reason = "Attempted duplicate return" }, afterReturn.Revision),
            HttpStatusCode.BadRequest, "over_return");
        Assert.Equal(3, await fixture.InScope(async services =>
            await services.GetRequiredService<FirstBlickDbContext>().Entities.IgnoreQueryFilters()
                .CountAsync(x => x.TenantId == g.Tenant.Id && x.Kind == "stock-movements")));
    }

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        using var result = await pending;
        Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync());
        return AppJson.Read<T>(await result.Content.ReadAsStringAsync());
    }

    private static async Task Error(Task<HttpResponseMessage> pending, HttpStatusCode status, string code)
    {
        using var result = await pending;
        Assert.Equal(status, result.StatusCode);
        Assert.Contains(code, await result.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
