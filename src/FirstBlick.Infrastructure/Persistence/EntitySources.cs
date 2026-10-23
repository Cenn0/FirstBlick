using FirstBlick.Core.Business;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FirstBlick.Infrastructure.Persistence;

/// <summary>Whole source sets with a single-statement snapshot and bounded JSON materialization.</summary>
public static class EntitySources
{
    public const int RecordLimit = 10_000;
    public const int ByteLimit = 12 * 1024 * 1024;

    public static async Task<List<T>> Read<T>(FirstBlickDbContext db, Guid tenantId,
        CancellationToken ct = default, IReadOnlyCollection<EntityReference>? targets = null, bool includeArchived = false,
        string errorPrefix = "source", string[]? uniqueKeys = null, EntitySourceSelection? selection = null)
        where T : TenantEntity
    {
        var kind = BusinessRegistry.KindOf<T>();
        selection?.EnsureKind(kind);
        var selectedIds = selection?.Ids.Distinct().Select(x => x.ToString("D")).ToArray() ?? [];
        var projectIds = selection?.ProjectIds.Distinct().Select(x => x.ToString("D")).ToArray() ?? [];
        Guard.Require(selectedIds.Length <= RecordLimit && projectIds.Length <= RecordLimit,
            "source_selection_budget", "A source selection supports at most 10,000 identifiers per dimension.");
        var references = targets?.Distinct().ToArray() ?? [];
        Guard.Require(references.Length <= 100, "source_targets_budget", "A source read supports at most 100 explicit targets.");
        Guard.Require(uniqueKeys is null || uniqueKeys.Length <= 500, "source_keys_budget", "A source read supports at most 500 explicit keys.");
        if (targets is not null && references.Length == 0) return [];
        if (uniqueKeys is { Length: 0 }) return [];
        // Window aggregates and conditional payload projection run in one PostgreSQL statement.
        // An oversized set returns only metadata, never its JSON bodies or a truncated usable ledger.
        var rows = await db.Database.SqlQueryRaw<EntitySourceRow>("""
            WITH sources AS (
                SELECT e.*, count(*) OVER () AS "Records",
                       sum(octet_length(e."Data"::text)) OVER () AS "Bytes"
                FROM "Entities" e
                WHERE e."TenantId"=@tenant AND e."Kind"=@kind AND (@archived OR NOT e."Archived")
                  AND (@allKeys OR e."UniqueKey"=ANY(@keys))
                  AND CASE @selection
                    WHEN 'All' THEN TRUE
                    WHEN 'OwnEmployee' THEN e."Data"->>'userId'=@user AND e."Data"->>'active'='true'
                    WHEN 'Employees' THEN e."Id"::text=ANY(@ids) OR e."Data"->>'mentorEmployeeId'=ANY(@ids)
                    WHEN 'Training' THEN e."Data"->>'apprenticeEmployeeId'=ANY(@ids) OR e."Data"->>'mentorEmployeeId'=ANY(@ids)
                    WHEN 'EmployeeWork' THEN e."Data"->>'employeeId'=ANY(@ids)
                    WHEN 'Assignments' THEN e."Data"->>'employeeId'=ANY(@ids) AND e."Data"->>'cancelled'='false'
                    WHEN 'ProjectWork' THEN e."Data"->>'projectId'=ANY(@projects)
                    WHEN 'Tasks' THEN e."Data"->>'projectId'=ANY(@projects) OR e."Data"->>'assignedEmployeeId'=ANY(@ids)
                    WHEN 'SubmittedForms' THEN e."Data"->>'submittedBy'=@user
                    WHEN 'UserPreference' THEN e."Data"->>'userId'=@user
                    WHEN 'Ids' THEN e."Id"::text=ANY(@ids)
                    ELSE FALSE END
                  AND (@all OR EXISTS (
                    SELECT 1 FROM "Links" l
                    JOIN unnest(@targetKinds, @targetIds) AS t(kind,id)
                      ON l."TargetKind"=t.kind AND l."TargetId"=t.id
                    WHERE l."TenantId"=e."TenantId" AND l."SourceKind"=e."Kind" AND l."SourceId"=e."Id"))
            )
            SELECT "TenantId", "Kind", "Id", "Revision", "Archived", "Records", "Bytes",
                   CASE WHEN "Records"<=@records AND "Bytes"<=@bytes AND "Records"<=@remainingRecords AND "Bytes"<=@remainingBytes THEN "Data"::text ELSE NULL END AS "Data"
            FROM sources ORDER BY "Id" LIMIT @limit
            """, new NpgsqlParameter("tenant", tenantId), new NpgsqlParameter("kind", kind),
            new NpgsqlParameter("archived", includeArchived), new NpgsqlParameter("all", targets is null),
            new NpgsqlParameter("targetKinds", references.Select(x => x.Kind).ToArray()),
            new NpgsqlParameter("targetIds", references.Select(x => x.Id).ToArray()),
            new NpgsqlParameter("allKeys", uniqueKeys is null), new NpgsqlParameter("keys", uniqueKeys ?? []),
            new NpgsqlParameter("selection", (selection?.Mode ?? EntitySelectionMode.All).ToString()),
            new NpgsqlParameter("user", (selection?.UserId ?? Guid.Empty).ToString("D")),
            new NpgsqlParameter("ids", selectedIds), new NpgsqlParameter("projects", projectIds),
            new NpgsqlParameter("remainingRecords", db.SourceBudget.RemainingRecords),
            new NpgsqlParameter("remainingBytes", db.SourceBudget.RemainingBytes),
            new NpgsqlParameter("records", RecordLimit), new NpgsqlParameter("bytes", ByteLimit),
            new NpgsqlParameter("limit", RecordLimit + 1)).ToListAsync(ct);
        Guard.Require(rows.Count == 0 || (rows[0].Records <= RecordLimit && rows[0].Bytes <= ByteLimit),
            errorPrefix + "_budget", "The source set exceeds 10,000 records or 12 MiB. No partial result was used.");
        db.SourceBudget.Consume(rows.Count, rows.Count == 0 ? 0 : rows[0].Bytes);
        var result = new List<T>(rows.Count);
        foreach (var row in rows)
        {
            var value = AppJson.Read<T>(row.Data!);
            Guard.Require(row.TenantId == tenantId && value.TenantId == tenantId && row.Kind == kind
                && row.Id == value.Id && row.Revision == value.Revision && row.Archived == value.Archived,
                errorPrefix + "_metadata", "The source metadata does not match its stored body.");
            if (targets is not null)
                Guard.Require(value.References().Any(references.Contains), errorPrefix + "_reference", "The source body no longer refers to its retained target.");
            if (uniqueKeys is not null)
                Guard.Require(uniqueKeys.Contains(EntityProjection.UniqueKey(value)), errorPrefix + "_key", "The source body no longer matches its retained key.");
            Guard.Require(selection is null || selection.Matches(value), errorPrefix + "_selection", "The source body does not match its selected policy scope.");
            result.Add(value);
        }
        return result;
    }
}

internal sealed class EntitySourceRow
{
    public Guid TenantId { get; set; }
    public string Kind { get; set; } = "";
    public Guid Id { get; set; }
    public long Revision { get; set; }
    public bool Archived { get; set; }
    public long Records { get; set; }
    public long Bytes { get; set; }
    public string? Data { get; set; }
}
