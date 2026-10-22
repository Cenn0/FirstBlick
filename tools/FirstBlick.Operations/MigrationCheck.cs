using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;

namespace FirstBlick.Operations;

internal static class MigrationCheck
{
    private const string Boundary = "Canonical compatibility check only. Original export ownership, complete source extraction and real legacy cutover require operator evidence; no target is written by this command.";
    public static async Task<MigrationCheckReport> Run(string directory, string reportPath)
    {
        MigrationCheckReport report;
        try { report = (await Load(directory)).Report; }
        catch (Exception ex) when (ContractFailure(ex))
        { report = new(MigrationJson.Format, Guid.Empty, "", DateTimeOffset.UtcNow, "Blocked", [new("invalid_contract", "migration.json", "The canonical bundle could not be read: " + (ex is OperationsException ? ex.Message : ex.GetType().Name))], [], 0, Boundary); }
        await MigrationJson.WriteReport(reportPath, report); return report;
    }

    public static async Task<CheckedMigration> Load(string directory)
    {
        var input = BundleFiles.SafePath(directory, "migration.json");
        Require.That(File.Exists(input) && new FileInfo(input).Length <= 104_857_600, "A migration.json file of at most 100 MiB is required.");
        var source = await File.ReadAllTextAsync(input, new UTF8Encoding(false, true));
        using var json = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 64 });
        var originalDigest = MigrationJson.Hash(json.RootElement); // Also rejects duplicate properties.
        var bundle = JsonSerializer.Deserialize<MigrationBundle>(source, MigrationJson.Options) ?? throw new OperationsException("The migration bundle is empty.");
        Require.That(originalDigest == MigrationJson.Digest(bundle), "Supply every canonical property exactly as serialized by this contract; implicit defaults, omitted IDs, unknown fields and alternate decimal representations are not accepted.");
        var digest = await BundleFiles.Hash(input); var issues = new List<MigrationIssue>(); var entities = new List<TenantEntity>();
        void Check(string location, Action action)
        {
            if (issues.Count >= 500) return;
            try { action(); }
            catch (Exception ex) when (ContractFailure(ex))
            { issues.Add(new(ex is DomainException domain ? domain.Code : "migration_rule", location, ex is DomainException or OperationsException ? ex.Message : ex.GetType().Name)); }
        }
        Check("bundle", () =>
        {
            Require.That(bundle.Format == MigrationJson.Format, "Unsupported canonical migration format."); Guard.Id(bundle.BundleId, "BundleId");
            Guard.Text(bundle.SourceSystem, "SourceSystem", 200); Guard.Text(bundle.SourceVersion, "SourceVersion", 200); Guard.Text(bundle.TargetRelease, "TargetRelease", 200);
            Guard.Utc(bundle.ExportedAt, "ExportedAt"); Require.That(bundle.ExportedAt <= DateTimeOffset.UtcNow.AddMinutes(5), "Export time cannot be in the future.");
            Guard.Items(bundle.Tenants, "Tenants", 1000, 1); Guard.Items(bundle.Accounts, "Accounts", 100_000); Guard.Items(bundle.Memberships, "Memberships", 100_000);
            Guard.Items(bundle.Entities, "Entities", 100_000); Guard.Items(bundle.Documents, "Documents", 100_000); Guard.Items(bundle.Counters, "Counters", 100_000);
            Guard.Items(bundle.History, "History", 100_000); Guard.Items(bundle.EvidenceFiles, "EvidenceFiles", 10_000, 1); Guard.Items(bundle.Uncertainties, "Uncertainties", 1000);
            Guard.Items(bundle.Controls, "Controls", 1000, 1);
        });
        if (issues.Count != 0) return Result([]);
        foreach (var uncertainty in bundle.Uncertainties)
            issues.Add(new("unresolved_source_evidence", uncertainty.Kind ?? "bundle", $"{uncertainty.Code}: {uncertainty.Description}"));
        var tenants = bundle.Tenants.Select(x => x.Data.Id).ToHashSet(); var users = bundle.Accounts.Select(x => x.Id).ToHashSet();
        Check("identities", () =>
        {
            Require.That(tenants.Count == bundle.Tenants.Count && users.Count == bundle.Accounts.Count, "Tenant and user UUIDs must be unique.");
            Require.That(bundle.Tenants.Select(x => x.SourceId).Distinct(StringComparer.Ordinal).Count() == bundle.Tenants.Count
                && bundle.Accounts.Select(x => x.SourceId).Distinct(StringComparer.Ordinal).Count() == bundle.Accounts.Count, "Source tenant/account IDs must map one-to-one.");
            Require.That(bundle.Accounts.Select(x => x.Email.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count() == bundle.Accounts.Count, "A normalized email cannot belong to two identity UUIDs.");
            Require.That(bundle.Memberships.Select(x => (x.TenantId, x.UserId)).Distinct().Count() == bundle.Memberships.Count, "Membership mappings must be unique.");
        });
        foreach (var tenant in bundle.Tenants) Check($"tenant/{tenant.SourceId}", () =>
        {
            SourceId(tenant.SourceId, tenant.Data.Id); Guard.Text(tenant.Data.Name, "Tenant name", 200); Guard.Text(tenant.Data.TimeZone, "Time zone", 120);
            _ = TimeZoneInfo.FindSystemTimeZoneById(tenant.Data.TimeZone); AccessRules.ValidateModules(tenant.Data.Modules);
            Require.That(tenant.Data.PolicyRevision > 0 && tenant.Data.ApprovalActions.Distinct(StringComparer.Ordinal).Count() == tenant.Data.ApprovalActions.Length
                && tenant.Data.ApprovalActions.All(x => x is "invoice.issue" or "purchase.issue" or "supplier-invoice.approve"), "Unknown approval policy or invalid policy revision.");
        });
        foreach (var account in bundle.Accounts) Check($"account/{account.SourceId}", () =>
        { SourceId(account.SourceId, account.Id); Guard.Email(account.Email, true); Guard.Text(account.DisplayName, "DisplayName", 160); Guard.Text(account.PhoneNumber, "PhoneNumber", 40, false); });
        foreach (var membership in bundle.Memberships) Check($"membership/{membership.TenantId}/{membership.UserId}", () =>
        { Require.That(tenants.Contains(membership.TenantId) && users.Contains(membership.UserId) && Enum.IsDefined(membership.Role) && membership.Revision > 0, "Membership must reference a supplied tenant/user and known role."); });
        Check("evidence", () => Require.That(bundle.EvidenceFiles.Select(x => x.File).Distinct(StringComparer.Ordinal).Count() == bundle.EvidenceFiles.Count, "Evidence paths must be unique."));
        var evidence = bundle.EvidenceFiles.Select(x => x.File).ToHashSet(StringComparer.Ordinal);
        foreach (var file in bundle.EvidenceFiles)
        {
            try { await VerifyFile(directory, file, 536_870_912); }
            catch (Exception ex) when (ex is OperationsException or IOException) { issues.Add(new("evidence_checksum", file.File, ex is OperationsException ? ex.Message : ex.GetType().Name)); }
        }
        foreach (var item in bundle.Entities) Check($"{item.Kind}/{item.SourceId}", () =>
        {
            Require.That(BusinessRegistry.Entities.ContainsKey(item.Kind), "Unknown entity kind; no untyped JSON is imported.");
            Require.That(item.Data.ValueKind == JsonValueKind.Object && Encoding.UTF8.GetByteCount(item.Data.GetRawText()) <= 1_900_000, "Entity exceeds the typed payload budget.");
            var entity = AppJson.Read(item.Kind, item.Data.GetRawText()); SourceId(item.SourceId, entity.Id);
            Require.That(tenants.Contains(entity.TenantId), "Entity tenant is absent from the identity mapping.");
            Require.That(MigrationJson.IsHash(item.Sha256) && MigrationJson.Hash(item.Data) == item.Sha256, "Entity source checksum is incorrect.");
            Require.That(MigrationJson.HashJson(MigrationEntityJson.Serialize(entity, item.Data)) == item.Sha256, "Entity JSON must be complete canonical typed JSON; defaults or computed evidence changed.");
            entity.Validate(); // Re-evaluation verifies a supported immutable contract; it never replaces historical values.
            Require.That(entity.CreatedAt.Ticks % 10 == 0 && entity.UpdatedAt.Ticks % 10 == 0 && entity.UpdatedAt <= bundle.ExportedAt,
                "Entity envelope timestamps must fit PostgreSQL microseconds and cannot postdate the export; their historical JSON values are never truncated by the importer.");
            Require.That(item.ContractParties != MigrationEvidence.Unknown && item.DeliveryHistory != MigrationEvidence.Unknown, "Unknown party/delivery evidence blocks migration and must be explicitly reconciled.");
            Require.That(item.EvidenceFile is null || evidence.Contains(item.EvidenceFile), "An entity source evidence path must identify one of the verified original files.");
            var contractual = entity is Quote { Status: not QuoteStatus.Draft } or Order or Invoice { Status: InvoiceStatus.Issued }
                or SupplierInvoice { Status: SupplierInvoiceStatus.Approved } or PurchaseOrder { Status: not PurchaseOrderStatus.Draft };
            if (contractual) Require.That(item.ContractParties == MigrationEvidence.Confirmed && item.EvidenceFile is not null && evidence.Contains(item.EvidenceFile), "Issued contracts require confirmed historical parties and an original evidence file.");
            if (entity is Quote quote && quote.Status != QuoteStatus.Draft)
            {
                Require.That(quote.Presentation is not null && item.DeliveryHistory == MigrationEvidence.Confirmed, "Historical quote presentation and complete dispatch evidence are required; current customer master data cannot replace them.");
                Guard.Text(quote.Presentation!.BuyerAddress, "Historical buyer address", 2000);
                Require.That(quote.Deliveries.All(x => x.State is DeliveryState.Sent or DeliveryState.Failed), "Pending, processing or uncertain legacy sends must be reconciled before migration; no implicit delivery job is created.");
                foreach (var attempt in quote.Deliveries.Where(x => x.State == DeliveryState.Sent))
                    Require.That(attempt.ResolvedAt.HasValue && attempt.ResolvedAt >= attempt.RequestedAt && !string.IsNullOrWhiteSpace(attempt.ProviderReference), "A sent attempt requires preserved time and provider evidence.");
            }
            if (entity is Order order) Require.That(order.QuotePresentation is not null, "The accepted historical order presentation is required.");
            Require.That(entity switch
            {
                BankTransaction bank => !string.IsNullOrEmpty(bank.ImportDigest), PaymentAllocation payment => !string.IsNullOrEmpty(payment.PostedDigest),
                SupplierPaymentAllocation payment => !string.IsNullOrEmpty(payment.PostedDigest), StockMovement movement => !string.IsNullOrEmpty(movement.PostedDigest),
                FormSubmission submission => !string.IsNullOrEmpty(submission.SubmittedDigest), _ => true
            }, "Posted imports, allocations, stock movements and submissions require their immutable evidence digests.");
            entities.Add(entity);
        });
        Check("entity-identities", () => Require.That(entities.Select(x => (x.TenantId, x.GetType(), x.Id)).Distinct().Count() == entities.Count
            && bundle.Entities.Select(x => (tenant: x.Data.GetProperty("tenantId").GetGuid(), x.Kind, x.SourceId)).Distinct().Count() == bundle.Entities.Count, "Entity UUID/source mappings must be unique within tenant and kind."));
        foreach (var item in bundle.Documents)
        {
            Check($"document/{item.SourceId}", () => ValidateDocument(item, tenants, users));
            try
            {
                await VerifyFile(directory, item.File, 26_214_400);
                if (item.Data.SourceKind is "customer-communications" or "vehicle-costs")
                    await VerifyLedgerOriginalContent(directory, item);
            }
            catch (Exception ex) when (ex is OperationsException or IOException) { issues.Add(new("document_checksum", item.SourceId, ex is OperationsException ? ex.Message : ex.GetType().Name)); }
        }
        Check("document-identities", () => Require.That(bundle.Documents.Select(x => x.Data.Id).Distinct().Count() == bundle.Documents.Count
            && bundle.Documents.Select(x => (x.Data.TenantId, x.SourceId)).Distinct().Count() == bundle.Documents.Count, "Document UUIDs and source IDs must be unique."));
        foreach (var item in bundle.History) Check($"history/{item.SourceId}", () =>
        {
            SourceId(item.SourceId, item.Id); Require.That(tenants.Contains(item.TenantId), "History tenant is absent.");
            Require.That(item.Category is "audit" or "dispatch" or "contract" or "numbering" or "account", "Unknown historical evidence category.");
            Guard.Utc(item.OccurredAt, "Historical event time"); Require.That(item.OccurredAt <= bundle.ExportedAt, "History cannot postdate the export.");
            Require.That(item.Kind is null == !item.EntityId.HasValue && (item.Kind is null || BusinessRegistry.Entities.ContainsKey(item.Kind)), "Historical entity reference is incomplete.");
            Require.That(item.Data.ValueKind == JsonValueKind.Object && Encoding.UTF8.GetByteCount(item.Data.GetRawText()) <= 100_000
                && MigrationJson.IsHash(item.Sha256) && MigrationJson.Hash(item.Data) == item.Sha256 && evidence.Contains(item.EvidenceFile), "Historical evidence must have bounded immutable data and its source file.");
        });
        Check("history-identities", () => Require.That(bundle.History.Select(x => x.Id).Distinct().Count() == bundle.History.Count
            && bundle.History.Select(x => (x.TenantId, x.SourceId)).Distinct().Count() == bundle.History.Count, "Historical source mappings must be unique."));
        if (entities.Count == bundle.Entities.Count && !issues.Any(x => x.Location is "entity-identities" or "identities" or "document-identities"))
        {
            var graph = new MigrationGraph(bundle, entities);
            foreach (var entity in entities) Check($"relations/{BusinessRegistry.Entities.Single(x => x.Value == entity.GetType()).Key}/{entity.Id}", () => graph.Validate(entity));
            foreach (var item in bundle.Documents) Check($"document-relations/{item.Data.Id}", () => graph.ValidateDocument(item));
            foreach (var item in bundle.History.Where(x => x.EntityId.HasValue)) Check($"history-relations/{item.Id}", () => graph.RequireReference(item.TenantId, item.Kind!, item.EntityId!.Value));
            foreach (var tenant in bundle.Tenants) Check($"ledger/{tenant.Data.Id}", () => graph.ValidateLedger(tenant.Data.Id));
        }
        Check("numbering", () => ValidateCounters(bundle, entities));
        List<MigrationControl> observed = [];
        if (entities.Count == bundle.Entities.Count) Check("controls", () =>
        {
            observed = MigrationControls.Calculate(bundle, entities);
            Require.That(bundle.Controls.Select(x => x.TenantId).Distinct().Count() == bundle.Controls.Count && bundle.Controls.Count == tenants.Count, "One independent source control record is required per tenant.");
            foreach (var actual in observed) Require.That(bundle.Controls.SingleOrDefault(x => x.TenantId == actual.TenantId) == actual, $"Source totals or counts/digests differ for tenant {actual.TenantId}.");
        });
        return Result(observed);

        CheckedMigration Result(List<MigrationControl> observed) => new(bundle, Path.GetFullPath(directory), digest, entities,
            new(MigrationJson.Format, bundle.BundleId, digest, DateTimeOffset.UtcNow, issues.Count == 0 ? "Compatible" : "Blocked", issues.Take(500).ToList(), observed, bundle.Uncertainties.Count, Boundary));
    }

    private static bool ContractFailure(Exception exception) => exception is DomainException or OperationsException or JsonException
        or ArgumentException or IOException or InvalidOperationException or OverflowException or FormatException
        or TimeZoneNotFoundException or InvalidTimeZoneException or KeyNotFoundException;

    internal static void SourceId(string sourceId, Guid id)
    {
        Guard.Text(sourceId, "SourceId", 240); Guard.Id(id, "Mapped UUID");
        Require.That(!Guid.TryParse(sourceId, out var original) || original == id, "An original UUID must remain unchanged; non-UUID IDs require an explicit stable mapping.");
    }
    internal static async Task VerifyFile(string directory, FileProof proof, long maximum)
    {
        Require.That(MigrationJson.IsHash(proof.Sha256) && proof.Size > 0 && proof.Size <= maximum, "Invalid file size or SHA-256 proof.");
        var path = BundleFiles.SafePath(directory, proof.File);
        Require.That(File.Exists(path) && new FileInfo(path).Length == proof.Size && await BundleFiles.Hash(path) == proof.Sha256, "Missing file or incorrect SHA-256/byte count.");
    }
    private static async Task VerifyLedgerOriginalContent(string directory, MigrationDocument item)
    {
        // Match the bounded signature check used before upload and on download.
        // A valid checksum alone cannot prove the declared file format.
        await using var input = File.OpenRead(BundleFiles.SafePath(directory, item.File.File));
        var bytes = new byte[8];
        var length = await input.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false);
        var valid = item.Data.ContentType switch
        {
            "application/pdf" => bytes.AsSpan(0, length).StartsWith("%PDF-"u8),
            "image/png" => bytes.AsSpan(0, length).StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/jpeg" => length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255,
            _ => false
        };
        Require.That(valid, "A ledger original must retain bytes matching its declared file format.");
    }
    private static void ValidateDocument(MigrationDocument item, HashSet<Guid> tenants, HashSet<Guid> users)
    {
        var document = item.Data; SourceId(item.SourceId, document.Id); Guard.Id(document.UploadedBy, "Uploader");
        Require.That(tenants.Contains(document.TenantId) && users.Contains(document.UploadedBy), "Document tenant/uploader mapping is absent.");
        Guard.Text(document.Name, "Document name", 240); Guard.Text(document.ObjectKey, "Original object key", 1024); Guard.Text(item.SourceStore, "SourceStore", 200);
        Require.That(Path.GetFileName(document.Name) == document.Name && document.Name.All(x => !char.IsControl(x)), "Document name must be a plain filename.");
        var extension = Path.GetExtension(document.Name).ToLowerInvariant();
        Require.That((document.ContentType, extension) is ("application/pdf", ".pdf") or ("image/png", ".png") or ("image/jpeg", ".jpg" or ".jpeg") or ("text/plain", ".txt") or ("text/csv", ".csv"), "Unsupported document content type/extension.");
        Require.That(document.State is "Ready" or "Archived" && document.Revision > 0 && document.Size == item.File.Size && document.Sha256 == item.File.Sha256, "Only verified/archived documents with matching content proofs are migratable.");
        Guard.Utc(document.CreatedAt, "Document creation time"); Require.That(document.CreatedAt.Ticks % 10 == 0, "Document timestamp must fit PostgreSQL microsecond precision without truncation.");
        Require.That(document.ProjectId.HasValue ? document.ProjectKind == "projects" : document.ProjectKind is null, "Document project kind is inconsistent.");
        Require.That(document.SourceId.HasValue == (document.SourceKind is not null), "Document source reference is incomplete.");
        Require.That(document.VersionId == item.SourceVersionId, "Original document version metadata must be retained exactly; unversioned exports explicitly use null.");
    }
    private static void ValidateCounters(MigrationBundle bundle, List<TenantEntity> entities)
    {
        string[] kinds = ["AN", "AU", "RE", "KO", "BE"];
        var counters = bundle.Counters.ToDictionary(x => (x.TenantId, x.Kind, x.Year));
        var numbers = entities.Select(x => (x.TenantId, Number: x switch { Quote q when q.Status != QuoteStatus.Draft => q.Number, Order order => order.Number,
            Invoice invoice when invoice.Status == InvoiceStatus.Issued => invoice.Number, PurchaseOrder order when order.Status != PurchaseOrderStatus.Draft => order.Number, _ => "" })).Where(x => x.Number != "").ToList();
        Require.That(numbers.Select(x => (x.TenantId, x.Number)).Distinct().Count() == numbers.Count, "Issued commercial numbers must be unique within a business.");
        foreach (var counter in bundle.Counters)
        {
            Require.That(bundle.Tenants.Any(x => x.Data.Id == counter.TenantId) && kinds.Contains(counter.Kind) && counter.Year is >= 1900 and <= 9999 && counter.Value is >= 0 and < long.MaxValue, "Invalid or unsupported source numbering counter.");
            Require.That(counter.ExpectedNextNumber == $"{counter.Kind}-{counter.Year}-{counter.Value + 1:D6}" && !numbers.Contains((counter.TenantId, counter.ExpectedNextNumber)), "Expected next number differs from the preserved counter or collides with an existing document.");
        }
        foreach (var number in numbers)
        {
            var match = Regex.Match(number.Number, "^(AN|AU|RE|KO|BE)-([0-9]{4})-([0-9]{1,19})$", RegexOptions.CultureInvariant);
            if (!match.Success) continue; // Custom legacy numbers remain unchanged; future counters are explicit.
            var key = (number.TenantId, match.Groups[1].Value, int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
            Require.That(counters.TryGetValue(key, out var counter) && long.TryParse(match.Groups[3].Value, out var used) && used <= counter.Value, "A source counter is missing or lower than an already issued number.");
        }
        foreach (var tenant in bundle.Tenants)
        {
            var year = TimeZoneInfo.ConvertTime(bundle.ExportedAt, TimeZoneInfo.FindSystemTimeZoneById(tenant.Data.TimeZone)).Year;
            Require.That(kinds.All(kind => counters.ContainsKey((tenant.Data.Id, kind, year))), "All five numbering counters for the export business year must be supplied, including unused zero counters.");
        }
    }
}
