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
        string errorPrefix = "source", string[]? uniqueKeys = null)
        where T : TenantEntity
    {
        var kind = BusinessRegistry.KindOf<T>();
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
                  AND (@all OR EXISTS (
                    SELECT 1 FROM "Links" l
                    JOIN unnest(@targetKinds, @targetIds) AS t(kind,id)
                      ON l."TargetKind"=t.kind AND l."TargetId"=t.id
                    WHERE l."TenantId"=e."TenantId" AND l."SourceKind"=e."Kind" AND l."SourceId"=e."Id"))
            )
            SELECT "TenantId", "Kind", "Id", "Revision", "Archived", "Records", "Bytes",
                   CASE WHEN "Records"<=@records AND "Bytes"<=@bytes THEN "Data"::text ELSE NULL END AS "Data"
            FROM sources ORDER BY "Id" LIMIT @limit
            """, new NpgsqlParameter("tenant", tenantId), new NpgsqlParameter("kind", kind),
            new NpgsqlParameter("archived", includeArchived), new NpgsqlParameter("all", targets is null),
            new NpgsqlParameter("targetKinds", references.Select(x => x.Kind).ToArray()),
            new NpgsqlParameter("targetIds", references.Select(x => x.Id).ToArray()),
            new NpgsqlParameter("allKeys", uniqueKeys is null), new NpgsqlParameter("keys", uniqueKeys ?? []),
            new NpgsqlParameter("records", RecordLimit), new NpgsqlParameter("bytes", ByteLimit),
            new NpgsqlParameter("limit", RecordLimit + 1)).ToListAsync(ct);
        Guard.Require(rows.Count == 0 || (rows[0].Records <= RecordLimit && rows[0].Bytes <= ByteLimit),
            errorPrefix + "_budget", "The source set exceeds 10,000 records or 12 MiB. No partial result was used.");
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
