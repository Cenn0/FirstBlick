using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record SettingsRequest(string Name, string TimeZone, string[] Modules, string[] ApprovalActions, long PolicyRevision);
public sealed record BusinessCreationRequest(string Name, string TimeZone);
public sealed record MembershipRequest(BusinessRole Role, bool Active, long Revision);
public sealed record InvitationRequest(string Email, BusinessRole Role);
public sealed record ApprovalBody(string Action, string Kind, Guid EntityId, long Revision, JsonElement? Payload);
public sealed record JobResolution(bool Delivered, string Evidence, string ProviderReference);

public static class ManagementEndpoints
{
    private static void Director(TenantScope scope) => Guard.Require(scope.Role == BusinessRole.ManagingDirector, "forbidden", "This action requires a managing director.");
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/businesses", (BusinessCreationRequest body, HttpContext http, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands) =>
            commands.Execute(http, "settings", "business.create", body, async ct =>
            {
                Director(scope);
                Guard.Text(body.Name, "Name", 200);
                Guard.Text(body.TimeZone, "TimeZone", 100);
                Guard.Require(TimeZoneInfo.TryFindSystemTimeZoneById(body.TimeZone, out _), "invalid_timezone", "The timezone is unknown.");
                // An account can be a director in several businesses. Serialize its creation
                // requests even when they originate from different existing businesses.
                var ownerLock = "business.create:" + scope.UserId;
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({ownerLock}, 0))", ct);
                Guard.Require(await db.Memberships.CountAsync(x => x.UserId == scope.UserId && x.Active && x.Role == BusinessRole.ManagingDirector, ct) < 20,
                    "business_limit", "This account already manages the maximum number of businesses.");
                var tenant = new BusinessTenant { Name = body.Name.Trim(), TimeZone = body.TimeZone };
                db.Tenants.Add(tenant);
                db.Memberships.Add(new Membership { TenantId = tenant.Id, UserId = scope.UserId, Role = BusinessRole.ManagingDirector });
                db.Audit.Add(new AuditEntry { TenantId = tenant.Id, ActorId = scope.UserId, Action = "business.created", Kind = "settings", EntityId = tenant.Id,
                    Revision = 1, ContentDigest = ContentDigest.Of(new { tenant.Name, tenant.TimeZone }) });
                Audit(db, scope, "business.create", "settings", scope.TenantId, 1, new { tenant.Id });
                return new { tenant.Id, tenant.Name, tenant.TimeZone, tenant.Modules };
            }));
        api.MapGet("/settings", async (FirstBlickDbContext db, TenantScope scope, CancellationToken ct) => Results.Ok(await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == scope.TenantId, ct)));
        api.MapPut("/settings", (SettingsRequest body, HttpContext http, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands, ApprovalService approvals) =>
            commands.Execute(http, "settings", "settings.change", body, async ct =>
            {
                Director(scope); var tenant = await db.Tenants.SingleAsync(x => x.Id == scope.TenantId, ct); CommandExecutor.Match(http, tenant.PolicyRevision);
                Guard.Text(body.Name, "Name", 200); Guard.Text(body.TimeZone, "TimeZone", 100);
                Guard.Require(TimeZoneInfo.TryFindSystemTimeZoneById(body.TimeZone, out _), "invalid_timezone", "The timezone is unknown.");
                Guard.Items(body.Modules, "Modules", 20, 1); Guard.Items(body.ApprovalActions, "ApprovalActions", 20);
                AccessRules.ValidateModules(body.Modules);
                Guard.Require(body.ApprovalActions.All(ApprovalService.Actions.Contains), "invalid_approval", "An approval action is unknown.");
                await approvals.Require(http, "settings.change", "settings", tenant.Id, tenant.PolicyRevision, AppJson.Serialize(body), ct);
                // A timezone change also has to preserve scheduling invariants; dates cannot silently move between business days.
                Guard.Require(tenant.TimeZone == body.TimeZone || !await db.Entities.AnyAsync(x => x.Kind == "schedule-assignments" && !x.Archived, ct),
                    "timezone_in_use", "Existing schedule assignments require a reviewed timezone migration.");
                tenant.Name = body.Name; tenant.TimeZone = body.TimeZone; tenant.Modules = body.Modules.Distinct().ToArray(); tenant.ApprovalActions = body.ApprovalActions.Distinct().ToArray(); tenant.PolicyRevision++;
                Audit(db, scope, "settings.change", "settings", tenant.Id, tenant.PolicyRevision, body); return tenant;
            }));
        api.MapGet("/members", async (FirstBlickDbContext db, TenantScope scope, CancellationToken ct) =>
        {
            Director(scope);
            return Results.Ok(await (from m in db.Memberships.AsNoTracking() join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                where m.TenantId == scope.TenantId select new { m.UserId, u.Email, u.DisplayName, m.Role, m.Active, m.Revision }).ToListAsync(ct));
        });
        api.MapPut("/members/{id:guid}", (Guid id, MembershipRequest body, HttpContext http, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands, ApprovalService approvals) =>
            commands.Execute(http, "memberships", "membership.change", new { id, body }, async ct =>
            {
                Director(scope); Guard.Require(Enum.IsDefined(body.Role), "invalid_role", "The role is unknown.");
                var member = await db.Memberships.SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.UserId == id, ct) ?? throw new DomainException("not_found", "The membership is unavailable.");
                CommandExecutor.Match(http, member.Revision);
                if (body.Active && !member.Active)
                {
                    var account = await db.Users.SingleAsync(x => x.Id == id, ct);
                    Guard.Require(await MigrationAccounts.FindPending(db, scope.TenantId, id, account.Email, ct) is null,
                        "migration_activation_required", "Send a fresh personal invitation to activate an imported or restored account with reviewed permissions.");
                }
                if (member.Active && member.Role == BusinessRole.ManagingDirector && (!body.Active || body.Role != BusinessRole.ManagingDirector))
                    Guard.Require(await db.Memberships.CountAsync(x => x.TenantId == scope.TenantId && x.Active && x.Role == BusinessRole.ManagingDirector, ct) > 1,
                        "last_director", "The last managing director cannot be removed or downgraded.");
                await approvals.Require(http, "membership.change", "memberships", id, member.Revision, AppJson.Serialize(body), ct);
                member.Role = body.Role; member.Active = body.Active; member.Revision++;
                Audit(db, scope, "membership.change", "memberships", id, member.Revision, body); return member;
            }));
        api.MapPost("/members/invitations", (InvitationRequest body, HttpContext http, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands, ApprovalService approvals) =>
            commands.Execute(http, "memberships", "membership.invite", body, async ct =>
            {
                Director(scope); Guard.Email(body.Email, true); Guard.Require(Enum.IsDefined(body.Role), "invalid_role", "The role is unknown.");
                var tenant = await db.Tenants.SingleAsync(x => x.Id == scope.TenantId, ct);
                Guard.Require(body.Role != BusinessRole.Partner || tenant.Modules.Contains("portal"), "module_disabled", "Enable the portal before inviting a partner. A partner receives no content until individually granted access.");
                await approvals.Require(http, "membership.invite", "settings", tenant.Id, tenant.PolicyRevision, AppJson.Serialize(body), ct);
                var normalized = body.Email.ToUpperInvariant();
                var user = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == normalized, ct);
                if (user is not null)
                {
                    var member = await db.Memberships.SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.UserId == user.Id, ct);
                    Guard.Require(member is null || (!member.Active && await MigrationAccounts.FindPending(db, scope.TenantId, user.Id, user.Email, ct) is not null),
                        "membership_exists", "Use membership management for an existing member. Pending migrated accounts require a fresh invitation.");
                }
                var token = AuthEndpoints.NewToken();
                var invitation = new Invitation { TenantId = scope.TenantId, InvitedBy = scope.UserId, Email = body.Email, Role = body.Role, TokenHash = ContentDigest.OfUtf8(token), ExpiresAt = DateTimeOffset.UtcNow.AddDays(2) };
                db.Invitations.Add(invitation); Audit(db, scope, "membership.invite", "invitation", invitation.Id, 1, new { body.Email, body.Role });
                return new { invitation.Id, invitation.Email, invitation.ExpiresAt, token };
            }));
        api.MapGet("/approvals", async (FirstBlickDbContext db, TenantScope scope, CancellationToken ct) =>
        { Director(scope); return Results.Ok(await db.Approvals.AsNoTracking().OrderByDescending(x => x.ExpiresAt).Take(100).ToListAsync(ct)); });
        api.MapPost("/approvals", (ApprovalBody body, HttpContext http, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands, ApprovalService approvals) =>
            commands.Execute(http, "approvals", "approval.request", body, async ct =>
            {
                Director(scope); Guard.Require(ApprovalService.Actions.Contains(body.Action), "invalid_approval", "The action is unsupported.");
                Guard.Require((body.Action, body.Kind) is ("invoice.issue", "invoices") or ("purchase.issue", "purchase-orders") or ("supplier-invoice.approve", "supplier-invoices") or ("settings.change", "settings") or ("membership.change", "memberships") or ("membership.invite", "settings"), "invalid_approval", "Action and target do not match.");
                var proposed = body.Payload?.GetRawText() ?? "{}";
                Guard.Require(proposed.Length <= 20_000, "payload_too_large", "The approval proposal exceeds its budget.");
                var fingerprint = await approvals.Fingerprint(body.Kind, body.EntityId, proposed, ct);
                Guard.Require(body.Revision == fingerprint.Revision, "revision_conflict", "The approval target changed.");
                var tenant = await db.Tenants.SingleAsync(x => x.Id == scope.TenantId, ct);
                var request = new ApprovalRequest { TenantId = scope.TenantId, RequestedBy = scope.UserId, Action = body.Action, Kind = body.Kind,
                    EntityId = body.EntityId, EntityRevision = body.Revision, PolicyRevision = tenant.PolicyRevision, ContentDigest = fingerprint.Digest,
                    ProposedData = ApprovalService.Canonical(proposed), ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) };
                db.Approvals.Add(request); Audit(db, scope, "approval.request", body.Kind, request.Id, request.Revision, new { request.ContentDigest }); return request;
            }));
        api.MapPost("/approvals/{id:guid}/approve", (Guid id, HttpContext http, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands, ApprovalService approvals) =>
            commands.Execute(http, "approvals", "approval.approve", new { id }, async ct =>
            {
                Director(scope); var request = await db.Approvals.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new DomainException("not_found", "The approval is unavailable.");
                CommandExecutor.Match(http, request.Revision);
                Guard.Require(request.RequestedBy != scope.UserId, "self_approval", "A different managing director must approve this request.");
                var tenant = await db.Tenants.SingleAsync(x => x.Id == scope.TenantId, ct);
                var fingerprint = await approvals.Fingerprint(request.Kind, request.EntityId, request.ProposedData, ct);
                Guard.Require(request.ApprovedAt is null && request.ConsumedAt is null && request.ExpiresAt > DateTimeOffset.UtcNow && request.PolicyRevision == tenant.PolicyRevision && request.EntityRevision == fingerprint.Revision && request.ContentDigest == fingerprint.Digest,
                    "approval_invalid", "The request is expired, already decided or no longer matches the proposed operation.");
                Guard.Require(await db.Memberships.AnyAsync(x => x.TenantId == scope.TenantId && x.UserId == request.RequestedBy && x.Active && x.Role == BusinessRole.ManagingDirector, ct), "approval_invalid", "The requester is no longer a managing director.");
                request.ApprovedBy = scope.UserId; request.ApprovedAt = DateTimeOffset.UtcNow; request.Revision++;
                Audit(db, scope, "approval.approve", request.Kind, request.Id, request.Revision, new { request.ContentDigest }); return request;
            }));
        api.MapGet("/audit", async (FirstBlickDbContext db, TenantScope scope, CancellationToken ct) =>
        { Director(scope); return Results.Ok(await db.Audit.AsNoTracking().OrderByDescending(x => x.Id).Take(200).ToListAsync(ct)); });
        api.MapGet("/jobs", async (FirstBlickDbContext db, TenantScope scope, CancellationToken ct) =>
        {
            Director(scope); return Results.Ok(await db.Jobs.AsNoTracking().OrderByDescending(x => x.AvailableAt).Take(100)
                .Select(x => new { x.Id, x.Type, x.Kind, x.EntityId, x.State, x.Attempts, x.LastError, x.ProviderReference, x.LeaseUntil, x.AvailableAt, x.CompletedAt }).ToListAsync(ct));
        });
        api.MapPost("/jobs/{id:guid}/resolve", (Guid id, JobResolution body, HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, CommandExecutor commands) =>
            commands.Execute(http, "jobs", "job.resolve", new { id, body }, async ct =>
            {
                Director(scope); Guard.Text(body.Evidence, "Evidence", 2000); Guard.Text(body.ProviderReference, "ProviderReference", 300, false);
                var job = await db.Jobs.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new DomainException("not_found", "The job is unavailable.");
                Guard.Require(job.State == JobState.Uncertain, "invalid_transition", "Only an uncertain external attempt can be reconciled.");
                if (job.Type == "quote.delivery")
                {
                    var quote = await store.Get<Quote>(job.EntityId, ct);
                    using var payload = JsonDocument.Parse(job.Payload);
                    var attempt = quote.Deliveries.Single(x => x.Id == payload.RootElement.GetProperty("deliveryId").GetGuid());
                    if (attempt.State == DeliveryState.Processing) CommercialWorkflow.MarkDeliveryUncertain(attempt);
                    CommercialWorkflow.ResolveDelivery(attempt, body.Delivered, body.Evidence, body.ProviderReference, DateTimeOffset.UtcNow);
                    await store.Save(quote, "delivery.reconciled", false, ct);
                }
                job.State = body.Delivered ? JobState.Completed : JobState.Failed; job.ProviderReference = body.ProviderReference;
                job.LastError = body.Delivered ? "" : "confirmed_not_delivered"; job.LeaseToken = null; job.LeaseUntil = null; job.CompletedAt = DateTimeOffset.UtcNow;
                Audit(db, scope, "job.resolve", job.Kind, job.Id, 1, new { body.Delivered, body.Evidence, body.ProviderReference });
                return new { job.Id, job.State };
            }));
    }
    internal static void Audit(FirstBlickDbContext db, TenantScope scope, string action, string kind, Guid id, long revision, object value) =>
        db.Audit.Add(new AuditEntry { TenantId = scope.TenantId, ActorId = scope.UserId, Action = action, Kind = kind, EntityId = id,
            Revision = revision, ContentDigest = ContentDigest.Of(value), CorrelationId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? "" });
}
