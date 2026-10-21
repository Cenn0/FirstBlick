using System.Globalization;
using System.Security.Cryptography;
using FirstBlick.Core.Business;

namespace FirstBlick.Core.Integrations;

public sealed record BankCsvRow(int Record, string AccountReference, string ExternalId, DateOnly BookedOn, decimal Amount, string Currency, string Counterparty, string Reference)
{
    public BankTransaction ToEntity(Guid tenantId)
    {
        var bank = new BankTransaction { TenantId = tenantId, AccountReference = AccountReference, ExternalId = ExternalId, BookedOn = BookedOn,
            Amount = Amount, Currency = Currency, Counterparty = Counterparty, Reference = Reference };
        PaymentWorkflow.SealImport(bank); return bank;
    }
    public string StorageKey => BankCsvV1.StorageKey(AccountReference, ExternalId);
}
public sealed record BankCsvBatch(string Schema, string SourceSha256, IReadOnlyList<BankCsvRow> Rows);
public sealed record BankCsvPreviewRow(int Record, string State, Guid? ExistingId, string ImportDigest, BankCsvRow Values);
public sealed record BankCsvPreview(string Schema, string SourceSha256, int New, int Reused, int Conflicts, bool CanImport, IReadOnlyList<BankCsvPreviewRow> Rows);
public sealed record BankImportItem(int Record, Guid Id, string ImportDigest, bool Created);
public sealed record IntegrationEventV1(string Schema, Guid Id, Guid TenantId, Guid ActorId, DateTimeOffset OccurredAt, string Type, string SourceSha256);
public sealed record BankImportReceipt(IntegrationEventV1 Event, string Adapter, int Created, int Reused, IReadOnlyList<BankImportItem> Items);

public static class BankCsvV1
{
    public const string Schema = "firstblick.bank-csv/1.0.0";
    public const int MaximumRows = 1000;
    public static readonly IReadOnlyList<string> Header = Array.AsReadOnly(new[] { "account_reference", "external_id", "booked_on", "amount_eur", "currency", "counterparty", "reference" });

    public static BankCsvBatch Read(ReadOnlySpan<byte> source)
    {
        var records = CsvV1.Parse(source, MaximumRows + 1, Header.Count);
        Guard.Require(records[0].Fields.SequenceEqual(Header, StringComparer.Ordinal), "csv_header", "The exact FirstBlick bank CSV-v1 header and column order are required.");
        Guard.Require(records.Count > 1, "csv_empty", "The bank file must contain at least one transaction.");
        var rows = records.Skip(1).Select(record =>
        {
            Guard.Require(record.Fields.Count == Header.Count, "csv_columns", $"CSV record {record.Number} does not contain {Header.Count} fields.");
            var fields = record.Fields;
            Guard.Require(DateOnly.TryParseExact(fields[2], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date), "csv_date", $"CSV record {record.Number} requires an ISO booking date.");
            var amount = ExactEuros(fields[3], record.Number);
            var row = new BankCsvRow(record.Number, fields[0], fields[1], date, amount, fields[4], fields[5], fields[6]);
            // Reuse the business ledger's complete bounds, currency and immutable-input validation.
            _ = row.ToEntity(new Guid("10000000-0000-0000-0000-000000000001"));
            Guard.Require(fields[0] == fields[0].Trim() && fields[1] == fields[1].Trim()
                && !fields[0].Any(char.IsControl) && !fields[1].Any(char.IsControl), "csv_key", $"CSV record {record.Number} requires exact account/external identifiers without surrounding whitespace or control characters.");
            return row;
        }).ToArray();
        return new(Schema, Convert.ToHexStringLower(SHA256.HashData(source)), Array.AsReadOnly(rows));
    }

    public static BankCsvPreview Preview(BankCsvBatch batch, Guid tenantId, IReadOnlyCollection<BankTransaction> existing)
    {
        Guard.Id(tenantId, nameof(tenantId)); Guard.Require(batch.Schema == Schema, "adapter_version", "This adapter version is unsupported.");
        Guard.Items(batch.Rows, nameof(batch.Rows), MaximumRows, 1); Guard.Items(existing, nameof(existing), MaximumRows);
        var known = new Dictionary<string, BankTransaction>(StringComparer.Ordinal);
        foreach (var bank in existing)
        {
            bank.Validate(); Guard.Require(bank.TenantId == tenantId && !string.IsNullOrEmpty(bank.ImportDigest) && !bank.Archived, "invalid_import_evidence", "Existing bank evidence must be sealed and belong to this business.");
            Guard.Require(known.TryAdd(StorageKey(bank.AccountReference, bank.ExternalId), bank), "duplicate_bank_key", "Stored bank evidence has duplicate external identifiers.");
        }
        var rows = new List<BankCsvPreviewRow>(); var fresh = 0; var reused = 0; var conflicts = 0;
        foreach (var row in batch.Rows)
        {
            var candidate = row.ToEntity(tenantId);
            if (known.TryGetValue(row.StorageKey, out var old))
            {
                var equal = old.ImportDigest == candidate.ImportDigest;
                rows.Add(new(row.Record, equal ? "Reused" : "Conflict", existing.Any(x => x.Id == old.Id) ? old.Id : null, candidate.ImportDigest, row));
                if (equal) reused++; else conflicts++;
            }
            else { known.Add(row.StorageKey, candidate); fresh++; rows.Add(new(row.Record, "New", null, candidate.ImportDigest, row)); }
        }
        return new(Schema, batch.SourceSha256, fresh, reused, conflicts, conflicts == 0, rows.AsReadOnly());
    }

    public static string StorageKey(string accountReference, string externalId) => "bank:" + ContentDigest.Of(new { AccountReference = accountReference, ExternalId = externalId });

    private static decimal ExactEuros(string value, int record)
    {
        var digits = value.AsSpan(); if (digits.Length > 0 && digits[0] == '-') digits = digits[1..];
        var dot = digits.IndexOf('.');
        Guard.Require(dot >= 1 && digits.Length - dot == 3 && digits[..dot].IndexOfAnyExceptInRange('0', '9') < 0 && digits[(dot + 1)..].IndexOfAnyExceptInRange('0', '9') < 0
            && (dot == 1 || digits[0] != '0') && decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _),
            "csv_amount", $"CSV record {record} requires an exact EUR value such as 1234.56 or -12.50; grouping, exponent notation and implicit rounding are rejected.");
        var amount = decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        Money.Amount(amount, "Bank amount", true); Guard.Require(amount != 0, "zero_transaction", "Bank imports cannot contain zero-value rows.");
        return amount;
    }
}
