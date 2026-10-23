using System.Security.Claims;
using System.Security.Cryptography;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record LoginRequest(string Email, string Password);
public sealed record ActivationRequest(string Email, string Token, string? Password, string DisplayName);
public sealed record RecoveryRequest(string Email);
public sealed record ResetRequest(string Email, string Token, string Password);

public static class AuthEndpoints
{
    public static void Map(WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth");
        auth.MapGet("/csrf", (HttpContext http, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            http.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
            { HttpOnly = false, SameSite = SameSiteMode.Strict, Secure = http.Request.IsHttps, Path = "/" });
            return Results.Ok(new { ready = true });
        });
        auth.MapPost("/login", async (LoginRequest body, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) =>
        {
            Guard.Email(body.Email, true); Guard.Text(body.Password, "Password", 1024);
            var user = await users.FindByEmailAsync(body.Email);
            if (user is null) return Results.Unauthorized();
            var result = await signIn.PasswordSignInAsync(user, body.Password, false, true);
            return result.Succeeded ? Results.Ok(new { authenticated = true }) : Results.Unauthorized();
        }).RequireRateLimiting("authentication");
        auth.MapPost("/logout", async (HttpContext http, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) =>
        {
            var user = await users.GetUserAsync(http.User);
            if (user is not null) await users.UpdateSecurityStampAsync(user);
            await signIn.SignOutAsync();
            http.Response.Cookies.Delete("XSRF-TOKEN");
            return Results.Ok(new { authenticated = false });
        }).RequireAuthorization();
        auth.MapGet("/me", async (HttpContext http, UserManager<ApplicationUser> users, FirstBlickDbContext db, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(http.User);
            if (user is null) return Results.Unauthorized();
            var canCreateBusiness = await Bootstrap.IsOperator(db, user.Id, ct);
            var memberships = await db.Memberships.AsNoTracking().Where(x => x.UserId == user.Id && x.Active).ToListAsync(ct);
            var ids = memberships.Select(x => x.TenantId).ToList();
            var tenants = await db.Tenants.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
            var heldTenants = new HashSet<Guid>();
            foreach (var tenant in tenants) if (await RecoveryBarrier.IsHeld(db, tenant.Id, ct)) heldTenants.Add(tenant.Id);
            var personalUserId = user.Id.ToString(); var personalTenants = ids.ToArray();
            var staffData = await db.Database.SqlQuery<string>($"SELECT \"Data\"::text AS \"Value\" FROM \"Entities\" WHERE \"TenantId\"=ANY({personalTenants}) AND \"Kind\"='employees' AND NOT \"Archived\" AND \"Data\"->>'userId'={personalUserId} AND \"Data\"->>'active'='true'").ToListAsync(ct);
            var staffByTenant = staffData.Select(AppJson.Read<Employee>).ToDictionary(x => x.TenantId, x => x.Id);
            var selected = Guid.TryParse(http.Request.Headers["X-Tenant-Id"], out var requested) && ids.Contains(requested) ? requested : ids.FirstOrDefault();
            return Results.Ok(new
            {
                user = new { user.Id, user.Email, user.DisplayName }, activeTenantId = selected, canCreateBusiness,
                tenants = tenants.Select(t =>
                {
                    var role = memberships.Single(m => m.TenantId == t.Id).Role;
                    var capabilities = BusinessRegistry.Entities.Keys.Append("documents").Append("portal-access").SelectMany(k =>
                        (AccessRules.CanRead(role, k) && (k != "portal-access" || t.Modules.Contains("portal")) ? new[] { $"{k}.read" } : Array.Empty<string>()).Concat(
                        AccessRules.CanWrite(role, k) && t.Modules.Contains(AccessRules.ModuleFor(k)) ? new[] { $"{k}.write" } : Array.Empty<string>())).ToList();
                    if (role == BusinessRole.ManagingDirector) capabilities.AddRange(["settings.manage", "members.manage", "approvals.manage"]);
                    var recoveryHold = heldTenants.Contains(t.Id); if (recoveryHold) capabilities.Clear();
                    return new { t.Id, t.Name, role, t.Modules, capabilities, recoveryHold, employeeId = staffByTenant.TryGetValue(t.Id, out var staffId) ? (Guid?)staffId : null };
                })
            });
        }).RequireAuthorization();
        auth.MapPost("/activate", Activate).RequireRateLimiting("authentication");
        auth.MapPost("/recover", Recover).RequireRateLimiting("authentication");
        auth.MapPost("/reset", async (ResetRequest body, UserManager<ApplicationUser> users) =>
        {
            Guard.Email(body.Email, true); Guard.Text(body.Token, "Token", 4096); Guard.Text(body.Password, "Password", 1024);
            var user = await users.FindByEmailAsync(body.Email);
            if (user is null) throw new DomainException("invalid_recovery", "The recovery token is invalid or expired.");
            var result = await users.ResetPasswordAsync(user, body.Token, body.Password);
            if (!result.Succeeded) throw new DomainException("invalid_recovery", "The recovery token is invalid, expired, or the password does not satisfy the password policy.");
            return Results.Ok(new { reset = true });
        }).RequireRateLimiting("authentication");
    }

    private static async Task<IResult> Activate(ActivationRequest body, HttpContext http, FirstBlickDbContext db, UserManager<ApplicationUser> users, CancellationToken ct)
    {
        Guard.Email(body.Email, true); Guard.Text(body.Token, "Token", 256); Guard.Text(body.DisplayName, "DisplayName", 160);
        var hash = ContentDigest.OfUtf8(body.Token);
        var invitation = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        Guard.Require(invitation is not null, "invalid_invitation", "The invitation is invalid or expired.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serialize first credential claims across different businesses for the same personal identity.
        var identityLock = "identity.activation:" + users.NormalizeEmail(body.Email);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({identityLock}, 0))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({invitation!.TenantId.ToString()}, 0))", ct);
        await RecoveryBarrier.EnsureAvailable(db, invitation.TenantId, ct);
        invitation = await db.Invitations.SingleAsync(x => x.Id == invitation.Id, ct);
        Guard.Require(invitation.UsedAt is null && invitation.ExpiresAt > DateTimeOffset.UtcNow && string.Equals(invitation.Email, body.Email, StringComparison.OrdinalIgnoreCase),
            "invalid_invitation", "The invitation is invalid or expired.");
        Guard.Require(await db.Memberships.AnyAsync(x => x.TenantId == invitation.TenantId && x.UserId == invitation.InvitedBy && x.Active && x.Role == BusinessRole.ManagingDirector, ct),
            "invalid_invitation", "The invitation has been revoked by a change of responsibility.");
        // Only a trusted operator's pending import marker permits first credential creation
        // on an unconfirmed identity without credentials or active memberships. Ordinary invitations cannot reset accounts.
        var user = await users.FindByEmailAsync(body.Email);
        CommandReceipt? migrationReceipt = null; MigrationAccountMarker? migration = null;
        if (user is not null)
        {
            var pending = await MigrationAccounts.FindPending(db, invitation.TenantId, user.Id, user.Email, ct);
            migration = pending?.Marker; migrationReceipt = pending?.Receipt;
        }
        if (user is not null)
        {
            var firstCredential = migration is not null && string.IsNullOrEmpty(user.PasswordHash) && !user.EmailConfirmed
                && !await db.Memberships.AnyAsync(x => x.UserId == user.Id && x.Active, ct);
            if (firstCredential)
            {
                Guard.Text(body.Password, "Password", 1024);
                var credential = await users.AddPasswordAsync(user, body.Password!);
                if (!credential.Succeeded) throw new DomainException("invalid_account", string.Join(" ", credential.Errors.Select(e => e.Description)));
                user.EmailConfirmed = true; user.DisplayName = body.DisplayName; user.LockoutEnd = null; user.AccessFailedCount = 0;
                var updated = await users.UpdateAsync(user);
                if (!updated.Succeeded) throw new DomainException("invalid_account", string.Join(" ", updated.Errors.Select(e => e.Description)));
            }
            else
            {
                Guard.Require(http.User.FindFirstValue(ClaimTypes.NameIdentifier) == user.Id.ToString(), "existing_account_login_required", "Sign in to your existing personal account before accepting this invitation.");
                Guard.Require(string.IsNullOrEmpty(body.Password), "invitation_not_recovery", "An invitation cannot change an existing account password.");
            }
        }
        else
        {
            Guard.Text(body.Password, "Password", 1024);
            user = new ApplicationUser { Id = Guid.NewGuid(), UserName = body.Email, Email = body.Email, EmailConfirmed = true, DisplayName = body.DisplayName };
            var result = await users.CreateAsync(user, body.Password!);
            if (!result.Succeeded) throw new DomainException("invalid_account", string.Join(" ", result.Errors.Select(e => e.Description)));
        }
        var previousMembership = await db.Memberships.SingleOrDefaultAsync(x => x.TenantId == invitation.TenantId && x.UserId == user.Id, ct);
        Guard.Require(previousMembership is null || (migration is not null && !previousMembership.Active), "membership_exists", "Membership already exists; use the explicit membership-management workflow.");
        if (previousMembership is null) db.Memberships.Add(new Membership { TenantId = invitation.TenantId, UserId = user.Id, Role = invitation.Role });
        else { previousMembership.Active = true; previousMembership.Role = invitation.Role; previousMembership.Revision++; }
        if (migration is not null)
        {
            migrationReceipt!.Response = AppJson.Serialize(migration with { State = "Claimed" });
            db.Audit.Add(new AuditEntry { TenantId = invitation.TenantId, ActorId = user.Id, Action = "migration.account_claimed", Kind = "identity", EntityId = user.Id,
                Revision = 1, ContentDigest = ContentDigest.Of(new { migration.BundleId, migration.SourceAccountId, invitationId = invitation.Id, invitation.Role, userId = user.Id }) });
        }
        invitation.UsedAt = DateTimeOffset.UtcNow;
        db.Audit.Add(new AuditEntry { TenantId = invitation.TenantId, ActorId = user.Id, Action = "invitation.redeemed", Kind = "membership", EntityId = invitation.Id, Revision = 1, ContentDigest = ContentDigest.Of(new { invitation.InvitedBy, user.Id, invitation.Role }) });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Results.Ok(new { activated = true });
    }

    private static async Task<IResult> Recover(RecoveryRequest body, UserManager<ApplicationUser> users, FirstBlickDbContext db, IDataProtectionProvider protection, CancellationToken ct)
    {
        Guard.Email(body.Email, true);
        var user = await users.FindByEmailAsync(body.Email);
        if (user is not null && user.EmailConfirmed)
        {
            // The limiter is per HTTP host/IP. Deduplicate this account across API hosts in PostgreSQL.
            // Membership and the cooldown are read again after obtaining the lock, before any token is generated.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var recoveryLock = $"identity.recovery:{user.Id}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({recoveryLock}, 0))", ct);
            Membership? membership = null;
            foreach (var candidate in await db.Memberships.AsNoTracking().Where(x => x.UserId == user.Id && x.Active).OrderBy(x => x.TenantId).ToListAsync(ct))
                if (!await RecoveryBarrier.IsHeld(db, candidate.TenantId, ct)) { membership = candidate; break; }
            var recent = await db.Jobs.IgnoreQueryFilters().AnyAsync(x => x.Type == "identity.recovery" && x.EntityId == user.Id && x.AvailableAt > DateTimeOffset.UtcNow.AddMinutes(-5), ct);
            if (membership is not null && !recent)
            {
                var token = await users.GeneratePasswordResetTokenAsync(user);
                var message = AppJson.Serialize(new { email = user.Email, token, userId = user.Id });
                db.Jobs.Add(new OutboxJob { TenantId = membership.TenantId, ActorId = Guid.Empty, EntityId = user.Id, Type = "identity.recovery", Kind = "identity",
                    Payload = AppJson.Serialize(new { protectedMessage = protection.CreateProtector("FirstBlick.Identity.Recovery.v1").Protect(message) }) });
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        return Results.Accepted(value: new { message = "If an active account exists, a recovery message has been queued through the configured email channel." });
    }

    internal static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}
