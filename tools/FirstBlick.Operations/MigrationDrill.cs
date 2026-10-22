using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Core.Technical;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FirstBlick.Operations;

internal static class MigrationDrill
{
    public static async Task<object> Run(Settings original, string output)
    {
        var watch = Stopwatch.StartNew(); var directory = BundleFiles.NewDirectory(output); var nonce = Guid.NewGuid().ToString("N")[..24];
        var database = $"firstblick_restore_{nonce}"; var bucket = $"firstblick-restore-{nonce}"; var sourceDirectory = BundleFiles.NewDirectory(Path.Combine(directory, "source"));
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)); var pfx = Path.Combine(directory, "target-data-protection.pfx");
        using var rsa = RSA.Create(3072); var request = new CertificateRequest("CN=FirstBlick isolated migration drill", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        await File.WriteAllBytesAsync(pfx, certificate.Export(X509ContentType.Pfx, password));
        var settings = original with { CertificatePath = pfx, CertificatePassword = password, Release = $"migration-drill-{nonce}", Local = false,
            OperatorEmail = $"migration-operator-{nonce}@example.invalid", OperatorPassword = $"Aa1!Migration-Operator-{nonce}" };
        var bundle = await Source(sourceDirectory, settings.Release); await WriteBundle(sourceDirectory, bundle);
        var checkedSource = await MigrationCheck.Load(sourceDirectory);
        await MigrationJson.WriteReport(Path.Combine(directory, "source-check.json"), checkedSource.Report);
        Require.That(checkedSource.Report.State == "Compatible", "Synthetic source failed compatibility checks; inspect source-check.json.");
        VerifyWarehouseBranches(checkedSource.Entities.OfType<StockMovement>().ToArray());
        VerifyOperationalLedgers(checkedSource.Entities, bundle.Memberships);
        var sourceSupplierInvoices = bundle.Entities.Where(x => x.Kind == "supplier-invoices").ToList();
        Require.That(sourceSupplierInvoices.Count == 2 && sourceSupplierInvoices.Any(x => !x.Data.TryGetProperty("electronicSource", out _))
            && sourceSupplierInvoices.Any(x => x.Data.TryGetProperty("electronicSource", out var value) && value.ValueKind == JsonValueKind.Null),
            "Supplier invoice fixtures must cover both historical absence and explicit null electronic evidence.");
        var negatives = new List<string>();
        async Task Reject(string name, MigrationBundle altered, Func<MigrationIssue, bool>? expectedIssue = null)
        {
            await WriteBundle(sourceDirectory, altered); var result = await MigrationCheck.Load(sourceDirectory);
            await MigrationJson.WriteReport(Path.Combine(directory, name + ".json"), result.Report);
            Require.That(result.Report.State == "Blocked" && (expectedIssue is null || result.Report.Issues.Any(expectedIssue)),
                "Migration accepted negative fixture or missed the expected rule " + name + "."); negatives.Add(name);
        }
        await RejectCanonicalSources(bundle, checkedSource.Entities, Reject);
        // A malformed numeric exponent must produce a concrete Blocked dry-run
        // report, rather than disappearing into a generic unreported exception.
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "migration.json"), "{\"format\":\"firstblick.migration.v1\",\"sourceEvidence\":1e9999999999999999999}");
        var exponentReport = await MigrationCheck.Run(sourceDirectory, Path.Combine(directory, "out-of-budget-json-exponent.json"));
        Require.That(exponentReport.State == "Blocked" && exponentReport.Issues.Any(x => x.Code == "invalid_contract"), "Invalid JSON exponent did not produce a blocked report.");
        negatives.Add("out-of-budget-json-exponent");
        await WriteBundle(sourceDirectory, bundle);
        var documentPath = BundleFiles.SafePath(sourceDirectory, bundle.Documents[0].File.File); var bytes = await File.ReadAllBytesAsync(documentPath);
        var corrupt = (byte[])bytes.Clone(); corrupt[0] ^= 1; await File.WriteAllBytesAsync(documentPath, corrupt);
        var refusedCorruption = false;
        try { await MigrationImport.Run(settings, sourceDirectory, database, bucket, Path.Combine(directory, "must-not-import.json")); }
        catch (OperationsException) { refusedCorruption = true; }
        finally { await File.WriteAllBytesAsync(documentPath, bytes); }
        await using (var connection = await PostgresFiles.Open(settings, "postgres"))
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname = @name)", connection); command.Parameters.AddWithValue("name", database);
            Require.That(refusedCorruption && !(bool)(await command.ExecuteScalarAsync())!, "Corrupt migration created a target database.");
        }
        negatives.Add("corrupt-object-before-target");
        using (var integer = JsonDocument.Parse("{\"n\":9007199254740993}"))
        using (var adjacent = JsonDocument.Parse("{\"n\":9007199254740992}"))
        using (var tiny = JsonDocument.Parse("{\"n\":1e-100}"))
        using (var zero = JsonDocument.Parse("{\"n\":0}"))
        using (var decimalForm = JsonDocument.Parse("{\"n\":100.000}"))
        using (var exponentForm = JsonDocument.Parse("{\"n\":1e2}"))
            Require.That(MigrationJson.Hash(integer.RootElement) != MigrationJson.Hash(adjacent.RootElement) && MigrationJson.Hash(tiny.RootElement) != MigrationJson.Hash(zero.RootElement)
                && MigrationJson.Hash(decimalForm.RootElement) == MigrationJson.Hash(exponentForm.RootElement), "Canonical JSON changed exact numeric values.");

        var imported = await MigrationImport.Run(settings, sourceDirectory, database, bucket, Path.Combine(directory, "import-report.json"));
        var target = settings with { ConnectionString = settings.Connection(database), Bucket = bucket };
        var compared = await MigrationCompare.Run(settings, sourceDirectory, database, bucket, Path.Combine(directory, "compare-report.json"));
        Require.That(compared.State == "Match" && compared.JobsPresent == 0 && compared.RecoveryHeld, "Imported target differs from the source or is active.");
        List<TableProof> before, after;
        await using (var connection = await PostgresFiles.Open(target)) before = await PostgresFiles.TableProofs(connection);
        var repeated = await MigrationImport.Run(settings, sourceDirectory, database, bucket, Path.Combine(directory, "repeat-report.json"));
        await using (var connection = await PostgresFiles.Open(target)) after = await PostgresFiles.TableProofs(connection);
        Require.That(repeated.RepeatedWithoutWrites && before.SequenceEqual(after), "Repeating the same source bundle changed database rows.");
        using var client = settings.S3();
        var versions = await client.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket });
        Require.That(versions.Versions?.Count == bundle.Documents.Count, "Import retry uploaded duplicate S3 versions.");
        var sourceTenant = bundle.Tenants[0].Data.Id;
        await using (var db = target.Db())
        {
            var owner = await db.Users.SingleAsync(x => x.Id == bundle.Accounts[0].Id);
            Require.That(owner.PasswordHash is null && !owner.EmailConfirmed && await db.Memberships.Where(x => x.UserId == owner.Id).AllAsync(x => !x.Active), "Legacy account can still authenticate or use an old role.");
            var operatorUser = await db.Users.SingleAsync(x => x.Id == imported.OperatorUserId);
            Require.That(new PasswordHasher<ApplicationUser>().VerifyHashedPassword(operatorUser, operatorUser.PasswordHash!, settings.OperatorPassword!) != PasswordVerificationResult.Failed,
                "Fresh operator credential is invalid.");
            var quote = AppJson.Read<Quote>((await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.Kind == "quotes")).Data);
            var customer = AppJson.Read<Customer>((await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.Kind == "customers" && x.TenantId == sourceTenant)).Data);
            Require.That(quote.Presentation!.BuyerName != customer.Name && quote.Presentation.BuyerName == "Historical buyer name" && quote.Deliveries.Single().State == DeliveryState.Sent,
                "Migration reconstructed historical contract parties or changed delivery history.");
            var sourceCustomer = checkedSource.Entities.OfType<Customer>().Single(x => x.TenantId == sourceTenant);
            Require.That(customer.Contacts is { Count: 3 } && customer.Contacts.SequenceEqual(sourceCustomer.Contacts!)
                && customer.Contacts.Count(contact => contact.IsPrimary) == 1 && customer.Contacts.Count(contact => contact.Archived) == 2,
                "Migration changed active, primary or archived customer contact identities and actor history.");
            var retainedOperationalRows = await db.Entities.IgnoreQueryFilters().Where(x => x.TenantId == sourceTenant)
                .Select(x => new { x.Kind, x.Data }).ToListAsync();
            VerifyOperationalLedgers(retainedOperationalRows.Select(x => AppJson.Read(x.Kind, x.Data)).ToArray(), bundle.Memberships);
            Require.That(AppJson.Read<PortalShare>((await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.Kind == "portal-shares")).Data).RevokedAt == imported.ImportedAt, "Legacy portal permission was not quarantined.");
            var importedStock = (await db.Entities.IgnoreQueryFilters().Where(x => x.Kind == "stock-movements" && x.TenantId == sourceTenant)
                .Select(x => x.Data).ToListAsync()).Select(AppJson.Read<StockMovement>).ToArray();
            VerifyWarehouseBranches(importedStock);
            var importedInspectionDocuments = await db.Documents.IgnoreQueryFilters()
                .Where(x => x.TenantId == sourceTenant && x.SourceKind == "equipment-inspections").ToListAsync();
            Require.That(importedInspectionDocuments.Count == 2 && importedInspectionDocuments.Any(x => x.State == "Ready")
                && importedInspectionDocuments.Any(x => x.State == "Archived"), "Migration lost available or archived equipment inspection originals.");
            foreach (var document in importedInspectionDocuments)
            {
                var inspection = AppJson.Read<EquipmentInspection>((await db.Entities.IgnoreQueryFilters()
                    .SingleAsync(x => x.TenantId == sourceTenant && x.Kind == "equipment-inspections" && x.Id == document.SourceId)).Data);
                Require.That(inspection.Completion?.Digest == document.SourceDigest && document.RendererVersion == "equipment-inspection-original-v1"
                    && document.ProjectId is null && document.ProjectKind is null,
                    "Imported inspection original lost its exact completion source or changed the sealed outcome.");
            }
            foreach (var source in sourceSupplierInvoices)
            {
                var id = source.Data.GetProperty("id").GetGuid();
                var row = await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.Kind == source.Kind && x.Id == id);
                using var stored = JsonDocument.Parse(row.Data);
                var receipt = await db.Receipts.IgnoreQueryFilters().SingleAsync(x => x.TenantId == row.TenantId && x.ActorId == Guid.Empty && x.Key == MigrationImport.EntityKey(source.Kind, id));
                var mapping = AppJson.Read<MigrationSourceMarker>(receipt.Response);
                Require.That(MigrationJson.Hash(stored.RootElement) == source.Sha256 && mapping.SourceSha256 == source.Sha256 && mapping.ImportedSha256 == source.Sha256
                    && stored.RootElement.TryGetProperty("electronicSource", out _) == source.Data.TryGetProperty("electronicSource", out _),
                    "Supplier invoice import changed the historical JSON shape, source checksum or provenance.");
            }
            foreach (var tenant in bundle.Tenants) Require.That(await RecoveryBarrier.IsHeld(db, tenant.Data.Id), "Imported tenant lacks a persistent hold.");
        }
        var scope = new TenantScope { TenantId = sourceTenant, UserId = imported.OperatorUserId, Role = BusinessRole.ManagingDirector };
        await using (var db = new FirstBlickDbContext(new DbContextOptionsBuilder<FirstBlickDbContext>().UseNpgsql(target.ConnectionString).Options, scope))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({sourceTenant.ToString()}, 0))");
            var sourceCounter = bundle.Counters.Single(x => x.TenantId == sourceTenant && x.Kind == "AN");
            var actualNext = await new EntityStore(db, scope).NextNumber("AN", new DateOnly(sourceCounter.Year, 1, 1)); await db.SaveChangesAsync();
            Require.That(actualNext == sourceCounter.ExpectedNextNumber, "The real application's next-number service collided after import.");
            await transaction.RollbackAsync();
        }
        // Prove the read-only compare detects post-import mutation, then restore it.
        await using (var db = target.Db())
        {
            var counter = await db.Counters.IgnoreQueryFilters().FirstAsync(); var saved = counter.Value; counter.Value++; await db.SaveChangesAsync();
            var mismatch = await MigrationCompare.Run(settings, sourceDirectory, database, bucket, Path.Combine(directory, "changed-target-report.json"));
            Require.That(mismatch.State == "Mismatch" && mismatch.Issues.Any(x => x.Location == "counters"), "Compare accepted changed target numbering.");
            counter.Value = saved; await db.SaveChangesAsync();
        }
        foreach (var source in sourceSupplierInvoices)
        {
            await using var db = target.Db(); var id = source.Data.GetProperty("id").GetGuid();
            var row = await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.Kind == source.Kind && x.Id == id); var saved = row.Data;
            var changed = JsonNode.Parse(saved)!.AsObject(); var wasExplicit = changed.ContainsKey("electronicSource");
            if (wasExplicit) changed.Remove("electronicSource"); else changed["electronicSource"] = null;
            row.Data = changed.ToJsonString(AppJson.Options); await db.SaveChangesAsync();
            try
            {
                var reportName = wasExplicit ? "supplier-invoice-removed-null-target.json" : "supplier-invoice-added-null-target.json";
                var mismatch = await MigrationCompare.Run(settings, sourceDirectory, database, bucket, Path.Combine(directory, reportName));
                Require.That(mismatch.State == "Mismatch" && mismatch.Issues.Any(x => x.Location == $"{source.Kind}/{id}"),
                    "Compare accepted a changed historical electronicSource property shape.");
            }
            finally { row.Data = saved; await db.SaveChangesAsync(); }
        }
        // A later disaster restore must retain an unclaimed legacy account's
        // non-UUID source identity and historical role/activity. Roll back this
        // isolated mutation after verifying the exact recovery implementation.
        await using (var db = target.Db())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(); var recoveryId = Guid.NewGuid();
            await RecoveryAccounts.QuarantineExisting(db, recoveryId, imported.BundleSha256); await db.SaveChangesAsync();
            foreach (var sourceMember in bundle.Memberships)
            {
                var sourceAccount = bundle.Accounts.Single(x => x.Id == sourceMember.UserId); var key = MigrationAccountMarker.Key(sourceAccount.Id);
                var receipt = await db.Receipts.IgnoreQueryFilters().SingleAsync(x => x.TenantId == sourceMember.TenantId && x.ActorId == Guid.Empty && x.Key == key);
                var marker = AppJson.Read<MigrationAccountMarker>(receipt.Response);
                Require.That(marker.SourceAccountId == sourceAccount.SourceId && marker.SourceRole == sourceMember.Role && marker.SourceActive == sourceMember.Active
                    && marker.State == "Pending" && marker.BundleId == recoveryId, "Recovery lost the original pending account's source identity or membership provenance.");
                var evidenceKey = $"operations.recovery-account:{recoveryId:N}:{sourceAccount.Id:N}";
                Require.That(await db.Receipts.IgnoreQueryFilters().AnyAsync(x => x.TenantId == sourceMember.TenantId && x.ActorId == Guid.Empty && x.Key == evidenceKey), "Previous account provenance was overwritten without preserving its original receipt.");
            }
            await transaction.RollbackAsync();
        }
        string serverVersion;
        await using (var connection = await PostgresFiles.Open(target)) serverVersion = await PostgresFiles.Scalar(connection, "SHOW server_version");
        await PostgresFiles.DropDrillDatabase(settings, database, nonce); await StorageFiles.DeleteDrillBucket(client, bucket, nonce);
        watch.Stop();
        var report = new { status = "passed", completedAt = DateTimeOffset.UtcNow, format = bundle.Format, bundle.BundleId, sourceBundleSha256 = imported.BundleSha256,
            postgreSql = serverVersion, entities = imported.Entities, documents = imported.Documents, historicalRecords = imported.HistoricalRecords,
            sourceIdMappingsPreserved = true, immutableSnapshotsAndPartyNamesPreserved = true, knownDeliveryHistoryPreserved = true, noLegacyJobsCreated = true,
            partialPaymentsAndCrossLedgerCapacityVerified = true, exactObjectBytesAndVersionMappingsVerified = true, oldCredentialsAndRolesInactive = true,
            historicalSupplierInvoiceJsonShapesAndChecksumsPreserved = true, changedSupplierInvoiceJsonShapeDetected = true,
            warehouseTransferChainsAndReturnsPreserved = true, warehouseBranchCapacityIndependentOfOtherStockVerified = true,
            stockLedgerVisibilityChronologyAndPurchaseStatusVerified = true,
            equipmentInspectionEvidenceAndAvailabilityPreserved = true,
            equipmentOriginalsAndArchivedDocumentsPreserved = true,
            customerContactIdentitiesPrimaryArchiveAndActorsPreserved = true,
            customerCommunicationSnapshotsVoidsAndInactiveActorsPreserved = true,
            vehicleChronologyCostsReversalsAndHistoricalIdentitiesPreserved = true,
            namedFreshOperatorVerified = true, portalGrantsRevoked = true, persistentRecoveryHoldVerified = true, repeatHasNoDatabaseOrObjectWrites = true,
            laterRecoveryPreservesPendingSourceIdentity = true,
            actualNextNumberVerified = true, changedTargetDetected = true, exactCanonicalNumbersVerified = true, negativeCases = negatives,
            verifiedControls = imported.VerifiedControls, objectMappings = imported.ObjectMappings, isolatedTargetCleaned = true, totalSeconds = watch.Elapsed.TotalSeconds,
            boundary = "Synthetic canonical export tested against real PostgreSQL and versioned S3. No unavailable D1/R2 source export, commercial ownership or real cutover is claimed." };
        await MigrationJson.WriteReport(Path.Combine(directory, "migration-drill.json"), report); return report;
    }

    // The container drill and isolated canonical-source harness execute the same
    // mutations, so fixture interactions cannot hide behind a narrower local suite.
    private static async Task RejectCanonicalSources(MigrationBundle bundle, IEnumerable<TenantEntity> original,
        Func<string, MigrationBundle, Func<MigrationIssue, bool>?, Task> reject)
    {
        var entities = original.ToArray();
        Task Reject(string name, MigrationBundle altered, Func<MigrationIssue, bool>? expectedIssue = null) =>
            reject(name, altered, expectedIssue);
        await Reject("supplier-invoice-missing-other-default", ChangeSupplierInvoiceShape(bundle, value => value.Remove("reviewNote")),
            issue => issue.Location.StartsWith("supplier-invoices/", StringComparison.Ordinal) && issue.Message.Contains("complete canonical typed JSON", StringComparison.Ordinal));
        await Reject("supplier-invoice-unknown-property", ChangeSupplierInvoiceShape(bundle, value => value["unexpectedLegacyProperty"] = null),
            issue => issue.Location.StartsWith("supplier-invoices/", StringComparison.Ordinal) && issue.Message == nameof(JsonException));
        await Reject("changed-frozen-price", Change<Quote>(bundle, quote => quote.Lines[0].UnitMaterialCost += 1m));
        await Reject("missing-historical-parties", Change<Quote>(bundle, quote => quote.Presentation = null));
        await Reject("unknown-dispatch-state", Change<Quote>(bundle, quote => quote.Deliveries[0].State = DeliveryState.Uncertain));
        await Reject("missing-tenant-reference", Change<Project>(bundle, project => project.CustomerId = Guid.NewGuid()));
        await Reject("unknown-method-version", Change<TechnicalRecord>(bundle, calculation => { calculation.MethodVersion = "999.0.0"; calculation.Digest = TechnicalRecords.Digest(calculation); }));
        var invalidZone = AppJson.Read<BusinessTenant>(AppJson.Serialize(bundle.Tenants[0].Data)); invalidZone.TimeZone = "Synthetic/Unavailable_Zone";
        await Reject("unknown-business-timezone", bundle with { Tenants = [bundle.Tenants[0] with { Data = invalidZone }, .. bundle.Tenants.Skip(1)] });
        var customerBankId = entities.OfType<PaymentAllocation>().Single().BankTransactionId;
        await Reject("cross-ledger-bank-overallocation", Change<SupplierPaymentAllocation>(bundle, allocation =>
        { allocation.BankTransactionId = customerBankId; allocation.PostedDigest = PayablesWorkflow.AllocationDigest(allocation); }, x => x.Direction == SupplierPaymentDirection.Refund));
        await Reject("warehouse-transfer-pair-mismatch", Change<StockMovement>(bundle, movement =>
        { movement.Quantity -= 1m; movement.PostedDigest = InventoryRules.MovementDigest(movement); }, x => x.Kind == StockMovementKind.TransferOut));
        await Reject("supplier-return-wrong-source", Change<StockMovement>(bundle, movement =>
        { movement.Reference = Guid.NewGuid().ToString("D"); movement.PostedDigest = InventoryRules.MovementDigest(movement); }, x => x.Kind == StockMovementKind.SupplierReturn));
        await Reject("project-return-wrong-value", Change<StockMovement>(bundle, movement =>
        { movement.UnitCost += 1m; movement.PostedDigest = InventoryRules.MovementDigest(movement); }, x => x.Kind == StockMovementKind.Return));
        await Reject("warehouse-chain-cycle", Change<StockMovement>(bundle, movement =>
        { movement.TransferSourceId = movement.Id; movement.PostedDigest = InventoryRules.MovementDigest(movement); }, x => x.Kind == StockMovementKind.TransferIn));
        await Reject("warehouse-branch-overallocation-with-other-stock", OverallocateWarehouseBranch(bundle),
            issue => issue.Code == "invalid_transfer_chain" && issue.Message.Contains("over-allocates a source quantity", StringComparison.Ordinal));
        await Reject("purchase-receipt-status-mismatch", Change<PurchaseOrder>(bundle, purchase => purchase.Status = PurchaseOrderStatus.Received),
            issue => issue.Message == "Imported purchase order status differs from its net purchase receipts and supplier returns.");
        await Reject("stock-movement-after-export", Change<StockMovement>(bundle, movement =>
        { movement.OccurredAt = bundle.ExportedAt.AddTicks(10); movement.PostedDigest = InventoryRules.MovementDigest(movement); }, x => x.Kind == StockMovementKind.Adjustment),
            issue => issue.Message == "Historical inventory movements cannot postdate the export.");
        await Reject("archived-stock-movement", Change<StockMovement>(bundle, movement => movement.Archived = true, x => x.Kind == StockMovementKind.Adjustment),
            issue => issue.Message == "Historical inventory movements cannot be archived out of the active stock ledger.");
        await Reject("equipment-result-projection-mismatch", Change<Equipment>(bundle, equipment => equipment.LastInspectionOn = equipment.LastInspectionOn!.Value.AddDays(-1)));
        await Reject("equipment-missing-planned-check", Change<Equipment>(bundle, equipment => equipment.PendingInspectionId = null));
        var outOfOrderEquipment = Change<EquipmentInspection>(bundle, inspection =>
        {
            var changed = inspection.Completion! with { PerformedOn = inspection.Completion.PerformedOn.AddDays(-1) };
            inspection.Completion = changed with { Digest = EquipmentInspectionWorkflow.CompletionDigest(inspection, changed) };
        }, x => x.Completion?.Result == EquipmentInspectionResult.Passed);
        outOfOrderEquipment = Change<Equipment>(outOfOrderEquipment, equipment => equipment.LastInspectionOn = equipment.LastInspectionOn!.Value.AddDays(-1));
        await Reject("equipment-out-of-order-performance", outOfOrderEquipment);
        await Reject("equipment-unknown-historical-actor", Change<EquipmentInspection>(bundle, inspection =>
        {
            var changed = inspection.Completion! with { RecordedBy = Guid.NewGuid() };
            inspection.Completion = changed with { Digest = EquipmentInspectionWorkflow.CompletionDigest(inspection, changed) };
        }, x => x.Status == EquipmentInspectionStatus.Completed));
        await Reject("equipment-original-wrong-completion-digest", ChangeInspectionDocument(bundle, document => document.SourceDigest = new string('0', 64)),
            issue => issue.Message == "An equipment inspection original must bind to the sealed completion digest.");
        var otherCompletedInspection = entities.OfType<EquipmentInspection>().Single(x => x.Completion?.Result == EquipmentInspectionResult.Passed);
        await Reject("equipment-original-swapped-source", ChangeInspectionDocument(bundle, document => document.SourceId = otherCompletedInspection.Id),
            issue => issue.Message == "An equipment inspection original must bind to the sealed completion digest.");
        var scheduledInspection = entities.OfType<EquipmentInspection>().Single(x => x.Status == EquipmentInspectionStatus.Scheduled);
        await Reject("equipment-original-unperformed-source", ChangeInspectionDocument(bundle, document =>
        { document.SourceId = scheduledInspection.Id; document.SourceDigest = scheduledInspection.ScopeDigest; }),
            issue => issue.Message == "An equipment inspection original must refer to a completed inspection.");
        var cancelledInspection = entities.OfType<EquipmentInspection>().Single(x => x.Status == EquipmentInspectionStatus.Cancelled);
        await Reject("equipment-original-cancelled-source", ChangeInspectionDocument(bundle, document =>
        { document.SourceId = cancelledInspection.Id; document.SourceDigest = cancelledInspection.Cancellation!.Digest; }),
            issue => issue.Message == "An equipment inspection original must refer to a completed inspection.");
        await Reject("equipment-original-cross-tenant-source", ChangeInspectionDocument(bundle, document => document.TenantId = bundle.Tenants[1].Data.Id),
            issue => issue.Message.StartsWith("Missing tenant-qualified equipment-inspections reference", StringComparison.Ordinal));
        await Reject("equipment-original-project-rebinding", ChangeInspectionDocument(bundle, document =>
        { document.ProjectId = entities.OfType<Project>().Single().Id; document.ProjectKind = "projects"; }),
            issue => issue.Message == "An equipment inspection original must retain its supported document format and independent equipment source.");
        await Reject("equipment-original-changed-format-marker", ChangeInspectionDocument(bundle, document => document.RendererVersion = "unverified-original"),
            issue => issue.Message == "An equipment inspection original must retain its supported document format and independent equipment source.");
        await Reject("equipment-original-before-completion", ChangeInspectionDocument(bundle, document => document.CreatedAt = entities.OfType<EquipmentInspection>()
            .Single(x => x.Id == document.SourceId).Completion!.RecordedAt.AddTicks(-10)),
            issue => issue.Message == "An equipment inspection original cannot predate its sealed completion or postdate the export.");
        await Reject("equipment-original-after-export", ChangeInspectionDocument(bundle, document => document.CreatedAt = bundle.ExportedAt.AddTicks(10)),
            issue => issue.Message == "An equipment inspection original cannot predate its sealed completion or postdate the export.");
        await Reject("equipment-original-missing-version", ChangeInspectionDocument(bundle, document => document.VersionId = null),
            issue => issue.Message == "An equipment inspection original must retain its immutable source version.");
        await Reject("customer-contact-unknown-creator", ChangeContact(bundle, contact => !contact.Archived,
            (_, contact) => contact with { CreatedBy = Guid.NewGuid() }),
            issue => issue.Message == "A historical actor/user must retain a supplied tenant membership, including inactive members.");
        await Reject("customer-contact-unknown-editor", ChangeContact(bundle, contact => !contact.Archived,
            (_, contact) => contact with { UpdatedBy = Guid.NewGuid() }),
            issue => issue.Message == "A historical actor/user must retain a supplied tenant membership, including inactive members.");
        await Reject("customer-contact-unknown-archiver", ChangeContact(bundle, contact => contact.Archived, (_, contact) =>
        {
            var actor = Guid.NewGuid(); return contact with { UpdatedBy = actor, ArchivedBy = actor };
        }), issue => issue.Message == "A historical actor/user must retain a supplied tenant membership, including inactive members.");
        await Reject("customer-contact-after-customer-envelope", ChangeContact(bundle, contact => !contact.Archived,
            (customer, contact) => contact with { UpdatedAt = customer.UpdatedAt.AddTicks(10) }),
            issue => issue.Message == "Customer contact history must fit its retained customer envelope and export date.");
        await RejectOperationalLedgerSources(bundle, entities, Reject);
        var collision = bundle.Counters.Select(x => x.Kind == "AN" && x.Value > 0 ? x with { Value = x.Value - 1, ExpectedNextNumber = $"AN-{x.Year}-{x.Value:D6}" } : x).ToList();
        await Reject("numbering-collision", Controls(bundle with { Counters = collision }));
        await Reject("explicit-unresolved-source", bundle with { Uncertainties = [new("legacy_delivery_unknown", "Synthetic old export lacks provider acknowledgement; the original uncertainty is retained.", bundle.Tenants[0].Data.Id, "quotes", entities.OfType<Quote>().Single().Id, bundle.EvidenceFiles[0].File)] });
    }

    private static async Task WriteBundle(string directory, MigrationBundle bundle) => await File.WriteAllTextAsync(Path.Combine(directory, "migration.json"), JsonSerializer.Serialize(bundle, MigrationJson.Options));
    private static MigrationBundle ChangeSupplierInvoiceShape(MigrationBundle bundle, Action<JsonObject> mutation)
    {
        var list = bundle.Entities.ToList(); var index = list.FindIndex(x => x.Kind == "supplier-invoices");
        var value = JsonNode.Parse(list[index].Data.GetRawText())!.AsObject(); mutation(value);
        var data = JsonSerializer.SerializeToElement(value, MigrationJson.Options); list[index] = list[index] with { Data = data, Sha256 = MigrationJson.Hash(data) };
        var changed = bundle with { Entities = list };
        // Only source spelling changes in these fixtures. Preserve the original
        // typed business totals so an unrelated control mismatch cannot mask the
        // exact canonical-contract rejection being exercised.
        return changed with { Controls = MigrationControls.Calculate(changed, bundle.Entities.Select(x => AppJson.Read(x.Kind, x.Data.GetRawText()))) };
    }
    private static MigrationBundle Change<T>(MigrationBundle bundle, Action<T> mutation, Func<T, bool>? select = null) where T : TenantEntity
    {
        var kind = BusinessRegistry.KindOf<T>(); var list = bundle.Entities.ToList();
        var index = list.FindIndex(x => x.Kind == kind && (select is null || select(AppJson.Read<T>(x.Data.GetRawText()))));
        var entity = AppJson.Read<T>(list[index].Data.GetRawText()); mutation(entity); var data = MigrationJson.Element(entity);
        list[index] = list[index] with { Data = data, Sha256 = MigrationJson.Hash(data) }; return Controls(bundle with { Entities = list });
    }
    private static MigrationBundle ChangeContact(MigrationBundle bundle, Func<CustomerContact, bool> select,
        Func<Customer, CustomerContact, CustomerContact> mutation) => Change<Customer>(bundle, customer =>
    {
        var index = customer.Contacts!.FindIndex(contact => select(contact));
        // An archived contact seals its last editor/time to its archiver/time.
        // Select a contact whose other invariants remain valid for this fault.
        customer.Contacts[index] = mutation(customer, customer.Contacts[index]);
    }, customer => customer.Contacts?.Any(select) == true);
    private static MigrationBundle ChangeInspectionDocument(MigrationBundle bundle, Action<DocumentObject> mutation)
    {
        var documents = bundle.Documents.ToList(); var index = documents.FindIndex(x => x.Data.SourceKind == "equipment-inspections");
        var document = AppJson.Read<DocumentObject>(AppJson.Serialize(documents[index].Data)); mutation(document);
        documents[index] = documents[index] with { Data = document, SourceVersionId = document.VersionId };
        return Controls(bundle with { Documents = documents });
    }
    private static MigrationBundle Controls(MigrationBundle bundle) => bundle with { Controls = MigrationControls.Calculate(bundle, bundle.Entities.Select(x => AppJson.Read(x.Kind, x.Data.GetRawText()))) };

    private static void VerifyOperationalLedgers(IEnumerable<TenantEntity> source, IReadOnlyList<MigrationMembership> memberships)
    {
        var entities = source.ToArray();
        var communications = entities.OfType<CustomerCommunication>().ToArray();
        var voiding = entities.OfType<CustomerCommunicationVoid>().Single();
        Require.That(communications.Length == 2, "Migration lost an original communication entry.");
        var customer = entities.OfType<Customer>().Single(x => x.Id == communications[0].CustomerId);
        foreach (var entry in communications)
        {
            entry.Validate();
            var contact = customer.Contacts!.Single(x => x.Id == entry.Contact!.Id);
            var project = entities.OfType<Project>().Single(x => x.Id == entry.Project!.Id);
            Require.That(entry.CustomerName != customer.Name && entry.Contact!.Name != contact.Name && contact.Archived
                && entry.Project!.Name != project.Name && memberships.Any(x => x.TenantId == entry.TenantId && x.UserId == entry.RecordedBy && !x.Active),
                "Communication import lost historical labels, an archived contact or inactive actor provenance.");
        }
        Require.That(communications.Any(x => x.OccurredAt < customer.CreatedAt),
            "Historical conversations before master-data creation must remain importable.");
        CustomerCommunicationWorkflow.EnsureVoidSource(communications.Single(x => x.Id == voiding.CommunicationId), voiding);

        var trips = entities.OfType<VehicleTrip>().ToArray(); var costs = entities.OfType<VehicleCost>().ToArray();
        var reversals = entities.OfType<VehicleLedgerReversal>().ToArray();
        Require.That(trips.Length == 3 && costs.Length == 2 && reversals.Length == 2,
            "Migration lost retained vehicle originals or reversals.");
        VehicleLedgerWorkflow.ValidateActiveTrips(trips, reversals);
        foreach (var entry in trips.Cast<VehicleLedgerEntry>().Concat(costs))
        {
            entry.Validate();
            var vehicle = entities.OfType<Equipment>().Single(x => x.Id == entry.VehicleId);
            var project = entities.OfType<Project>().Single(x => x.Id == entry.ProjectId);
            Require.That(vehicle.Archived && entry.VehicleSnapshot!.Name != vehicle.Name && entry.ProjectTitle != project.Name
                && memberships.Any(x => x.TenantId == entry.TenantId && x.UserId == entry.RecordedBy && !x.Active),
                "Vehicle import lost original labels, an archived vehicle or inactive actor provenance.");
        }
        foreach (var trip in trips)
        {
            var driver = entities.OfType<Employee>().Single(x => x.Id == trip.DriverEmployeeId);
            Require.That(!driver.Active && driver.Archived && driver.Name != trip.DriverName,
                "Vehicle import lost the retained inactive driver's historical identity.");
        }
        foreach (var reversal in reversals)
            VehicleLedgerWorkflow.ValidateReversal(reversal, entities.OfType<VehicleLedgerEntry>().Single(x => x.Id == reversal.SourceId));
        var reversed = reversals.Select(x => x.SourceId).ToHashSet();
        Require.That(trips.Where(x => !reversed.Contains(x.Id)).Sum(x => x.OdometerEndKm - x.OdometerStartKm) == 50m
            && costs.Where(x => !reversed.Contains(x.Id)).Sum(x => x.Amount) == 85m,
            "Migration changed effective operational kilometres or costs after retained reversals.");
    }

    private static async Task RejectOperationalLedgerSources(MigrationBundle bundle, IEnumerable<TenantEntity> original,
        Func<string, MigrationBundle, Func<MigrationIssue, bool>?, Task> reject)
    {
        var entities = original.ToArray();
        var communications = entities.OfType<CustomerCommunication>().ToArray();
        var voided = entities.OfType<CustomerCommunicationVoid>().Single();
        var active = communications.Single(x => x.Id != voided.CommunicationId);
        var customer = entities.OfType<Customer>().Single(x => x.Id == active.CustomerId);
        bool ActorIssue(MigrationIssue issue) => issue.Message == "A historical actor/user must retain a supplied tenant membership, including inactive members.";
        await reject("communication-missing-retained-contact", Change<CustomerCommunication>(bundle, entry =>
        { entry.Contact = entry.Contact! with { Id = Guid.NewGuid() }; entry.Digest = CustomerCommunicationWorkflow.Digest(entry); }, x => x.Id == active.Id),
            issue => issue.Message == "A communication contact must retain its original customer-owned identity before recording.");
        await reject("communication-missing-retained-project", Change<CustomerCommunication>(bundle, entry =>
        { entry.Project = entry.Project! with { Id = Guid.NewGuid() }; entry.Digest = CustomerCommunicationWorkflow.Digest(entry); }, x => x.Id == active.Id), null);
        await reject("communication-unknown-historical-recorder", Change<CustomerCommunication>(bundle, entry =>
        { entry.RecordedBy = Guid.NewGuid(); entry.Digest = CustomerCommunicationWorkflow.Digest(entry); }, x => x.Id == active.Id), ActorIssue);
        await reject("communication-unknown-historical-voider", Change<CustomerCommunicationVoid>(bundle, entry =>
        { entry.VoidedBy = Guid.NewGuid(); entry.Digest = CustomerCommunicationWorkflow.VoidDigest(entry); }), ActorIssue);
        await reject("communication-contact-created-after-recording", Change<Customer>(bundle, value =>
            value.Contacts![0] = value.Contacts[0] with { CreatedAt = active.RecordedAt.AddTicks(10) }, x => x.Id == customer.Id),
            issue => issue.Message == "A communication contact must retain its original customer-owned identity before recording.");
        await reject("communication-project-created-after-recording", Change<Project>(bundle,
            value => value.CreatedAt = active.RecordedAt.AddTicks(10), x => x.Id == active.Project!.Id),
            issue => issue.Message == "A communication project must belong to its customer and exist before recording.");
        await reject("communication-after-customer-envelope", Change<CustomerCommunication>(bundle, entry =>
        {
            entry.RecordedAt = customer.UpdatedAt.AddTicks(10); entry.CreatedAt = entry.RecordedAt; entry.UpdatedAt = entry.RecordedAt;
            entry.Digest = CustomerCommunicationWorkflow.Digest(entry);
        }, x => x.Id == active.Id), issue => issue.Message == "A communication must fit its customer envelope, recording and export date.");
        await reject("communication-after-export", Change<CustomerCommunication>(bundle, entry =>
        {
            entry.RecordedAt = bundle.ExportedAt.AddTicks(10); entry.CreatedAt = entry.RecordedAt; entry.UpdatedAt = entry.RecordedAt;
            entry.Digest = CustomerCommunicationWorkflow.Digest(entry);
        }, x => x.Id == active.Id), null);
        await reject("communication-void-wrong-source-digest", Change<CustomerCommunicationVoid>(bundle, entry =>
        { entry.SourceDigest = new string('0', 64); entry.Digest = CustomerCommunicationWorkflow.VoidDigest(entry); }),
            issue => issue.Message == "A communication void must retain the original customer's exact journal entry and digest.");
        await reject("communication-void-before-original", Change<CustomerCommunicationVoid>(bundle, entry =>
        {
            entry.VoidedAt = communications.Single(x => x.Id == entry.CommunicationId).RecordedAt.AddTicks(-10);
            entry.CreatedAt = entry.VoidedAt; entry.UpdatedAt = entry.VoidedAt; entry.Digest = CustomerCommunicationWorkflow.VoidDigest(entry);
        }), issue => issue.Message == "A communication void must fit its original recording, customer envelope and export date.");
        await reject("communication-void-after-customer-envelope", Change<CustomerCommunicationVoid>(bundle, entry =>
        {
            entry.VoidedAt = customer.UpdatedAt.AddTicks(10); entry.CreatedAt = entry.VoidedAt; entry.UpdatedAt = entry.VoidedAt;
            entry.Digest = CustomerCommunicationWorkflow.VoidDigest(entry);
        }), issue => issue.Message == "A communication void must fit its original recording, customer envelope and export date.");
        await reject("communication-duplicate-void", Duplicate<CustomerCommunicationVoid>(bundle,
            entry => entry.Digest = CustomerCommunicationWorkflow.VoidDigest(entry)),
            issue => issue.Message == "The imported record violates its shared database uniqueness contract.");

        var trips = entities.OfType<VehicleTrip>().OrderBy(x => x.RecordedAt).ToArray();
        var secondActive = trips.Last(); var vehicle = entities.OfType<Equipment>().Single(x => x.Id == secondActive.VehicleId);
        await reject("vehicle-trip-unknown-historical-recorder", Change<VehicleTrip>(bundle, entry =>
        { entry.RecordedBy = Guid.NewGuid(); entry.Digest = entry.CalculateDigest(); }), ActorIssue);
        await reject("vehicle-reversal-unknown-historical-recorder", Change<VehicleLedgerReversal>(bundle, entry =>
        { entry.RecordedBy = Guid.NewGuid(); entry.Digest = entry.CalculateDigest(); }), ActorIssue);
        await reject("vehicle-trip-driver-created-after-recording", Change<Employee>(bundle,
            entry => entry.CreatedAt = trips[0].RecordedAt.AddTicks(10), x => x.Id == trips[0].DriverEmployeeId),
            issue => issue.Message == "A vehicle trip's retained driver must exist before recording.");
        await reject("vehicle-trip-active-overlap", Change<VehicleTrip>(bundle, entry =>
        {
            entry.Start = trips[0].Start; entry.End = trips[0].End; entry.Digest = entry.CalculateDigest();
        }, x => x.Id == secondActive.Id), issue => issue.Location.StartsWith("ledger/", StringComparison.Ordinal));
        await reject("vehicle-trip-backwards-odometer", Change<VehicleTrip>(bundle, entry =>
        { entry.OdometerStartKm = 110m; entry.Digest = entry.CalculateDigest(); }, x => x.Id == secondActive.Id),
            issue => issue.Location.StartsWith("ledger/", StringComparison.Ordinal));
        await reject("vehicle-trip-after-vehicle-envelope", Change<VehicleTrip>(bundle, entry =>
        {
            entry.RecordedAt = vehicle.UpdatedAt.AddTicks(10); entry.CreatedAt = entry.RecordedAt; entry.UpdatedAt = entry.RecordedAt;
            entry.Digest = entry.CalculateDigest();
        }, x => x.Id == secondActive.Id), issue => issue.Message == "A vehicle ledger entry must retain its vehicle identity and fit the vehicle envelope and export date.");
        await reject("vehicle-cost-after-recording-date", Change<VehicleCost>(bundle, entry =>
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(bundle.Tenants.Single(x => x.Data.Id == entry.TenantId).Data.TimeZone);
            entry.SpentOn = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.RecordedAt, zone).DateTime).AddDays(1);
            entry.Digest = entry.CalculateDigest();
        }), issue => issue.Message == "A vehicle cost cannot occur after its recording's business date.");
        await reject("vehicle-cost-after-export", Change<VehicleCost>(bundle, entry =>
        {
            entry.RecordedAt = bundle.ExportedAt.AddTicks(10); entry.CreatedAt = entry.RecordedAt; entry.UpdatedAt = entry.RecordedAt;
            entry.Digest = entry.CalculateDigest();
        }), null);
        await reject("vehicle-reversal-wrong-source-digest", Change<VehicleLedgerReversal>(bundle, entry =>
        { entry.SourceDigest = new string('0', 64); entry.Digest = entry.CalculateDigest(); }),
            issue => issue.Message == "The reversal must retain its exact original vehicle entry and chronological evidence.");
        await reject("vehicle-reversal-before-original", Change<VehicleLedgerReversal>(bundle, entry =>
        {
            entry.RecordedAt = entities.OfType<VehicleLedgerEntry>().Single(x => x.Id == entry.SourceId).RecordedAt.AddTicks(-10);
            entry.CreatedAt = entry.RecordedAt; entry.UpdatedAt = entry.RecordedAt; entry.Digest = entry.CalculateDigest();
        }), issue => issue.Message == "The reversal must retain its exact original vehicle entry and chronological evidence.");
        await reject("vehicle-reversal-after-vehicle-envelope", Change<VehicleLedgerReversal>(bundle, entry =>
        {
            entry.RecordedAt = vehicle.UpdatedAt.AddTicks(10); entry.CreatedAt = entry.RecordedAt; entry.UpdatedAt = entry.RecordedAt;
            entry.Digest = entry.CalculateDigest();
        }), issue => issue.Message == "A vehicle reversal must fit its vehicle envelope and export date.");
        await reject("vehicle-duplicate-reversal", Duplicate<VehicleLedgerReversal>(bundle, entry => entry.Digest = entry.CalculateDigest()),
            issue => issue.Message == "The imported record violates its shared database uniqueness contract.");
    }

    private static MigrationBundle Duplicate<T>(MigrationBundle bundle, Action<T> reseal) where T : TenantEntity
    {
        var kind = BusinessRegistry.KindOf<T>(); var source = bundle.Entities.First(x => x.Kind == kind);
        var duplicate = AppJson.Read<T>(source.Data.GetRawText()); duplicate.Id = Guid.NewGuid(); reseal(duplicate);
        var data = MigrationJson.Element(duplicate);
        return Controls(bundle with { Entities = [.. bundle.Entities, source with { SourceId = duplicate.Id.ToString(), Data = data, Sha256 = MigrationJson.Hash(data) }] });
    }

    private static void VerifyWarehouseBranches(IReadOnlyCollection<StockMovement> movements)
    {
        var parent = movements.Single(x => x.Kind == StockMovementKind.TransferIn && x.Location == "Außenlager");
        var branch = movements.Single(x => x.Kind == StockMovementKind.TransferIn && x.Location == "Nebenlager");
        var returnedBranch = movements.Single(x => x.Kind == StockMovementKind.TransferIn && x.Location == "Baustellenlager");
        Require.That(branch.TransferSourceId == parent.Id && returnedBranch.TransferSourceId == parent.Id
            && InventoryTransferChain.Inspect(parent, movements).EvidenceRemaining == 1m
            && InventoryTransferChain.Inspect(branch, movements).EvidenceRemaining == 3m
            && InventoryTransferChain.Inspect(returnedBranch, movements).EvidenceRemaining == 4m
            && InventoryRules.Balance(parent.TenantId, parent.MaterialId, parent.Location, movements, []).OnHand == 9m,
            "Imported warehouse branches must retain their separate receipt capacity despite unrelated counted stock and a downstream return.");
    }

    private static MigrationBundle OverallocateWarehouseBranch(MigrationBundle bundle)
    {
        var branch = bundle.Entities.Where(x => x.Kind == "stock-movements").Select(x => AppJson.Read<StockMovement>(x.Data.GetRawText()))
            .Single(x => x.Kind == StockMovementKind.TransferIn && x.Location == "Nebenlager");
        var changed = Change<StockMovement>(bundle, movement =>
        { movement.Quantity += 2m; movement.PostedDigest = InventoryRules.MovementDigest(movement); }, x => x.Id == branch.Id);
        changed = Change<StockMovement>(changed, movement =>
        { movement.Quantity -= 2m; movement.PostedDigest = InventoryRules.MovementDigest(movement); }, x => x.Kind == StockMovementKind.TransferOut && x.Reference == branch.Reference);
        var movements = changed.Entities.Where(x => x.Kind == "stock-movements").Select(x => AppJson.Read<StockMovement>(x.Data.GetRawText())).ToArray();
        var parent = movements.Single(x => x.Id == branch.TransferSourceId);
        Require.That(InventoryRules.Balance(parent.TenantId, parent.MaterialId, parent.Location, movements, []).Available == 7m
            && movements.Where(x => x.Kind == StockMovementKind.TransferIn && x.TransferSourceId == parent.Id).Sum(x => x.Quantity) == parent.Quantity + 1m,
            "The negative branch fixture must over-allocate its source while retaining enough unrelated physical stock.");
        return changed;
    }

    private static async Task<MigrationBundle> Source(string directory, string release)
    {
        var at = MigrationImport.MicrosecondNow().AddDays(-1); var date = DateOnly.FromDateTime(at.UtcDateTime); var entities = new List<TenantEntity>();
        var tenant = new BusinessTenant { Name = "Synthetic legacy business" }; var second = new BusinessTenant { Name = "Synthetic second business" };
        var owner = new MigrationAccount("legacy-owner-17", Guid.NewGuid(), "legacy-owner@example.invalid", "Historical owner", null);
        var partner = new MigrationAccount("legacy-partner-4", Guid.NewGuid(), "legacy-partner@example.invalid", "Historical partner", null);
        var formerRecorder = new MigrationAccount("legacy-former-recorder-8", Guid.NewGuid(), "legacy-former-recorder@example.invalid", "Historical inactive recorder", null);
        T Add<T>(T entity) where T : TenantEntity { entity.CreatedAt = at; entity.UpdatedAt = at; entities.Add(entity); return entity; }
        var customer = Add(new Customer { TenantId = tenant.Id, Name = "Historical buyer name", Address = "Historical buyer address", Email = partner.Email });
        var building = Add(new Building { TenantId = tenant.Id, CustomerId = customer.Id, Name = "Legacy building", Address = "Legacy building address" });
        var project = Add(new Project { TenantId = tenant.Id, CustomerId = customer.Id, BuildingId = building.Id, Name = "Legacy contract project" });
        var quote = Add(new Quote { TenantId = tenant.Id, CustomerId = customer.Id, ProjectId = project.Id, Title = "Historical roof work",
            Lines = [new QuoteLine { Description = "Historical effective work", Quantity = 10m, UnitMaterialCost = 100m, VatPercent = 19m }] });
        CommercialWorkflow.IssueQuote(quote, $"AN-{date.Year}-000042"); CommercialWorkflow.FreezePresentation(quote, tenant.Name, customer, at);
        CommercialWorkflow.AcceptQuote(quote, quote.IssuedDigest, at, date);
        quote.Deliveries.Add(new DeliveryAttempt { Channel = CommunicationChannel.Email, Recipient = partner.Email, RequestedAt = at, State = DeliveryState.Sent,
            ResolvedAt = at, ProviderReference = "synthetic-preserved-provider-receipt", ResolutionNote = "Synthetic fixture: export contains the original provider acknowledgement." });
        var order = Add(CommercialWorkflow.CreateOrder(quote, $"AU-{date.Year}-000017"));
        var invoice = Add(InvoiceWorkflow.DraftFromOrder(order, customer)); invoice.SellerName = tenant.Name; invoice.SellerAddress = "Original seller address";
        invoice.DueDate = date.AddDays(14); invoice.ServiceFrom = date; invoice.ServiceUntil = date;
        InvoiceWorkflow.IssueForOrder(invoice, order, [], [], $"RE-{date.Year}-000073", date);
        var credit = Add(InvoiceWorkflow.DraftCreditNote(invoice, "Preserved partial correction")); credit.DueDate = date; credit.Lines[0].Quantity = 2m;
        InvoiceWorkflow.IssueCreditNote(credit, invoice, [], $"KO-{date.Year}-000002", date);
        var bank = Add(new BankTransaction { TenantId = tenant.Id, AccountReference = "legacy-account", ExternalId = "bank-row-1", Amount = 500m, BookedOn = date }); PaymentWorkflow.SealImport(bank);
        var payment = Add(PaymentWorkflow.Allocate(invoice, bank, [], [credit], 500m, 10m, 100m, "Preserved discount and retention", owner.Id, at));
        var supplier = Add(new Supplier { TenantId = tenant.Id, Name = "Historical supplier", Address = "Historical supplier address" });
        var material = Add(new Material { TenantId = tenant.Id, Name = "Historical roofing material", Sku = "MIGRATION-MATERIAL", Unit = "Stk" });
        var purchase = Add(new PurchaseOrder { TenantId = tenant.Id, SupplierId = supplier.Id, ProjectId = project.Id, Title = "Historical material order",
            Lines = [new PurchaseLine { MaterialId = material.Id, Description = "Retained material", Quantity = 20m, UnitNet = 10m, VatPercent = 19m }] });
        InventoryRules.Order(purchase, $"BE-{date.Year}-000003");
        var stock = new List<StockMovement>();
        StockMovement Stock(StockMovement movement) { stock.Add(movement); return Add(movement); }
        var receipt = Stock(InventoryRules.Receive(purchase, purchase.Lines[0].Id, 20m, stock, "Hauptlager", at));
        var firstTransfer = InventoryRules.Transfer(tenant.Id, material.Id, "Hauptlager", "Außenlager", 10m, 10m, 20m,
            "Historical source-bound warehouse transfer", stock, [], at.AddMinutes(1), receipt.Id);
        Stock(firstTransfer.Out); Stock(firstTransfer.In);
        var secondTransfer = InventoryRules.Transfer(tenant.Id, material.Id, "Außenlager", "Baustellenlager", 6m, 10m, 10m,
            "Historical second warehouse transfer", stock, [], at.AddMinutes(2), firstTransfer.In.Id);
        Stock(secondTransfer.Out); Stock(secondTransfer.In);
        var branchTransfer = InventoryRules.Transfer(tenant.Id, material.Id, "Außenlager", "Nebenlager", 3m, 10m, 4m,
            "Historical sibling warehouse branch", stock, [], at.AddMinutes(2).AddSeconds(10), firstTransfer.In.Id);
        Stock(branchTransfer.Out); Stock(branchTransfer.In);
        Stock(InventoryRules.CountAdjustment(tenant.Id, material.Id, "Außenlager", 1m, 9m, 10m,
            "Historical unrelated counted stock does not extend receipt provenance", "HISTORICAL-COUNT-1", stock, [], at.AddMinutes(2).AddSeconds(20)));
        Stock(InventoryRules.SupplierReturn(purchase, receipt, 2m, "Historical supplier return from the final warehouse",
            stock, [], at.AddMinutes(3), secondTransfer.In.Id));
        var reservation = Add(new MaterialReservation { TenantId = tenant.Id, MaterialId = material.Id, ProjectId = project.Id, Location = "Hauptlager", Quantity = 2m });
        InventoryRules.Reserve(reservation, stock, []);
        var consumption = Stock(InventoryRules.Consume(reservation, 2m, 10m, at.AddMinutes(4)));
        Stock(InventoryRules.ProjectReturn(reservation, consumption, 1m, "Historical unused material return", stock, at.AddMinutes(5)));
        var equipment = Add(new Equipment { TenantId = tenant.Id, Name = "Historical roofing machine", InventoryNumber = "MIGRATION-EQUIPMENT",
            Kind = EquipmentKind.Machine, Location = "Werkstatt", NextInspectionOn = date });
        var failedInspection = EquipmentInspectionWorkflow.Plan(equipment, EquipmentInspectionKind.Inspection, date,
            "Historical failed inspection", "Synthetic inspection record", owner.Id, at.AddMinutes(6));
        entities.Add(failedInspection);
        EquipmentInspectionWorkflow.Complete(equipment, failedInspection, date, EquipmentInspectionResult.Failed,
            "Synthetic inspector", "SYNTHETIC-CHECK-FAILED", "Retained defect description", null, false, date, owner.Id, at.AddMinutes(7));
        failedInspection.Revision = 2; failedInspection.UpdatedAt = at.AddMinutes(7);
        var repair = EquipmentInspectionWorkflow.Plan(equipment, EquipmentInspectionKind.Repair, date,
            "Historical repair and reassessment", "Synthetic documented reassessment", owner.Id, at.AddMinutes(8));
        entities.Add(repair);
        EquipmentInspectionWorkflow.Complete(equipment, repair, date, EquipmentInspectionResult.Passed,
            "Synthetic inspector", "SYNTHETIC-REPAIR-PASSED", "Documented repair and deployment assessment", date.AddDays(180), true, date, owner.Id, at.AddMinutes(9));
        repair.Revision = 2; repair.UpdatedAt = at.AddMinutes(9);
        var cancelled = EquipmentInspectionWorkflow.Plan(equipment, EquipmentInspectionKind.Maintenance, date.AddDays(90),
            "Historical appointment cancelled", "Retained unused appointment", owner.Id, at.AddMinutes(9).AddSeconds(10));
        entities.Add(cancelled);
        EquipmentInspectionWorkflow.Cancel(equipment, cancelled, "Superseded by the explicitly scheduled inspection", owner.Id, at.AddMinutes(9).AddSeconds(20));
        cancelled.Revision = 2; cancelled.UpdatedAt = at.AddMinutes(9).AddSeconds(20);
        entities.Add(EquipmentInspectionWorkflow.Plan(equipment, EquipmentInspectionKind.Inspection, date.AddDays(180),
            "Retained next equipment appointment", "Explicit operator-chosen date", owner.Id, at.AddMinutes(10)));
        equipment.Revision = 8; equipment.UpdatedAt = at.AddMinutes(10);
        var payable = Add(new SupplierInvoice { TenantId = tenant.Id, SupplierId = supplier.Id, Number = "SUP-17", Title = "Preserved supplier invoice", InvoiceDate = date, DueDate = date.AddDays(14), Net = 200m, Tax = 38m, Gross = 238m });
        PayablesWorkflow.Approve(payable, supplier, owner.Id, owner.Id, at, false);
        var outgoing = Add(new BankTransaction { TenantId = tenant.Id, AccountReference = "legacy-account", ExternalId = "bank-row-2", Amount = -238m, BookedOn = date }); PaymentWorkflow.SealImport(outgoing);
        var supplierPayment = Add(PayablesWorkflow.Allocate(payable, outgoing, [], [payment], [], 238m, 0m, "Preserved supplier payment", SupplierPaymentDirection.Payment, owner.Id, at));
        var supplierCredit = Add(new SupplierInvoice { TenantId = tenant.Id, SupplierId = supplier.Id, OriginalInvoiceId = payable.Id, Number = "SUP-CREDIT-2", Title = "Preserved supplier correction",
            Kind = SupplierInvoiceKind.CreditNote, InvoiceDate = date, DueDate = date, Net = 100m, Tax = 19m, Gross = 119m });
        PayablesWorkflow.ApproveCredit(supplierCredit, payable, supplier, [], owner.Id, owner.Id, at, false);
        var incomingRefund = Add(new BankTransaction { TenantId = tenant.Id, AccountReference = "legacy-account", ExternalId = "bank-row-3", Amount = 100m, BookedOn = date }); PaymentWorkflow.SealImport(incomingRefund);
        Add(PayablesWorkflow.Allocate(payable, incomingRefund, [supplierPayment], [payment], [supplierCredit], 100m, 0m, "Preserved supplier refund", SupplierPaymentDirection.Refund, owner.Id, at));
        Add(TechnicalRecords.Create(tenant.Id, project.Id, building.Id, null, "Historical supported geometry", "roof.rectangle", JsonSerializer.SerializeToElement(new RectangularRoofInput(10, 4, 3, "Synthetic measured source")), owner.Id, at));
        Add(new PortalShare { TenantId = tenant.Id, RecipientUserId = partner.Id, GrantedBy = owner.Id, ProjectId = project.Id, Title = "Historical named grant", SourceKind = "quotes", SourceId = quote.Id,
            SourceDigest = quote.IssuedDigest, ExpiresAt = at.AddDays(30), AllowAcceptance = true });
        Add(new Customer { TenantId = second.Id, Name = "Second tenant customer", Address = "Separate historical address" });
        customer.Name = "Current changed master name"; customer.Address = "Current changed master address"; // Frozen quote/invoice parties must remain historical.
        _ = CustomerContactWorkflow.Add(customer, new("Synthetic billing contact", "Buchhaltung", "billing@example.invalid", "", "Retained original contact", true), owner.Id, at.AddMinutes(11));
        _ = CustomerContactWorkflow.Add(customer, new("Synthetic site contact", "Objektbetreuung", "site@example.invalid", "", "Current primary contact", true), owner.Id, at.AddMinutes(12));
        var archivedContact = CustomerContactWorkflow.Add(customer, new("Synthetic former contact", "Frühere Objektbetreuung", "former@example.invalid", "", "Retained archived contact", false), owner.Id, at.AddMinutes(13));
        CustomerContactWorkflow.Archive(customer, archivedContact.Id, owner.Id, at.AddMinutes(14));
        customer.Revision = 5; customer.UpdatedAt = at.AddMinutes(14);
        SeedRetainedOperationalLedgers(tenant.Id, customer, project, formerRecorder.Id, owner.Id, at, entities);
        foreach (var entity in entities) entity.Validate();
        Directory.CreateDirectory(Path.Combine(directory, "evidence")); Directory.CreateDirectory(Path.Combine(directory, "objects"));
        await File.WriteAllTextAsync(Path.Combine(directory, "evidence", "source-records.txt"), "Synthetic canonical source only. Historical party names, prices, accepted contract, provider acknowledgement and old source IDs were intentionally retained. No real provider was called.\n");
        var evidence = await BundleFiles.Proof(directory, "evidence/source-records.txt");
        var documents = new List<MigrationDocument>();
        foreach (var selected in new[] { tenant, second })
        {
            var id = Guid.NewGuid(); var file = $"objects/{id:N}.txt"; var body = Encoding.UTF8.GetBytes(selected == tenant ? "Synthetic original historical contract bytes; never reconstructed from changed master data.\n" : "Synthetic archived original file bytes.\n");
            await File.WriteAllBytesAsync(Path.Combine(directory, file), body); var proof = await BundleFiles.Proof(directory, file);
            var data = new DocumentObject { Id = id, TenantId = selected.Id, UploadedBy = owner.Id, Name = "historical-original.txt", ContentType = "text/plain", Size = body.Length, Sha256 = proof.Sha256,
                ObjectKey = $"legacy/r2/{id:N}", VersionId = null, State = selected == tenant ? "Ready" : "Archived", CreatedAt = at,
                ProjectId = selected == tenant ? project.Id : null, ProjectKind = selected == tenant ? "projects" : null, SourceId = selected == tenant ? quote.Id : null,
                SourceKind = selected == tenant ? "quotes" : null, SourceDigest = selected == tenant ? quote.IssuedDigest : "", RendererVersion = "legacy-source-retained" };
            documents.Add(new("legacy-file-" + documents.Count, data, proof, "synthetic-r2-export", null));
        }
        foreach (var inspection in new[] { failedInspection, repair })
        {
            var id = Guid.NewGuid(); var file = $"objects/{id:N}.png";
            // A valid synthetic one-pixel PNG stands in for independently retained
            // original report bytes; no report is reconstructed from current state.
            var body = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            await File.WriteAllBytesAsync(Path.Combine(directory, file), body); var proof = await BundleFiles.Proof(directory, file);
            var version = $"synthetic-original-{id:N}";
            var data = new DocumentObject { Id = id, TenantId = tenant.Id, UploadedBy = owner.Id, Name = "historical-inspection-original.png",
                ContentType = "image/png", Size = body.Length, Sha256 = proof.Sha256, ObjectKey = $"legacy/original-inspections/{id:N}",
                VersionId = version, State = inspection == failedInspection ? "Ready" : "Archived", Revision = inspection == failedInspection ? 2 : 3,
                CreatedAt = inspection.Completion!.RecordedAt.AddSeconds(1), SourceId = inspection.Id, SourceKind = "equipment-inspections",
                SourceDigest = inspection.Completion.Digest, RendererVersion = "equipment-inspection-original-v1" };
            documents.Add(new("legacy-equipment-original-" + documents.Count, data, proof, "synthetic-versioned-original-export", version));
        }
        var envelopes = entities.Select(entity =>
        {
            var contractual = entity is Quote { Status: not QuoteStatus.Draft } or Order or Invoice { Status: InvoiceStatus.Issued }
                or SupplierInvoice { Status: SupplierInvoiceStatus.Approved } or PurchaseOrder { Status: not PurchaseOrderStatus.Draft };
            var data = MigrationJson.Element(entity);
            if (entity.Id == payable.Id)
            {
                var historicalInvoice = JsonNode.Parse(data.GetRawText())!.AsObject();
                Require.That(historicalInvoice.Remove("electronicSource"), "Supplier invoice fixture no longer contains its current optional evidence field.");
                data = JsonSerializer.SerializeToElement(historicalInvoice, MigrationJson.Options);
            }
            return new MigrationEntity(entity.Id.ToString(), BusinessRegistry.Entities.Single(x => x.Value == entity.GetType()).Key, data,
                MigrationJson.Hash(data), contractual ? MigrationEvidence.Confirmed : MigrationEvidence.NotApplicable, entity is Quote ? MigrationEvidence.Confirmed : MigrationEvidence.NotApplicable, contractual ? evidence.File : null);
        }).ToList();
        var counters = new List<MigrationCounter>();
        foreach (var selected in new[] { tenant, second }) foreach (var kind in new[] { "AN", "AU", "RE", "KO", "BE" })
        { var value = selected == tenant ? kind switch { "AN" => 42, "AU" => 17, "RE" => 73, "KO" => 2, "BE" => 3, _ => 0 } : 0; counters.Add(new(selected.Id, kind, date.Year, value, $"{kind}-{date.Year}-{value + 1:D6}")); }
        var historical = MigrationJson.Element(new { legacyAttemptId = "provider-row-9", state = "sent", providerReference = quote.Deliveries[0].ProviderReference, quoteId = quote.Id, quoteDigest = quote.IssuedDigest });
        var bundle = new MigrationBundle(MigrationJson.Format, Guid.NewGuid(), "synthetic-canonical-d1-r2-fixture", "source-fixture-1", release, MigrationImport.MicrosecondNow(),
            [new("legacy-tenant-a", tenant), new("legacy-tenant-b", second)], [owner, partner, formerRecorder],
            [new(tenant.Id, owner.Id, BusinessRole.ManagingDirector, true, 7), new(second.Id, owner.Id, BusinessRole.Worker, false, 3), new(tenant.Id, partner.Id, BusinessRole.Partner, true, 2),
                new(tenant.Id, formerRecorder.Id, BusinessRole.Office, false, 4)],
            envelopes, documents, counters, [new("legacy-event-9", Guid.NewGuid(), tenant.Id, "dispatch", "quotes", quote.Id, at, historical, MigrationJson.Hash(historical), evidence.File)], [evidence], [], []);
        return Controls(bundle);
    }

    private static void SeedRetainedOperationalLedgers(Guid tenant, Customer customer, Project project, Guid recorder, Guid director,
        DateTimeOffset at, List<TenantEntity> entities)
    {
        var wholeSecond = new DateTimeOffset(at.Ticks - at.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var contact = customer.Contacts![0];
        var communication = CustomerCommunicationWorkflow.Record(customer,
            new(CustomerCommunicationChannel.Telephone, CustomerCommunicationDirection.Incoming, wholeSecond.AddDays(-10),
                "Historical conversation before master-data creation", "Retained manual account; no message was sent.", contact.Id, project.Id),
            project, recorder, at.AddMinutes(15));
        entities.Add(communication);
        customer.Revision++; customer.UpdatedAt = communication.RecordedAt;
        var correctedCommunication = CustomerCommunicationWorkflow.Record(customer,
            new(CustomerCommunicationChannel.Note, CustomerCommunicationDirection.Internal, wholeSecond,
                "Historical superseded note", "Original retained after explicit correction.", contact.Id, project.Id),
            project, recorder, at.AddMinutes(16));
        entities.Add(correctedCommunication);
        customer.Revision++; customer.UpdatedAt = correctedCommunication.RecordedAt;
        entities.Add(CustomerCommunicationWorkflow.Void(correctedCommunication, correctedCommunication.Digest,
            "Synthetic recorded correction, original retained", director, at.AddMinutes(17)));
        customer.Revision++; customer.UpdatedAt = at.AddMinutes(17);
        CustomerContactWorkflow.Update(customer, contact.Id,
            new("Current renamed billing contact", contact.Function, contact.Email, contact.Phone, contact.Notes, false), director, at.AddMinutes(18));
        CustomerContactWorkflow.Archive(customer, contact.Id, director, at.AddMinutes(19));
        customer.Name = "Current customer renamed after conversation"; customer.Revision += 2; customer.UpdatedAt = at.AddMinutes(19);

        var vehicle = new Equipment { TenantId = tenant, CreatedAt = at, UpdatedAt = at, Name = "Historical ledger vehicle",
            Kind = EquipmentKind.Vehicle, InventoryNumber = "MIGRATION-VEHICLE", Registration = "SYNTHETIC-17" };
        var driver = new Employee { TenantId = tenant, CreatedAt = at, UpdatedAt = at, Name = "Historical ledger driver", Active = true };
        entities.Add(vehicle); entities.Add(driver);
        VehicleTrip Trip(int startMinute, decimal from, decimal to, string purpose, int recordedMinute)
        {
            var value = VehicleLedgerWorkflow.RecordTrip(vehicle, driver, project,
                new(driver.Id, project.Id, wholeSecond.AddMinutes(startMinute), wholeSecond.AddMinutes(startMinute + 1),
                    from, to, purpose, "Synthetic depot", "Synthetic project", "Preserved operational journey"), recorder, at.AddMinutes(recordedMinute));
            entities.Add(value); vehicle.Revision++; vehicle.UpdatedAt = value.RecordedAt; return value;
        }
        _ = Trip(1, 100m, 120m, "Historical retained first journey", 20);
        var mistaken = Trip(3, 120m, 160m, "Historical incorrect journey", 21);
        entities.Add(VehicleLedgerWorkflow.Reverse(mistaken, "Original remains; wrong final odometer", director, at.AddMinutes(22)));
        vehicle.Revision++; vehicle.UpdatedAt = at.AddMinutes(22);
        _ = Trip(3, 120m, 150m, "Historical corrected journey", 23);
        var fuel = VehicleLedgerWorkflow.RecordCost(vehicle, project,
            new(project.Id, DateOnly.FromDateTime(at.UtcDateTime), VehicleCostCategory.Fuel, 85m, "EUR", "SYNTHETIC-FUEL-17", "Operational cost only"),
            DateOnly.FromDateTime(at.AddMinutes(24).UtcDateTime), recorder, at.AddMinutes(24));
        entities.Add(fuel);
        vehicle.Revision++; vehicle.UpdatedAt = fuel.RecordedAt;
        var mistakenCost = VehicleLedgerWorkflow.RecordCost(vehicle, project,
            new(project.Id, DateOnly.FromDateTime(at.UtcDateTime), VehicleCostCategory.Repair, 120m, "EUR", "SYNTHETIC-REPAIR-18", "Incorrect operational entry"),
            DateOnly.FromDateTime(at.AddMinutes(25).UtcDateTime), recorder, at.AddMinutes(25));
        entities.Add(mistakenCost);
        vehicle.Revision++; vehicle.UpdatedAt = mistakenCost.RecordedAt;
        entities.Add(VehicleLedgerWorkflow.Reverse(mistakenCost, "Original retained; repair cost entered in error", director, at.AddMinutes(26)));
        vehicle.Revision++; vehicle.UpdatedAt = at.AddMinutes(26);
        vehicle.Name = "Current renamed archived vehicle"; vehicle.Archived = true; vehicle.Available = false;
        vehicle.Revision++; vehicle.UpdatedAt = at.AddMinutes(27);
        driver.Name = "Current renamed inactive driver"; driver.Active = false; driver.Archived = true;
        driver.Revision = 2; driver.UpdatedAt = at.AddMinutes(27);
        project.Name = "Current project renamed after operational entries"; project.Revision++; project.UpdatedAt = at.AddMinutes(27);
    }
}
