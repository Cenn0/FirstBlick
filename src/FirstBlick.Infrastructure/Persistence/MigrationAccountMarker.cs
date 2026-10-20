using FirstBlick.Core.Security;
using FirstBlick.Core.Business;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Infrastructure.Persistence;

/// <summary>Only operator imports create these receipts, using ActorId Guid.Empty.</summary>
public sealed record MigrationAccountMarker(Guid BundleId, string SourceAccountId, Guid UserId, string Email, string State,
    BusinessRole SourceRole, bool SourceActive)
{
    public static string Key(Guid userId) => $"migration.account:{userId:N}";
}

public sealed record PendingMigrationAccount(MigrationAccountMarker Marker, CommandReceipt Receipt);

public static class MigrationAccounts
{
    public static async Task<PendingMigrationAccount?> FindPending(FirstBlickDbContext db, Guid tenantId, Guid userId, string? email, CancellationToken ct)
    {
        var key = MigrationAccountMarker.Key(userId);
        var receipt = await db.Receipts.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ActorId == Guid.Empty && x.Key == key, ct);
        if (receipt is null) return null;
        var marker = AppJson.Read<MigrationAccountMarker>(receipt.Response);
        Guard.Require(marker.UserId == userId && marker.BundleId != Guid.Empty && !string.IsNullOrWhiteSpace(marker.SourceAccountId)
            && string.Equals(marker.Email, email, StringComparison.OrdinalIgnoreCase) && Enum.IsDefined(marker.SourceRole)
            && marker.State is "Pending" or "Claimed" && receipt.RequestDigest.Length == 64 && receipt.RequestDigest.All(char.IsAsciiHexDigit),
            "invalid_migration_claim", "The operator's account migration evidence is incomplete or does not match this identity.");
        return marker.State == "Pending" ? new(marker, receipt) : null;
    }
}
