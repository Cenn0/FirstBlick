using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Amazon.S3.Model;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FirstBlick.Operations;

internal static class RestoreDrill
{
    public static async Task<object> Run(Settings original, string output)
    {
        var watch = Stopwatch.StartNew(); var directory = BundleFiles.NewDirectory(output); var nonce = Guid.NewGuid().ToString("N")[..24];
        var sourceDatabase = $"firstblick_drill_{nonce}"; var targetDatabase = $"firstblick_restore_{nonce}";
        var sourceBucket = $"firstblick-drill-{nonce}"; var targetBucket = $"firstblick-restore-{nonce}";
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)); var pfx = Path.Combine(directory, "drill-data-protection.pfx");
        using var rsa = RSA.Create(3072); var request = new CertificateRequest("CN=FirstBlick isolated restore drill", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        await File.WriteAllBytesAsync(pfx, certificate.Export(X509ContentType.Pfx, password));
        await File.WriteAllTextAsync(Path.Combine(directory, "drill-secrets.env"), $"DataProtection__CertificatePassword='{password}'\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(pfx, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(Path.Combine(directory, "drill-secrets.env"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var settings = original with { ConnectionString = original.Connection(sourceDatabase), Bucket = sourceBucket, CertificatePath = pfx, CertificatePassword = password, Release = $"restore-drill-{nonce}", Local = false };
        await PostgresFiles.CreateDatabase(original, sourceDatabase, true);
        using var client = settings.S3(); await StorageFiles.EnableVersioning(client, sourceBucket, true);
        await using (var db = settings.Db()) await db.Database.MigrateAsync();
        var seed = await Seed(settings, certificate);
        var backupDirectory = Path.Combine(directory, "backup");
        var manifest = await Backup.Create(settings, backupDirectory);
        Require.That(manifest.Objects.Count == 2 && manifest.KeyProbe is not null && manifest.Certificate is not null, "Drill backup omitted objects or encrypted keyring proof.");
        // A later source write must remain outside the recovery snapshot, even though the source stays online.
        await using (var db = settings.Db())
        {
            db.Tenants.Add(new BusinessTenant { Name = "Written after backup: requires takeover reconciliation" }); await db.SaveChangesAsync();
        }
        var corrupted = BundleFiles.SafePath(backupDirectory, manifest.Objects[0].File); var originalBytes = await File.ReadAllBytesAsync(corrupted);
        var tampered = (byte[])originalBytes.Clone(); tampered[0] ^= 1; await File.WriteAllBytesAsync(corrupted, tampered);
        var rejected = false;
        try { await Restore.Run(settings, backupDirectory, targetDatabase, targetBucket, Path.Combine(directory, "must-not-exist.json")); }
        catch (OperationsException ex) when (ex.Message.Contains("integrity", StringComparison.OrdinalIgnoreCase)) { rejected = true; }
        finally { await File.WriteAllBytesAsync(corrupted, originalBytes); }
        Require.That(rejected, "Drill failed to reject a corrupted document before restoring.");
        await using (var connection = await PostgresFiles.Open(settings, "postgres"))
        {
            var exists = await PostgresFiles.Scalar(connection, $"SELECT count(*) FROM pg_database WHERE datname = '{targetDatabase}'");
            Require.That(exists == "0", "Corrupt backup created a target database.");
        }
        var certificateRejected = false;
        try { await Restore.Run(settings with { CertificatePassword = "wrong-drill-password" }, backupDirectory, targetDatabase, targetBucket, Path.Combine(directory, "wrong-key-report.json")); }
        catch (CryptographicException) { certificateRejected = true; }
        Require.That(certificateRejected, "Drill accepted the wrong Data Protection certificate password.");
        var restored = await Restore.Run(settings, backupDirectory, targetDatabase, targetBucket, Path.Combine(directory, "restore-report.json"));
        var existingTargetRejected = false;
        try { await Restore.Run(settings, backupDirectory, targetDatabase, targetBucket, Path.Combine(directory, "overwrite-report.json")); }
        catch (OperationsException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal)) { existingTargetRejected = true; }
        Require.That(existingTargetRejected, "Drill accepted an existing restore target.");
        var target = settings with { ConnectionString = settings.Connection(targetDatabase), Bucket = targetBucket };
        await using (var db = target.Db())
        {
            var quote = AppJson.Read<Quote>((await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.Id == seed.QuoteId)).Data);
            Require.That(quote.Status == QuoteStatus.Accepted && quote.Snapshot!.Digest == seed.QuoteDigest && quote.Deliveries.Single().State == DeliveryState.Uncertain,
                "Restore changed accepted commercial values or did not quarantine delivery state.");
            Require.That(await db.Tenants.CountAsync() == 2, "Restore included writes committed after the backup.");
            Require.That(await db.Links.IgnoreQueryFilters().CountAsync() == seed.LinkCount, "Restore lost tenant-qualified references.");
            Require.That(await db.Counters.IgnoreQueryFilters().SingleAsync(x => x.Kind == "AN") is { Value: 7 }, "Restore lost the commercial numbering counter.");
            Require.That(await db.Receipts.IgnoreQueryFilters().AnyAsync(x => x.Key == "drill-command" && x.Response == "{\"number\": \"AN-2026-000007\"}"), "Restore changed idempotency evidence.");
            Require.That(await db.Jobs.IgnoreQueryFilters().CountAsync(x => x.Type != "entity.changed" && x.State == JobState.Uncertain) == 2, "Restore can automatically re-send a pre-backup external job.");
            Require.That(await db.Jobs.IgnoreQueryFilters().AnyAsync(x => x.Type == "entity.changed" && x.State == JobState.Pending), "Restore unnecessarily removed an internal event.");
            var user = await db.Users.SingleAsync();
            Require.That(user.SecurityStamp != seed.SecurityStamp && new PasswordHasher<ApplicationUser>().VerifyHashedPassword(user, user.PasswordHash!, seed.LoginPassword) != PasswordVerificationResult.Failed,
                "Restore failed to invalidate old sessions or preserve the login credential.");
            Require.That(await db.Invitations.AllAsync(x => x.UsedAt != null), "Restore resurrected an outstanding invitation.");
            Require.That(await db.Approvals.IgnoreQueryFilters().AllAsync(x => x.ExpiresAt < DateTimeOffset.UtcNow), "Restore resurrected a privileged approval token.");
            var oldVersionDoc = await db.Documents.IgnoreQueryFilters().SingleAsync(x => x.Id == seed.OldVersionDocument);
            using var objectResult = await client.GetObjectAsync(new GetObjectRequest { BucketName = targetBucket, Key = oldVersionDoc.ObjectKey, VersionId = oldVersionDoc.VersionId });
            using var reader = new StreamReader(objectResult.ResponseStream);
            Require.That(await reader.ReadToEndAsync() == seed.OldVersionContents, "Restore retrieved an overwritten latest object instead of the stored historical version.");
            Require.That(oldVersionDoc.VersionId != seed.OldVersionId && restored.Versions.All(x => x.RestoredVersionId.Length > 0), "Restore did not bind the newly created S3 versions.");
        }
        // Do not retain live duplicate synthetic databases/buckets after a successful drill. The verified bundle/report remain.
        await PostgresFiles.DropDrillDatabase(original, sourceDatabase, nonce); await PostgresFiles.DropDrillDatabase(original, targetDatabase, nonce);
        await StorageFiles.DeleteDrillBucket(client, sourceBucket, nonce); await StorageFiles.DeleteDrillBucket(client, targetBucket, nonce);
        watch.Stop();
        var report = new { status = "passed", completedAt = DateTimeOffset.UtcNow, sourcePostgreSql = manifest.DatabaseVersion, manifest.PgDumpVersion,
            manifest.SnapshotAt, verifiedTables = restored.VerifiedTables, verifiedRows = restored.VerifiedRows, verifiedObjectVersions = restored.VerifiedObjects,
            historicalVersionRestored = true, archivedDocumentRestored = true, corruptionRejectedBeforeTargetCreation = true, exactDatabaseRowsBeforeRecoveryBarrier = true,
            wrongCertificatePasswordRejectedBeforeTargetCreation = true, existingTargetOverwriteRejected = true,
            encryptedDataProtectionVerified = restored.DataProtectionVerified, sessionsInvalidated = true, invitationsInvalidated = true, privilegedApprovalsInvalidated = true,
            restored.QuarantinedJobs, restored.QuarantinedDeliveries, postBackupWritesExcluded = true, sourceAndTargetCleaned = true,
            restored.RestoreSeconds, totalSeconds = watch.Elapsed.TotalSeconds, workload = "small synthetic two-tenant fixture; production RPO/RTO require a representative drill" };
        await File.WriteAllTextAsync(Path.Combine(directory, "drill-report.json"), JsonSerializer.Serialize(report, BundleFiles.Json));
        return report;
    }

    private sealed record SeedResult(Guid QuoteId, string QuoteDigest, int LinkCount, Guid OldVersionDocument, string OldVersionId, string OldVersionContents, string SecurityStamp, string LoginPassword);
    private static async Task<SeedResult> Seed(Settings settings, X509Certificate2 certificate)
    {
        var tenant = new BusinessTenant { Name = "Restore drill A" }; var otherTenant = new BusinessTenant { Name = "Restore drill B" };
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "restore@example.invalid", NormalizedUserName = "RESTORE@EXAMPLE.INVALID", Email = "restore@example.invalid",
            NormalizedEmail = "RESTORE@EXAMPLE.INVALID", EmailConfirmed = true, DisplayName = "Restore drill", SecurityStamp = Guid.NewGuid().ToString(), ConcurrencyStamp = Guid.NewGuid().ToString() };
        const string loginPassword = "Isolated-Drill-Login!2026"; user.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(user, loginPassword);
        await using var db = settings.Db(); db.AddRange(tenant, otherTenant, user);
        db.Memberships.Add(new Membership { TenantId = tenant.Id, UserId = user.Id, Role = BusinessRole.ManagingDirector });
        await db.SaveChangesAsync();
        var scope = new TenantScope { TenantId = tenant.Id, UserId = user.Id, Role = BusinessRole.ManagingDirector };
        await using var tenantDb = new FirstBlickDbContext(new DbContextOptionsBuilder<FirstBlickDbContext>().UseNpgsql(settings.ConnectionString).Options, scope);
        var store = new EntityStore(tenantDb, scope);
        var customer = new Customer { TenantId = tenant.Id, Name = "Snapshot customer", Email = "customer@example.invalid" };
        var building = new Building { TenantId = tenant.Id, CustomerId = customer.Id, Name = "Archive roof", Address = "Synthetic address" };
        var project = new Project { TenantId = tenant.Id, CustomerId = customer.Id, BuildingId = building.Id, Name = "Immutable reference chain" };
        var quote = new Quote { TenantId = tenant.Id, CustomerId = customer.Id, ProjectId = project.Id, Title = "Accepted snapshot", Lines = [new QuoteLine { Description = "Restore line", Quantity = 3, UnitMaterialCost = 21.13m, VatPercent = 19 }] };
        CommercialWorkflow.IssueQuote(quote, "AN-2026-000007"); CommercialWorkflow.AcceptQuote(quote, quote.Snapshot!.Digest, DateTimeOffset.UtcNow, DateOnly.FromDateTime(DateTime.UtcNow));
        var delivery = CommercialWorkflow.RequestDelivery(quote, CommunicationChannel.Email, "customer@example.invalid", DateTimeOffset.UtcNow);
        await store.Save(customer, "drill.seed", true); await store.Save(building, "drill.seed", true); await store.Save(project, "drill.seed", true); await store.Save(quote, "drill.seed", true);
        tenantDb.Counters.Add(new NumberCounter { TenantId = tenant.Id, Kind = "AN", Year = 2026, Value = 7 });
        tenantDb.Receipts.Add(new CommandReceipt { TenantId = tenant.Id, ActorId = user.Id, Key = "drill-command", RequestDigest = ContentDigest.Of("drill"), Response = "{\"number\":\"AN-2026-000007\"}" });
        tenantDb.Jobs.Add(new OutboxJob { TenantId = tenant.Id, ActorId = user.Id, Type = "quote.delivery", Kind = "quotes", EntityId = quote.Id, EntityRevision = quote.Revision,
            Payload = AppJson.Serialize(new { deliveryId = delivery.Id, snapshotDigest = quote.Snapshot.Digest, channel = "Email", recipient = "customer@example.invalid" }), State = JobState.Pending });
        tenantDb.Jobs.Add(new OutboxJob { TenantId = tenant.Id, ActorId = user.Id, Type = "identity.recovery", State = JobState.Leased, ExternalAttemptStarted = true, LeaseToken = Guid.NewGuid(), LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(2) });
        tenantDb.Invitations.Add(new Invitation { TenantId = tenant.Id, InvitedBy = user.Id, Email = "pending@example.invalid", TokenHash = ContentDigest.Of("not-a-real-invitation"), Role = BusinessRole.Office, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        tenantDb.Approvals.Add(new ApprovalRequest { TenantId = tenant.Id, RequestedBy = user.Id, ApprovedBy = Guid.NewGuid(), ApprovedAt = DateTimeOffset.UtcNow,
            Action = "invoice.issue", Kind = "invoices", EntityId = Guid.NewGuid(), EntityRevision = 1, PolicyRevision = 1, ContentDigest = ContentDigest.Of("{}"), ProposedData = "{}", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        await tenantDb.SaveChangesAsync();
        await using (var provider = KeyProtection.Provider(settings, certificate, true))
            _ = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("drill.keyring").Protect("pre-backup encrypted payload");
        using var client = settings.S3();
        const string historical = "%PDF-1.7\nSynthetic historical accepted version.\n%%EOF\n";
        var ready = await AddDocument(tenant.Id, historical, "Ready", project.Id, quote.Id, quote.Snapshot.Digest);
        _ = await AddDocument(otherTenant.Id, "%PDF-1.7\nSynthetic archived document.\n%%EOF\n", "Archived", null, null, "");
        // Latest bytes are deliberately wrong for the DB reference; only GET with the stored version can pass.
        await using (var overwrite = new MemoryStream(Encoding.UTF8.GetBytes("Overwritten latest: never part of the accepted snapshot.")))
            await client.PutObjectAsync(new PutObjectRequest { BucketName = settings.Bucket, Key = ready.ObjectKey, InputStream = overwrite, ContentType = "application/pdf", AutoCloseStream = false });
        await db.SaveChangesAsync();
        return new(quote.Id, quote.Snapshot.Digest, await tenantDb.Links.CountAsync(), ready.Id, ready.VersionId!, historical, user.SecurityStamp!, loginPassword);

        async Task<DocumentObject> AddDocument(Guid tenantId, string text, string state, Guid? projectId, Guid? sourceId, string digest)
        {
            var bytes = Encoding.UTF8.GetBytes(text); var doc = new DocumentObject { TenantId = tenantId, UploadedBy = user.Id, Name = "drill.pdf", ContentType = "application/pdf", Size = bytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), State = state, ProjectId = projectId, ProjectKind = projectId.HasValue ? "projects" : null,
                SourceId = sourceId, SourceKind = sourceId.HasValue ? "quotes" : null, SourceDigest = digest, RendererVersion = "drill-v1" };
            doc.ObjectKey = $"{tenantId:N}/{doc.Id:N}/drill.pdf";
            await using var stream = new MemoryStream(bytes);
            doc.VersionId = (await client.PutObjectAsync(new PutObjectRequest { BucketName = settings.Bucket, Key = doc.ObjectKey, InputStream = stream, ContentType = doc.ContentType, AutoCloseStream = false })).VersionId;
            Require.That(!string.IsNullOrEmpty(doc.VersionId) && doc.VersionId != "null", "Drill storage did not create a real object version.");
            db.Documents.Add(doc); return doc;
        }
    }
}
