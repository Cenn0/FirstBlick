using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3.Model;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Operations;

internal sealed record MigrationSourceMarker(string SourceId, string Kind, Guid Id, string SourceSha256, string ImportedSha256,
    MigrationEvidence ContractParties, MigrationEvidence DeliveryHistory, FileProof? SourceEvidence, string SecurityTransformation, string? Original);
internal sealed record MigrationDocumentReceipt(string SourceId, string SourceStore, string? SourceVersionId, DocumentObject OriginalMetadata, MigrationObjectMapping Mapping);
internal sealed record MigrationCompareReport(Guid BundleId, string BundleSha256, DateTimeOffset ComparedAt, string State, List<MigrationIssue> Issues,
    int ComparedEntities, int ComparedDocuments, int ComparedHistoricalRecords, int JobsPresent, bool RecoveryHeld,
    List<MigrationControl> Observed, List<MigrationObjectMapping> ObjectMappings);

internal static class MigrationCompare
{
    public static async Task<MigrationCompareReport> Run(Settings settings, string input, string database, string bucket, string reportPath)
    {
        var check = await MigrationCheck.Load(input);
        Require.That(check.Report.State == "Compatible", "Source bundle is not compatible; run migration-check first.");
        var target = settings with { ConnectionString = settings.Connection(database), Bucket = bucket };
        var result = await Compare(target, check, await MigrationImport.Marker(target, check), true);
        await MigrationJson.WriteReport(reportPath, result); return result;
    }
    public static async Task<MigrationCompareReport> Compare(Settings target, CheckedMigration check, MigrationImportMarker marker, bool requirePrepared)
    {
        var bundle = check.Bundle; var issues = new List<MigrationIssue>();
        void Verify(bool condition, string location, string message) { if (!condition && issues.Count < 500) issues.Add(new("import_difference", location, message)); }
        await using var db = target.Db();
        // One read-only repeatable-read view for every relational comparison.
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        var tenants = await db.Tenants.AsNoTracking().ToListAsync(); var users = await db.Users.AsNoTracking().ToListAsync();
        var memberships = await db.Memberships.AsNoTracking().ToListAsync();
        var rows = await db.Entities.IgnoreQueryFilters().AsNoTracking().ToListAsync(); var links = await db.Links.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        var documents = await db.Documents.IgnoreQueryFilters().AsNoTracking().ToListAsync(); var counters = await db.Counters.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        var receipts = await db.Receipts.IgnoreQueryFilters().AsNoTracking().ToListAsync(); var jobCount = await db.Jobs.IgnoreQueryFilters().CountAsync();
        Verify(tenants.Count == bundle.Tenants.Count, "tenants", "Tenant count differs from the canonical source.");
        Verify(rows.Count == check.Entities.Count && documents.Count == bundle.Documents.Count, "records", "Entity/document counts differ from the source.");
        Verify(users.Count == bundle.Accounts.Count + 1 && memberships.Count == bundle.Memberships.Count + bundle.Tenants.Count, "identities", "Expected only source identities plus the explicit new operator.");
        Verify(jobCount == 0 && !await db.Invitations.AnyAsync() && !await db.Approvals.IgnoreQueryFilters().AnyAsync(), "inert-import", "Import may not create legacy jobs, invitations or privileged approvals.");
        Verify(!await db.UserLogins.AnyAsync() && !await db.UserTokens.AnyAsync() && !await db.UserClaims.AnyAsync() && !await db.UserRoles.AnyAsync(), "credentials", "No legacy external logins, tokens, Identity claims or Identity roles may be imported.");
        var allHeld = true;
        CommandReceipt? Receipt(Guid tenant, string key)
        {
            var receipt = receipts.SingleOrDefault(x => x.TenantId == tenant && x.ActorId == Guid.Empty && x.Key == key);
            Verify(receipt is not null, $"receipts/{tenant}/{key}", "Required migration receipt is absent."); return receipt;
        }
        foreach (var tenant in bundle.Tenants)
        {
            Verify(MigrationJson.Digest(tenants.SingleOrDefault(x => x.Id == tenant.Data.Id)) == MigrationJson.Digest(tenant.Data), $"tenant/{tenant.Data.Id}", "Tenant identity, modules, timezone or approval policy changed.");
            var tenantSource = Receipt(tenant.Data.Id, "migration.tenant");
            if (tenantSource is not null) Verify(tenantSource.RequestDigest == check.Sha256 && MigrationJson.HashJson(tenantSource.Response) == MigrationJson.Digest(tenant), "tenant-mapping", "Original tenant source mapping changed.");
            var held = Receipt(tenant.Data.Id, RecoveryResume.HoldKey);
            if (held is not null)
            {
                var value = AppJson.Read<OperationsRecoveryHold>(held.Response);
                var expected = new OperationsRecoveryHold(bundle.BundleId, "Held", bundle.ExportedAt, bundle.TargetRelease, "legacy_migration_requires_reconciliation", null, null);
                Verify(value == expected && held.RequestDigest == ContentDigest.Of(expected), "recovery-hold", "Import no longer has the exact unreleased recovery barrier."); allHeld &= value == expected;
            }
            else allHeld = false;
            var anchor = Receipt(tenant.Data.Id, MigrationImport.MarkerKey(bundle.BundleId));
            if (anchor is not null) Verify(anchor.RequestDigest == check.Sha256 && AppJson.Read<MigrationImportMarker>(anchor.Response) == marker, "bundle-receipt", "Bundle identity, source version or immutable import marker differs.");
            if (requirePrepared)
            {
                var prepared = Receipt(tenant.Data.Id, RecoveryResume.PreparedKey);
                Verify(marker.State == "Prepared" && prepared?.RequestDigest == bundle.BundleId.ToString("N"), "prepared", "Import preparation did not complete.");
            }
        }
        foreach (var account in bundle.Accounts)
        {
            var user = users.SingleOrDefault(x => x.Id == account.Id);
            Verify(user is not null && user.Email == account.Email && user.DisplayName == account.DisplayName && user.PhoneNumber == account.PhoneNumber
                && user.NormalizedEmail == account.Email.ToUpperInvariant() && user.UserName == account.Email && user.NormalizedUserName == account.Email.ToUpperInvariant()
                && user.PasswordHash is null && !user.EmailConfirmed && !user.PhoneNumberConfirmed && !user.TwoFactorEnabled && !string.IsNullOrEmpty(user.SecurityStamp),
                $"account/{account.Id}", "Historical identity mapping or disabled-credential state differs.");
        }
        foreach (var member in bundle.Memberships)
        {
            var actual = memberships.SingleOrDefault(x => x.TenantId == member.TenantId && x.UserId == member.UserId);
            Verify(actual is not null && !actual.Active && actual.Role == member.Role && actual.Revision == member.Revision, $"membership/{member.UserId}", "Source membership must preserve role/revision while remaining inactive.");
            var receipt = Receipt(member.TenantId, MigrationAccountMarker.Key(member.UserId));
            if (receipt is not null)
            {
                var account = bundle.Accounts.Single(x => x.Id == member.UserId); var expected = new MigrationAccountMarker(bundle.BundleId, account.SourceId, account.Id, account.Email, "Pending", member.Role, member.Active);
                Verify(receipt.RequestDigest == check.Sha256 && AppJson.Read<MigrationAccountMarker>(receipt.Response) == expected, "account-takeover", "Fresh-invitation account takeover evidence changed.");
            }
        }
        var recoveryOperator = users.SingleOrDefault(x => x.Id == marker.OperatorUserId);
        Verify(recoveryOperator is not null && recoveryOperator.EmailConfirmed && !string.IsNullOrWhiteSpace(recoveryOperator.PasswordHash)
            && !bundle.Accounts.Any(x => x.Id == recoveryOperator.Id || string.Equals(x.Email, recoveryOperator.Email, StringComparison.OrdinalIgnoreCase))
            && bundle.Tenants.All(x => memberships.Any(m => m.TenantId == x.Data.Id && m.UserId == marker.OperatorUserId && m.Active && m.Role == BusinessRole.ManagingDirector)), "operator", "A distinct explicit operator with a new credential is required for every held tenant.");

        var expectedLinks = new HashSet<(Guid, string, Guid, string, Guid)>(); var actualEnvelopes = new List<MigrationEntity>(); var actualEntities = new List<TenantEntity>();
        foreach (var original in check.Entities)
        {
            var kind = BusinessRegistry.Entities.Single(x => x.Value == original.GetType()).Key; var expected = MigrationImport.EffectiveEntity(original, marker.ImportedAt);
            var envelope = bundle.Entities.Single(x => x.Kind == kind && x.Data.GetProperty("tenantId").GetGuid() == original.TenantId && x.Data.GetProperty("id").GetGuid() == original.Id);
            var row = rows.SingleOrDefault(x => x.TenantId == original.TenantId && x.Kind == kind && x.Id == original.Id);
            Verify(row is not null, $"{kind}/{original.Id}", "A source entity is missing."); if (row is null) continue;
            using var actualJson = JsonDocument.Parse(row.Data); var actualHash = MigrationJson.Hash(actualJson.RootElement);
            var expectedJson = MigrationEntityJson.Serialize(expected, envelope.Data); var expectedHash = MigrationJson.HashJson(expectedJson);
            Verify(actualHash == expectedHash && row.Revision == expected.Revision && row.CreatedAt == expected.CreatedAt && row.UpdatedAt == expected.UpdatedAt && row.Archived == expected.Archived
                && row.UniqueKey == EntityProjection.UniqueKey(expected) && row.SearchText == EntityProjection.SearchText(expectedJson), $"{kind}/{original.Id}", "Historical typed values, revision, index projection or permitted grant revocation differ.");
            var receipt = Receipt(original.TenantId, MigrationImport.EntityKey(kind, original.Id));
            if (receipt is not null)
            {
                var mapping = AppJson.Read<MigrationSourceMarker>(receipt.Response);
                Verify(receipt.RequestDigest == check.Sha256 && mapping.SourceId == envelope.SourceId && mapping.Kind == kind && mapping.Id == original.Id
                    && mapping.SourceSha256 == envelope.Sha256 && mapping.ImportedSha256 == expectedHash
                    && mapping.ContractParties == envelope.ContractParties && mapping.DeliveryHistory == envelope.DeliveryHistory
                    && mapping.SourceEvidence == (envelope.EvidenceFile is null ? null : bundle.EvidenceFiles.Single(x => x.File == envelope.EvidenceFile))
                    && mapping.SecurityTransformation == (expected.Revision == original.Revision ? "none" : "portal_grant_revoked")
                    && (expected.Revision == original.Revision ? mapping.Original is null : mapping.Original == MigrationEntityJson.Serialize(original, envelope.Data)), "source-mapping", "Original source identity/checksum or security transformation evidence differs.");
            }
            foreach (var reference in original.References().Distinct()) expectedLinks.Add((original.TenantId, kind, original.Id, reference.Kind, reference.Id));
            var actual = AppJson.Read(kind, row.Data);
            if (actual is PortalShare && original is PortalShare originalGrant && originalGrant.RevokedAt is null)
            {
                ((PortalShare)actual).RevokedAt = null; actual.UpdatedAt = original.UpdatedAt; actual.Revision = original.Revision;
            }
            actualEntities.Add(actual); var actualSource = MigrationEntityJson.Element(actual, envelope.Data);
            actualEnvelopes.Add(envelope with { Data = actualSource, Sha256 = MigrationJson.Hash(actualSource) });
        }
        Verify(expectedLinks.SetEquals(links.Select(x => (x.TenantId, x.SourceKind, x.SourceId, x.TargetKind, x.TargetId))), "references", "Tenant-qualified reference edges differ from the source graph.");
        Verify(counters.Count == bundle.Counters.Count && bundle.Counters.All(x => counters.Any(actual => actual.TenantId == x.TenantId && actual.Kind == x.Kind && actual.Year == x.Year && actual.Value == x.Value)), "counters", "A numbering counter is missing, advanced or changed.");
        var actualHistory = new List<MigrationHistory>();
        foreach (var source in bundle.History)
        {
            var receipt = Receipt(source.TenantId, MigrationImport.HistoryKey(source.Id)); if (receipt is null) continue;
            var actual = AppJson.Read<MigrationHistory>(receipt.Response); actualHistory.Add(actual);
            Verify(receipt.RequestDigest == check.Sha256 && MigrationJson.Digest(actual) == MigrationJson.Digest(source), "historical-evidence", "An immutable source audit/dispatch/contract record changed.");
        }
        Verify(receipts.Count(x => x.Key.StartsWith("migration.history:", StringComparison.Ordinal)) == bundle.History.Count, "historical-count", "Historical evidence count differs.");
        var mappings = new List<MigrationObjectMapping>(); var actualDocuments = new List<MigrationDocument>(); using var client = target.S3();
        foreach (var source in bundle.Documents)
        {
            var document = documents.SingleOrDefault(x => x.Id == source.Data.Id); var receipt = Receipt(source.Data.TenantId, MigrationImport.DocumentKey(source.Data.Id));
            Verify(document is not null && receipt is not null, $"document/{source.Data.Id}", "Document or source object mapping is missing."); if (document is null || receipt is null) continue;
            var stored = AppJson.Read<MigrationDocumentReceipt>(receipt.Response); var mapping = stored.Mapping; mappings.Add(mapping);
            Verify(receipt.RequestDigest == check.Sha256 && stored.SourceId == source.SourceId && stored.SourceStore == source.SourceStore && stored.SourceVersionId == source.SourceVersionId
                && MigrationJson.Digest(stored.OriginalMetadata) == MigrationJson.Digest(source.Data) && mapping.DocumentId == source.Data.Id && mapping.SourceKey == source.Data.ObjectKey
                && mapping.SourceVersionId == source.SourceVersionId && mapping.SourceStore == source.SourceStore && mapping.TargetKey == MigrationImport.TargetKey(source.Data)
                && mapping.TargetVersionId == document.VersionId && mapping.Sha256 == source.File.Sha256 && mapping.Size == source.File.Size, "object-mapping", "Original R2/S3 key/version or target immutable object mapping changed.");
            var expected = AppJson.Read<DocumentObject>(AppJson.Serialize(source.Data)); expected.ObjectKey = mapping.TargetKey; expected.VersionId = mapping.TargetVersionId;
            Verify(MigrationJson.Digest(document) == MigrationJson.Digest(expected) && document.VersionId is { Length: > 0 } and not "null", "document-metadata", "Document metadata differs beyond the deliberate new S3 key/version.");
            using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = target.Bucket, Key = document.ObjectKey, VersionId = document.VersionId });
            Verify(response.VersionId == document.VersionId && response.ContentLength == source.File.Size && Convert.ToHexStringLower(await SHA256.HashDataAsync(response.ResponseStream)) == source.File.Sha256, "object-bytes", "Restored object version, SHA-256 or byte count differs.");
            var historical = AppJson.Read<DocumentObject>(AppJson.Serialize(document)); historical.ObjectKey = source.Data.ObjectKey; historical.VersionId = source.Data.VersionId;
            actualDocuments.Add(source with { Data = historical });
        }
        List<MigrationControl> observed = [];
        if (actualEntities.Count == check.Entities.Count && actualDocuments.Count == bundle.Documents.Count && actualHistory.Count == bundle.History.Count)
        {
            observed = MigrationControls.Calculate(bundle with { Entities = actualEnvelopes, Documents = actualDocuments, History = actualHistory }, actualEntities);
            Verify(MigrationJson.Digest(observed) == MigrationJson.Digest(check.Report.Observed), "control-totals", "Imported sums/counts/canonical digests differ from independently supplied source controls.");
        }
        await transaction.CommitAsync();
        return new(bundle.BundleId, check.Sha256, DateTimeOffset.UtcNow, issues.Count == 0 ? "Match" : "Mismatch", issues, actualEntities.Count, actualDocuments.Count, actualHistory.Count, jobCount, allHeld, observed, mappings);
    }
}
