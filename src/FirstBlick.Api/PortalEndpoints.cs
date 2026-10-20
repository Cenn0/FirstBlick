using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using FirstBlick.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record PortalGrantRequest(Guid RecipientUserId, string SourceKind, Guid SourceId, DateTimeOffset ExpiresAt, string Title, bool AllowAcceptance);
public sealed record PortalAcceptRequest(string Digest);
public sealed record PortalFeedbackRequest(string Message);

public static class PortalEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/portal/recipients", async (FirstBlickDbContext db, TenantScope scope, CommandExecutor commands, CancellationToken ct) =>
        {
            await commands.Authorize("portal-shares", false, ct);
            return Results.Ok(await (from membership in db.Memberships.AsNoTracking()
                join user in db.Users.AsNoTracking() on membership.UserId equals user.Id
                where membership.TenantId == scope.TenantId && membership.Active && membership.Role == BusinessRole.Partner
                orderby user.DisplayName, user.Id
                select new { userId = user.Id, user.DisplayName, user.Email }).Take(500).ToListAsync(ct));
        }).WithTags("portal");

        api.MapPost("/portal/grants", (PortalGrantRequest body, HttpContext http, EntityStore store, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands) =>
            commands.Execute(http, "portal-shares", "portal.grant", body, async ct =>
            {
                Guard.Require(await db.Memberships.AnyAsync(m => m.TenantId == scope.TenantId && m.UserId == body.RecipientUserId && m.Active && m.Role == BusinessRole.Partner, ct),
                    "invalid_recipient", "Select an active personally identified partner in this business.");
                var now = DateTimeOffset.UtcNow;
                var share = new PortalShare { TenantId = scope.TenantId, RecipientUserId = body.RecipientUserId, GrantedBy = scope.UserId,
                    SourceKind = body.SourceKind, SourceId = body.SourceId, Title = body.Title, ExpiresAt = body.ExpiresAt,
                    AllowAcceptance = body.AllowAcceptance, CreatedAt = now, UpdatedAt = now };
                if (body.SourceKind == "quotes")
                {
                    var quote = await store.Get<Quote>(body.SourceId, ct); quote.Validate();
                    Guard.Require(quote.Presentation is not null && quote.Status is QuoteStatus.Issued or QuoteStatus.Accepted, "missing_snapshot", "Share an issued quote with frozen parties.");
                    Guard.Require(!body.AllowAcceptance || quote.Status == QuoteStatus.Issued, "invalid_transition", "Only a still-open issued quote can be offered for acceptance.");
                    share.ProjectId = quote.ProjectId; share.SourceDigest = quote.IssuedDigest;
                }
                else if (body.SourceKind == "documents")
                {
                    var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == body.SourceId, ct);
                    Guard.Require(document is not null && document.State == "Ready" && document.ProjectId.HasValue, "document_not_ready", "Share a verified project document.");
                    Guard.Require(document.SourceKind is null || AccessRules.CanRead(scope.Role, document.SourceKind), "forbidden", "You cannot release a document whose source is outside your role.");
                    share.ProjectId = document.ProjectId.Value; share.SourceDigest = document.Sha256;
                }
                share.Validate(); await store.Save(share, "portal.grant", true, ct); return share;
            })).WithTags("portal");

        api.MapPost("/portal/grants/{id:guid}/revoke", (Guid id, HttpContext http, EntityStore store, CommandExecutor commands) =>
            commands.Execute(http, "portal-shares", "portal.revoke", new { id }, async ct =>
            {
                var share = await store.Get<PortalShare>(id, ct); CommandExecutor.Match(http, share.Revision);
                Guard.Require(share.RevokedAt is null, "invalid_transition", "This grant has already been revoked.");
                share.RevokedAt = DateTimeOffset.UtcNow; await store.Save(share, "portal.revoke", false, ct); return share;
            })).WithTags("portal");

        api.MapGet("/portal/shares", async (HttpContext http, FirstBlickDbContext db, EntityStore store, TenantScope scope, CommandExecutor commands, CancellationToken ct) =>
        {
            await commands.Authorize("portal-access", false, ct); await Enabled(db, scope, ct);
            var (page, pageSize) = Page(http);
            var recipient = scope.UserId.ToString(); var tenant = scope.TenantId;
            var now = DateTimeOffset.UtcNow;
            var total = await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM \"Entities\" WHERE \"TenantId\"={tenant} AND \"Kind\"='portal-shares' AND NOT \"Archived\" AND \"Data\"->>'recipientUserId'={recipient} AND \"Data\"->>'revokedAt' IS NULL AND (\"Data\"->>'expiresAt')::timestamptz>{now}").SingleAsync(ct);
            var json = await db.Database.SqlQuery<string>($"SELECT \"Data\"::text AS \"Value\" FROM \"Entities\" WHERE \"TenantId\"={tenant} AND \"Kind\"='portal-shares' AND NOT \"Archived\" AND \"Data\"->>'recipientUserId'={recipient} AND \"Data\"->>'revokedAt' IS NULL AND (\"Data\"->>'expiresAt')::timestamptz>{now} ORDER BY \"CreatedAt\" DESC, \"Id\" LIMIT {pageSize} OFFSET {checked((page - 1) * pageSize)}").ToListAsync(ct);
            return Results.Ok(new { items = json.Select(x => PublicShare(AppJson.Read<PortalShare>(x))), total, page, pageSize });
        }).WithTags("portal");

        api.MapGet("/portal/shares/{id:guid}", async (Guid id, HttpContext http, EntityStore store, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands, CancellationToken ct) =>
        {
            await commands.Authorize("portal-access", false, ct); var share = await Current(id, store, db, scope, ct);
            http.Response.Headers.ETag = $"\"{share.Revision}\"";
            var feedback = await Feedback(id, db, scope, 1, 50, ct);
            if (share.SourceKind == "quotes") return Results.Ok(new { share = PublicShare(share), quote = PublicQuote(await store.Get<Quote>(share.SourceId, ct)), document = (object?)null, feedback });
            var document = await db.Documents.SingleAsync(d => d.Id == share.SourceId, ct);
            return Results.Ok(new { share = PublicShare(share), quote = (object?)null,
                document = new { document.Id, document.Name, document.ContentType, document.Size, document.Sha256, document.SourceDigest }, feedback });
        }).WithTags("portal");

        api.MapGet("/portal/shares/{id:guid}/feedback", async (Guid id, HttpContext http, EntityStore store, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands, CancellationToken ct) =>
        {
            await commands.Authorize("portal-access", false, ct); _ = await Current(id, store, db, scope, ct); var (page, pageSize) = Page(http);
            return Results.Ok(await Feedback(id, db, scope, page, pageSize, ct));
        }).WithTags("portal");

        api.MapGet("/portal/shares/{id:guid}/download", async (Guid id, HttpContext http, EntityStore store, FirstBlickDbContext db, TenantScope scope, IObjectStore objects, CommandExecutor commands, CancellationToken ct) =>
        {
            await commands.Authorize("portal-access", false, ct); var share = await Current(id, store, db, scope, ct);
            Guard.Require(share.SourceKind == "documents", "unsupported_download", "This grant is not a document download.");
            var document = await db.Documents.SingleAsync(d => d.Id == share.SourceId, ct);
            var bytes = await DocumentEndpoints.ReadVerified(objects, document, ct);
            http.Response.Headers["X-Content-SHA256"] = document.Sha256;
            return Results.File(bytes, document.ContentType, document.Name, lastModified: document.CreatedAt);
        }).WithTags("portal");

        api.MapPost("/portal/shares/{id:guid}/accept", (Guid id, PortalAcceptRequest body, HttpContext http, EntityStore store, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands) =>
            commands.Execute(http, "portal-access", "portal.quote_accept", new { id, body }, async ct =>
            {
                var share = await Current(id, store, db, scope, ct);
                Guard.Require(share.SourceKind == "quotes" && share.AllowAcceptance, "forbidden", "This grant does not permit quote acceptance.");
                var quote = await store.Get<Quote>(share.SourceId, ct); CommandExecutor.Match(http, quote.Revision);
                var zone = await db.Tenants.Where(t => t.Id == scope.TenantId).Select(t => t.TimeZone).SingleAsync(ct);
                var now = DateTimeOffset.UtcNow; var businessDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(zone)).DateTime);
                CommercialWorkflow.AcceptQuote(quote, body.Digest, now, businessDate); await store.Save(quote, "portal.quote_accept", false, ct);
                return new { shareId = share.Id, quote = PublicQuote(quote) };
            }, async ct => { var share = await Current(id, store, db, scope, ct); Guard.Require(share.AllowAcceptance && share.SourceKind == "quotes" && share.SourceDigest == body.Digest, "forbidden", "The exact quote acceptance grant is no longer available."); })).WithTags("portal");

        api.MapPost("/portal/shares/{id:guid}/feedback", (Guid id, PortalFeedbackRequest body, HttpContext http, EntityStore store, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands) =>
            commands.Execute(http, "portal-access", "portal.feedback", new { id, body }, async ct =>
            {
                var share = await Current(id, store, db, scope, ct); CommandExecutor.Match(http, share.Revision);
                return PublicFeedback(await AddFeedback(share, body.Message, false, store, scope, ct));
            }, async ct => { _ = await Current(id, store, db, scope, ct); })).WithTags("portal");

        api.MapPost("/portal/grants/{id:guid}/reply", (Guid id, PortalFeedbackRequest body, HttpContext http, EntityStore store, TenantScope scope, CommandExecutor commands) =>
            commands.Execute(http, "portal-shares", "portal.reply", new { id, body }, async ct =>
            {
                var share = await store.Get<PortalShare>(id, ct); CommandExecutor.Match(http, share.Revision);
                Guard.Require(share.RevokedAt is null && share.ExpiresAt > DateTimeOffset.UtcNow, "invalid_transition", "Replies require an active grant.");
                return await AddFeedback(share, body.Message, true, store, scope, ct);
            })).WithTags("portal");

        api.MapGet("/portal/grants/{id:guid}/feedback", async (Guid id, HttpContext http, EntityStore store, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands, CancellationToken ct) =>
        {
            await commands.Authorize("portal-shares", false, ct); _ = await store.Get<PortalShare>(id, ct); var (page, pageSize) = Page(http);
            return Results.Ok(await Feedback(id, db, scope, page, pageSize, ct));
        }).WithTags("portal");
    }

    private static async Task<PortalShare> Current(Guid id, EntityStore store, FirstBlickDbContext db, TenantScope scope, CancellationToken ct)
    {
        await Enabled(db, scope, ct);
        Guard.Require(scope.Role == BusinessRole.Partner, "forbidden", "This view belongs to the personally identified external recipient.");
        var share = await store.Get<PortalShare>(id, ct);
        Guard.Require(!share.Archived && share.RecipientUserId == scope.UserId && share.RevokedAt is null && share.ExpiresAt > DateTimeOffset.UtcNow, "not_found", "This shared content is unavailable.");
        if (share.SourceKind == "quotes")
        {
            var quote = await store.Get<Quote>(share.SourceId, ct); quote.Validate();
            Guard.Require(!quote.Archived && quote.Presentation is not null && quote.IssuedDigest == share.SourceDigest && quote.Status is QuoteStatus.Issued or QuoteStatus.Accepted,
                "not_found", "The released quote version is unavailable.");
        }
        else
        {
            var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == share.SourceId, ct);
            Guard.Require(document is not null && document.State == "Ready" && document.Sha256 == share.SourceDigest && document.ProjectId == share.ProjectId
                && !string.IsNullOrEmpty(document.VersionId) && document.VersionId != "null", "not_found", "The released document version is unavailable.");
        }
        return share;
    }

    private static async Task Enabled(FirstBlickDbContext db, TenantScope scope, CancellationToken ct) =>
        Guard.Require(await db.Tenants.AnyAsync(t => t.Id == scope.TenantId && t.Modules.Contains("portal"), ct), "module_disabled", "External portal access is disabled for this business.");

    private static async Task<PortalFeedback> AddFeedback(PortalShare share, string message, bool fromBusiness, EntityStore store, TenantScope scope, CancellationToken ct)
    {
        var feedback = new PortalFeedback { TenantId = scope.TenantId, ShareId = share.Id, ProjectId = share.ProjectId, AuthorUserId = scope.UserId,
            FromBusiness = fromBusiness, Message = message, SourceDigest = share.SourceDigest };
        await store.Save(feedback, fromBusiness ? "portal.reply" : "portal.feedback", true, ct); return feedback;
    }

    private static async Task<object> Feedback(Guid shareId, FirstBlickDbContext db, TenantScope scope, int page, int pageSize, CancellationToken ct)
    {
        var share = shareId.ToString(); var tenant = scope.TenantId;
        var query = db.Database.SqlQuery<string>($"SELECT \"Data\"::text AS \"Value\" FROM \"Entities\" WHERE \"TenantId\"={tenant} AND \"Kind\"='portal-feedback' AND \"Data\"->>'shareId'={share} ORDER BY \"CreatedAt\" DESC, \"Id\"");
        var total = await query.CountAsync(ct); var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new { items = rows.Select(x => PublicFeedback(AppJson.Read<PortalFeedback>(x))), total, page, pageSize };
    }

    private static (int Page, int PageSize) Page(HttpContext http) =>
        (int.TryParse(http.Request.Query["page"], out var p) ? Math.Clamp(p, 1, 100_000) : 1,
         int.TryParse(http.Request.Query["pageSize"], out var s) ? Math.Clamp(s, 1, 100) : 30);

    private static object PublicShare(PortalShare share) => new { share.Id, share.Revision, share.Title, share.SourceKind, share.SourceId, share.ProjectId, share.SourceDigest, share.AllowAcceptance, share.ExpiresAt };
    private static object PublicFeedback(PortalFeedback entry) => new { entry.Id, entry.Message, entry.FromBusiness, entry.SourceDigest, entry.CreatedAt };
    private static object PublicQuote(Quote quote) => new
    {
        quote.Id, quote.Revision, quote.Number, quote.Title, quote.Version, quote.Status, quote.ValidUntil, quote.IssuedDigest, quote.Currency,
        presentation = new { quote.Presentation!.SellerName, quote.Presentation.BuyerName, quote.Presentation.BuyerAddress, quote.Presentation.IssuedAt },
        quote.Snapshot!.Net, quote.Snapshot.Tax, quote.Snapshot.Gross,
        lines = quote.Snapshot.Lines.Select(line => new { line.Inputs.Description, line.Inputs.Unit, line.Inputs.Quantity,
            line.UnitNet, line.Inputs.VatPercent, line.Net, line.Tax, line.Gross })
    };
}
