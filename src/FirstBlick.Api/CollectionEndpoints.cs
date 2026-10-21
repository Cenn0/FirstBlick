using System.Data;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record OpenCollectionRequest(Guid InvoiceId, Guid AssignedToUserId, DateOnly NextReviewOn, string Reason);
public sealed record CollectionPreviewRequest(CollectionStepInput Input);
public sealed record ApproveCollectionStepRequest(CollectionStepInput Input, string PreviewDigest);
public sealed record WithdrawCollectionStepRequest(Guid StepId, string Reason);
public sealed record RecordCollectionContactRequest(Guid StepId, string StepDigest, CollectionContactChannel Channel, DateTimeOffset OccurredAt, string EvidenceReference);
public sealed record OpenCollectionDisputeRequest(decimal Amount, string Reason, string EvidenceReference, DateOnly NextReviewOn);
public sealed record ResolveCollectionDisputeRequest(string Reason, string EvidenceReference, DateOnly NextReviewOn);
public sealed record ReviewCollectionRequest(Guid AssignedToUserId, DateOnly NextReviewOn, string Reason);
public sealed record CloseCollectionRequest(CollectionClosureKind Kind, string Reason, string EvidenceReference = "");
public sealed record ReopenCollectionRequest(Guid AssignedToUserId, DateOnly NextReviewOn, string Reason, string EvidenceReference);
public sealed record CollectionAssignee(Guid Id, string DisplayName);
public sealed record CollectionBalanceResponse(string Schema, Guid CaseId, long CaseRevision, CollectionCaseStatus Status, CollectionLedger Ledger);

public static class CollectionEndpoints
{
    public const string Kind = "collection-cases";

    public static void Map(RouteGroupBuilder api)
    {
        var routes = api.MapGroup("/" + Kind).WithTags(Kind);
        CollectionWorklistEndpoints.Map(routes);
        routes.MapGet("/assignees", async ([AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await using var tx = await ReadLock(c, false, ct);
            var now = DateTimeOffset.UtcNow;
            var people = await (from member in c.Db.Memberships.AsNoTracking()
                join user in c.Db.Users.AsNoTracking() on member.UserId equals user.Id
                where member.TenantId == c.Scope.TenantId && member.Active && user.EmailConfirmed && (user.LockoutEnd == null || user.LockoutEnd <= now)
                    && (member.Role == BusinessRole.ManagingDirector || member.Role == BusinessRole.Office || member.Role == BusinessRole.Accounting)
                orderby user.DisplayName, user.Id
                select new CollectionAssignee(user.Id, user.DisplayName)).Take(1001).ToListAsync(ct);
            Guard.Require(people.Count <= 1000, "collection_owner_budget", "This business exceeds the supported 1,000 collection assignees.");
            await tx.CommitAsync(ct); return Results.Ok(people);
        }).Produces<List<CollectionAssignee>>();
        routes.MapGet("/{id:guid}", async (Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await using var tx = await ReadLock(c, false, ct); var value = await Current(c, id, false, ct);
            c.Http.Response.Headers.ETag = $"\"{value.Revision}\""; await tx.CommitAsync(ct); return Results.Ok(value);
        }).Produces<CollectionCase>();
        routes.MapGet("/{id:guid}/balance", async (Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await using var tx = await ReadLock(c, false, ct); var value = await Current(c, id, false, ct);
            var ledger = await Ledger(c, value.InvoiceId, ct); CollectionWorkflow.CheckLedger(value, ledger);
            await tx.CommitAsync(ct); return Results.Ok(new CollectionBalanceResponse("firstblick.collection-balance/1.0.0", value.Id, value.Revision, value.Status, ledger));
        }).Produces<CollectionBalanceResponse>();
        routes.MapPost("/from-invoice", (OpenCollectionRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute(Kind, "collection.open", body, async ct =>
            {
                await Owner(c, body.AssignedToUserId, ct);
                Guard.Require(!await c.Db.Entities.AnyAsync(x => x.Kind == Kind && x.UniqueKey == "invoice:" + body.InvoiceId, ct),
                    "collection_exists", "This invoice already has a retained collection case; reopen that case explicitly if appropriate.");
                var ledger = await Ledger(c, body.InvoiceId, ct);
                var value = CollectionWorkflow.Open(ledger, body.AssignedToUserId, body.NextReviewOn, body.Reason, c.Scope.UserId, DateTimeOffset.UtcNow);
                return await c.SaveNew(value, "collection.open", ct);
            })).Produces<CollectionCase>();
        routes.MapPost("/{id:guid}/step-preview", async (Guid id, CollectionPreviewRequest body, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            // A preview is a read-only calculation. It does not consume an idempotency key or write an audit/outbox row.
            await using var tx = await ReadLock(c, true, ct); var value = await Current(c, id, true, ct);
            await Owner(c, value.AssignedToUserId, ct); var result = CollectionWorkflow.Preview(value, await Ledger(c, value.InvoiceId, ct), body.Input);
            await tx.CommitAsync(ct); return Results.Ok(result);
        }).Produces<CollectionPreview>();
        routes.MapPost("/{id:guid}/approve-step", (Guid id, ApproveCollectionStepRequest body, [AsParameters] BusinessCommandServices c) =>
            Mutate(c, id, "step.approved", body, async (value, ct) =>
            {
                await Owner(c, value.AssignedToUserId, ct);
                CollectionWorkflow.Approve(value, await Ledger(c, value.InvoiceId, ct), body.Input, body.PreviewDigest, c.Scope.UserId, DateTimeOffset.UtcNow);
            })).Produces<CollectionCase>();
        routes.MapPost("/{id:guid}/withdraw-step", (Guid id, WithdrawCollectionStepRequest body, [AsParameters] BusinessCommandServices c) =>
            Mutate(c, id, "step.withdrawn", body, async (value, ct) =>
                CollectionWorkflow.Withdraw(value, body.StepId, body.Reason, await c.BusinessDate(ct), c.Scope.UserId, DateTimeOffset.UtcNow))).Produces<CollectionCase>();
        routes.MapPost("/{id:guid}/record-contact", (Guid id, RecordCollectionContactRequest body, [AsParameters] BusinessCommandServices c) =>
            Mutate(c, id, "contact.recorded", body, async (value, ct) =>
            {
                var zoneId = await c.Db.Tenants.Where(x => x.Id == c.Scope.TenantId).Select(x => x.TimeZone).SingleAsync(ct);
                var occurredOn = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(body.OccurredAt, TimeZoneInfo.FindSystemTimeZoneById(zoneId)).DateTime);
                CollectionWorkflow.RecordContact(value, body.StepId, body.StepDigest, body.Channel, body.OccurredAt, occurredOn,
                    body.EvidenceReference, await c.BusinessDate(ct), c.Scope.UserId, DateTimeOffset.UtcNow);
            })).Produces<CollectionCase>();
        routes.MapPost("/{id:guid}/dispute", (Guid id, OpenCollectionDisputeRequest body, [AsParameters] BusinessCommandServices c) =>
            Mutate(c, id, "dispute.opened", body, async (value, ct) =>
                CollectionWorkflow.Dispute(value, await Ledger(c, value.InvoiceId, ct), body.Amount, body.Reason, body.EvidenceReference,
                    body.NextReviewOn, c.Scope.UserId, DateTimeOffset.UtcNow))).Produces<CollectionCase>();
        routes.MapPost("/{id:guid}/resolve-dispute", (Guid id, ResolveCollectionDisputeRequest body, [AsParameters] BusinessCommandServices c) =>
            Mutate(c, id, "dispute.resolved", body, async (value, ct) =>
                CollectionWorkflow.ResolveDispute(value, body.Reason, body.EvidenceReference, body.NextReviewOn, await c.BusinessDate(ct), c.Scope.UserId, DateTimeOffset.UtcNow))).Produces<CollectionCase>();
        routes.MapPost("/{id:guid}/review", (Guid id, ReviewCollectionRequest body, [AsParameters] BusinessCommandServices c) =>
            Mutate(c, id, "review.changed", body, async (value, ct) =>
            {
                await Owner(c, body.AssignedToUserId, ct);
                CollectionWorkflow.Review(value, body.AssignedToUserId, body.NextReviewOn, body.Reason, await c.BusinessDate(ct), c.Scope.UserId, DateTimeOffset.UtcNow);
            })).Produces<CollectionCase>();
        routes.MapPost("/{id:guid}/close", (Guid id, CloseCollectionRequest body, [AsParameters] BusinessCommandServices c) =>
            Mutate(c, id, "closed", body, async (value, ct) =>
                CollectionWorkflow.Close(value, await Ledger(c, value.InvoiceId, ct), body.Kind, body.Reason, body.EvidenceReference, c.Scope.UserId, DateTimeOffset.UtcNow))).Produces<CollectionCase>();
        routes.MapPost("/{id:guid}/reopen", (Guid id, ReopenCollectionRequest body, [AsParameters] BusinessCommandServices c) =>
            Mutate(c, id, "reopened", body, async (value, ct) =>
            {
                await Owner(c, body.AssignedToUserId, ct);
                CollectionWorkflow.Reopen(value, await Ledger(c, value.InvoiceId, ct), body.AssignedToUserId, body.NextReviewOn,
                    body.Reason, body.EvidenceReference, c.Scope.UserId, DateTimeOffset.UtcNow);
            })).Produces<CollectionCase>();
        // There is no editable aggregate endpoint. Keep explicit rejections to avoid an HTML fallback being mistaken for success.
        routes.MapPost("", (JsonElement body, [AsParameters] BusinessCommandServices c) => RejectGeneric(c, body));
        routes.MapPut("/{id:guid}", (Guid id, JsonElement body, [AsParameters] BusinessCommandServices c) => RejectGeneric(c, body));
        routes.MapDelete("/{id:guid}", (Guid id, [AsParameters] BusinessCommandServices c) => RejectGeneric(c, new { id }));
    }

    private static Task<IResult> RejectGeneric(BusinessCommandServices c, object body) =>
        c.Execute(Kind, "collection.generic-rejected", body, _ => throw new DomainException("command_required", "Use explicit collection commands; retained cases cannot be replaced or deleted."));
    private static Task<IResult> Mutate(BusinessCommandServices c, Guid id, string action, object body, Func<CollectionCase, CancellationToken, Task> apply) =>
        c.Execute(Kind, "collection." + action, body, async ct =>
        {
            var value = await Current(c, id, true, ct); await apply(value, ct);
            await c.Store.Save(value, "collection." + action, false, ct); return value;
        });
    public static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> ReadLock(BusinessCommandServices c, bool write, CancellationToken ct)
    {
        c.Http.Response.Headers.CacheControl = "no-store, private"; c.Http.Response.Headers.Pragma = "no-cache";
        var tx = await c.Db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await c.Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({c.Scope.TenantId.ToString()},0))", ct);
            await c.Commands.Authorize(Kind, write, ct);
            Guard.Require(c.Scope.Role is BusinessRole.ManagingDirector or BusinessRole.Office or BusinessRole.Accounting, "forbidden", "Collection cases require current director, office or accounting access.");
            return tx;
        }
        catch { await tx.DisposeAsync(); throw; }
    }
    private static async Task<CollectionCase> Current(BusinessCommandServices c, Guid id, bool match, CancellationToken ct)
    {
        await RecordPolicy.EnsureVisible(Kind, id, c.Scope, c.Store, ct);
        var value = ReadRow<CollectionCase>(await c.Store.Find(Kind, id, ct), c.Scope.TenantId, Kind);
        if (match) CommandExecutor.Match(c.Http, value.Revision); return value;
    }
    private static async Task Owner(BusinessCommandServices c, Guid id, CancellationToken ct)
    {
        Guard.Id(id, "Assigned person"); var now = DateTimeOffset.UtcNow;
        var available = await (from member in c.Db.Memberships.AsNoTracking()
            join user in c.Db.Users.AsNoTracking() on member.UserId equals user.Id
            where member.TenantId == c.Scope.TenantId && member.UserId == id && member.Active && user.EmailConfirmed
                && (user.LockoutEnd == null || user.LockoutEnd <= now)
                && (member.Role == BusinessRole.ManagingDirector || member.Role == BusinessRole.Office || member.Role == BusinessRole.Accounting)
            select member.UserId).AnyAsync(ct);
        Guard.Require(available, "collection_owner_unavailable", "Choose a currently available finance member of this business.");
    }
    private static async Task<CollectionLedger> Ledger(BusinessCommandServices c, Guid invoiceId, CancellationToken ct)
    {
        await RecordPolicy.EnsureVisible("invoices", invoiceId, c.Scope, c.Store, ct);
        var invoiceRow = await c.Store.Find("invoices", invoiceId, ct);
        var invoice = ReadRow<Invoice>(invoiceRow, c.Scope.TenantId, "invoices");
        Guard.Require(invoice.Status == InvoiceStatus.Issued && invoice.Kind != InvoiceKind.CreditNote, "invalid_invoice", "Select an issued receivable invoice.");
        var invoiceText = invoiceId.ToString(); var tenant = c.Scope.TenantId;
        var rows = await c.Db.Entities.FromSqlInterpolated($"""
            SELECT * FROM "Entities"
            WHERE "TenantId"={tenant} AND NOT "Archived" AND (
              ("Kind"='invoices' AND "Data"->>'originalInvoiceId'={invoiceText} AND "Data"->>'status'='Issued') OR
              ("Kind"='payment-allocations' AND "Data"->>'invoiceId'={invoiceText}))
            ORDER BY "Kind","Id" LIMIT 1000
            """).AsNoTracking().ToListAsync(ct);
        Guard.Require(rows.Count < 1000, "collection_source_budget", "This invoice exceeds the supported 1,000 financial sources; no partial balance is produced.");
        var credits = rows.Where(x => x.Kind == "invoices").Select(x => ReadRow<Invoice>(x, tenant, "invoices")).ToArray();
        var allocations = rows.Where(x => x.Kind == "payment-allocations").Select(x => ReadRow<PaymentAllocation>(x, tenant, "payment-allocations")).ToArray();
        var sources = rows.Prepend(invoiceRow).Select(x => new CollectionSource(x.Kind, x.Id, x.Revision, ContentDigest.OfUtf8(x.Data))).ToArray();
        return CollectionLedger.Capture(invoice, allocations, credits, sources, await c.BusinessDate(ct));
    }
    private static T ReadRow<T>(StoredEntity row, Guid tenantId, string kind) where T : TenantEntity
    {
        var value = AppJson.Read<T>(row.Data);
        Guard.Require(row.TenantId == tenantId && row.Kind == kind && value.TenantId == tenantId && value.Id == row.Id && value.Revision == row.Revision && value.Archived == row.Archived,
            "collection_source_metadata", "Stored financial metadata does not match its retained body.");
        Guard.Require(!row.Archived, "not_found", "The source record is archived."); value.Validate(); return value;
    }
}
