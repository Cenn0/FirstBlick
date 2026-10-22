using System.Data;
using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FirstBlick.Api;

public static class BusinessActivityEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/reports/business-activity", Report).Produces<ActivityReport>().WithName("BusinessActivityReport").WithTags("reports");
        api.MapGet("/reports/business-activity/export", Export).WithName("ExportBusinessActivity").WithTags("reports");
    }

    private static async Task<IResult> Report(HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, CommandExecutor commands)
        => Results.Ok(await Read(http, db, scope, store, commands, false));

    private static async Task<IResult> Export(HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, CommandExecutor commands)
    {
        var format = http.Request.Query["format"].ToString();
        Guard.Require(format is "csv" or "json", "activity_format", "Choose CSV or JSON export.");
        var expected = http.Request.Query["expectedDigest"].ToString();
        Guard.Require(expected.Length == 64 && expected.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "activity_digest", "Load and review the report before exporting it.");
        var report = await Read(http, db, scope, store, commands, true);
        Guard.Require(expected == report.Digest, "activity_report_changed", "The report sources or settings changed. Refresh and review the report before exporting.");
        var content = format == "csv" ? BusinessActivity.SourceCsv(report) : AppJson.Serialize(report);
        var bytes = Encoding.UTF8.GetBytes(content);
        http.Response.Headers["X-Content-Sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        http.Response.Headers["X-Report-Digest"] = report.Digest;
        http.Response.Headers["X-Report-Tenant"] = report.TenantId.ToString();
        return Results.File(bytes, format == "csv" ? "text/csv; charset=utf-8" : "application/json; charset=utf-8",
            $"firstblick-activity-{report.Current.From:yyyy-MM-dd}-{report.Current.To:yyyy-MM-dd}.{format}");
    }

    private static async Task<ActivityReport> Read(HttpContext http, FirstBlickDbContext db, TenantScope scope,
        EntityStore store, CommandExecutor commands, bool export)
    {
        var allowed = new HashSet<string>(["from", "to", "projectId", "includeArchived"], StringComparer.Ordinal);
        if (export) { allowed.Add("format"); allowed.Add("expectedDigest"); }
        Guard.Require(http.Request.Query.All(x => allowed.Contains(x.Key) && x.Value.Count == 1),
            "unsupported_query", "Only the documented report filters are supported, once each.");
        Guard.Require(DateOnly.TryParseExact(http.Request.Query["from"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            && DateOnly.TryParseExact(http.Request.Query["to"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "activity_range", "Supply from and to as calendar dates (yyyy-MM-dd).");
        var to = DateOnly.ParseExact(http.Request.Query["to"].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Guid? projectId = null;
        if (http.Request.Query.ContainsKey("projectId"))
        {
            Guard.Require(Guid.TryParseExact(http.Request.Query["projectId"], "D", out var id) && id != Guid.Empty,
                "activity_project", "The project filter must be a complete project identifier.");
            projectId = id;
        }
        var archive = http.Request.Query["includeArchived"].ToString();
        Guard.Require(archive is "" or "true" or "false", "activity_archive", "includeArchived must be true or false.");
        var filter = new ActivityFilter(from, to, projectId, archive == "true");
        var previousFrom = BusinessActivity.PreviousFrom(filter);
        var ct = http.RequestAborted;
        Reader(scope);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({scope.TenantId.ToString()}, 0))", ct);
        await commands.Authorize("projects", false, ct);
        await commands.Authorize("tasks", false, ct);
        Reader(scope); // Recheck changes made while waiting for the tenant lock.
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == scope.TenantId, ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone);
        var reportZone = zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana
            : throw new DomainException("activity_timezone", "The configured time zone cannot be used for a calendar report.");
        if (projectId.HasValue) _ = await store.Get<Project>(projectId.Value, ct);
        const string sql = """
            SELECT "Kind", "Id", "Revision", "Archived", "Data"->>'projectId' AS "ProjectReference",
                COALESCE("Data"->>'name', "Data"->>'title') AS "Title", "Data"->>'status' AS "CurrentStatus",
                timezone(@zone, "CreatedAt")::date AS "CreatedOn", "Data"->>'dueDate' AS "DueDateText"
            FROM "Entities"
            WHERE "TenantId"=@tenant AND "Kind" IN ('projects','tasks') AND (@archived OR NOT "Archived")
                AND (@project='' OR ("Kind"='projects' AND "Id"::text=@project) OR ("Kind"='tasks' AND "Data"->>'projectId'=@project))
                AND (timezone(@zone, "CreatedAt")::date BETWEEN @from AND @to
                    OR ("Kind"='tasks' AND "Data"->>'dueDate' BETWEEN @fromText AND @toText))
            ORDER BY "Kind", "Id" LIMIT @limit
            """;
        var rows = await db.Database.SqlQueryRaw<SourceRow>(sql, new NpgsqlParameter("zone", reportZone),
            new NpgsqlParameter("tenant", scope.TenantId), new NpgsqlParameter("archived", filter.IncludeArchived),
            new NpgsqlParameter("project", projectId?.ToString() ?? ""), new NpgsqlParameter("from", previousFrom), new NpgsqlParameter("to", to),
            new NpgsqlParameter("fromText", previousFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), new NpgsqlParameter("toText", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new NpgsqlParameter("limit", BusinessActivity.MaximumSources + 1)).ToListAsync(ct);
        Guard.Require(rows.Count <= BusinessActivity.MaximumSources, "activity_budget", "More than 5,000 sources match. Narrow the period or select a project; no partial totals are returned.");
        var sources = rows.Select(x => new BusinessActivitySource(x.Kind, x.Id, x.Revision, x.Kind == "projects" ? x.Id : Guid.Parse(x.ProjectReference!),
            x.Title, x.CurrentStatus, x.Archived, x.CreatedOn, x.DueDateText is null ? null : DateOnly.ParseExact(x.DueDateText, "yyyy-MM-dd", CultureInfo.InvariantCulture))).ToArray();
        var result = BusinessActivity.Create(scope.TenantId, reportZone, DateTimeOffset.UtcNow, filter, sources);
        await transaction.CommitAsync(ct);
        http.Response.Headers.CacheControl = "no-store, private";
        http.Response.Headers.Pragma = "no-cache";
        return result;
    }

    private static void Reader(TenantScope scope) => Guard.Require(scope.Role is BusinessRole.ManagingDirector or BusinessRole.Office or BusinessRole.Accounting or BusinessRole.SiteManager,
        "forbidden", "Business-wide reports require an office, accounting, site-management or director role.");

    private sealed class SourceRow
    {
        public string Kind { get; set; } = "";
        public Guid Id { get; set; }
        public long Revision { get; set; }
        public bool Archived { get; set; }
        public string? ProjectReference { get; set; }
        public string Title { get; set; } = "";
        public string CurrentStatus { get; set; } = "";
        public DateOnly CreatedOn { get; set; }
        public string? DueDateText { get; set; }
    }
}
