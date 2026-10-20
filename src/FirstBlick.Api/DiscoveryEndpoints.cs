using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public static class DiscoveryEndpoints
{
    private const string CopySchema = "firstblick.self-data-copy/1.0.0";
    private static readonly string[] CopyKinds = ["time-entries", "diary-entries", "form-submissions", "notification-preferences"];
    // These fields are deliberately narrower than the generic SearchText projection: notes, addresses,
    // staff data, bank references, line items and form answers must not become a search-match oracle.
    private static readonly IReadOnlyDictionary<string, SearchFields> Searchable = new Dictionary<string, SearchFields>(StringComparer.Ordinal)
    {
        ["customers"] = new("name"), ["buildings"] = new("name"), ["projects"] = new("name", "number"),
        ["tasks"] = new("title"), ["catalog-items"] = new("name"), ["quotes"] = new("title", "number"),
        ["orders"] = new("title", "number"), ["change-orders"] = new("title"), ["invoices"] = new("title", "number"),
        ["suppliers"] = new("name"), ["purchase-orders"] = new("title", "number"), ["supplier-invoices"] = new("title", "number"),
        ["materials"] = new("name", "sku"), ["equipment"] = new("name", "inventoryNumber"), ["defects"] = new("title"),
        ["acceptances"] = new("title"), ["maintenance-contracts"] = new("title"), ["form-definitions"] = new("name"),
        ["roof-revisions"] = new("title"), ["measurements"] = new("title"), ["technical-results"] = new("title")
    };
    private static readonly string[] HistoryKinds = ["projects", "tasks", "quotes", "orders", "change-orders", "invoices", "payment-allocations",
        "purchase-orders", "supplier-invoices", "supplier-payment-allocations", "stock-movements", "material-reservations",
        "time-entries", "schedule-assignments", "diary-entries", "defects", "acceptances", "form-submissions", "roof-revisions", "measurements", "technical-results"];

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/search", Search).WithTags("discovery");
        api.MapGet("/projects/{id:guid}/history", History).WithTags("discovery");
        api.MapGet("/self/data-copy", DataCopy).WithTags("account");
    }

    private static async Task<IResult> Search(HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, CommandExecutor commands)
    {
        AllowedQuery(http, "q", "kind", "page", "pageSize");
        var page = Page.Read(http); var query = SingleQuery(http, "q", "").Trim();
        Guard.Require(query.Length is >= 2 and <= 200 && !query.Contains('\0'), "search_query", "Search requires 2 to 200 characters.");
        var selectedKind = SingleQuery(http, "kind", "");
        Guard.Require(selectedKind.Length == 0 || selectedKind == "documents" || Searchable.ContainsKey(selectedKind), "search_kind", "This resource is not in the searchable field whitelist.");
        var ct = http.RequestAborted;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await Lock(db, scope, ct); await commands.Authorize("projects", false, ct);
        if (selectedKind.Length != 0) await commands.Authorize(selectedKind, false, ct);
        var access = new DiscoveryAccess(db, scope, store); var sql = new DiscoverySql();
        var tenant = sql.Add(scope.TenantId); var term = sql.Add(query.ToLowerInvariant());
        var kinds = Searchable.Keys.Where(x => selectedKind.Length == 0 || x == selectedKind).ToArray();
        var visible = await access.KindFilter(sql, "e.\"Kind\"", "e.\"Id\"", kinds, ct);
        var titleCases = string.Join(' ', Searchable.Select(x => $"WHEN '{x.Key}' THEN e.\"Data\"->>'{x.Value.Title}'"));
        var numberCases = string.Join(' ', Searchable.Where(x => x.Value.Number is not null).Select(x => $"WHEN '{x.Key}' THEN e.\"Data\"->>'{x.Value.Number}'"));
        var entities = $"""
            SELECT e."Kind",e."Id",e."Revision",COALESCE(CASE e."Kind" {titleCases} END,'') AS "Title",
                COALESCE(CASE e."Kind" {numberCases} END,'') AS "Number",e."UpdatedAt"
            FROM "Entities" e WHERE e."TenantId"={tenant} AND NOT e."Archived" AND {visible}
            """;
        var documents = "";
        if (selectedKind.Length == 0 || selectedKind == "documents")
        {
            var documentAccess = await access.DocumentsFilter(sql, ct);
            documents = $"""
                UNION ALL SELECT 'documents'::text AS "Kind",d."Id",d."Revision",d."Name" AS "Title",''::text AS "Number",d."CreatedAt" AS "UpdatedAt"
                FROM "Documents" d WHERE d."TenantId"={tenant} AND d."State"<>'Archived' AND {documentAccess}
                """;
        }
        var selection = $"FROM ({entities} {documents}) result WHERE position({term} in lower(result.\"Title\"))>0 OR position({term} in lower(result.\"Number\"))>0";
        var total = (await sql.Read<CountRow>(db, "SELECT count(*)::bigint AS \"Value\" " + selection, ct)).Single().Value;
        var take = sql.Add(page.PageSize); var skip = sql.Add(page.Offset);
        var rows = await sql.Read<SearchItem>(db, "SELECT * " + selection + $" ORDER BY \"UpdatedAt\" DESC,\"Kind\",\"Id\" LIMIT {take} OFFSET {skip}", ct);
        var capturedAt = DateTimeOffset.UtcNow; await transaction.CommitAsync(ct); NoStore(http);
        return Results.Ok(new { schema = "firstblick.search/1.0.0", items = rows, total, page = page.PageNumber, page.PageSize, capturedAt });
    }

    private static async Task<IResult> History(Guid id, HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, CommandExecutor commands)
    {
        AllowedQuery(http, "page", "pageSize"); var page = Page.Read(http); var ct = http.RequestAborted;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await Lock(db, scope, ct); await commands.Authorize("projects", false, ct);
        var access = new DiscoveryAccess(db, scope, store); var projectIds = await access.Visible("projects", ct);
        Guard.Require(projectIds is null || projectIds.Contains(id), "not_found", "The project does not exist or is outside your assigned work.");
        Guard.Require(await db.Entities.AnyAsync(x => x.Kind == "projects" && x.Id == id, ct), "not_found", "The project does not exist.");
        var sql = new DiscoverySql(); var tenant = sql.Add(scope.TenantId); var project = sql.Add(id); var kinds = sql.Add(HistoryKinds);
        // Follow incoming project-owned references only. Outgoing references to shared customers, buildings,
        // employees, materials or bank transactions must never join unrelated projects into this history.
        var graph = $"""
            WITH RECURSIVE related("Kind","Id") AS (
                SELECT 'projects'::text,{project}::uuid
                UNION
                SELECT l."SourceKind",l."SourceId" FROM "Links" l
                JOIN related r ON l."TargetKind"=r."Kind" AND l."TargetId"=r."Id"
                WHERE l."TenantId"={tenant} AND l."SourceKind"=ANY({kinds})
            )
            """;
        var count = (await sql.Read<CountRow>(db, graph + " SELECT count(*)::bigint AS \"Value\" FROM (SELECT 1 FROM related LIMIT 10001) bounded", ct)).Single().Value;
        Guard.Require(count <= 10_000, "history_budget", "The project's current graph exceeds 10,000 linked records. A complete authorized history cannot be paged within this release's graph budget.");
        var visible = await access.KindFilter(sql, "r.\"Kind\"", "r.\"Id\"", HistoryKinds, ct);
        var documentAccess = await access.DocumentsFilter(sql, ct);
        var keys = graph + $"""
            , visible_keys AS (
                SELECT r."Kind",r."Id" FROM related r WHERE {visible}
                UNION
                SELECT 'documents'::text,d."Id" FROM "Documents" d WHERE d."TenantId"={tenant} AND {documentAccess}
                AND (d."ProjectId"={project} OR EXISTS(SELECT 1 FROM related r WHERE r."Kind"=d."SourceKind" AND r."Id"=d."SourceId"))
            )
            """;
        var tail = $"FROM \"Audit\" a JOIN visible_keys k ON a.\"Kind\"=k.\"Kind\" AND a.\"EntityId\"=k.\"Id\" WHERE a.\"TenantId\"={tenant}";
        var total = (await sql.Read<CountRow>(db, keys + " SELECT count(*)::bigint AS \"Value\" " + tail, ct)).Single().Value;
        var take = sql.Add(page.PageSize); var skip = sql.Add(page.Offset);
        var events = await sql.Read<HistoryRow>(db, keys + " SELECT a.\"Id\",a.\"At\",a.\"ActorId\",a.\"Action\",a.\"Kind\",a.\"EntityId\",a.\"Revision\",a.\"ContentDigest\" "
            + tail + $" ORDER BY a.\"At\" DESC,a.\"Id\" DESC LIMIT {take} OFFSET {skip}", ct);
        var items = events.Select(x => new { x.Id, occurredAt = x.At, action = SafeAction(x.Action), x.Kind, x.EntityId, x.Revision,
            title = HistoryTitle(x.Kind, x.Action), performedByCurrentUser = x.ActorId == scope.UserId, contentDigest = SafeDigest(x.ContentDigest) }).ToArray();
        var capturedAt = DateTimeOffset.UtcNow; await transaction.CommitAsync(ct); NoStore(http);
        return Results.Ok(new { schema = "firstblick.project-history/1.0.0", projectId = id, associationScope = "current-accessible-project-links",
            items, total, page = page.PageNumber, page.PageSize, capturedAt });
    }

    private static async Task<IResult> DataCopy(HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store)
    {
        // No subject, account, employee, tenant or category selector: the signed-in account is the subject.
        AllowedQuery(http); var ct = http.RequestAborted;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await Lock(db, scope, ct);
        var membership = await db.Memberships.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.UserId == scope.UserId && x.Active, ct);
        Guard.Require(membership is not null && Enum.IsDefined(membership.Role), "forbidden", "Business access has been revoked.");
        scope.Role = membership.Role; await RecoveryBarrier.EnsureAvailable(db, scope.TenantId, ct);
        // Project only public account fields. Password hashes, security stamps, recovery tokens and keys
        // are never materialized for this response, nor put into its manifest or audit record.
        var profile = await db.Users.AsNoTracking().Where(x => x.Id == scope.UserId)
            .Select(x => new { x.Id, x.DisplayName, x.Email, x.EmailConfirmed, x.PhoneNumber, x.PhoneNumberConfirmed }).SingleAsync(ct);
        var tenantInfo = await db.Tenants.AsNoTracking().Where(x => x.Id == scope.TenantId).Select(x => new { x.Id, x.Name, x.TimeZone }).SingleAsync(ct);
        var currentMembership = new { membership.TenantId, membership.UserId, membership.Role, membership.Active, membership.Revision, tenant = tenantInfo };
        var access = new DiscoveryAccess(db, scope, store); var sql = new DiscoverySql(); var tenant = sql.Add(scope.TenantId); var user = sql.Add(scope.UserId.ToString());
        var permittedKinds = CopyKinds.Where(x => AccessRules.CanRead(scope.Role, x)).ToArray();
        var policy = await access.KindFilter(sql, "e.\"Kind\"", "e.\"Id\"", permittedKinds, ct);
        var selection = $"""
            FROM "Entities" e WHERE e."TenantId"={tenant} AND NOT e."Archived" AND {policy} AND (
                (e."Kind" IN ('time-entries','diary-entries') AND EXISTS (
                    SELECT 1 FROM "Entities" own WHERE own."TenantId"={tenant} AND own."Kind"='employees' AND NOT own."Archived"
                    AND own."Data"->>'userId'={user} AND own."Id"::text=e."Data"->>'employeeId'))
                OR (e."Kind"='form-submissions' AND e."Data"->>'submittedBy'={user})
                OR (e."Kind"='notification-preferences' AND e."Data"->>'userId'={user})
            )
            """;
        var budget = (await sql.Read<DiscoveryBudget>(db, "SELECT count(*)::bigint AS \"Records\",COALESCE(sum(octet_length(e.\"Data\"::text)),0)::bigint AS \"Bytes\" " + selection, ct)).Single();
        Guard.Require(budget.Records <= 5000 && budget.Bytes <= 10 * 1024 * 1024, "data_copy_budget", "The self-service copy exceeds 5,000 records or 10 MiB of source data. Request an assisted copy; no partial archive has been returned.");
        var source = await sql.Read<CopySource>(db, "SELECT e.\"Kind\",e.\"Id\",e.\"Revision\",e.\"Data\"::text AS \"Data\" " + selection + " ORDER BY e.\"Kind\",e.\"Id\"", ct);
        var records = source.Select(row =>
        {
            var entity = AppJson.Read(row.Kind, row.Data); entity.Validate();
            Guard.Require(entity.Id == row.Id && entity.TenantId == scope.TenantId && entity.Revision == row.Revision && !entity.Archived,
                "data_copy_source", "A stored record does not agree with its tenant, identifier, revision or archive metadata.");
            return new { row.Kind, row.Id, row.Revision, data = JsonSerializer.SerializeToElement(entity, entity.GetType(), AppJson.Options) };
        }).ToArray();
        var capturedAt = DateTimeOffset.UtcNow; var captureId = Guid.NewGuid();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["profile.json"] = Json(profile), ["membership.json"] = Json(currentMembership), ["records.json"] = Json(records)
        };
        var coverage = CopyKinds.Select(kind => new { kind, included = permittedKinds.Contains(kind), records = source.Count(x => x.Kind == kind),
            selection = "own-nonarchived-currently-readable", reason = permittedKinds.Contains(kind) ? "current-record-policy" : "role-does-not-read-category" }).ToArray();
        var manifest = new { schema = CopySchema, purpose = "self-service-data-copy", captureId, scope.TenantId, scope.UserId, capturedAt,
            coverage, limits = new { maximumRecords = 5000, maximumSourceBytes = 10 * 1024 * 1024 },
            exclusions = new[] { "other-accounts-and-tenants", "archived-or-inaccessible-records", "employee-profiles", "documents-and-attachments", "training-records",
                "audit-and-communications", "credentials-and-recovery-secrets", "other-business-records" },
            completeness = "A bounded copy of the listed current resources, not a complete data-subject access response.",
            files = files.Select(x => new { name = x.Key, bytes = x.Value.Length, sha256 = Sha(x.Value) }).ToArray() };
        files.Add("manifest.json", Json(manifest));
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Key, CompressionLevel.Optimal); entry.LastWriteTime = capturedAt;
                using var stream = entry.Open(); stream.Write(file.Value);
            }
        var bytes = buffer.ToArray(); var digest = Sha(bytes);
        ManagementEndpoints.Audit(db, scope, "self.data-copy", "identity", scope.UserId, membership.Revision,
            new { captureId, schema = CopySchema, records = source.Count, manifestSha256 = Sha(files["manifest.json"]), zipSha256 = digest });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); NoStore(http);
        http.Response.Headers["X-FirstBlick-Export-Schema"] = CopySchema; http.Response.Headers["X-Content-SHA256"] = digest;
        return Results.File(bytes, "application/zip", $"FirstBlick-self-data-copy-v1-{captureId:N}.zip");
    }

    private static Task Lock(FirstBlickDbContext db, TenantScope scope, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({scope.TenantId.ToString()},0))", ct);
    private static byte[] Json<T>(T value) => Encoding.UTF8.GetBytes(AppJson.Serialize(value));
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string? SafeDigest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit) ? value.ToLowerInvariant() : null;
    private static void NoStore(HttpContext http) { http.Response.Headers.CacheControl = "no-store, private"; http.Response.Headers.Pragma = "no-cache"; }
    private static void AllowedQuery(HttpContext http, params string[] names)
        => Guard.Require(http.Request.Query.Keys.All(x => names.Contains(x, StringComparer.Ordinal)), "unsupported_query", "This endpoint does not support one or more supplied query parameters.");
    private static string SingleQuery(HttpContext http, string name, string fallback)
    {
        if (!http.Request.Query.TryGetValue(name, out var value)) return fallback;
        Guard.Require(value.Count == 1, "invalid_query", "Each query parameter may be supplied only once."); return value[0] ?? "";
    }
    private static string SafeAction(string action) => action switch
    {
        "create" or "update" or "archive" or "quote.issue" or "quote.accept" or "quote.reject" or "quote.delivery.request" or "order.from_quote" or "change.accept"
            or "invoice.issue" or "invoice.correction" or "payment.allocate" or "payment.refund_reconcile" or "payment.reverse" or "payment.release_retention"
            or "supplier-invoice.approve" or "supplier-invoice.reject" or "supplier-payment.allocate" or "supplier-payment.reverse" or "purchase.issue" or "purchase.receive"
            or "material.consume" or "material.release" or "material.reserve" or "time.approve" or "defect.resolve" or "defect.accept"
            or "form.submit" or "roof.publish" or "roof.revise" or "measurement.freeze" or "technical.calculate"
            or "document.stage" or "document.ready" or "document.generated" or "document.archive" => action,
        _ => "record.changed"
    };
    private static string HistoryTitle(string kind, string action)
    {
        var subject = kind switch
        {
            "projects" => "Projekt", "tasks" => "Aufgabe", "quotes" => "Angebot", "orders" => "Auftrag", "change-orders" => "Nachtrag", "invoices" => "Rechnung",
            "payment-allocations" => "Zahlungszuordnung", "purchase-orders" => "Bestellung", "supplier-invoices" => "Lieferantenrechnung", "supplier-payment-allocations" => "Lieferantenzahlung",
            "stock-movements" => "Lagerbewegung", "material-reservations" => "Materialreservierung", "time-entries" => "Zeiteintrag", "schedule-assignments" => "Einsatz",
            "diary-entries" => "Tagebucheintrag", "defects" => "Mangel", "acceptances" => "Abnahme", "form-submissions" => "Formularantwort", "roof-revisions" => "Dachversion",
            "measurements" => "Aufmaß", "technical-results" => "Berechnung", "documents" => "Dokument", _ => "Datensatz"
        };
        var verb = SafeAction(action) switch
        {
            "create" or "order.from_quote" or "invoice.correction" or "document.generated" => "erstellt",
            "archive" or "document.archive" => "archiviert", "quote.issue" or "invoice.issue" => "ausgestellt", "quote.accept" or "change.accept" or "defect.accept" => "angenommen",
            "quote.reject" or "supplier-invoice.reject" => "abgelehnt", "roof.publish" => "veröffentlicht", "measurement.freeze" => "eingefroren",
            "time.approve" or "supplier-invoice.approve" => "freigegeben", "document.ready" => "bereitgestellt", "quote.delivery.request" => "zum Versand vorgemerkt", _ => "geändert"
        };
        return subject + " " + verb;
    }

    private sealed record SearchFields(string Title, string? Number = null);
    private sealed record Page(int PageNumber, int PageSize)
    {
        public int Offset => checked((PageNumber - 1) * PageSize);
        public static Page Read(HttpContext http)
        {
            Guard.Require(int.TryParse(SingleQuery(http, "page", "1"), NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page is >= 1 and <= 10_000,
                "invalid_page", "page must be an integer between 1 and 10,000.");
            Guard.Require(int.TryParse(SingleQuery(http, "pageSize", "25"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size is >= 1 and <= 50,
                "invalid_page", "pageSize must be an integer between 1 and 50."); return new(page, size);
        }
    }
    public sealed class SearchItem
    {
        public string Kind { get; set; } = ""; public Guid Id { get; set; } public long Revision { get; set; }
        public string Title { get; set; } = ""; public string Number { get; set; } = ""; public DateTimeOffset UpdatedAt { get; set; }
    }
    public sealed class HistoryRow
    {
        public long Id { get; set; } public DateTimeOffset At { get; set; } public Guid ActorId { get; set; } public string Action { get; set; } = "";
        public string Kind { get; set; } = ""; public Guid EntityId { get; set; } public long Revision { get; set; } public string ContentDigest { get; set; } = "";
    }
    public sealed class CopySource
    {
        public string Kind { get; set; } = ""; public Guid Id { get; set; } public long Revision { get; set; } public string Data { get; set; } = "";
    }
    public sealed class CountRow { public long Value { get; set; } }
}
