using System.Data;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FirstBlick.Api;

public static class IntegrationEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/integrations/v1/bank-csv/preview", Preview).WithTags("integrations");
        api.MapPost("/integrations/v1/bank-csv/import", Import).WithTags("integrations");
        api.MapGet("/integrations/v1/exports/accounting", (DateOnly from, DateOnly to, HttpContext http, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands)
            => Export("accounting", from, to, http, db, scope, commands)).WithTags("integrations");
        api.MapGet("/integrations/v1/exports/open-items", (DateOnly from, DateOnly to, HttpContext http, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands)
            => Export("open-items", from, to, http, db, scope, commands)).WithTags("integrations");
        api.MapGet("/integrations/v1/exports/payroll-time", (DateOnly from, DateOnly to, HttpContext http, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands)
            => Export("payroll-time", from, to, http, db, scope, commands)).WithTags("integrations");
    }

    private static async Task<IResult> Preview(HttpContext http, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands)
    {
        var ct = http.RequestAborted; await commands.Authorize("bank-transactions", true, ct);
        var batch = BankCsvV1.Read(await ReadCsv(http));
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await TenantLock(db, scope, ct); await commands.Authorize("bank-transactions", true, ct);
        var preview = BankCsvV1.Preview(batch, scope.TenantId, await ExistingBank(batch, db, ct));
        await transaction.CommitAsync(ct); return Results.Ok(preview);
    }

    private static async Task<IResult> Import(HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, CommandExecutor commands)
    {
        var ct = http.RequestAborted; await commands.Authorize("bank-transactions", true, ct);
        var batch = BankCsvV1.Read(await ReadCsv(http));
        var reviewed = http.Request.Headers["X-Import-SHA256"].ToString();
        Guard.Require(reviewed.Length == 64 && reviewed.All(char.IsAsciiHexDigit) && string.Equals(reviewed, batch.SourceSha256, StringComparison.OrdinalIgnoreCase),
            "preview_mismatch", "Supply X-Import-SHA256 from the reviewed preview; it must match the actual CSV bytes.");
        // RequestFingerprint captures JSON only. Bind this command to a server-calculated digest of the actual binary body.
        return await commands.Execute(http, "bank-transactions", "adapter.bank-csv.import",
            new { schema = BankCsvV1.Schema, headerContract = BankCsvV1.Header, sourceSha256 = batch.SourceSha256 }, async token =>
            {
                var existing = await ExistingBank(batch, db, token);
                var preview = BankCsvV1.Preview(batch, scope.TenantId, existing);
                Guard.Require(preview.CanImport, "idempotency_conflict", "At least one account/external identifier has different imported contents. Resolve the conflict without replacing bank evidence.");
                var known = existing.ToDictionary(x => BankCsvV1.StorageKey(x.AccountReference, x.ExternalId), StringComparer.Ordinal);
                var result = new List<BankImportItem>(); var created = 0;
                foreach (var row in batch.Rows)
                {
                    if (known.TryGetValue(row.StorageKey, out var prior)) result.Add(new(row.Record, prior.Id, prior.ImportDigest, false));
                    else
                    {
                        var entity = row.ToEntity(scope.TenantId);
                        await store.Save(entity, "adapter.bank-csv.import", true, token);
                        known.Add(row.StorageKey, entity); created++; result.Add(new(row.Record, entity.Id, entity.ImportDigest, true));
                    }
                }
                var envelope = new IntegrationEventV1("firstblick.integration-event/1.0.0", Guid.NewGuid(), scope.TenantId, scope.UserId, DateTimeOffset.UtcNow,
                    "bank-import.completed", batch.SourceSha256);
                var receipt = new BankImportReceipt(envelope, BankCsvV1.Schema, created, result.Count - created, result.AsReadOnly());
                ManagementEndpoints.Audit(db, scope, "adapter.bank-csv.completed", "bank-transactions", envelope.Id, 1, receipt);
                return receipt;
            });
    }

    private static async Task<List<BankTransaction>> ExistingBank(BankCsvBatch batch, FirstBlickDbContext db, CancellationToken ct)
    {
        var keys = batch.Rows.Select(x => x.StorageKey).Distinct(StringComparer.Ordinal).ToArray();
        var values = await db.Entities.AsNoTracking().Where(x => x.Kind == "bank-transactions" && x.UniqueKey != null && keys.Contains(x.UniqueKey)).Select(x => x.Data).ToListAsync(ct);
        return values.Select(AppJson.Read<BankTransaction>).ToList();
    }

    private static async Task<byte[]> ReadCsv(HttpContext http)
    {
        Guard.Require(MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var contentType)
            && contentType.MediaType?.Equals("text/csv", StringComparison.OrdinalIgnoreCase) == true
            && (string.IsNullOrWhiteSpace(contentType.CharSet) || contentType.CharSet.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)),
            "csv_content_type", "Use text/csv with UTF-8 encoding.");
        Guard.Require(http.Request.ContentLength is null or <= CsvV1.MaximumInputBytes, "payload_too_large", "CSV requests are limited to 2 MiB.");
        using var bytes = new MemoryStream(); var buffer = new byte[81920];
        while (true)
        {
            var count = await http.Request.Body.ReadAsync(buffer, http.RequestAborted); if (count == 0) break;
            Guard.Require(bytes.Length + count <= CsvV1.MaximumInputBytes, "payload_too_large", "CSV requests are limited to 2 MiB.");
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    private static async Task<IResult> Export(string kind, DateOnly from, DateOnly to, HttpContext http, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands)
    {
        var ct = http.RequestAborted;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await TenantLock(db, scope, ct);
        var resource = kind == "payroll-time" ? "time-entries" : "invoices";
        await commands.Authorize(resource, false, ct);
        Guard.Require(kind == "payroll-time" ? scope.Role is BusinessRole.ManagingDirector or BusinessRole.Office
            : scope.Role is BusinessRole.ManagingDirector or BusinessRole.Office or BusinessRole.Accounting,
            "forbidden", "Your role does not allow this bulk export.");
        Guard.Require(!http.Request.Query.ContainsKey("asOf") && !http.Request.Query.ContainsKey("as_of"), "unsupported_as_of", "These exports capture current records; historical accounting cutoffs are not supported.");
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == scope.TenantId, ct);
        var window = new ExportWindow(from, to, tenant.TimeZone); window.Validate();
        var capture = new ExportContext(Guid.NewGuid(), scope.TenantId, scope.UserId, DateTimeOffset.UtcNow, window);
        var source = await ReadExportSources(kind, window, db, scope, ct);
        List<T> Read<T>() where T : TenantEntity => source.Where(x => x.Kind == BusinessRegistry.KindOf<T>()).Select(x => AppJson.Read<T>(x.Data)).ToList();
        ExportBundle bundle;
        if (kind == "payroll-time") bundle = PayrollTimeExport.Build(capture, Read<TimeEntry>(), Read<Employee>(), Read<Project>());
        else
        {
            var ledger = new LedgerExportSources(Read<Invoice>(), Read<SupplierInvoice>(), Read<BankTransaction>(), Read<PaymentAllocation>(), Read<SupplierPaymentAllocation>());
            bundle = kind == "accounting" ? LedgerExport.Accounting(capture, ledger) : LedgerExport.OpenItems(capture, ledger);
        }
        var bytes = bundle.ToZip(); var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        ManagementEndpoints.Audit(db, scope, "export." + kind, resource, capture.Id, 1, new { bundle.Manifest, zipSha256 = digest });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        http.Response.Headers["X-FirstBlick-Export-Schema"] = bundle.Manifest.Schema;
        http.Response.Headers["X-Content-SHA256"] = digest;
        return Results.File(bytes, "application/zip", $"FirstBlick-{kind}-v1-{from:yyyyMMdd}-{to:yyyyMMdd}.zip");
    }

    private static Task TenantLock(FirstBlickDbContext db, TenantScope scope, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({scope.TenantId.ToString()}, 0))", ct);

    public sealed class ExportSourceRow { public string Kind { get; set; } = ""; public string Data { get; set; } = ""; }
    public sealed class ExportSourceBudget { public long Records { get; set; } public long Bytes { get; set; } }

    private static async Task<List<ExportSourceRow>> ReadExportSources(string kind, ExportWindow window, FirstBlickDbContext db, TenantScope scope, CancellationToken ct)
    {
        // Only fixed source-code fragments choose the table/selection. Tenant, dates and UTC boundaries remain SQL parameters.
        var cte = kind == "payroll-time" ? PayrollCte : FinancialCte(kind == "open-items");
        var tail = " FROM \"Entities\" e JOIN selected s ON s.\"Kind\"=e.\"Kind\" AND s.\"Id\"=e.\"Id\" WHERE e.\"TenantId\"=@tenant";
        NpgsqlParameter[] Parameters() => [new("tenant", scope.TenantId), new("from", window.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), new("to", window.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new("start", window.Start), new("end", window.EndExclusive)];
#pragma warning disable EF1002, EF1003 // Fixed adapter SQL fragments only; every request value is a separate Npgsql parameter.
        var budget = await db.Database.SqlQueryRaw<ExportSourceBudget>(cte + " SELECT count(*)::bigint AS \"Records\", COALESCE(sum(octet_length(e.\"Data\"::text)),0)::bigint AS \"Bytes\"" + tail, Parameters()).SingleAsync(ct);
        Guard.Require(budget.Records <= ExportLimits.MaximumSources && budget.Bytes <= ExportLimits.MaximumSourceBytes, "export_budget", "The date window and its linked evidence exceed 10,000 records or 12 MiB of source data. Select a smaller window.");
        return await db.Database.SqlQueryRaw<ExportSourceRow>(cte + " SELECT e.\"Kind\",e.\"Data\"::text AS \"Data\"" + tail + " ORDER BY e.\"Kind\",e.\"Id\"", Parameters()).ToListAsync(ct);
#pragma warning restore EF1002, EF1003
    }

    private const string PayrollCte = """
        WITH time_ids AS (
            SELECT "Kind","Id" FROM "Entities"
            WHERE "TenantId"=@tenant AND "Kind"='time-entries' AND NOT "Archived" AND "Data"->>'approved'='true'
              AND ("Data"->>'start')::timestamptz < @end AND ("Data"->>'end')::timestamptz > @start
        ), selected AS (
            SELECT "Kind","Id" FROM time_ids
            UNION
            SELECT l."TargetKind" AS "Kind",l."TargetId" AS "Id" FROM "Links" l
            JOIN time_ids t ON t."Kind"=l."SourceKind" AND t."Id"=l."SourceId"
            WHERE l."TenantId"=@tenant AND l."TargetKind" IN ('employees','projects')
        )
        """;

    private static string FinancialCte(bool openItems)
    {
        const string invoiceDates = "(e.\"Kind\"='invoices' AND e.\"Data\"->>'issueDate' BETWEEN @from AND @to) OR (e.\"Kind\"='supplier-invoices' AND e.\"Data\"->>'invoiceDate' BETWEEN @from AND @to)";
        var seed = openItems
            ? "(" + invoiceDates + ") AND e.\"Data\"->>'kind'<>'CreditNote'"
            : "(" + invoiceDates + " OR (e.\"Kind\"='bank-transactions' AND e.\"Data\"->>'bookedOn' BETWEEN @from AND @to) OR (e.\"Kind\" IN ('payment-allocations','supplier-payment-allocations') AND (((e.\"Data\"->>'allocatedAt')::timestamptz >= @start AND (e.\"Data\"->>'allocatedAt')::timestamptz < @end) OR ((e.\"Data\"->>'reversedAt')::timestamptz >= @start AND (e.\"Data\"->>'reversedAt')::timestamptz < @end))) OR (e.\"Kind\"='payment-allocations' AND EXISTS (SELECT 1 FROM jsonb_array_elements(COALESCE(e.\"Data\"->'retentionReleases','[]'::jsonb)) AS releases(value) WHERE (releases.value->>'releasedAt')::timestamptz >= @start AND (releases.value->>'releasedAt')::timestamptz < @end)))";
        return """
            WITH RECURSIVE eligible AS (
                SELECT "Kind","Id" FROM "Entities" WHERE "TenantId"=@tenant AND (
                    ("Kind"='invoices' AND "Data"->>'status'='Issued') OR
                    ("Kind"='supplier-invoices' AND "Data"->>'status'='Approved') OR
                    "Kind" IN ('bank-transactions','payment-allocations','supplier-payment-allocations'))
            ), edges AS (
                SELECT l."SourceKind" AS "FromKind",l."SourceId" AS "FromId",l."TargetKind" AS "Kind",l."TargetId" AS "Id"
                FROM "Links" l JOIN eligible a ON a."Kind"=l."SourceKind" AND a."Id"=l."SourceId"
                JOIN eligible b ON b."Kind"=l."TargetKind" AND b."Id"=l."TargetId" WHERE l."TenantId"=@tenant
                UNION ALL
                SELECT l."TargetKind",l."TargetId",l."SourceKind",l."SourceId"
                FROM "Links" l JOIN eligible a ON a."Kind"=l."SourceKind" AND a."Id"=l."SourceId"
                JOIN eligible b ON b."Kind"=l."TargetKind" AND b."Id"=l."TargetId" WHERE l."TenantId"=@tenant
            ), selected AS (
                SELECT e."Kind",e."Id" FROM "Entities" e JOIN eligible a ON a."Kind"=e."Kind" AND a."Id"=e."Id"
                WHERE e."TenantId"=@tenant AND (
            """ + seed + """
                )
                UNION
                SELECT edges."Kind",edges."Id" FROM selected s JOIN edges ON edges."FromKind"=s."Kind" AND edges."FromId"=s."Id"
            )
            """;
    }
}
