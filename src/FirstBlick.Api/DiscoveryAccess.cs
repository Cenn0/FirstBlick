using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FirstBlick.Api;

/// <summary>Request-local adapter to the same record policy used by individual resource reads.</summary>
internal sealed class DiscoveryAccess(FirstBlickDbContext db, TenantScope scope, EntityStore store)
{
    private readonly Dictionary<string, Guid[]?> visible = new(StringComparer.Ordinal);
    private readonly HashSet<string> policySources = new(StringComparer.Ordinal);

    public async Task<Guid[]?> Visible(string kind, CancellationToken ct)
    {
        if (visible.TryGetValue(kind, out var cached)) return cached;
        if (!AccessRules.CanRead(scope.Role, kind)) return visible[kind] = [];
        // Discovery additionally bounds the combined dependency budget before invoking the policy.
        // EntityStore independently bounds each actual read in a single-statement snapshot.
        var dependencies = new List<string>();
        if (kind == "notification-preferences") dependencies.Add(kind);
        else if (scope.Role is BusinessRole.Worker or BusinessRole.Trainer)
        {
            dependencies.Add("employees");
            if (kind is "training-records" or "time-entries" or "diary-entries" or "form-submissions") dependencies.Add(kind);
            else if (kind != "employees")
            {
                dependencies.Add("schedule-assignments");
                if (kind == "buildings") dependencies.Add("projects");
                if (kind is "tasks" or "defects") dependencies.Add(kind);
            }
        }
        if (dependencies.Any(x => !policySources.Contains(x)))
        {
            policySources.UnionWith(dependencies);
            var sql = new DiscoverySql(); var tenant = sql.Add(scope.TenantId); var kinds = sql.Add(policySources.ToArray());
            var budget = (await sql.Read<DiscoveryBudget>(db, $"""
                SELECT count(*)::bigint AS "Records",COALESCE(sum(octet_length("Data"::text)),0)::bigint AS "Bytes"
                FROM "Entities" WHERE "TenantId"={tenant} AND NOT "Archived" AND "Kind"=ANY({kinds})
                """, ct)).Single();
            Guard.Require(budget.Records <= 10_000 && budget.Bytes <= 12 * 1024 * 1024,
                "discovery_policy_budget", "The current record-policy inputs exceed 10,000 records or 12 MiB. This request cannot safely return a partial authorization view.");
        }
        return visible[kind] = await RecordPolicy.VisibleIds(kind, scope, store, ct);
    }

    public async Task<string> KindFilter(DiscoverySql sql, string kindColumn, string idColumn, IEnumerable<string> kinds, CancellationToken ct)
    {
        var predicates = new List<string>();
        foreach (var kind in kinds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!BusinessRegistry.Entities.ContainsKey(kind) || !AccessRules.CanRead(scope.Role, kind)) continue;
            var ids = await Visible(kind, ct);
            if (ids is { Length: 0 }) continue;
            var condition = kindColumn + "=" + sql.Add(kind);
            if (ids is not null) condition += " AND " + idColumn + "=ANY(" + sql.Add(ids) + ")";
            predicates.Add("(" + condition + ")");
        }
        return predicates.Count == 0 ? "FALSE" : "(" + string.Join(" OR ", predicates) + ")";
    }

    public async Task<string> DocumentsFilter(DiscoverySql sql, CancellationToken ct)
    {
        if (!AccessRules.CanRead(scope.Role, "documents")) return "FALSE";
        var sourceKinds = await db.Documents.AsNoTracking().Where(x => x.SourceKind != null).Select(x => x.SourceKind!).Distinct().ToListAsync(ct);
        var sources = await KindFilter(sql, "d.\"SourceKind\"", "d.\"SourceId\"", sourceKinds, ct);
        var predicate = "((d.\"SourceKind\" IS NULL AND d.\"SourceId\" IS NULL) OR " + sources + ")";
        if (scope.Role == BusinessRole.Worker)
        {
            var projects = await Visible("projects", ct);
            predicate += " AND d.\"ProjectId\"=ANY(" + sql.Add(projects ?? []) + ")";
        }
        return "(" + predicate + ")";
    }
}

/// <summary>Only fixed code fragments build SQL; identifiers never come from an HTTP request.</summary>
internal sealed class DiscoverySql
{
    private readonly List<(string Name, object Value)> parameters = [];
    public string Add(object value)
    {
        var name = "d" + parameters.Count; parameters.Add((name, value)); return "@" + name;
    }
    public Task<List<T>> Read<T>(FirstBlickDbContext db, string statement, CancellationToken ct)
    {
#pragma warning disable EF1002, EF1003 // SQL is composed exclusively from fixed internal fragments; every value is a fresh Npgsql parameter.
        return db.Database.SqlQueryRaw<T>(statement, parameters.Select(x => new NpgsqlParameter(x.Name, x.Value)).ToArray()).ToListAsync(ct);
#pragma warning restore EF1002, EF1003
    }
}

internal sealed class DiscoveryBudget
{
    public long Records { get; set; }
    public long Bytes { get; set; }
}
