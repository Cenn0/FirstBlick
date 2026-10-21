using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace FirstBlick.Api;

public sealed record CustomerContactsView(Guid CustomerId, Guid TenantId, string CustomerName, long Revision, IReadOnlyList<CustomerContact> Contacts);

public static class CustomerContactEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/customers/{id:guid}/contacts", async (Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await c.Commands.Authorize("customers", false, ct);
            var customer = await c.Current<Customer>(id, ct, false); customer.Validate();
            c.Http.Response.Headers.ETag = $"\"{customer.Revision}\""; return Results.Ok(View(customer));
        }).WithTags("customers");

        api.MapPost("/customers/{id:guid}/contacts", (Guid id, CustomerContactDetails body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("customers", "customer.contact_add", body, async ct =>
            {
                var customer = await c.Current<Customer>(id, ct);
                CustomerContactWorkflow.Add(customer, body, c.Scope.UserId, DateTimeOffset.UtcNow);
                await c.Store.Save(customer, "customer.contact_add", false, ct); return View(customer);
            })).WithTags("customers");

        api.MapPost("/customers/{id:guid}/contacts/{contactId:guid}/update", (Guid id, Guid contactId, CustomerContactDetails body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("customers", "customer.contact_update", body, async ct =>
            {
                var customer = await c.Current<Customer>(id, ct);
                CustomerContactWorkflow.Update(customer, contactId, body, c.Scope.UserId, DateTimeOffset.UtcNow);
                await c.Store.Save(customer, "customer.contact_update", false, ct); return View(customer);
            })).WithTags("customers");

        api.MapPost("/customers/{id:guid}/contacts/{contactId:guid}/archive", (Guid id, Guid contactId, [AsParameters] BusinessCommandServices c) =>
            c.Execute("customers", "customer.contact_archive", new { id, contactId }, async ct =>
            {
                var customer = await c.Current<Customer>(id, ct);
                CustomerContactWorkflow.Archive(customer, contactId, c.Scope.UserId, DateTimeOffset.UtcNow);
                await c.Store.Save(customer, "customer.contact_archive", false, ct); return View(customer);
            })).WithTags("customers");

        api.MapPost("/customers/{id:guid}/contacts/{contactId:guid}/restore", (Guid id, Guid contactId, [AsParameters] BusinessCommandServices c) =>
            c.Execute("customers", "customer.contact_restore", new { id, contactId }, async ct =>
            {
                var customer = await c.Current<Customer>(id, ct);
                CustomerContactWorkflow.Restore(customer, contactId, c.Scope.UserId, DateTimeOffset.UtcNow);
                await c.Store.Save(customer, "customer.contact_restore", false, ct); return View(customer);
            })).WithTags("customers");
    }

    private static CustomerContactsView View(Customer customer) => new(customer.Id, customer.TenantId, customer.Name, customer.Revision, customer.Contacts ?? []);
}
