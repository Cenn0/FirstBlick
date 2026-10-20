using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Operations;

internal sealed record VersionMapping(Guid DocumentId, string SourceVersionId, string RestoredVersionId);
internal sealed record RestoreReport(DateTimeOffset SnapshotAt, DateTimeOffset RestoredAt, string Release, string Database, string Bucket,
    int VerifiedTables, long VerifiedRows, int VerifiedObjects, bool DataProtectionVerified, int QuarantinedJobs, int QuarantinedDeliveries,
    int InvalidatedUsers, int InvalidatedInvitations, int InvalidatedApprovals, double RestoreSeconds, string Activation, List<VersionMapping> Versions);

internal static class Restore
{
    public static async Task<RestoreReport> Run(Settings settings, string input, string database, string bucket, string reportPath)
    {
        var watch = Stopwatch.StartNew();
        // Verify every file and configuration precondition before creating any target resource.
        var manifest = await BundleFiles.Verify(input);
        Require.That(Regex.IsMatch(bucket, "^firstblick-restore-[a-z0-9][a-z0-9-]{1,42}$", RegexOptions.CultureInvariant), "Restore requires a new bucket named firstblick-restore-NAME.");
        Require.That(!Path.Exists(reportPath), "Restore report already exists; choose a new report path.");
        Require.That(!Path.GetFullPath(reportPath).StartsWith(Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Restore report must be outside the immutable backup directory.");
        Require.That(settings.Local || manifest.Release != "unversioned-local" && settings.Release == manifest.Release, "Production restore requires the exact FIRSTBLICK_RELEASE recorded in the backup.");
        using var certificate = manifest.Certificate is { } cert ? settings.Certificate(BundleFiles.SafePath(input, cert.File)) : settings.Certificate();
        if (manifest.Certificate is { } expected) Require.That(certificate?.Thumbprint == expected.Thumbprint, "Backup certificate thumbprint differs from the manifest.");
        await using (var connection = await PostgresFiles.Open(settings, "postgres"))
            Require.That(PostgresFiles.Major(await PostgresFiles.Scalar(connection, "SHOW server_version")) == manifest.DatabaseMajor, "Restore proof requires the same PostgreSQL major version; perform major upgrades separately after recovery.");
        Require.That(PostgresFiles.Major(await PostgresFiles.ToolVersion("pg_restore")) >= manifest.DatabaseMajor, "pg_restore must be at least as new as the backup PostgreSQL major version.");
        using var client = settings.S3();
        await StorageFiles.EnableVersioning(client, bucket, true);
        await PostgresFiles.CreateDatabase(settings, database);
        await PostgresFiles.Restore(settings, database, BundleFiles.SafePath(input, "database.dump"));
        var restoredSettings = settings with { ConnectionString = settings.Connection(database), Bucket = bucket };
        await using (var connection = await PostgresFiles.Open(restoredSettings))
        {
            var actual = await PostgresFiles.TableProofs(connection);
            Require.That(manifest.Tables.SequenceEqual(actual), "Restored table counts or row SHA-256 differ from the exported snapshot. Keep the isolated target disabled.");
        }
        await KeyProtection.VerifyProbe(restoredSettings, certificate, manifest.KeyProbe);
        var versions = new List<VersionMapping>();
        foreach (var obj in manifest.Objects)
            versions.Add(new(obj.DocumentId, obj.VersionId, await StorageFiles.Upload(client, bucket, obj, BundleFiles.SafePath(input, obj.File))));
        var quarantine = await ApplyRecoveryBarrier(restoredSettings, manifest, versions);
        watch.Stop();
        var report = new RestoreReport(manifest.SnapshotAt, DateTimeOffset.UtcNow, manifest.Release, database, bucket, manifest.Tables.Count,
            manifest.Tables.Sum(x => x.Rows), versions.Count, manifest.KeyProbe is not null, quarantine.Jobs, quarantine.Deliveries,
            quarantine.Users, quarantine.Invitations, quarantine.Approvals, watch.Elapsed.TotalSeconds, "isolated; reconcile external effects and post-snapshot writes before API/worker activation", versions);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await using (var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await JsonSerializer.SerializeAsync(output, report, BundleFiles.Json);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(reportPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return report;
    }

    private static async Task<(int Jobs, int Deliveries, int Users, int Invitations, int Approvals)> ApplyRecoveryBarrier(Settings settings, BackupManifest manifest, List<VersionMapping> versions)
    {
        await using var db = settings.Db(); await using var transaction = await db.Database.BeginTransactionAsync();
        var now = DateTimeOffset.UtcNow; var correlation = $"restore:{Guid.NewGuid():N}";
        var documents = await db.Documents.IgnoreQueryFilters().ToDictionaryAsync(x => x.Id);
        foreach (var mapping in versions)
        {
            var doc = documents[mapping.DocumentId]; var proof = manifest.Objects.Single(x => x.DocumentId == mapping.DocumentId);
            Require.That(doc.VersionId == mapping.SourceVersionId && doc.ObjectKey == proof.Key && doc.TenantId == proof.TenantId && doc.Sha256.Equals(proof.Sha256, StringComparison.OrdinalIgnoreCase), "Restored document reference differs from backup metadata.");
            doc.VersionId = mapping.RestoredVersionId; doc.Revision = checked(doc.Revision + 1);
            db.Audit.Add(new AuditEntry { TenantId = doc.TenantId, ActorId = Guid.Empty, Action = "operations.restore.version", Kind = "documents", EntityId = doc.Id,
                Revision = doc.Revision, ContentDigest = ContentDigest.Of(mapping), CorrelationId = correlation, At = now });
        }
        var jobs = await db.Jobs.IgnoreQueryFilters().Where(x => x.Type != "entity.changed" && (x.State == JobState.Pending || x.State == JobState.Leased)).ToListAsync();
        foreach (var job in jobs)
        {
            // Even Pending may already have reached a provider after the snapshot. Never infer "not sent" from a restored lease.
            job.State = JobState.Uncertain; job.LeaseToken = null; job.LeaseUntil = null; job.LastError = "restore_requires_reconciliation";
            db.Audit.Add(new AuditEntry { TenantId = job.TenantId, ActorId = Guid.Empty, Action = "operations.restore.quarantine", Kind = "jobs", EntityId = job.Id,
                Revision = job.EntityRevision, ContentDigest = ContentDigest.Of(new { job.Id, job.State, job.ExternalAttemptStarted }), CorrelationId = correlation, At = now });
        }
        var deliveryCount = 0;
        foreach (var row in await db.Entities.IgnoreQueryFilters().Where(x => x.Kind == "quotes").ToListAsync())
        {
            var quote = AppJson.Read<Quote>(row.Data); var attempts = quote.Deliveries.Where(x => x.State is DeliveryState.Queued or DeliveryState.Processing).ToList();
            if (attempts.Count == 0) continue;
            foreach (var attempt in attempts) { attempt.State = DeliveryState.Uncertain; attempt.ResolutionNote = "Restore requires reconciliation with provider evidence."; }
            quote.Revision = checked(quote.Revision + 1); quote.UpdatedAt = now; quote.Validate();
            row.Revision = quote.Revision; row.UpdatedAt = now; row.Data = AppJson.Serialize(quote); deliveryCount += attempts.Count;
            db.Audit.Add(new AuditEntry { TenantId = row.TenantId, ActorId = Guid.Empty, Action = "operations.restore.delivery", Kind = row.Kind, EntityId = row.Id,
                Revision = row.Revision, ContentDigest = ContentDigest.OfUtf8(row.Data), CorrelationId = correlation, At = now });
        }
        // A snapshot may predate a logout, password reset or invitation revocation. Require fresh sessions and fresh invitations.
        var users = await db.Users.ToListAsync(); foreach (var user in users) user.SecurityStamp = Guid.NewGuid().ToString();
        var invitations = await db.Invitations.Where(x => x.UsedAt == null).ToListAsync(); foreach (var invitation in invitations) invitation.UsedAt = now;
        var approvals = await db.Approvals.IgnoreQueryFilters().Where(x => x.ConsumedAt == null && x.ExpiresAt > now).ToListAsync();
        foreach (var approval in approvals) { approval.ExpiresAt = now.AddTicks(-1); approval.Revision = checked(approval.Revision + 1); }
        foreach (var tenant in await db.Tenants.ToListAsync()) db.Audit.Add(new AuditEntry { TenantId = tenant.Id, ActorId = Guid.Empty, Action = "operations.restore.authentication", Kind = "tenants", EntityId = tenant.Id,
            Revision = tenant.PolicyRevision, ContentDigest = ContentDigest.Of(new { invalidatedSessions = true, invalidatedInvitations = true, invalidatedApprovals = true, manifest.SnapshotAt }), CorrelationId = correlation, At = now });
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        return (jobs.Count, deliveryCount, users.Count, invitations.Count, approvals.Count);
    }
}
