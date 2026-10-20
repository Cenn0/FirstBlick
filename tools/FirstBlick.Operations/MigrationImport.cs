using System.Text.Json;
using System.Text.RegularExpressions;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FirstBlick.Operations;

internal sealed record MigrationImportMarker(Guid BundleId, string BundleSha256, DateTimeOffset ImportedAt, DateTimeOffset ExportedAt, string Release,
    string SourceSystem, string SourceVersion, string Database, string Bucket, Guid OperatorUserId, string State);
internal sealed record MigrationObjectMapping(Guid DocumentId, string SourceStore, string SourceKey, string? SourceVersionId,
    string TargetKey, string TargetVersionId, string Sha256, long Size);
internal sealed record MigrationImportReport(Guid BundleId, string BundleSha256, string State, string Database, string Bucket,
    Guid OperatorUserId, DateTimeOffset ImportedAt, bool NoLegacyJobs, bool LegacyCredentialsDisabled, bool RecoveryHeld, bool RepeatedWithoutWrites,
    int Entities, int Documents, int HistoricalRecords, List<MigrationControl> VerifiedControls, List<MigrationObjectMapping> ObjectMappings);

internal static class MigrationImport
{
    public static string MarkerKey(Guid bundleId) => $"migration.bundle:{bundleId:N}";
    public static string EntityKey(string kind, Guid id) => $"migration.source:{kind}:{id:N}";
    public static string DocumentKey(Guid id) => $"migration.document:{id:N}";
    public static string HistoryKey(Guid id) => $"migration.history:{id:N}";
    public static string TargetKey(DocumentObject document) => $"{document.TenantId:N}/{document.Id:N}/migration-{document.Sha256[..16]}{Path.GetExtension(document.Name).ToLowerInvariant()}";

    public static async Task<MigrationImportReport> Run(Settings settings, string input, string database, string bucket, string reportPath)
    {
        Require.That(!Path.Exists(reportPath), "Import report already exists; choose a new path.");
        Require.That(!Path.GetFullPath(reportPath).StartsWith(Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Import reports must remain outside the immutable source bundle.");
        var check = await MigrationCheck.Load(input); var bundle = check.Bundle;
        Require.That(check.Report.State == "Compatible", "Migration is blocked. Run migration-check and resolve its concrete report before creating a target.");
        Require.That(Regex.IsMatch(database, "^firstblick_restore_[a-z0-9_]{1,42}$", RegexOptions.CultureInvariant)
            && Regex.IsMatch(bucket, "^firstblick-restore-[a-z0-9][a-z0-9-]{1,42}$", RegexOptions.CultureInvariant), "Migration only creates isolated firstblick_restore_* databases and firstblick-restore-* buckets.");
        Require.That(settings.Local || settings.Release == bundle.TargetRelease && settings.Release != "unversioned-local", "Migration requires the exact reviewed target FIRSTBLICK_RELEASE.");
        var target = settings with { ConnectionString = settings.Connection(database), Bucket = bucket };
        if (await DatabaseExists(settings, database))
        {
            var previous = await Marker(target, check);
            Require.That(previous.State == "Prepared", "An existing partial import cannot be overwritten. Investigate its held target and use new target names for a new attempt.");
            var comparison = await MigrationCompare.Compare(target, check, previous, true);
            Require.That(comparison.State == "Match", "Existing target differs from this source bundle. No import retry writes were made; run migration-compare for details.");
            var repeated = Report(previous, check, comparison.ObjectMappings, true);
            await MigrationJson.WriteReport(reportPath, repeated); return repeated;
        }
        RecoveryAccounts.ValidateOperator(settings);
        Require.That(!bundle.Accounts.Any(x => string.Equals(x.Email, settings.OperatorEmail, StringComparison.OrdinalIgnoreCase)), "Operator email must be distinct from all source identities.");
        using var certificate = settings.Certificate(); // Validate the new target's encryption configuration; legacy keys/tokens are never imported.
        using var client = settings.S3();
        await StorageFiles.EnableVersioning(client, bucket, true);
        await PostgresFiles.CreateDatabase(settings, database);
        await using (var db = target.Db()) await db.Database.MigrateAsync();
        var importedAt = MicrosecondNow();
        var marker = await InsertHeld(target, check, database, bucket, importedAt);
        var mappings = new List<MigrationObjectMapping>();
        foreach (var item in bundle.Documents)
        {
            var proof = new ObjectProof(item.Data.Id, item.Data.TenantId, TargetKey(item.Data), item.SourceVersionId ?? "", item.File.Size, item.File.Sha256,
                item.Data.ContentType, item.Data.State, item.File.File);
            var version = await StorageFiles.Upload(client, bucket, proof, BundleFiles.SafePath(input, item.File.File));
            mappings.Add(new(item.Data.Id, item.SourceStore, item.Data.ObjectKey, item.SourceVersionId, proof.Key, version, item.File.Sha256, item.File.Size));
        }
        await InsertDocuments(target, check, mappings);
        Require.That(await BundleFiles.Hash(BundleFiles.SafePath(input, "migration.json")) == check.Sha256, "The source manifest changed during import. Target remains held.");
        var verified = await MigrationCompare.Compare(target, check, marker, false);
        if (verified.State != "Match") await MigrationJson.WriteReport(reportPath + ".failed-comparison.json", verified);
        Require.That(verified.State == "Match", "Post-import data/object comparison failed. The target remains held and unprepared.");
        await Complete(target, check, marker);
        var report = Report(marker with { State = "Prepared" }, check, mappings, false);
        await MigrationJson.WriteReport(reportPath, report); return report;
    }

    private static MigrationImportReport Report(MigrationImportMarker marker, CheckedMigration check, List<MigrationObjectMapping> mappings, bool repeated) =>
        new(marker.BundleId, marker.BundleSha256, "PreparedHeld", marker.Database, marker.Bucket, marker.OperatorUserId, marker.ImportedAt,
            true, true, true, repeated, check.Entities.Count, mappings.Count, check.Bundle.History.Count, check.Report.Observed, mappings);

    internal static async Task<MigrationImportMarker> Marker(Settings settings, CheckedMigration check)
    {
        await using var db = settings.Db(); var firstTenant = check.Bundle.Tenants[0].Data.Id;
        var receipt = await db.Receipts.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TenantId == firstTenant && x.ActorId == Guid.Empty && x.Key == MarkerKey(check.Bundle.BundleId));
        Require.That(receipt is not null && receipt.RequestDigest == check.Sha256, "The existing target has no receipt for this exact canonical source bundle.");
        var marker = AppJson.Read<MigrationImportMarker>(receipt!.Response);
        Require.That(marker.BundleId == check.Bundle.BundleId && marker.BundleSha256 == check.Sha256 && marker.Bucket == settings.Bucket
            && marker.Database == new NpgsqlConnectionStringBuilder(settings.ConnectionString).Database && marker.Release == check.Bundle.TargetRelease, "Existing import target or bundle identity differs.");
        return marker;
    }
    private static async Task<bool> DatabaseExists(Settings settings, string name)
    {
        await using var connection = await PostgresFiles.Open(settings, "postgres");
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname = @name)", connection); command.Parameters.AddWithValue("name", name);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
    private static async Task<MigrationImportMarker> InsertHeld(Settings target, CheckedMigration check, string database, string bucket, DateTimeOffset at)
    {
        var bundle = check.Bundle;
        var sourceAccounts = bundle.Accounts.ToDictionary(x => x.Id);
        var sourceEntities = bundle.Entities.ToDictionary(x => (x.Data.GetProperty("tenantId").GetGuid(), x.Kind, x.Data.GetProperty("id").GetGuid()));
        await using var db = target.Db(); await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var tenant in bundle.Tenants)
        {
            db.Tenants.Add(AppJson.Read<BusinessTenant>(AppJson.Serialize(tenant.Data)));
            var hold = new OperationsRecoveryHold(bundle.BundleId, "Held", bundle.ExportedAt, bundle.TargetRelease, "legacy_migration_requires_reconciliation", null, null);
            AddReceipt(db, tenant.Data.Id, RecoveryResume.HoldKey, ContentDigest.Of(hold), hold, at);
            AddReceipt(db, tenant.Data.Id, "migration.tenant", check.Sha256, tenant, at);
        }
        foreach (var account in bundle.Accounts)
        {
            var normalized = account.Email.ToUpperInvariant();
            db.Users.Add(new ApplicationUser { Id = account.Id, UserName = account.Email, NormalizedUserName = normalized, Email = account.Email, NormalizedEmail = normalized,
                EmailConfirmed = false, PasswordHash = null, DisplayName = account.DisplayName, PhoneNumber = account.PhoneNumber, PhoneNumberConfirmed = false,
                TwoFactorEnabled = false, SecurityStamp = Guid.NewGuid().ToString(), ConcurrencyStamp = Guid.NewGuid().ToString() });
        }
        foreach (var membership in bundle.Memberships)
        {
            db.Memberships.Add(new Membership { TenantId = membership.TenantId, UserId = membership.UserId, Role = membership.Role, Active = false, Revision = membership.Revision });
            var account = sourceAccounts[membership.UserId];
            AddReceipt(db, membership.TenantId, MigrationAccountMarker.Key(account.Id), check.Sha256,
                new MigrationAccountMarker(bundle.BundleId, account.SourceId, account.Id, account.Email, "Pending", membership.Role, membership.Active), at);
        }
        foreach (var original in check.Entities)
        {
            var kind = BusinessRegistry.Entities.Single(x => x.Value == original.GetType()).Key;
            var originalJson = AppJson.Serialize(original); var entity = EffectiveEntity(original, at); var serialized = AppJson.Serialize(entity);
            db.Entities.Add(new StoredEntity { TenantId = entity.TenantId, Kind = kind, Id = entity.Id, Revision = entity.Revision, CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt, Archived = entity.Archived, Data = serialized, SearchText = EntityProjection.SearchText(serialized), UniqueKey = EntityProjection.UniqueKey(entity) });
            foreach (var reference in entity.References().Distinct()) db.Links.Add(new EntityLink { TenantId = entity.TenantId, SourceKind = kind, SourceId = entity.Id, TargetKind = reference.Kind, TargetId = reference.Id });
            var envelope = sourceEntities[(entity.TenantId, kind, entity.Id)];
            AddReceipt(db, entity.TenantId, EntityKey(kind, entity.Id), check.Sha256, new { envelope.SourceId, kind, entity.Id, sourceSha256 = envelope.Sha256, importedSha256 = MigrationJson.HashJson(serialized),
                envelope.ContractParties, envelope.DeliveryHistory, sourceEvidence = envelope.EvidenceFile is null ? null : bundle.EvidenceFiles.Single(x => x.File == envelope.EvidenceFile),
                securityTransformation = entity.Revision != original.Revision ? "portal_grant_revoked" : "none", original = entity.Revision != original.Revision ? originalJson : null }, at);
        }
        foreach (var counter in bundle.Counters) db.Counters.Add(new NumberCounter { TenantId = counter.TenantId, Kind = counter.Kind, Year = counter.Year, Value = counter.Value });
        foreach (var history in bundle.History) AddReceipt(db, history.TenantId, HistoryKey(history.Id), check.Sha256, history, at);
        var operatorId = await RecoveryAccounts.ProvisionOperator(db, target, bundle.Tenants.Select(x => x.Data.Id), bundle.BundleId);
        var marker = new MigrationImportMarker(bundle.BundleId, check.Sha256, at, bundle.ExportedAt, bundle.TargetRelease, bundle.SourceSystem, bundle.SourceVersion, database, bucket, operatorId, "Importing");
        foreach (var tenant in bundle.Tenants)
        {
            AddReceipt(db, tenant.Data.Id, MarkerKey(bundle.BundleId), check.Sha256, marker, at);
            db.Audit.Add(new AuditEntry { TenantId = tenant.Data.Id, ActorId = Guid.Empty, Action = "operations.migration.import", Kind = "tenants", EntityId = tenant.Data.Id,
                Revision = tenant.Data.PolicyRevision, ContentDigest = check.Sha256, CorrelationId = bundle.BundleId.ToString("N"), At = at });
        }
        // Deliberately no EntityStore.Save, Jobs, Invitations, Approvals, old passwords,
        // external login links or legacy Data Protection keys. The held data is inert.
        await db.SaveChangesAsync(); await transaction.CommitAsync(); return marker;
    }
    internal static TenantEntity EffectiveEntity(TenantEntity original, DateTimeOffset importedAt)
    {
        var kind = BusinessRegistry.Entities.Single(x => x.Value == original.GetType()).Key;
        var result = AppJson.Read(kind, AppJson.Serialize(original));
        if (result is PortalShare { RevokedAt: null } share)
        {
            share.RevokedAt = importedAt; share.UpdatedAt = importedAt; share.Revision = checked(share.Revision + 1); share.Validate();
        }
        return result;
    }
    private static async Task InsertDocuments(Settings target, CheckedMigration check, List<MigrationObjectMapping> mappings)
    {
        await using var db = target.Db(); await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var source in check.Bundle.Documents)
        {
            var mapping = mappings.Single(x => x.DocumentId == source.Data.Id); var document = AppJson.Read<DocumentObject>(AppJson.Serialize(source.Data));
            document.ObjectKey = mapping.TargetKey; document.VersionId = mapping.TargetVersionId; db.Documents.Add(document);
            AddReceipt(db, document.TenantId, DocumentKey(document.Id), check.Sha256, new { source.SourceId, source.SourceStore, source.SourceVersionId, originalMetadata = source.Data, mapping }, MicrosecondNow());
        }
        await db.SaveChangesAsync(); await transaction.CommitAsync();
    }
    private static async Task Complete(Settings target, CheckedMigration check, MigrationImportMarker marker)
    {
        await using var db = target.Db(); await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var tenant in check.Bundle.Tenants)
        {
            var receipt = await db.Receipts.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenant.Data.Id && x.ActorId == Guid.Empty && x.Key == MarkerKey(check.Bundle.BundleId));
            receipt.Response = AppJson.Serialize(marker with { State = "Prepared" });
            AddReceipt(db, tenant.Data.Id, RecoveryResume.PreparedKey, check.Bundle.BundleId.ToString("N"), new { operationId = check.Bundle.BundleId, preparedAt = MicrosecondNow(), objects = check.Bundle.Documents.Count,
                legacyCredentialsDisabled = true, bundleSha256 = check.Sha256 }, MicrosecondNow());
        }
        await db.SaveChangesAsync(); await transaction.CommitAsync();
    }
    internal static void AddReceipt<T>(FirstBlickDbContext db, Guid tenant, string key, string digest, T response, DateTimeOffset createdAt) =>
        db.Receipts.Add(new CommandReceipt { TenantId = tenant, ActorId = Guid.Empty, Key = key, RequestDigest = digest, Response = AppJson.Serialize(response), CreatedAt = createdAt });
    internal static DateTimeOffset MicrosecondNow()
    {
        var now = DateTimeOffset.UtcNow; return now.AddTicks(-(now.Ticks % 10));
    }
}
