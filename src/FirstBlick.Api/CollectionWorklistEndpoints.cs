using System.Data;
using System.Globalization;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record CollectionWorklistItem(Guid Id, long Revision, Guid InvoiceId, string InvoiceNumber, string BuyerName,
    DateOnly InvoiceDueOn, Guid AssignedToUserId, DateOnly NextReviewOn, CollectionCaseStatus Status, DateTimeOffset UpdatedAt);
public sealed record CollectionWorklistResponse(string Schema, DateTimeOffset CapturedAt, IReadOnlyList<CollectionWorklistItem> Items, long Total, int Page, int PageSize);

/// <summary>Bounded invoice-labelled projection; no full financial or collection histories are loaded.</summary>
public static class CollectionWorklistEndpoints
{
    private sealed record Filter(int Page, int PageSize, string Status, string Search, string InvoiceId);
    public sealed record WorklistRow(Guid Id, long Revision, Guid InvoiceId, string InvoiceNumber, string BuyerName,
        DateOnly InvoiceDueOn, Guid AssignedToUserId, DateOnly NextReviewOn, string Status, DateTimeOffset UpdatedAt);
    private static readonly string[] Parameters = ["page", "pageSize", "status", "q", "invoiceId"];

    /// <param name="routes">The existing /collection-cases route group.</param>
    public static void Map(RouteGroupBuilder routes)
    {
        routes.MapGet("", Read).Produces<CollectionWorklistResponse>();
        routes.MapGet("/worklist", Read).Produces<CollectionWorklistResponse>();
    }
    private static async Task<IResult> Read([AsParameters] BusinessCommandServices c, CancellationToken ct)
        {
            var filter = Parse(c.Http.Request.Query);
            await using var transaction = await CollectionEndpoints.ReadLock(c, false, ct);
            var visible = await RecordPolicy.VisibleIds("collection-cases", c.Scope, c.Store, ct);
            var all = visible is null; var ids = visible ?? []; var tenant = c.Scope.TenantId;
            var total = await c.Db.Database.SqlQuery<long>($"""
                SELECT count(*)::bigint AS "Value"
                FROM "Entities" AS c JOIN "Entities" AS i
                  ON i."TenantId"=c."TenantId" AND i."Kind"='invoices' AND i."Id"::text=c."Data"->>'invoiceId'
                WHERE c."TenantId"={tenant} AND c."Kind"='collection-cases' AND NOT c."Archived"
                  AND NOT i."Archived" AND i."Data"->>'status'='Issued' AND i."Data"->>'kind'<>'CreditNote'
                  AND ({all} OR c."Id"=ANY({ids}))
                  AND ({filter.Status}='' OR c."Data"->>'status'={filter.Status})
                  AND ({filter.InvoiceId}='' OR i."Id"::text={filter.InvoiceId})
                  AND ({filter.Search}='' OR position(lower({filter.Search}) in lower(i."Data"->>'number'))>0
                       OR position(lower({filter.Search}) in lower(i."Data"->>'buyerName'))>0)
                """).SingleAsync(ct);
            var rows = await c.Db.Database.SqlQuery<WorklistRow>($"""
                SELECT c."Id", c."Revision", i."Id" AS "InvoiceId", i."Data"->>'number' AS "InvoiceNumber",
                    i."Data"->>'buyerName' AS "BuyerName", (i."Data"->>'dueDate')::date AS "InvoiceDueOn",
                    (c."Data"->>'assignedToUserId')::uuid AS "AssignedToUserId", (c."Data"->>'nextReviewOn')::date AS "NextReviewOn",
                    c."Data"->>'status' AS "Status", c."UpdatedAt"
                FROM "Entities" AS c JOIN "Entities" AS i
                  ON i."TenantId"=c."TenantId" AND i."Kind"='invoices' AND i."Id"::text=c."Data"->>'invoiceId'
                WHERE c."TenantId"={tenant} AND c."Kind"='collection-cases' AND NOT c."Archived"
                  AND NOT i."Archived" AND i."Data"->>'status'='Issued' AND i."Data"->>'kind'<>'CreditNote'
                  AND ({all} OR c."Id"=ANY({ids}))
                  AND ({filter.Status}='' OR c."Data"->>'status'={filter.Status})
                  AND ({filter.InvoiceId}='' OR i."Id"::text={filter.InvoiceId})
                  AND ({filter.Search}='' OR position(lower({filter.Search}) in lower(i."Data"->>'number'))>0
                       OR position(lower({filter.Search}) in lower(i."Data"->>'buyerName'))>0)
                ORDER BY c."Data"->>'nextReviewOn', c."UpdatedAt" DESC, c."Id"
                LIMIT {filter.PageSize} OFFSET {checked((filter.Page - 1) * filter.PageSize)}
                """).ToListAsync(ct);
            var items = rows.Select(row =>
            {
                Guard.Require(Enum.TryParse<CollectionCaseStatus>(row.Status, false, out var status) && Enum.IsDefined(status) && status.ToString() == row.Status,
                    "collection_source_metadata", "A stored collection case has an unsupported status.");
                return new CollectionWorklistItem(row.Id, row.Revision, row.InvoiceId, row.InvoiceNumber, row.BuyerName,
                    row.InvoiceDueOn, row.AssignedToUserId, row.NextReviewOn, status, row.UpdatedAt);
            }).ToArray();
            await transaction.CommitAsync(ct);
            return Results.Ok(new CollectionWorklistResponse("firstblick.collection-worklist/1.0.0", DateTimeOffset.UtcNow,
                items, total, filter.Page, filter.PageSize));
        }

    private static Filter Parse(IQueryCollection query)
    {
        Guard.Require(query.All(pair => Parameters.Contains(pair.Key, StringComparer.Ordinal) && pair.Value.Count == 1),
            "unsupported_query", "Use each supported worklist parameter once.");
        var page = Number(query, "page", 1, 10_000); var size = Number(query, "pageSize", 25, 100);
        var status = query["status"].ToString();
        Guard.Require(status.Length == 0 || Enum.TryParse<CollectionCaseStatus>(status, false, out var parsed) && Enum.IsDefined(parsed)
            && parsed.ToString() == status, "invalid_collection_status", "Choose Active, Disputed, Closed or all statuses.");
        var search = query["q"].ToString().Trim();
        Guard.Text(search, "Search", 200, false); Guard.Require(search.Length is 0 or >= 2, "invalid_collection_search", "Use at least two search characters.");
        var invoice = query["invoiceId"].ToString();
        if (invoice.Length > 0)
        {
            Guard.Require(Guid.TryParseExact(invoice, "D", out var id) && id != Guid.Empty, "invalid_collection_invoice", "The invoice filter must be a complete invoice identifier.");
            invoice = id.ToString();
        }
        return new(page, size, status, search, invoice);
    }

    private static int Number(IQueryCollection query, string name, int fallback, int maximum)
    {
        if (!query.ContainsKey(name)) return fallback;
        Guard.Require(int.TryParse(query[name].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 1 && value <= maximum,
            "invalid_collection_page", $"{name} must be an integer from 1 to {maximum}.");
        return value;
    }
}
