using System.Data;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record OperationsSignal(string Code, string Severity, long Affected);
public sealed record OperationsQueue(long Pending, long Due, long Leased, long ExpiredLeases, long Failed, long Uncertain, double OldestDueAgeSeconds);
public sealed record OperationsDocuments(long Ready, long Staged, long Stalled);
public sealed record OperationsThresholds(int DueAgeSeconds, int StagedAgeSeconds);
public sealed record OperationsSnapshot(string Schema, DateTimeOffset CapturedAt, OperationsQueue Queue,
    OperationsDocuments Documents, OperationsThresholds Thresholds, IReadOnlyList<OperationsSignal> Signals);

public static class OperationsEndpoints
{
    public const int DueAgeWarningSeconds = 300;
    public const int StagedAgeWarningSeconds = 900;

    public static void Map(RouteGroupBuilder api)
        => api.MapGet("/operations/snapshot", Snapshot).Produces<OperationsSnapshot>().WithName("OperationsSnapshot").WithTags("operations");

    private static async Task<IResult> Snapshot(HttpContext http, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands)
    {
        Guard.Require(http.Request.Query.Count == 0, "unsupported_query", "The operational snapshot uses the current business and accepts no query parameters.");
        Director(scope);
        var ct = http.RequestAborted;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({scope.TenantId.ToString()}, 0))", ct);
        // Membership and the restore barrier may have changed while this read was waiting for a command.
        await commands.Authorize("jobs", false, ct);
        Director(scope);
        var row = await db.Database.SqlQuery<SnapshotRow>($"""
            SELECT statement_timestamp() AS "CapturedAt", jobs.*, documents.*
            FROM (
                SELECT count(*) FILTER (WHERE "State"='Pending') AS "Pending",
                    count(*) FILTER (WHERE "State"='Pending' AND "AvailableAt"<=statement_timestamp()) AS "Due",
                    count(*) FILTER (WHERE "State"='Leased') AS "Leased",
                    count(*) FILTER (WHERE "State"='Leased' AND ("LeaseUntil" IS NULL OR "LeaseUntil"<=statement_timestamp())) AS "ExpiredLeases",
                    count(*) FILTER (WHERE "State"='Failed') AS "Failed", count(*) FILTER (WHERE "State"='Uncertain') AS "Uncertain",
                    COALESCE(EXTRACT(EPOCH FROM (statement_timestamp()-min("AvailableAt") FILTER (WHERE "State"='Pending' AND "AvailableAt"<=statement_timestamp())))::double precision,0) AS "OldestDueAgeSeconds"
                FROM "Jobs" WHERE "TenantId"={scope.TenantId}
            ) jobs CROSS JOIN (
                SELECT count(*) FILTER (WHERE "State"='Ready') AS "ReadyDocuments", count(*) FILTER (WHERE "State"='Staged') AS "StagedDocuments",
                    count(*) FILTER (WHERE "State"='Staged' AND "CreatedAt"<=statement_timestamp()-({StagedAgeWarningSeconds} * interval '1 second')) AS "StalledDocuments"
                FROM "Documents" WHERE "TenantId"={scope.TenantId}
            ) documents
            """).SingleAsync(ct);
        var signals = new List<OperationsSignal>();
        if (row.Uncertain > 0) signals.Add(new("external_outcome_uncertain", "action", row.Uncertain));
        if (row.Failed > 0) signals.Add(new("failed_jobs", "warning", row.Failed));
        if (row.ExpiredLeases > 0) signals.Add(new("expired_leases", "warning", row.ExpiredLeases));
        if (row.OldestDueAgeSeconds >= DueAgeWarningSeconds) signals.Add(new("queue_delay", "warning", row.Due));
        if (row.StalledDocuments > 0) signals.Add(new("stalled_uploads", "warning", row.StalledDocuments));
        var result = new OperationsSnapshot("firstblick.operations/1.0.0", row.CapturedAt,
            new(row.Pending, row.Due, row.Leased, row.ExpiredLeases, row.Failed, row.Uncertain, row.OldestDueAgeSeconds),
            new(row.ReadyDocuments, row.StagedDocuments, row.StalledDocuments),
            new(DueAgeWarningSeconds, StagedAgeWarningSeconds), signals);
        await transaction.CommitAsync(ct);
        http.Response.Headers.CacheControl = "no-store, private";
        http.Response.Headers.Pragma = "no-cache";
        return Results.Ok(result);
    }

    private static void Director(TenantScope scope)
        => Guard.Require(scope.Role == BusinessRole.ManagingDirector, "forbidden", "The operational snapshot is available to managing directors only.");

    private sealed class SnapshotRow
    {
        public DateTimeOffset CapturedAt { get; set; }
        public long Pending { get; set; }
        public long Due { get; set; }
        public long Leased { get; set; }
        public long ExpiredLeases { get; set; }
        public long Failed { get; set; }
        public long Uncertain { get; set; }
        public double OldestDueAgeSeconds { get; set; }
        public long ReadyDocuments { get; set; }
        public long StagedDocuments { get; set; }
        public long StalledDocuments { get; set; }
    }
}
