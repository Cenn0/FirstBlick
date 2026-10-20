using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;

namespace FirstBlick.Operations;

// This is an explicit interchange contract, not a guess at an unavailable D1 schema.
// All identity mappings and historical snapshots must be supplied by the reviewed source adapter.
internal sealed record MigrationBundle(string Format, Guid BundleId, string SourceSystem, string SourceVersion, string TargetRelease,
    DateTimeOffset ExportedAt, List<MigrationTenant> Tenants, List<MigrationAccount> Accounts, List<MigrationMembership> Memberships,
    List<MigrationEntity> Entities, List<MigrationDocument> Documents, List<MigrationCounter> Counters, List<MigrationHistory> History,
    List<FileProof> EvidenceFiles, List<MigrationUncertainty> Uncertainties, List<MigrationControl> Controls);
internal sealed record MigrationTenant(string SourceId, BusinessTenant Data);
internal sealed record MigrationAccount(string SourceId, Guid Id, string Email, string DisplayName, string? PhoneNumber);
internal sealed record MigrationMembership(Guid TenantId, Guid UserId, BusinessRole Role, bool Active, long Revision);
internal enum MigrationEvidence { NotApplicable, Confirmed, Unknown }
internal sealed record MigrationEntity(string SourceId, string Kind, JsonElement Data, string Sha256,
    MigrationEvidence ContractParties, MigrationEvidence DeliveryHistory, string? EvidenceFile);
internal sealed record MigrationDocument(string SourceId, DocumentObject Data, FileProof File, string SourceStore, string? SourceVersionId);
internal sealed record MigrationCounter(Guid TenantId, string Kind, int Year, long Value, string ExpectedNextNumber);
internal sealed record MigrationHistory(string SourceId, Guid Id, Guid TenantId, string Category, string? Kind, Guid? EntityId,
    DateTimeOffset OccurredAt, JsonElement Data, string Sha256, string EvidenceFile);
internal sealed record MigrationUncertainty(string Code, string Description, Guid? TenantId, string? Kind, Guid? EntityId, string? EvidenceFile);
internal sealed record MigrationTotals(decimal QuotedGross, decimal OrderedGross, decimal InvoiceNet, decimal InvoiceTax, decimal InvoiceGross,
    decimal CreditGross, decimal Receipts, decimal Refunds, decimal Discounts, decimal Retained, decimal BankNet,
    decimal SupplierInvoiceGross, decimal SupplierCreditGross, decimal SupplierPayments, decimal SupplierRefunds);
internal sealed record MigrationControl(Guid TenantId, int EntityCount, int DocumentCount, int MembershipCount,
    string EntityDigest, string DocumentDigest, string HistoryDigest, string AccountDigest, string CounterDigest, MigrationTotals Totals);
internal sealed record MigrationIssue(string Code, string Location, string Message);
internal sealed record MigrationCheckReport(string Format, Guid BundleId, string BundleSha256, DateTimeOffset CheckedAt, string State,
    List<MigrationIssue> Issues, List<MigrationControl> Observed, int Uncertainties, string Boundary);
internal sealed record CheckedMigration(MigrationBundle Bundle, string Directory, string Sha256, List<TenantEntity> Entities, MigrationCheckReport Report);

internal static class MigrationJson
{
    public const string Format = "firstblick.migration.v1";
    public static readonly JsonSerializerOptions Options = new(AppJson.Options)
    {
        WriteIndented = true, MaxDepth = 64, RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(T), Options);
    public static string Digest<T>(T value) => Hash(Element(value));
    public static string Hash(JsonElement element) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(element))));
    public static string HashJson(string json) { using var document = JsonDocument.Parse(json); return Hash(document.RootElement); }
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string Canonical(JsonElement element)
    {
        static void Write(Utf8JsonWriter writer, JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    var properties = value.EnumerateObject().ToArray();
                    Require.That(properties.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() == properties.Length, "Duplicate JSON properties are not a canonical migration document.");
                    writer.WriteStartObject();
                    foreach (var property in properties.OrderBy(x => x.Name, StringComparer.Ordinal))
                    { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
                    writer.WriteEndObject(); break;
                case JsonValueKind.Array:
                    writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); break;
                case JsonValueKind.Number:
                    // Normalize spelling exactly as decimal digits, never through binary
                    // floating point. JSONB may expand exponents without changing the value.
                    writer.WriteRawValue(Number(value.GetRawText())); break;
                default: value.WriteTo(writer); break;
            }
        }
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) Write(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static string Number(string text)
    {
        var negative = text[0] == '-'; if (negative) text = text[1..];
        var exponentAt = text.IndexOfAny(['e', 'E']); var exponent = 0;
        if (exponentAt >= 0)
        {
            Require.That(int.TryParse(text[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent), "JSON exponent exceeds the supported canonical evidence budget.");
            text = text[..exponentAt];
        }
        var point = text.IndexOf('.'); if (point >= 0) { exponent = checked(exponent - (text.Length - point - 1)); text = text.Remove(point, 1); }
        text = text.TrimStart('0'); if (text.Length == 0) return "0";
        var trimmed = text.TrimEnd('0'); exponent = checked(exponent + text.Length - trimmed.Length);
        Require.That(exponent is >= -10000 and <= 10000, "JSON exponent exceeds the supported canonical evidence budget.");
        return (negative ? "-" : "") + trimmed + (exponent == 0 ? "" : "e" + exponent.ToString(CultureInfo.InvariantCulture));
    }
    public static async Task WriteReport<T>(string path, T result)
    {
        Require.That(!Path.Exists(path), "Report already exists; choose a new report path.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, result, Options);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

internal static class MigrationControls
{
    public static List<MigrationControl> Calculate(MigrationBundle bundle, IEnumerable<TenantEntity> allEntities)
    {
        var all = allEntities.ToList(); var result = new List<MigrationControl>();
        foreach (var tenant in bundle.Tenants.OrderBy(x => x.Data.Id))
        {
            var id = tenant.Data.Id; var entities = all.Where(x => x.TenantId == id).ToList();
            var quotes = entities.OfType<Quote>().Where(x => x.Status != QuoteStatus.Draft).ToList();
            var invoices = entities.OfType<Invoice>().Where(x => x.Status == InvoiceStatus.Issued).ToList();
            var originals = invoices.Where(x => x.Kind != InvoiceKind.CreditNote).ToList();
            var payments = entities.OfType<PaymentAllocation>().Where(x => !x.ReversedAt.HasValue).ToList();
            var supplierInvoices = entities.OfType<SupplierInvoice>().Where(x => x.Status == SupplierInvoiceStatus.Approved).ToList();
            var supplierPayments = entities.OfType<SupplierPaymentAllocation>().Where(x => !x.ReversedAt.HasValue).ToList();
            var memberIds = bundle.Memberships.Where(x => x.TenantId == id).Select(x => x.UserId).ToHashSet();
            result.Add(new(id, entities.Count, bundle.Documents.Count(x => x.Data.TenantId == id), memberIds.Count,
                MigrationJson.Digest(bundle.Entities.Where(x => x.Data.GetProperty("tenantId").GetGuid() == id).OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Data.GetProperty("id").GetGuid()).Select(x => new { x.Kind, id = x.Data.GetProperty("id").GetGuid(), x.Sha256 }).ToArray()),
                MigrationJson.Digest(bundle.Documents.Where(x => x.Data.TenantId == id).OrderBy(x => x.Data.Id).ToArray()),
                MigrationJson.Digest(bundle.History.Where(x => x.TenantId == id).OrderBy(x => x.Id).ToArray()),
                MigrationJson.Digest(new { accounts = bundle.Accounts.Where(x => memberIds.Contains(x.Id)).OrderBy(x => x.Id).ToArray(), memberships = bundle.Memberships.Where(x => x.TenantId == id).OrderBy(x => x.UserId).ToArray() }),
                MigrationJson.Digest(bundle.Counters.Where(x => x.TenantId == id).OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Year).ToArray()),
                new(quotes.Sum(x => x.Snapshot!.Gross), entities.OfType<Order>().Sum(x => x.Contract!.Gross), originals.Sum(x => x.Snapshot!.Net), originals.Sum(x => x.Snapshot!.Tax),
                    originals.Sum(x => x.Snapshot!.Gross), invoices.Where(x => x.Kind == InvoiceKind.CreditNote).Sum(x => x.Snapshot!.Gross),
                    payments.Where(x => x.Direction == PaymentDirection.Receipt).Sum(x => x.Amount), payments.Where(x => x.Direction == PaymentDirection.Refund).Sum(x => x.Amount),
                    payments.Sum(x => x.DiscountAmount), payments.Sum(x => x.RetainedAmount - x.RetentionReleases.Sum(r => r.Amount)), entities.OfType<BankTransaction>().Sum(x => x.Amount),
                    supplierInvoices.Where(x => x.Kind == SupplierInvoiceKind.Invoice).Sum(x => x.Gross), supplierInvoices.Where(x => x.Kind == SupplierInvoiceKind.CreditNote).Sum(x => x.Gross),
                    supplierPayments.Where(x => x.Direction == SupplierPaymentDirection.Payment).Sum(x => x.Amount), supplierPayments.Where(x => x.Direction == SupplierPaymentDirection.Refund).Sum(x => x.Amount))));
        }
        return result;
    }
}
