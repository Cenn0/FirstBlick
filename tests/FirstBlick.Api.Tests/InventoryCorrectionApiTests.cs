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
