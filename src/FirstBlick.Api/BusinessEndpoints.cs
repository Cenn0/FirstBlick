using System.Reflection;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FirstBlick.Api;

public static class BusinessEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        foreach (var (kind, type) in BusinessRegistry.Entities)
        {
            if (kind is "collection-cases" or "cost-forecasts" or "supplier-cost-allocations" or "gaeb-exchanges") continue; // Dedicated, authorized commands own these immutable resources.
            typeof(BusinessEndpoints).GetMethod(nameof(MapResource), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type).Invoke(null, [api, kind]);
        }
        CommercialEndpoints.Map(api);
        FieldEndpoints.Map(api);
        api.MapGet("/dashboard", Dashboard);
    }

    private static void MapResource<T>(RouteGroupBuilder api, string kind) where T : TenantEntity
    {
        api.MapGet($"/{kind}", async (HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, CommandExecutor commands, CancellationToken ct) =>
        {
            await commands.Authorize(kind, false, ct);
            var page = int.TryParse(http.Request.Query["page"], out var p) ? Math.Clamp(p, 1, 100_000) : 1;
            var pageSize = int.TryParse(http.Request.Query["pageSize"], out var s) ? Math.Clamp(s, 1, 100) : 30;
            var search = http.Request.Query["search"].ToString().Trim().ToLowerInvariant();
            Guard.Text(search, "Search", 200, false);
            var status = http.Request.Query["status"].ToString();
            var project = http.Request.Query["projectId"].ToString();
            var customer = http.Request.Query["customerId"].ToString();
            var timeZone = await db.Tenants.Where(x => x.Id == scope.TenantId).Select(x => x.TimeZone).SingleAsync(ct);
            var due = http.Request.Query["due"].ToString() == "today" ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime).ToString("yyyy-MM-dd") : "";
            var openOnly = kind == "invoices" && http.Request.Query["openOnly"].ToString() == "true";
            var visible = await RecordPolicy.VisibleIds(kind, scope, store, ct);
            var predicate = "\"TenantId\"=@tenant AND \"Kind\"=@kind AND NOT \"Archived\" AND (@search='' OR position(@search in \"SearchText\")>0) AND (@status='' OR \"Data\"->>'status'=@status) AND (@project='' OR \"Data\"->>'projectId'=@project) AND (@customer='' OR \"Data\"->>'customerId'=@customer) AND (@due='' OR (@kind='tasks' AND \"Data\"->>'dueDate'<=@due AND \"Data\"->>'status' NOT IN ('Done','Cancelled')) OR (@kind='maintenance-contracts' AND \"Data\"->>'nextDueDate'<=@due AND \"Data\"->>'active'='true' AND (\"Data\"->>'endDate' IS NULL OR \"Data\"->>'nextDueDate'<=\"Data\"->>'endDate'))) AND (@all OR \"Id\"=ANY(@visible))";
            if (openOnly) predicate += " AND \"Id\" IN (" + FinanceReadModel.OpenInvoiceIdsSql + ")";
            NpgsqlParameter[] Parameters() => [new("tenant", scope.TenantId), new("kind", kind), new("search", search), new("status", status), new("project", project), new("customer", customer), new("due", due), new("all", visible is null), new("visible", visible ?? Array.Empty<Guid>())];
#pragma warning disable EF1002
            var total = await db.Database.SqlQueryRaw<long>($"SELECT count(*) AS \"Value\" FROM \"Entities\" WHERE {predicate}", Parameters()).SingleAsync(ct);
            var parameters = Parameters().Concat([new NpgsqlParameter("limit", pageSize), new NpgsqlParameter("offset", checked((page - 1) * pageSize))]).ToArray();
            var data = await db.Database.SqlQueryRaw<string>($"SELECT (\"Data\" - 'snapshot' - 'calculation' - 'contract' - 'lines' - 'deliveries' - 'fields' - 'values' || jsonb_build_object('net',COALESCE(\"Data\"->'snapshot'->'net',\"Data\"->'contract'->'net'),'gross',COALESCE(\"Data\"->'snapshot'->'gross',\"Data\"->'contract'->'gross')))::text AS \"Value\" FROM \"Entities\" WHERE {predicate} ORDER BY \"UpdatedAt\" DESC, \"Id\" LIMIT @limit OFFSET @offset", parameters).ToListAsync(ct);
#pragma warning restore EF1002
            return Results.Ok(new { items = data.Select(x => JsonSerializer.Deserialize<JsonElement>(x)), total, page, pageSize });
        }).WithName($"List_{kind}").WithTags(kind);
        api.MapGet($"/{kind}/{{id:guid}}", async (Guid id, HttpContext http, EntityStore store, TenantScope scope, CommandExecutor commands, CancellationToken ct) =>
        {
            await commands.Authorize(kind, false, ct);
            await RecordPolicy.EnsureVisible(kind, id, scope, store, ct);
            var stored = await store.Find(kind, id, ct);
            http.Response.Headers.ETag = $"\"{stored.Revision}\"";
            return Results.Content(stored.Data, "application/json");
        }).Produces<T>().WithName($"Get_{kind}").WithTags(kind);
        api.MapPost($"/{kind}", (T body, HttpContext http, EntityStore store, TenantScope scope, FirstBlickDbContext db, CommandExecutor commands) =>
            commands.Execute(http, kind, "create", body, async ct =>
            {
                EditRules.EnsureGenericCreate(body);
                body.Id = Guid.NewGuid(); body.TenantId = scope.TenantId; body.Revision = 1;
                body.CreatedAt = body.UpdatedAt = DateTimeOffset.UtcNow; body.Archived = false;
                await RecordPolicy.EnsureWrite(body, scope, store, db, ct);
                await GraphRules.Validate(body, store, db, scope, ct);
                await store.Save(body, "create", true, ct);
                return body;
            })).Produces<T>().WithName($"Create_{kind}").WithTags(kind);
        api.MapPut($"/{kind}/{{id:guid}}", (Guid id, T body, HttpContext http, EntityStore store, TenantScope scope, FirstBlickDbContext db, CommandExecutor commands) =>
            commands.Execute(http, kind, "update", body, async ct =>
            {
                var previous = await store.Get<T>(id, ct);
                CommandExecutor.Match(http, previous.Revision);
                await RecordPolicy.EnsureVisible(kind, id, scope, store, ct);
                if (previous is Employee employee && body is Employee changedEmployee && employee.UserId != changedEmployee.UserId)
                    Guard.Require(scope.Role == BusinessRole.ManagingDirector, "forbidden", "Only a managing director can change or remove a personal-account link.");
                EditRules.EnsureGenericUpdate(previous, body);
                body.Id = id; body.TenantId = scope.TenantId; body.Revision = previous.Revision;
                body.CreatedAt = previous.CreatedAt; body.Archived = previous.Archived;
                await RecordPolicy.EnsureWrite(body, scope, store, db, ct);
                await GraphRules.Validate(body, store, db, scope, ct);
                await store.Save(body, "update", false, ct);
                return body;
            })).Produces<T>().WithName($"Update_{kind}").WithTags(kind);
        api.MapDelete($"/{kind}/{{id:guid}}", (Guid id, HttpContext http, EntityStore store, TenantScope scope, FirstBlickDbContext db, CommandExecutor commands) =>
            commands.Execute(http, kind, "archive", new { id }, async ct =>
            {
                var entity = await store.Get<T>(id, ct); CommandExecutor.Match(http, entity.Revision);
                await RecordPolicy.EnsureVisible(kind, id, scope, store, ct);
                EditRules.EnsureArchivable(entity);
                Guard.Require(!await db.Links.AnyAsync(x => x.TargetKind == kind && x.TargetId == id, ct), "record_in_use", "This record is referenced by other work. Retain it in the project history.");
                Guard.Require(!await db.Documents.AnyAsync(x => (x.SourceKind == kind && x.SourceId == id) || (kind == "projects" && x.ProjectId == id), ct), "record_in_use", "Documents preserve this record in the project history.");
                entity.Archived = true; await store.Save(entity, "archive", false, ct); return entity;
            })).WithName($"Archive_{kind}").WithTags(kind);
    }
    private static async Task<IResult> Dashboard(TenantScope scope, FirstBlickDbContext db, EntityStore store, CancellationToken ct)
    {
        var counts = new Dictionary<string, long>();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == scope.TenantId, ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone)).DateTime).ToString("yyyy-MM-dd");
        foreach (var (key, kind) in new[] { ("projects", "projects"), ("quoteDrafts", "quotes"), ("tasksDue", "tasks"), ("openInvoices", "invoices"), ("employees", "employees"), ("maintenanceDue", "maintenance-contracts") })
        {
            if (!AccessRules.CanRead(scope.Role, kind) || !tenant.Modules.Contains(AccessRules.ModuleFor(kind))) continue;
            var visible = await RecordPolicy.VisibleIds(kind, scope, store, ct);
            var clause = key switch
            {
                "quoteDrafts" => "AND \"Data\"->>'status'='Draft'",
                "tasksDue" => "AND \"Data\"->>'dueDate'<=@today AND \"Data\"->>'status' NOT IN ('Done','Cancelled')",
                "maintenanceDue" => "AND \"Data\"->>'nextDueDate'<=@today AND \"Data\"->>'active'='true' AND (\"Data\"->>'endDate' IS NULL OR \"Data\"->>'nextDueDate'<=\"Data\"->>'endDate')",
                "openInvoices" => "AND \"Id\" IN (" + FinanceReadModel.OpenInvoiceIdsSql + ")",
                _ => ""
            };
#pragma warning disable EF1002
            counts[key] = await db.Database.SqlQueryRaw<long>($"SELECT count(*) AS \"Value\" FROM \"Entities\" WHERE \"TenantId\"=@tenant AND \"Kind\"=@kind AND NOT \"Archived\" AND (@all OR \"Id\"=ANY(@visible)) {clause}",
                new NpgsqlParameter("tenant", scope.TenantId), new NpgsqlParameter("kind", kind), new NpgsqlParameter("all", visible is null),
                new NpgsqlParameter("visible", visible ?? Array.Empty<Guid>()), new NpgsqlParameter("today", today)).SingleAsync(ct);
#pragma warning restore EF1002
        }
        return Results.Ok(counts);
    }
}
