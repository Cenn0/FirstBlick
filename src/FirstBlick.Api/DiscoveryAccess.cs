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

    public async Task<Guid[]?> Visible(string kind, CancellationToken ct)
    {
        if (visible.TryGetValue(kind, out var cached)) return cached;
        if (!AccessRules.CanRead(scope.Role, kind)) return visible[kind] = [];
        // Policy reads select their actual owner/project scope and consume the same scoped source budget
        // as direct lists, details and commands. Unrelated tenant history does not consume that budget.
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
