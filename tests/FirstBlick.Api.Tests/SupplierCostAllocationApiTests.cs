using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class SupplierCostAllocationApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Real_postgresql_allocation_is_capped_across_projects_and_reversal_is_separately_audited()
    {
        var g = await Setup(); var client = await fixture.Login(g.User);
        var path = $"/api/v1/supplier-invoices/{g.Invoice.Id}/cost-allocations";
        var first = await Ok<SupplierCostAllocation>(Send(client, HttpMethod.Post, path,
            new PostSupplierProjectCostRequest(g.Project1.Id, 70m, "Reviewed cost portion A", g.Invoice.ApprovedDigest)));
        var second = await Ok<SupplierCostAllocation>(Send(client, HttpMethod.Post, path,
            new PostSupplierProjectCostRequest(g.Project2.Id, 30m, "Reviewed cost portion B", g.Invoice.ApprovedDigest)));
        await ExpectError(Send(client, HttpMethod.Post, path,
            new PostSupplierProjectCostRequest(g.Project2.Id, .01m, "Over budget", g.Invoice.ApprovedDigest)),
            HttpStatusCode.BadRequest, "cost_allocation_exceeded");
        using (var list = await client.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var payload = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            Assert.Equal("100", payload.RootElement.GetProperty("allocatedNet").GetString());
            Assert.Equal("0", payload.RootElement.GetProperty("availableNet").GetString());
            Assert.Equal(2, payload.RootElement.GetProperty("active").GetArrayLength());
        }
        var undo = await Ok<SupplierCostAllocation>(Send(client, HttpMethod.Post,
            $"/api/v1/supplier-cost-allocations/{first.Id}/reverse",
            new ReverseSupplierProjectCostRequest("Wrong project, reverse and rebook"), first.Revision));
        Assert.Equal(first.Id, undo.ReversesAllocationId);
        await ExpectError(Send(client, HttpMethod.Post, $"/api/v1/supplier-cost-allocations/{first.Id}/reverse",
            new ReverseSupplierProjectCostRequest("Cannot reverse twice"), first.Revision),
            HttpStatusCode.BadRequest, "allocation_already_reversed");
        await Ok<SupplierCostAllocation>(Send(client, HttpMethod.Post, path,
            new PostSupplierProjectCostRequest(g.Project2.Id, 70m, "New destination", g.Invoice.ApprovedDigest)));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(4, await db.Entities.IgnoreQueryFilters().CountAsync(x =>
                x.TenantId == g.User.TenantId && x.Kind == "supplier-cost-allocations"));
            Assert.Equal(4, await db.Audit.IgnoreQueryFilters().CountAsync(x =>
                x.TenantId == g.User.TenantId && x.Kind == "supplier-cost-allocations"));
        });
        var project = await client.GetAsync($"/api/v1/projects/{g.Project2.Id}/supplier-cost-allocations");
        Assert.Equal(HttpStatusCode.OK, project.StatusCode);
    }

    [PostgresApiFact]
    public async Task Competing_project_postings_recheck_the_remaining_document_balance_under_the_tenant_lock()
    {
        var g = await Setup(); var client = await fixture.Login(g.User);
        var path = $"/api/v1/supplier-invoices/{g.Invoice.Id}/cost-allocations";
        var requests = await Task.WhenAll(
            Send(client, HttpMethod.Post, path,
                new PostSupplierProjectCostRequest(g.Project1.Id, 80m, "Concurrent A", g.Invoice.ApprovedDigest)),
            Send(client, HttpMethod.Post, path,
                new PostSupplierProjectCostRequest(g.Project2.Id, 80m, "Concurrent B", g.Invoice.ApprovedDigest)));
        Assert.Single(requests, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(requests, x => x.StatusCode == HttpStatusCode.BadRequest);
        var posted = AppJson.Read<SupplierCostAllocation>(await requests.Single(x => x.IsSuccessStatusCode).Content.ReadAsStringAsync());
        var reversed = requests.Single(x => !x.IsSuccessStatusCode);
        using var error = JsonDocument.Parse(await reversed.Content.ReadAsStringAsync());
        Assert.Equal("cost_allocation_exceeded", error.RootElement.GetProperty("code").GetString());
        Assert.Equal(80m, posted.NetAmount);
    }

    [PostgresApiFact]
    public async Task Credit_allocation_cannot_exceed_the_project_original_and_untrusted_roles_or_tenants_are_rejected()
    {
        var g = await Setup(); var owner = await fixture.Login(g.User);
        var path = $"/api/v1/supplier-invoices/{g.Invoice.Id}/cost-allocations";
        await Ok<SupplierCostAllocation>(Send(owner, HttpMethod.Post, path,
            new PostSupplierProjectCostRequest(g.Project1.Id, 40m, "Original portion", g.Invoice.ApprovedDigest)));
        var credit = new SupplierInvoice
        {
            TenantId = g.User.TenantId, SupplierId = g.Invoice.SupplierId, OriginalInvoiceId = g.Invoice.Id,
            Kind = SupplierInvoiceKind.CreditNote, Number = "SYN-CREDIT", Title = "Credit of supplier costs",
            InvoiceDate = new(2026, 10, 20), DueDate = new(2026, 11, 1), Net = 60, Tax = 11.40m, Gross = 71.40m
        };
        var supplier = await fixture.Read<Supplier>(g.User.TenantId, g.Invoice.SupplierId);
        PayablesWorkflow.ApproveCredit(credit, g.Invoice, supplier, [], Guid.NewGuid(), g.User.UserId, DateTimeOffset.UtcNow, false);
        credit = await fixture.Save(credit, g.User.UserId);
        var creditPath = $"/api/v1/supplier-invoices/{credit.Id}/cost-allocations";
        await ExpectError(Send(owner, HttpMethod.Post, creditPath,
            new PostSupplierProjectCostRequest(g.Project1.Id, 41m, "Credit greater than project", credit.ApprovedDigest)),
            HttpStatusCode.BadRequest, "credit_allocation_exceeded");
        var valid = await Ok<SupplierCostAllocation>(Send(owner, HttpMethod.Post, creditPath,
            new PostSupplierProjectCostRequest(g.Project1.Id, 40m, "Credit against same project", credit.ApprovedDigest)));
        Assert.Equal(40m, valid.NetAmount);
        var unauthorized = await fixture.Login(await fixture.CreateUser(g.User.TenantId, BusinessRole.Accounting));
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(unauthorized, HttpMethod.Post, path,
            new PostSupplierProjectCostRequest(g.Project2.Id, 1m, "Unauthorized", g.Invoice.ApprovedDigest))).StatusCode);
        var foreign = await fixture.CreateTenant(); var foreignUser = await fixture.CreateUser(foreign.Id);
        var other = await fixture.Login(foreignUser);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed,
            (await Send(owner, HttpMethod.Put, $"/api/v1/supplier-cost-allocations/{valid.Id}", valid, valid.Revision)).StatusCode);
    }

    private async Task<(ApiTestUser User, Project Project1, Project Project2, SupplierInvoice Invoice)> Setup()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Synthetic allocation customer" }, user.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id, Name = "Synthetic building", Address = "Synthetic" }, user.UserId);
        var first = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id, BuildingId = building.Id, Name = "Cost project A" }, user.UserId);
        var second = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id, BuildingId = building.Id, Name = "Cost project B" }, user.UserId);
        var supplier = await fixture.Save(new Supplier { TenantId = tenant.Id, Name = "Synthetic supplier" }, user.UserId);
        var invoice = new SupplierInvoice { TenantId = tenant.Id, SupplierId = supplier.Id, Number = "SYN-COST-1",
            Title = "Reviewed supplier service", InvoiceDate = new(2026, 10, 20), DueDate = new(2026, 11, 1),
            Net = 100m, Tax = 19m, Gross = 119m };
        PayablesWorkflow.Approve(invoice, supplier, Guid.NewGuid(), user.UserId, DateTimeOffset.UtcNow, false);
        return (user, first, second, await fixture.Save(invoice, user.UserId));
    }

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> action)
    {
        using var response = await action;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        return AppJson.Read<T>(body);
    }

    private static async Task ExpectError(Task<HttpResponseMessage> action, HttpStatusCode status, string code)
    {
        using var response = await action;
        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }
}
