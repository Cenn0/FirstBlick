using System.Text.Json;
using FirstBlick.Core.Business;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Infrastructure.Persistence;

public sealed record OperationsRecoveryHold(Guid OperationId, string State, DateTimeOffset SnapshotAt, string Release, string Reason,
    DateTimeOffset? ResolvedAt, string? EvidenceDigest)
{
    public const string Key = "operations.recovery-hold";
}

public static class RecoveryBarrier
{
    public static async Task<bool> IsHeld(FirstBlickDbContext db, Guid tenantId, CancellationToken ct = default)
    {
        var marker = await db.Receipts.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ActorId == Guid.Empty && x.Key == OperationsRecoveryHold.Key, ct);
        if (marker is null) return false;
        try
        {
            var hold = AppJson.Read<OperationsRecoveryHold>(marker.Response);
            // Unknown, incomplete or malformed operator markers fail closed.
            return hold.State != "Released" || hold.OperationId == Guid.Empty || hold.SnapshotAt == default || hold.SnapshotAt.Offset != TimeSpan.Zero
                || string.IsNullOrWhiteSpace(hold.Release) || string.IsNullOrWhiteSpace(hold.Reason)
                || hold.ResolvedAt is not { } resolved || resolved.Offset != TimeSpan.Zero || resolved < hold.SnapshotAt
                || hold.EvidenceDigest is not { Length: 64 } evidence || !evidence.All(char.IsAsciiHexDigit);
        }
        catch (Exception e) when (e is JsonException or DomainException or FormatException) { return true; }
    }

    public static async Task EnsureAvailable(FirstBlickDbContext db, Guid tenantId, CancellationToken ct = default) =>
        Guard.Require(!await IsHeld(db, tenantId, ct), "recovery_hold", "This business is held for recovery reconciliation. Its operator must verify current access, data and external effects before resuming work.");
}
