using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class CostApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task The_report_uses_real_project_sources_and_leaves_missing_costs_unknown_without_posting_anything()
    {
        var g = await Setup(); var client = await fixture.Login(g.User); var path = $"/api/v1/orders/{g.Order.Id}/cost-analysis";
        var unknown = await Report(client, path, Empty()); Assert.Null(unknown.ActualCost); Assert.Equal(80m, unknown.KnownActualCost); Assert.Equal(2, unknown.Sources.Count);
        Assert.Contains(unknown.Missing, x => x.Contains("hourly_cost_missing"));
        var before = await Counts(g.User.TenantId);
        var input = Empty() with { TimeCosts = [new(g.Time.Id, 40, "Synthetic cost record, EUR 40 per worked hour")], RemainingForecastCost = 100, ForecastEvidence = "Outstanding work reviewed", RecordedCostsCoverage = "Project source coverage reconciled" };
        var result = await Report(client, path, input); Assert.Equal(380m, result.ActualCost); Assert.Equal(480m, result.ForecastCost); Assert.Equal(520m, result.ForecastMargin);
        Assert.Contains(result.Sources, x => x.Id == g.Time.Id && x.Revision == g.Time.Revision && x.Digest.Length == 64 && x.NetCost == 300m);
        Assert.Equal(before, await Counts(g.User.TenantId));
        var second = await Report(client, path, input); Assert.Equal(result.Digest, second.Digest);
    }

    [PostgresApiFact]
    public async Task Source_level_costs_are_limited_to_director_and_office_and_foreign_sources_are_unavailable()
    {
        var g = await Setup();
        foreach (var role in Enum.GetValues<BusinessRole>())
        {
            var client = await fixture.Login(await fixture.CreateUser(g.User.TenantId, role));
            var response = await Send(client, HttpMethod.Post, $"/api/v1/orders/{g.Order.Id}/cost-analysis", Empty());
            Assert.Equal(role is BusinessRole.ManagingDirector or BusinessRole.Office ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        }
        var foreign = await Setup(); var owner = await fixture.Login(g.User);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(owner, HttpMethod.Post, $"/api/v1/orders/{foreign.Order.Id}/cost-analysis", Empty())).StatusCode);
        var invalid = await Send(owner, HttpMethod.Post, $"/api/v1/orders/{g.Order.Id}/cost-analysis", Empty() with { TimeCosts = [new(foreign.Time.Id, 40, "Unavailable foreign source")] });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("unknown_cost_source", JsonDocument.Parse(await invalid.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    [PostgresApiFact]
    public async Task Sensitive_source_reports_recheck_role_withdrawal_after_waiting_for_the_tenant_lock()
    {
        var g = await Setup(); var client = await fixture.Login(g.User);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@tenant,0))", connection, transaction))
        { acquire.Parameters.AddWithValue("tenant", g.User.TenantId.ToString()); await acquire.ExecuteNonQueryAsync(); }
        var response = Send(client, HttpMethod.Post, $"/api/v1/orders/{g.Order.Id}/cost-analysis", Empty()); var waiting = false;
        for (var attempt = 0; attempt < 150 && !waiting; attempt++)
        {
            await using var inspect = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND NOT granted AND database=(SELECT oid FROM pg_database WHERE datname=current_database()))", connection, transaction);
            waiting = (bool)(await inspect.ExecuteScalarAsync())!; if (!waiting) await Task.Delay(20);
        }
        Assert.True(waiting, "The request must be observed waiting for the business lock before the role is withdrawn.");
        await using (var revoke = new NpgsqlCommand("UPDATE \"Memberships\" SET \"Role\"='Accounting',\"Revision\"=\"Revision\"+1 WHERE \"TenantId\"=@tenant AND \"UserId\"=@user", connection, transaction))
        { revoke.Parameters.AddWithValue("tenant", g.User.TenantId); revoke.Parameters.AddWithValue("user", g.User.UserId); Assert.Equal(1, await revoke.ExecuteNonQueryAsync()); }
        await transaction.CommitAsync(); var result = await response;
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode); Assert.DoesNotContain("Synthetic cost customer", await result.Content.ReadAsStringAsync());
    }

    [PostgresApiFact]
    public async Task An_accepted_change_cannot_be_archived_to_remove_it_from_contract_and_cost_totals()
    {
        var g = await Setup(); var client = await fixture.Login(g.User);
        var change = new ChangeOrder { TenantId = g.User.TenantId, OrderId = g.Order.Id, Title = "Accepted source retained", Reason = "Reviewed addition",
            Lines = [new() { Description = "Additional work", UnitMaterialCost = 100, VatPercent = 19 }] };
        CommercialWorkflow.AcceptChange(change, DateTimeOffset.UtcNow); await fixture.Save(change, g.User.UserId);
        var response = await Send(client, HttpMethod.Delete, $"/api/v1/change-orders/{change.Id}", new { }, change.Revision);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("change_immutable", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        var result = await Report(client, $"/api/v1/orders/{g.Order.Id}/cost-analysis", Empty());
        Assert.Equal(1100m, result.ContractNet); Assert.Contains(result.ContractSources, x => x.Kind == "change-orders" && x.Id == change.Id);
    }

    [PostgresApiFact]
    public async Task Issued_purchase_and_completed_maintenance_evidence_remain_available_in_history()
    {
        var g = await Setup(); var client = await fixture.Login(g.User);
        var project = await fixture.Read<Project>(g.User.TenantId, g.Order.ProjectId);
        var supplier = await fixture.Save(new Supplier { TenantId = g.User.TenantId, Name = "Historical supplier" }, g.User.UserId);
        var material = await fixture.Save(new Material { TenantId = g.User.TenantId, Name = "Historical material" }, g.User.UserId);
        var purchase = new PurchaseOrder { TenantId = g.User.TenantId, ProjectId = project.Id, SupplierId = supplier.Id, Title = "Issued purchase evidence",
            Lines = [new() { MaterialId = material.Id, Description = "Ordered material", Quantity = 1, UnitNet = 10, VatPercent = 19 }] };
        InventoryRules.Order(purchase, "SYN-PURCHASE-1"); await fixture.Save(purchase, g.User.UserId);
        var contract = new MaintenanceContract { TenantId = g.User.TenantId, CustomerId = g.Order.CustomerId, BuildingId = project.BuildingId,
            Title = "Completed maintenance evidence", Scope = "Synthetic inspection", StartDate = new(2026, 1, 1), NextDueDate = new(2026, 1, 1) };
        OperationsRules.CompleteMaintenance(contract, contract.NextDueDate); await fixture.Save(contract, g.User.UserId);
        foreach (var (kind, id, revision, code) in new[] { ("purchase-orders", purchase.Id, purchase.Revision, "purchase_immutable"), ("maintenance-contracts", contract.Id, contract.Revision, "maintenance_immutable") })
        {
            var response = await Send(client, HttpMethod.Delete, $"/api/v1/{kind}/{id}", new { }, revision);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(code, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        }
        Assert.False((await fixture.Read<PurchaseOrder>(g.User.TenantId, purchase.Id)).Archived);
        Assert.False((await fixture.Read<MaintenanceContract>(g.User.TenantId, contract.Id)).Archived);
    }

    private Task<(int Entities, int Audit, int Jobs, int Receipts)> Counts(Guid tenantId) => fixture.InScope(async services =>
    {
        var db = services.GetRequiredService<FirstBlickDbContext>();
        return (await db.Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId), await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId),
            await db.Jobs.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId), await db.Receipts.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId));
    });
    private async Task<(ApiTestUser User, Order Order, TimeEntry Time)> Setup()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Synthetic cost customer" }, user.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id, Name = "Cost building", Address = "Synthetic address" }, user.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, BuildingId = building.Id, CustomerId = customer.Id, Name = "Cost project" }, user.UserId);
        var quote = new Quote { TenantId = tenant.Id, CustomerId = customer.Id, ProjectId = project.Id, Title = "Cost source order", Lines = [new() { Description = "Contract", UnitMaterialCost = 1000, VatPercent = 19 }] };
        CommercialWorkflow.IssueQuote(quote, "SYN-1"); CommercialWorkflow.FreezePresentation(quote, tenant.Name, customer, DateTimeOffset.UtcNow);
        CommercialWorkflow.AcceptQuote(quote, quote.IssuedDigest, DateTimeOffset.UtcNow, new DateOnly(2026, 10, 20)); await fixture.Save(quote, user.UserId);
        var order = await fixture.Save(CommercialWorkflow.CreateOrder(quote, "SYN-ORDER-1"), user.UserId);
        var employee = await fixture.Save(new Employee { TenantId = tenant.Id, Name = "Cost source employee", UserId = user.UserId }, user.UserId);
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        var time = await fixture.Save(new TimeEntry { TenantId = tenant.Id, ProjectId = project.Id, EmployeeId = employee.Id, Start = start, End = start.AddHours(8), Breaks = [new(start.AddHours(4), start.AddHours(4.5))], Activity = "Recorded synthetic work", Approved = true }, user.UserId);
        var material = await fixture.Save(new Material { TenantId = tenant.Id, Name = "Cost source material" }, user.UserId);
        var movement = new StockMovement { TenantId = tenant.Id, ProjectId = project.Id, MaterialId = material.Id, Quantity = -10, UnitCost = 8, Kind = StockMovementKind.Consumption, OccurredAt = start, Reference = "Synthetic source" };
        movement.PostedDigest = InventoryRules.MovementDigest(movement); await fixture.Save(movement, user.UserId);
        return (user, order, time);
    }
    private static CostAnalysisInput Empty() => new([], [], [], null, "", "");
    private static async Task<CostAnalysisResult> Report(HttpClient client, string path, CostAnalysisInput input)
    {
        var response = await Send(client, HttpMethod.Post, path, input); var body = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("report").Deserialize<CostAnalysisResult>(AppJson.Options)!;
    }
}
