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
            var memberships = await db.Memberships.AsNoTracking().Where(x => x.UserId == user.Id && x.Active).ToListAsync(ct);
            var ids = memberships.Select(x => x.TenantId).ToList();
            var tenants = await db.Tenants.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
            var selected = Guid.TryParse(http.Request.Headers["X-Tenant-Id"], out var requested) && ids.Contains(requested) ? requested : ids.FirstOrDefault();
            return Results.Ok(new
            {
                user = new { user.Id, user.Email, user.DisplayName }, activeTenantId = selected,
                tenants = tenants.Select(t =>
                {
                    var role = memberships.Single(m => m.TenantId == t.Id).Role;
                    var capabilities = BusinessRegistry.Entities.Keys.Append("documents").SelectMany(k =>
                        (AccessRules.CanRead(role, k) ? new[] { $"{k}.read" } : Array.Empty<string>()).Concat(
                        AccessRules.CanWrite(role, k) && t.Modules.Contains(AccessRules.ModuleFor(k)) ? new[] { $"{k}.write" } : Array.Empty<string>())).ToList();
                    if (role == BusinessRole.ManagingDirector) capabilities.AddRange(["settings.manage", "members.manage", "approvals.manage"]);
                    return new { t.Id, t.Name, role, t.Modules, capabilities };
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
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({invitation!.TenantId.ToString()}, 0))", ct);
        invitation = await db.Invitations.SingleAsync(x => x.Id == invitation.Id, ct);
        Guard.Require(invitation.UsedAt is null && invitation.ExpiresAt > DateTimeOffset.UtcNow && string.Equals(invitation.Email, body.Email, StringComparison.OrdinalIgnoreCase),
            "invalid_invitation", "The invitation is invalid or expired.");
        // An invitation cannot restore passwords, change an existing person's identity, or silently overwrite membership.
        var user = await users.FindByEmailAsync(body.Email);
        if (user is not null)
        {
            Guard.Require(http.User.FindFirstValue(ClaimTypes.NameIdentifier) == user.Id.ToString(), "existing_account_login_required", "Sign in to your existing personal account before accepting this invitation.");
            Guard.Require(string.IsNullOrEmpty(body.Password), "invitation_not_recovery", "An invitation cannot change an existing account password.");
        }
        else
        {
            Guard.Text(body.Password, "Password", 1024);
            user = new ApplicationUser { Id = Guid.NewGuid(), UserName = body.Email, Email = body.Email, EmailConfirmed = true, DisplayName = body.DisplayName };
            var result = await users.CreateAsync(user, body.Password!);
            if (!result.Succeeded) throw new DomainException("invalid_account", string.Join(" ", result.Errors.Select(e => e.Description)));
        }
        Guard.Require(!await db.Memberships.AnyAsync(x => x.TenantId == invitation.TenantId && x.UserId == user.Id, ct), "membership_exists", "Membership already exists; use the explicit membership-management workflow.");
        // The inviter must still be a director when the token is redeemed.
        Guard.Require(await db.Memberships.AnyAsync(x => x.TenantId == invitation.TenantId && x.UserId == invitation.InvitedBy && x.Active && x.Role == BusinessRole.ManagingDirector, ct),
            "invalid_invitation", "The invitation has been revoked by a change of responsibility.");
        db.Memberships.Add(new Membership { TenantId = invitation.TenantId, UserId = user.Id, Role = invitation.Role });
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
            var membership = await db.Memberships.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == user.Id && x.Active, ct);
            var recent = await db.Jobs.IgnoreQueryFilters().AnyAsync(x => x.Type == "identity.recovery" && x.EntityId == user.Id && x.AvailableAt > DateTimeOffset.UtcNow.AddMinutes(-5), ct);
            if (membership is not null && !recent)
            {
                var token = await users.GeneratePasswordResetTokenAsync(user);
                var message = AppJson.Serialize(new { email = user.Email, token, userId = user.Id });
                db.Jobs.Add(new OutboxJob { TenantId = membership.TenantId, ActorId = Guid.Empty, EntityId = user.Id, Type = "identity.recovery", Kind = "identity",
                    Payload = AppJson.Serialize(new { protectedMessage = protection.CreateProtector("FirstBlick.Identity.Recovery.v1").Protect(message) }) });
                await db.SaveChangesAsync(ct);
            }
        }
        return Results.Accepted(value: new { message = "If an active account exists, a recovery message has been queued through the configured email channel." });
    }

    internal static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}
