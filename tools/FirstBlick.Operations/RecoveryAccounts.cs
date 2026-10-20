using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Operations;

internal static class RecoveryAccounts
{
    public static void ValidateOperator(Settings settings)
    {
        Guard.Email(settings.OperatorEmail ?? "", true); Guard.Text(settings.OperatorDisplayName, "Operator display name", 160);
        var password = settings.OperatorPassword ?? "";
        Require.That(password.Length is >= 14 and <= 1024 && password.Any(char.IsUpper) && password.Any(char.IsLower) && password.Any(char.IsAsciiDigit) && password.Any(x => !char.IsLetterOrDigit(x)),
            "Provide Operations__OperatorEmail and a new Operations__OperatorPassword (at least 14 characters with upper/lowercase, digit and symbol). No legacy account is implicitly reactivated.");
    }
    public static async Task<int> QuarantineExisting(FirstBlickDbContext db, Guid operationId, string bundleDigest)
    {
        var users = await db.Users.ToDictionaryAsync(x => x.Id);
        foreach (var membership in await db.Memberships.ToListAsync())
        {
            var user = users[membership.UserId]; Guard.Email(user.Email ?? "", true);
            var key = MigrationAccountMarker.Key(user.Id);
            var receipt = await db.Receipts.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TenantId == membership.TenantId && x.ActorId == Guid.Empty && x.Key == key);
            if (receipt is null) { receipt = new CommandReceipt { TenantId = membership.TenantId, ActorId = Guid.Empty, Key = key }; db.Receipts.Add(receipt); }
            receipt.RequestDigest = bundleDigest;
            receipt.Response = AppJson.Serialize(new MigrationAccountMarker(operationId, user.Id.ToString(), user.Id, user.Email!, "Pending", membership.Role, membership.Active));
            membership.Active = false; membership.Revision = checked(membership.Revision + 1);
        }
        foreach (var user in users.Values)
        {
            user.PasswordHash = null; user.EmailConfirmed = false; user.SecurityStamp = Guid.NewGuid().ToString();
            user.PhoneNumberConfirmed = false; user.TwoFactorEnabled = false; user.ConcurrencyStamp = Guid.NewGuid().ToString();
        }
        // Old external-login links, authenticator keys and recovery codes must not bypass the fresh invitation path.
        db.UserLogins.RemoveRange(await db.UserLogins.ToListAsync()); db.UserTokens.RemoveRange(await db.UserTokens.ToListAsync());
        db.UserRoles.RemoveRange(await db.UserRoles.ToListAsync()); db.UserClaims.RemoveRange(await db.UserClaims.ToListAsync());
        return users.Count;
    }
    public static async Task<Guid> ProvisionOperator(FirstBlickDbContext db, Settings settings, IEnumerable<Guid> tenantIds, Guid operationId)
    {
        ValidateOperator(settings); var email = settings.OperatorEmail!; var normalized = email.ToUpperInvariant();
        Require.That(!await db.Users.AnyAsync(x => x.NormalizedEmail == normalized || x.Email != null && x.Email.ToUpper() == normalized)
            && !db.Users.Local.Any(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase)), "The recovery operator must use a new email address, distinct from every imported/restored identity.");
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, NormalizedUserName = normalized, Email = email, NormalizedEmail = normalized,
            EmailConfirmed = true, DisplayName = settings.OperatorDisplayName, SecurityStamp = Guid.NewGuid().ToString(), ConcurrencyStamp = Guid.NewGuid().ToString() };
        user.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(user, settings.OperatorPassword!); db.Users.Add(user);
        foreach (var tenantId in tenantIds)
        {
            db.Memberships.Add(new Membership { TenantId = tenantId, UserId = user.Id, Role = BusinessRole.ManagingDirector, Active = true });
            db.Audit.Add(new AuditEntry { TenantId = tenantId, ActorId = Guid.Empty, Action = "operations.operator.provisioned", Kind = "membership", EntityId = user.Id,
                Revision = 1, ContentDigest = ContentDigest.Of(new { operationId, user.Id, user.Email }), CorrelationId = operationId.ToString("N") });
        }
        return user.Id;
    }
}
