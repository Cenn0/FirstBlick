using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class InventoryTransferChainApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Three_warehouse_hops_return_against_the_original_purchase_and_preserve_database_links()
    {
        var graph = await Seed();
        var first = await Transfer(graph, graph.Receipt, "B", 8m, 10m);
        var second = await Transfer(graph, first, "C", 6m, 8m);
        var third = await Transfer(graph, second, "D", 4m, 6m);
        var trace = await Ok<StockTransferTrace>(graph.Client.GetAsync($"/api/v1/stock-movements/{third.Id}/transfer-chain"));
        Assert.Equal(graph.Receipt.Id, trace.OriginalReceiptId);
        Assert.Equal(3, trace.Transfers.Count);
        Assert.Equal(4m, trace.EvidenceRemaining);
        Assert.Equal(new[] { "B", "C", "D" }, trace.Transfers.Select(x => x.ToLocation));
        var path = $"/api/v1/stock-movements/{graph.Receipt.Id}/supplier-return";
        var body = new { quantity = 3m, reason = "Supplier accepted a return from the third warehouse", returnTransferId = third.Id };
        var key = Guid.NewGuid().ToString("N");
        var response = await Ok<JsonElement>(Send(graph.Client, HttpMethod.Post, path, body, graph.Receipt.Revision, key));
        var returned = AppJson.Read<StockMovement>(response.GetProperty("movement").GetRawText());
        Assert.Equal("D", returned.Location);
        Assert.Equal(5m, returned.UnitCost);
        Assert.Equal(graph.Receipt.Id.ToString("D"), returned.Reference);
        Assert.Equal(third.Id, returned.ReturnTransferId);
        Assert.Equal("PartiallyReceived", response.GetProperty("order").GetProperty("status").GetString());
        var replay = await Send(graph.Client, HttpMethod.Post, path, body, graph.Receipt.Revision, key);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(returned.Id, (await Ok<JsonElement>(Task.FromResult(replay))).GetProperty("movement").GetProperty("id").GetGuid());
        Assert.Equal(new StockBalance(1m, 0m, 1m), await Ok<StockBalance>(graph.Client.GetAsync($"/api/v1/materials/{graph.Material.Id}/stock?location=D")));
        trace = await Ok<StockTransferTrace>(graph.Client.GetAsync($"/api/v1/stock-movements/{third.Id}/transfer-chain"));
        Assert.Equal(1m, trace.EvidenceRemaining);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(7, await db.Links.IgnoreQueryFilters().CountAsync(x => x.TenantId == graph.User.TenantId
                && x.SourceKind == "stock-movements" && x.TargetKind == "stock-movements"));
            Assert.Equal(3, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == graph.User.TenantId && x.Action == "stock.transfer_out"));
            Assert.Equal(3, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == graph.User.TenantId && x.Action == "stock.transfer_in"));
            Assert.Equal(1, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == graph.User.TenantId && x.Action == "stock.supplier_return"));
        });
    }

    [PostgresApiFact]
    public async Task Competing_branch_allocations_cannot_reuse_source_quantity_even_when_other_stock_is_available()
    {
        var graph = await Seed();
        var first = await Transfer(graph, graph.Receipt, "B", 6m, 10m);
        var unrelated = new StockMovement { TenantId = graph.User.TenantId, MaterialId = graph.Material.Id,
            Kind = StockMovementKind.Receipt, Location = "B", Quantity = 10m, UnitCost = 5m, OccurredAt = DateTimeOffset.UtcNow };
        unrelated.PostedDigest = InventoryRules.MovementDigest(unrelated);
        await fixture.Save(unrelated, graph.User.UserId);
        var path = $"/api/v1/materials/{graph.Material.Id}/transfer";
        var bodies = new[] { TransferBody(first, "C", 4m, 16m), TransferBody(first, "D", 4m, 16m) };
        var keys = new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") };
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(index =>
            Send(graph.Client, HttpMethod.Post, path, bodies[index], key: keys[index])));
        var winner = Array.FindIndex(responses, x => x.IsSuccessStatusCode);
        Assert.InRange(winner, 0, 1);
        var posted = await Ok<JsonElement>(Task.FromResult(responses[winner]));
        var incoming = AppJson.Read<StockMovement>(posted.GetProperty("incoming").GetRawText());
        await Error(Task.FromResult(responses[1 - winner]), HttpStatusCode.BadRequest, "transfer_source_exceeded");
        var replay = await Send(graph.Client, HttpMethod.Post, path, bodies[winner], key: keys[winner]);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(incoming.Id, (await Ok<JsonElement>(Task.FromResult(replay))).GetProperty("incoming").GetProperty("id").GetGuid());
        var trace = await Ok<StockTransferTrace>(graph.Client.GetAsync($"/api/v1/stock-movements/{first.Id}/transfer-chain"));
        Assert.Equal(2m, trace.EvidenceRemaining);
        await Ok<JsonElement>(Send(graph.Client, HttpMethod.Post, $"/api/v1/stock-movements/{graph.Receipt.Id}/supplier-return",
            new { quantity = 2m, reason = "Return the remaining branch quantity", returnTransferId = first.Id }, graph.Receipt.Revision));
        await Error(Send(graph.Client, HttpMethod.Post, $"/api/v1/stock-movements/{graph.Receipt.Id}/supplier-return",
            new { quantity = 1m, reason = "Cannot take another lot's remaining warehouse stock", returnTransferId = first.Id }, graph.Receipt.Revision),
            HttpStatusCode.BadRequest, "supplier_return_transfer_exceeded");
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(2, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == graph.User.TenantId && x.Action == "stock.transfer_out"));
            Assert.Equal(2, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == graph.User.TenantId && x.Action == "stock.transfer_in"));
        });
    }

    [PostgresApiFact]
    public async Task Chain_reads_and_writes_are_tenant_scoped_and_replay_rechecks_current_warehouse_role()
    {
        var graph = await Seed();
        var other = await Seed();
        await Error(other.Client.GetAsync($"/api/v1/stock-movements/{graph.Receipt.Id}/transfer-chain"), HttpStatusCode.NotFound, "not_found");
        await Error(Send(other.Client, HttpMethod.Post, $"/api/v1/materials/{other.Material.Id}/transfer",
            TransferBody(graph.Receipt, "B", 1m, 10m)), HttpStatusCode.BadRequest, "invalid_transfer_source");
        await Error(Send(graph.Client, HttpMethod.Post, $"/api/v1/materials/{graph.Material.Id}/transfer",
            new { fromLocation = "A", toLocation = "B", quantity = 1m, unitCost = 6m, expectedFromOnHand = 10m,
                reason = "Wrong inherited price", transferSourceId = graph.Receipt.Id }), HttpStatusCode.BadRequest, "invalid_transfer_source");
        var path = $"/api/v1/materials/{graph.Material.Id}/transfer";
        var body = TransferBody(graph.Receipt, "B", 1m, 10m);
        var key = Guid.NewGuid().ToString("N");
        await Ok<JsonElement>(Send(graph.Client, HttpMethod.Post, path, body, key: key));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var member = await db.Memberships.IgnoreQueryFilters().SingleAsync(x => x.TenantId == graph.User.TenantId && x.UserId == graph.User.UserId);
            member.Role = BusinessRole.Office; await db.SaveChangesAsync();
        });
        await Error(Send(graph.Client, HttpMethod.Post, path, body, key: key), HttpStatusCode.Forbidden, "forbidden");
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(1, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == graph.User.TenantId && x.Action == "stock.transfer_out"));
            Assert.Equal(1, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == graph.User.TenantId && x.Action == "stock.transfer_in"));
        });
    }

    private sealed record Graph(ApiTestUser User, HttpClient Client, Material Material, PurchaseOrder Order, StockMovement Receipt);
    private async Task<Graph> Seed()
    {
        var tenant = await fixture.CreateTenant();
        var user = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(user);
        var supplier = await Ok<Supplier>(Send(client, HttpMethod.Post, "/api/v1/suppliers", new Supplier { Name = "Synthetic chain supplier" }));
        var material = await Ok<Material>(Send(client, HttpMethod.Post, "/api/v1/materials", new Material { Name = "Synthetic chain tiles" }));
        var order = await Ok<PurchaseOrder>(Send(client, HttpMethod.Post, "/api/v1/purchase-orders", new PurchaseOrder
        {
            SupplierId = supplier.Id, Title = "Synthetic linked warehouses",
            Lines = [new() { MaterialId = material.Id, Description = "Roof tiles", Quantity = 10m, UnitNet = 5m }]
        }));
        order = await Ok<PurchaseOrder>(Send(client, HttpMethod.Post, $"/api/v1/purchase-orders/{order.Id}/issue", new { }, order.Revision));
        var received = await Ok<JsonElement>(Send(client, HttpMethod.Post, $"/api/v1/purchase-orders/{order.Id}/receive",
            new { lineId = order.Lines[0].Id, quantity = 10m, location = "A" }, order.Revision));
        return new(user, client, material, AppJson.Read<PurchaseOrder>(received.GetProperty("order").GetRawText()),
            AppJson.Read<StockMovement>(received.GetProperty("receipt").GetRawText()));
    }
    private sealed record TransferRequest(string FromLocation, string ToLocation, decimal Quantity, decimal UnitCost,
        decimal ExpectedFromOnHand, string Reason, Guid TransferSourceId);
    private static TransferRequest TransferBody(StockMovement source, string target, decimal quantity, decimal expected) =>
        new(source.Location, target, quantity, source.UnitCost, expected, "Explicit recorded warehouse chain", source.Id);
    private static async Task<StockMovement> Transfer(Graph graph, StockMovement source, string target, decimal quantity, decimal expected)
    {
        var transferred = await Ok<JsonElement>(Send(graph.Client, HttpMethod.Post, $"/api/v1/materials/{graph.Material.Id}/transfer",
            TransferBody(source, target, quantity, expected)));
        return AppJson.Read<StockMovement>(transferred.GetProperty("incoming").GetRawText());
    }
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {json}"); return AppJson.Read<T>(json);
    }
    private static async Task Error(Task<HttpResponseMessage> pending, HttpStatusCode expected, string code)
    {
        using var response = await pending; var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected HTTP {(int)expected}, received {(int)response.StatusCode}: {json}");
        using var document = JsonDocument.Parse(json); Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
}
