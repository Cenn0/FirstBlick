using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;

namespace FirstBlick.Operations;

internal static partial class MigrationDrill
{
    private static async Task AddLedgerOriginalDocuments(string directory, IEnumerable<TenantEntity> source,
        List<MigrationDocument> documents, Guid uploader)
    {
        var entries = source.Where(x => x is CustomerCommunication or VehicleCost).ToArray();
        foreach (var entry in entries)
        {
            var id = Guid.NewGuid(); var file = $"objects/{id:N}.png";
            var body = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            await File.WriteAllBytesAsync(Path.Combine(directory, file), body);
            var proof = await BundleFiles.Proof(directory, file);
            var version = $"synthetic-ledger-original-{id:N}";
            var communication = entry as CustomerCommunication;
            var digest = communication?.Digest ?? ((VehicleCost)entry).Digest;
            var recordedAt = communication?.RecordedAt ?? ((VehicleCost)entry).RecordedAt;
            // Each source family retains both available and archived files. The
            // originals survive subsequent source voids and archived master data.
            var kind = communication is not null ? "customer-communications" : "vehicle-costs";
            var archived = documents.Any(x => x.Data.SourceKind == kind);
            var data = new DocumentObject
            {
                Id = id, TenantId = entry.TenantId, UploadedBy = uploader, Name = "historical-ledger-original.png",
                ContentType = "image/png", Size = body.Length, Sha256 = proof.Sha256,
                ObjectKey = $"legacy/ledger-originals/{id:N}", VersionId = version,
                State = archived ? "Archived" : "Ready", Revision = archived ? 3 : 2,
                CreatedAt = recordedAt.AddSeconds(1), SourceId = entry.Id,
                SourceKind = kind,
                SourceDigest = digest,
                RendererVersion = communication is not null ? "customer-communication-original-v1" : "vehicle-cost-original-v1"
            };
            documents.Add(new("legacy-ledger-original-" + id.ToString("N"), data, proof, "synthetic-versioned-original-export", version));
        }
    }

    private static void VerifyLedgerOriginalDocuments(IEnumerable<DocumentObject> source, IEnumerable<TenantEntity> entities)
    {
        var documents = source.Where(x => x.SourceKind is "customer-communications" or "vehicle-costs").ToArray();
        var records = entities.ToArray();
        Require.That(documents.Length == 4, "Migration lost customer communication or vehicle cost original files.");
        foreach (var kind in new[] { "customer-communications", "vehicle-costs" })
        {
            var family = documents.Where(x => x.SourceKind == kind).ToArray();
            Require.That(family.Length == 2 && family.Any(x => x.State == "Ready") && family.Any(x => x.State == "Archived"),
                "Migration lost an available or archived ledger original.");
            foreach (var document in family)
            {
                var original = records.Single(x => x.Id == document.SourceId && x.TenantId == document.TenantId);
                var digest = original is CustomerCommunication communication ? communication.Digest : ((VehicleCost)original).Digest;
                var renderer = original is CustomerCommunication ? "customer-communication-original-v1" : "vehicle-cost-original-v1";
                Require.That(document.SourceDigest == digest && document.RendererVersion == renderer
                    && document.ProjectId is null && document.ProjectKind is null
                    && !string.IsNullOrWhiteSpace(document.VersionId) && document.VersionId != "null" && document.Revision >= 2,
                    "A migrated ledger original lost its immutable source, version or access boundary.");
            }
        }
    }

    private static async Task RejectLedgerOriginalSources(MigrationBundle bundle, IEnumerable<TenantEntity> source,
        Func<string, MigrationBundle, Func<MigrationIssue, bool>?, Task> reject)
    {
        var entities = source.ToArray();
        foreach (var kind in new[] { "customer-communications", "vehicle-costs" })
        {
            var originals = entities.Where(x => kind == "customer-communications" ? x is CustomerCommunication : x is VehicleCost).ToArray();
            var selected = bundle.Documents.First(x => x.Data.SourceKind == kind).Data;
            var alternative = originals.Single(x => x.Id != selected.SourceId);
            Task Reject(string name, Action<DocumentObject> mutation, string message) => reject(kind + "-original-" + name,
                ChangeLedgerDocument(bundle, kind, mutation), issue => issue.Message == message);
            await Reject("wrong-digest", document => document.SourceDigest = new string('0', 64),
                "A ledger original must bind to its exact retained source digest.");
            await Reject("swapped-source", document => document.SourceId = alternative.Id,
                "A ledger original must bind to its exact retained source digest.");
            await reject(kind + "-original-cross-tenant", ChangeLedgerDocument(bundle, kind, document => document.TenantId = bundle.Tenants[1].Data.Id),
                issue => issue.Message.StartsWith("Missing tenant-qualified " + kind + " reference", StringComparison.Ordinal));
            await Reject("project-rebinding", document =>
            { document.ProjectId = entities.OfType<Project>().Single().Id; document.ProjectKind = "projects"; },
                "A ledger original must retain its source-specific format and cannot acquire project-based access.");
            await Reject("changed-format-marker", document => document.RendererVersion = "unverified-ledger-original",
                "A ledger original must retain its source-specific format and cannot acquire project-based access.");
            await Reject("unsupported-type", document => document.ContentType = "text/plain",
                "A ledger original must retain its source-specific format and cannot acquire project-based access.");
            var original = originals.Single(x => x.Id == selected.SourceId);
            var recordedAt = original is CustomerCommunication communication ? communication.RecordedAt : ((VehicleCost)original).RecordedAt;
            await Reject("before-entry", document => document.CreatedAt = recordedAt.AddTicks(-10),
                "A ledger original cannot predate its retained entry or postdate the export.");
            await Reject("after-export", document => document.CreatedAt = bundle.ExportedAt.AddTicks(10),
                "A ledger original cannot predate its retained entry or postdate the export.");
            await Reject("missing-version", document => document.VersionId = null,
                "A ledger original must retain its immutable source version.");
            await Reject("null-version", document => document.VersionId = "null",
                "A ledger original must retain its immutable source version.");
            await Reject("unverified-revision", document => document.Revision = 1,
                "A verified ledger original must retain its completed upload revision.");
            await Reject("oversized-filename", document => document.Name = new string('x', 177) + ".png",
                "A ledger original must retain a supported plain filename of at most 180 characters.");
            await Reject("filename-backslash", document => document.Name = @"folder\original.png",
                "A ledger original must retain a supported plain filename of at most 180 characters.");
            await Reject("declared-format-mismatch", document =>
            { document.Name = "original.jpg"; document.ContentType = "image/jpeg"; },
                "A ledger original must retain bytes matching its declared file format.");
        }
    }

    private static MigrationBundle ChangeLedgerDocument(MigrationBundle bundle, string kind, Action<DocumentObject> mutation)
    {
        var documents = bundle.Documents.ToList(); var index = documents.FindIndex(x => x.Data.SourceKind == kind);
        var document = AppJson.Read<DocumentObject>(AppJson.Serialize(documents[index].Data)); mutation(document);
        documents[index] = documents[index] with { Data = document, SourceVersionId = document.VersionId };
        return Controls(bundle with { Documents = documents });
    }
}
