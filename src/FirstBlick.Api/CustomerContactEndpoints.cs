using System.Data;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FirstBlick.Api;

public sealed record CustomerContactsView(Guid CustomerId, Guid TenantId, string CustomerName, long Revision, IReadOnlyList<CustomerContact> Contacts);
public sealed record CustomerContactHistoryView(Guid CustomerId, Guid TenantId, Guid ContactId, string CustomerName,
    long CustomerRevision, CustomerContact Current, IReadOnlyList<CustomerContactChange> Items, long Total, int Page, int PageSize);

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

        api.MapGet("/customers/{id:guid}/contacts/{contactId:guid}/history", History).WithTags("customers");

        api.MapPost("/customers/{id:guid}/contacts", (Guid id, CustomerContactDetails body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("customers", "customer.contact_add", body, async ct =>
            {
                var customer = await c.Current<Customer>(id, ct);
                var before = customer.Contacts?.ToArray() ?? []; var now = DateTimeOffset.UtcNow;
                var contact = CustomerContactWorkflow.Add(customer, body, c.Scope.UserId, now);
                await RecordHistory(c, customer, before, CustomerContactChangeKind.Created, contact.Id, now, ct);
                await c.Store.Save(customer, "customer.contact_add", false, ct); return View(customer);
            })).WithTags("customers");

        api.MapPost("/customers/{id:guid}/contacts/{contactId:guid}/update", (Guid id, Guid contactId, CustomerContactDetails body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("customers", "customer.contact_update", body, async ct =>
            {
                var customer = await c.Current<Customer>(id, ct);
                var before = customer.Contacts?.ToArray() ?? []; var now = DateTimeOffset.UtcNow;
                CustomerContactWorkflow.Update(customer, contactId, body, c.Scope.UserId, now);
                await RecordHistory(c, customer, before, CustomerContactChangeKind.Updated, contactId, now, ct);
                await c.Store.Save(customer, "customer.contact_update", false, ct); return View(customer);
            })).WithTags("customers");

        api.MapPost("/customers/{id:guid}/contacts/{contactId:guid}/archive", (Guid id, Guid contactId, [AsParameters] BusinessCommandServices c) =>
            c.Execute("customers", "customer.contact_archive", new { id, contactId }, async ct =>
            {
                var customer = await c.Current<Customer>(id, ct);
                var before = customer.Contacts?.ToArray() ?? []; var now = DateTimeOffset.UtcNow;
                CustomerContactWorkflow.Archive(customer, contactId, c.Scope.UserId, now);
                await RecordHistory(c, customer, before, CustomerContactChangeKind.Archived, contactId, now, ct);
                await c.Store.Save(customer, "customer.contact_archive", false, ct); return View(customer);
            })).WithTags("customers");

        api.MapPost("/customers/{id:guid}/contacts/{contactId:guid}/restore", (Guid id, Guid contactId, [AsParameters] BusinessCommandServices c) =>
            c.Execute("customers", "customer.contact_restore", new { id, contactId }, async ct =>
            {
                var customer = await c.Current<Customer>(id, ct);
                var before = customer.Contacts?.ToArray() ?? []; var now = DateTimeOffset.UtcNow;
                CustomerContactWorkflow.Restore(customer, contactId, c.Scope.UserId, now);
                await RecordHistory(c, customer, before, CustomerContactChangeKind.Restored, contactId, now, ct);
                await c.Store.Save(customer, "customer.contact_restore", false, ct); return View(customer);
            })).WithTags("customers");
    }

    private static async Task RecordHistory(BusinessCommandServices c, Customer customer, IReadOnlyList<CustomerContact> before,
        CustomerContactChangeKind action, Guid target, DateTimeOffset now, CancellationToken ct)
    {
        var name = await c.Db.Users.Where(x => x.Id == c.Scope.UserId).Select(x => x.DisplayName).SingleAsync(ct);
        // Preserve the original actor label; a later account rename does not rewrite this evidence.
        if (string.IsNullOrWhiteSpace(name)) name = c.Scope.UserId.ToString();
        foreach (var entry in CustomerContactHistory.Capture(customer, before, action, target, c.Scope.UserId, name, now))
            await c.Store.Save(entry, "customer.contact_history", true, ct);
    }

    private static async Task<IResult> History(Guid id, Guid contactId, [AsParameters] BusinessCommandServices c,
        CancellationToken ct, int page = 1)
    {
        Guard.Require(page is >= 1 and <= 100_000, "contact_history_page", "Select a supported contact history page.");
        await using var transaction = await c.Db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await c.Commands.Authorize("customers", false, ct);
        await RecordPolicy.EnsureVisible("customers", id, c.Scope, c.Store, ct);
        // The retained history remains readable after customer or contact archival.
        var customer = await c.Store.Get<Customer>(id, ct); customer.Validate();
        var contact = customer.Contacts?.SingleOrDefault(x => x.Id == contactId);
        Guard.Require(contact is not null, "not_found", "The contact does not belong to this customer.");
        const string source = """
            FROM "Entities" e WHERE e."TenantId"=@tenant AND e."Kind"='customer-contact-changes'
              AND e."Data"->>'contactId'=@contact
              AND EXISTS (SELECT 1 FROM "Links" l WHERE l."TenantId"=@tenant
                AND l."SourceKind"='customer-contact-changes' AND l."SourceId"=e."Id"
                AND l."TargetKind"='customers' AND l."TargetId"=@customer)
            """;
        object[] Parameters() => [new NpgsqlParameter("tenant", c.Scope.TenantId), new NpgsqlParameter("customer", id),
            new NpgsqlParameter("contact", contactId.ToString())];
        var total = await c.Db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" " + source, Parameters()).SingleAsync(ct);
        const int pageSize = 20;
        var rows = await c.Db.Entities.FromSqlRaw("SELECT e.* " + source
            + " ORDER BY (e.\"Data\"->>'customerRevision')::bigint DESC, e.\"Id\" DESC LIMIT 20 OFFSET @offset",
            [.. Parameters(), new NpgsqlParameter("offset", (page - 1) * pageSize)]).AsNoTracking().ToListAsync(ct);
        var entries = rows.Select(row =>
        {
            var entry = AppJson.Read<CustomerContactChange>(row.Data); CustomerContactHistory.EnsureSource(customer, entry);
            Guard.Require(entry.ContactId == contactId && entry.Id == row.Id && entry.TenantId == row.TenantId
                && entry.Revision == row.Revision && entry.Archived == row.Archived,
                "contact_history_source", "The contact history no longer matches its stored identity.");
            return entry;
        }).OrderByDescending(entry => entry.CustomerRevision).ThenByDescending(entry => entry.Id).ToArray();
        for (var i = 1; i < entries.Length; i++)
            Guard.Require(entries[i].CustomerRevision < entries[i - 1].CustomerRevision && entries[i].After == entries[i - 1].Before,
                "contact_history_chain", "The contact history contains a broken snapshot sequence.");
        if (page == 1 && entries.Length > 0)
            Guard.Require(entries[0].After == contact, "contact_history_chain", "The latest history must match the current contact values.");
        await transaction.CommitAsync(ct); c.Http.Response.Headers.ETag = $"\"{customer.Revision}\"";
        return Results.Ok(new CustomerContactHistoryView(id, c.Scope.TenantId, contactId, customer.Name, customer.Revision,
            contact, entries, total, page, pageSize));
    }

    private static CustomerContactsView View(Customer customer) => new(customer.Id, customer.TenantId, customer.Name, customer.Revision, customer.Contacts ?? []);
}
