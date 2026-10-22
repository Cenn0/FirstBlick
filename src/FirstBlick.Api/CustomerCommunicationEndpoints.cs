using System.Data;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FirstBlick.Api;

public sealed record CustomerCommunicationItem(CustomerCommunication Entry, CustomerCommunicationVoid? Voiding);
public sealed record CustomerCommunicationsView(Guid CustomerId, Guid TenantId, string CustomerName, long CustomerRevision,
    string TimeZone, IReadOnlyList<CustomerCommunicationItem> Items, long Total, int Page, int PageSize);
public sealed record CustomerCommunicationChange(Guid CustomerId, Guid TenantId, string CustomerName, long CustomerRevision,
    CustomerCommunication Entry, CustomerCommunicationVoid? Voiding);
public sealed record CustomerCommunicationVoidRequest(string SourceDigest, string Reason);

public static class CustomerCommunicationEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/customers/{id:guid}/communications", History).WithTags("customer-communications");
        api.MapPost("/customers/{id:guid}/communications", (Guid id, CustomerCommunicationRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("customer-communications", "customer.communication_record", body, async ct =>
            {
                var customer = await c.Current<Customer>(id, ct);
                Project? project = body.ProjectId is { } projectId ? await c.Current<Project>(projectId, ct, false) : null;
                var entry = CustomerCommunicationWorkflow.Record(customer, body, project, c.Scope.UserId, DateTimeOffset.UtcNow);
                await c.Store.Save(entry, "customer.communication_recorded", true, ct);
                await c.Store.Save(customer, "customer.communication_record", false, ct);
                return Change(customer, entry, null);
            })).Produces<CustomerCommunicationChange>();
        api.MapPost("/customers/{id:guid}/communications/{entryId:guid}/void", (Guid id, Guid entryId,
            CustomerCommunicationVoidRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("customer-communications", "customer.communication_void", body, async ct =>
            {
                var customer = await c.Current<Customer>(id, ct);
                var entry = await c.Store.Get<CustomerCommunication>(entryId, ct); entry.Validate();
                Guard.Require(entry.CustomerId == id, "not_found", "The conversation does not belong to the selected customer.");
                var key = $"communication:{entry.Id:N}";
                Guard.Require(!await c.Db.Entities.AnyAsync(x => x.Kind == "customer-communication-voids" && x.UniqueKey == key, ct),
                    "communication_already_void", "The original conversation already has a retained correction.");
                var voiding = CustomerCommunicationWorkflow.Void(entry, body.SourceDigest, body.Reason, c.Scope.UserId, DateTimeOffset.UtcNow);
                CustomerCommunicationWorkflow.EnsureVoidSource(entry, voiding);
                await c.Store.Save(voiding, "customer.communication_voided", true, ct);
                await c.Store.Save(customer, "customer.communication_void", false, ct);
                return Change(customer, entry, voiding);
            })).Produces<CustomerCommunicationChange>();
    }

    private static async Task<IResult> History(Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct, int page = 1)
    {
        await using var transaction = await c.Db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await c.Commands.Authorize("customer-communications", false, ct);
        var customer = await c.Current<Customer>(id, ct, false); customer.Validate();
        var links = c.Db.Links.Where(x => x.SourceKind == "customer-communications" && x.TargetKind == "customers" && x.TargetId == id);
        var query = c.Db.Entities.AsNoTracking().Where(x => x.Kind == "customer-communications" && links.Any(l => l.SourceId == x.Id));
        var total = await query.LongCountAsync(ct);
        page = Math.Clamp(page, 1, 100_000); const int pageSize = 20;
        // Stored UTC timestamps have one serializer and retain their full fractional
        // precision. Sorting the ISO strings avoids a PostgreSQL microsecond truncation.
        var rows = await c.Db.Database.SqlQueryRaw<string>("""
            SELECT e."Data"::text AS "Value" FROM "Entities" e
            WHERE e."TenantId"=@tenant AND e."Kind"='customer-communications'
              AND EXISTS (SELECT 1 FROM "Links" l WHERE l."TenantId"=@tenant
                AND l."SourceKind"='customer-communications' AND l."SourceId"=e."Id"
                AND l."TargetKind"='customers' AND l."TargetId"=@customer)
            ORDER BY e."Data"->>'occurredAt' DESC, e."Data"->>'recordedAt' DESC, e."Id" DESC
            LIMIT 20 OFFSET @offset
            """, new NpgsqlParameter("tenant", c.Scope.TenantId), new NpgsqlParameter("customer", id),
            new NpgsqlParameter("offset", (page - 1) * pageSize)).ToListAsync(ct);
        var entries = rows.Select(AppJson.Read<CustomerCommunication>).ToArray();
        foreach (var entry in entries)
        {
            entry.Validate();
            Guard.Require(entry.CustomerId == id && entry.TenantId == c.Scope.TenantId,
                "communication_source", "The retained conversation does not match its customer link.");
        }
        var ids = entries.Select(x => x.Id).ToArray();
        var voidLinks = c.Db.Links.Where(x => x.SourceKind == "customer-communication-voids" && x.TargetKind == "customer-communications" && ids.Contains(x.TargetId));
        var voidRows = await c.Db.Entities.AsNoTracking().Where(x => x.Kind == "customer-communication-voids" && voidLinks.Any(l => l.SourceId == x.Id))
            .Select(x => x.Data).ToListAsync(ct);
        var voidings = new Dictionary<Guid, CustomerCommunicationVoid>();
        foreach (var row in voidRows)
        {
            var voiding = AppJson.Read<CustomerCommunicationVoid>(row);
            var original = entries.SingleOrDefault(x => x.Id == voiding.CommunicationId);
            Guard.Require(original is not null, "communication_source", "The correction source does not match the selected history.");
            CustomerCommunicationWorkflow.EnsureVoidSource(original, voiding);
            Guard.Require(voidings.TryAdd(original.Id, voiding), "communication_source", "The conversation has more than one correction.");
        }
        var zoneId = await c.Db.Tenants.Where(x => x.Id == c.Scope.TenantId).Select(x => x.TimeZone).SingleAsync(ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var browserZone = TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;
        var items = entries.Select(entry => new CustomerCommunicationItem(entry, voidings.GetValueOrDefault(entry.Id))).ToArray();
        await transaction.CommitAsync(ct);
        c.Http.Response.Headers.ETag = $"\"{customer.Revision}\"";
        return Results.Ok(new CustomerCommunicationsView(id, c.Scope.TenantId, customer.Name, customer.Revision, browserZone, items, total, page, pageSize));
    }

    private static CustomerCommunicationChange Change(Customer customer, CustomerCommunication entry, CustomerCommunicationVoid? voiding)
        => new(customer.Id, customer.TenantId, customer.Name, customer.Revision, entry, voiding);
}
