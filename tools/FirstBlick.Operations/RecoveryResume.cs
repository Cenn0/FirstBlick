using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Operations;

internal sealed class ReconciliationDocument
{
    public string Format { get; set; } = "firstblick.recovery-reconciliation.v1";
    public Guid TenantId { get; set; }
    public Guid OperationId { get; set; }
    public DateTimeOffset SnapshotAt { get; set; }
    public string Release { get; set; } = "";
    public DateTimeOffset ReviewedAt { get; set; }
    public string Reviewer { get; set; } = "";
    public List<ReconciliationCheck> Checks { get; set; } = [];
    public List<FileProof> EvidenceFiles { get; set; } = [];
}
internal sealed record ReconciliationCheck(string Area, string Conclusion, string EvidenceFile);

internal static class RecoveryResume
{
    public const string HoldKey = OperationsRecoveryHold.Key;
    public const string PreparedKey = "operations.recovery-prepared";
    private static readonly string[] RequiredAreas = ["accounts_and_memberships", "passwords_and_session_revocations", "invitations_and_portal_grants",
        "commercial_numbering_and_post_snapshot_writes", "external_messages_and_payments", "offline_devices_and_revisions"];

    public static async Task<object> Run(Settings settings, string input, string reportPath)
    {
        Require.That(!Path.Exists(reportPath), "Resume report already exists; choose a new report path.");
        Require.That(File.Exists(input) && new FileInfo(input).Length <= 1_048_576, "A bounded reconciliation document is required.");
        var document = JsonSerializer.Deserialize<ReconciliationDocument>(await File.ReadAllTextAsync(input), BundleFiles.Json) ?? throw new OperationsException("Reconciliation document is empty.");
        Require.That(document.Format == "firstblick.recovery-reconciliation.v1" && document.TenantId != Guid.Empty && document.OperationId != Guid.Empty, "Invalid reconciliation format or identifiers.");
        Require.That(document.Reviewer.Length is >= 3 and <= 240 && document.ReviewedAt.Offset == TimeSpan.Zero && document.ReviewedAt >= document.SnapshotAt && document.ReviewedAt <= DateTimeOffset.UtcNow.AddMinutes(5), "Concrete reviewer and valid UTC review time are required.");
        Require.That(document.Checks.Count == RequiredAreas.Length && document.Checks.Select(x => x.Area).Order(StringComparer.Ordinal).SequenceEqual(RequiredAreas.Order(StringComparer.Ordinal)), "Every required recovery area must have one explicit review record.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(input))!;
        foreach (var check in document.Checks)
        {
            Require.That(check.Conclusion.Length is >= 20 and <= 4000 && document.EvidenceFiles.Count(x => x.File == check.EvidenceFile) == 1, "Every recovery area requires a concrete conclusion and one hashed evidence file.");
            var proof = document.EvidenceFiles.Single(x => x.File == check.EvidenceFile); var path = BundleFiles.SafePath(directory, proof.File);
            Require.That(File.Exists(path) && new FileInfo(path).Length == proof.Size && await BundleFiles.Hash(path) == proof.Sha256, "Reconciliation evidence checksum failed.");
        }
        var evidenceDigest = await BundleFiles.Hash(input);
        await using var db = settings.Db(); await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({document.TenantId.ToString()}, 0))");
        var receipt = await db.Receipts.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TenantId == document.TenantId && x.ActorId == Guid.Empty && x.Key == HoldKey)
            ?? throw new OperationsException("Tenant has no recovery hold.");
        var hold = AppJson.Read<OperationsRecoveryHold>(receipt.Response);
        Require.That(hold.OperationId == document.OperationId && hold.SnapshotAt == document.SnapshotAt && hold.Release == document.Release, "Review does not belong to this exact recovery operation and release.");
        Require.That(settings.Local || settings.Release == hold.Release, "Resume requires the reviewed application release.");
        var prepared = await db.Receipts.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TenantId == document.TenantId && x.ActorId == Guid.Empty && x.Key == PreparedKey);
        Require.That(prepared is not null && prepared.RequestDigest == hold.OperationId.ToString("N"), "Recovery preparation did not complete; object/account verification cannot be bypassed by a resume review.");
        Require.That(hold.State == "Held" || hold.State == "Released" && hold.EvidenceDigest == evidenceDigest, "Hold was already released using different evidence.");
        if (hold.State == "Held")
        {
            hold = hold with { State = "Released", ResolvedAt = DateTimeOffset.UtcNow, EvidenceDigest = evidenceDigest };
            receipt.Response = AppJson.Serialize(hold);
            db.Audit.Add(new AuditEntry { TenantId = document.TenantId, ActorId = Guid.Empty, Action = "operations.recovery.resume", Kind = "tenants", EntityId = document.TenantId,
                Revision = 1, ContentDigest = evidenceDigest, CorrelationId = document.OperationId.ToString("N") });
            await db.SaveChangesAsync();
        }
        await transaction.CommitAsync();
        var result = new { tenantId = document.TenantId, operationId = hold.OperationId, state = hold.State, hold.ResolvedAt, hold.EvidenceDigest,
            document.Reviewer, review = "operator evidence accepted; external facts are not automatically verified; existing Uncertain jobs remain quarantined" };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(result, BundleFiles.Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(reportPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return result;
    }
}
