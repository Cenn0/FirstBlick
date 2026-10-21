using System.Globalization;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FirstBlick.Api;

/// <summary>
/// Calendar projections query the tenant-scoped PostgreSQL JSON ledger directly, rather than
/// loading the whole history or silently truncating earlier assignments.
/// </summary>
public static class PlanningWeekEndpoints
{
    public sealed record AssignmentItem(Guid Id, long Revision, Guid EmployeeId, string EmployeeName,
        Guid ProjectId, string ProjectName, string Title, DateTimeOffset Start, DateTimeOffset End,
        IReadOnlyList<Guid> EquipmentIds, IReadOnlyList<DateOnly> Days);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/planning/week", async (string weekStart, CommandExecutor commands,
            TenantScope scope, FirstBlickDbContext db, EntityStore store, CancellationToken ct) =>
        {
            await commands.Authorize("schedule-assignments", false, ct);
            Guard.Require(DateOnly.TryParseExact(weekStart, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var monday),
                "invalid_planning_week", "Specify an ISO date (yyyy-MM-dd) for a Monday.");
            var timeZoneId = await db.Tenants.Where(x => x.Id == scope.TenantId)
                .Select(x => x.TimeZone).SingleAsync(ct);
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var days = PlanningWeek.Boundaries(monday, zone);
            var visible = await RecordPolicy.VisibleIds("schedule-assignments", scope, store, ct);
            NpgsqlParameter[] Params() =>
            [
                new("tenant", scope.TenantId), new("start", days[0].StartUtc),
                new("end", days[^1].EndUtc), new("all", visible is null),
                new("visible", visible ?? Array.Empty<Guid>())
            ];
            const string sql = """
                SELECT "Data"::text AS "Value" FROM "Entities"
                WHERE "TenantId"=@tenant AND "Kind"='schedule-assignments' AND NOT "Archived"
                  AND COALESCE("Data"->>'cancelled','false')='false'
                  AND ("Data"->>'start')::timestamptz < @end
                  AND ("Data"->>'end')::timestamptz > @start
                  AND (@all OR "Id" = ANY(@visible))
                ORDER BY ("Data"->>'start')::timestamptz, "Id"
                LIMIT 1001
                """;
#pragma warning disable EF1002
            var data = await db.Database.SqlQueryRaw<string>(sql, Params()).ToArrayAsync(ct);
#pragma warning restore EF1002
            Guard.Require(data.Length <= PlanningWeek.MaximumAssignments,
                "planning_week_budget", "The week contains more than 1,000 assignments. Narrow the schedule scope before producing a reliable view.");
            var assignments = data.Select(AppJson.Read<ScheduleAssignment>).ToArray();
            foreach (var assignment in assignments)
            {
                Guard.Require(assignment.TenantId == scope.TenantId && !assignment.Archived && !assignment.Cancelled
                    && PlanningWeek.DaysCovered(assignment.Start, assignment.End, days).Count > 0,
                    "planning_week_inconsistent", "The selected assignment disagrees with the business calendar scope.");
                assignment.Validate();
            }

            // Only the already-authorized assignment rows define which project and staff labels may be returned.
            var employeeIds = assignments.Select(x => x.EmployeeId).Distinct().ToArray();
            var projectIds = assignments.Select(x => x.ProjectId).Distinct().ToArray();
            var employees = await db.Entities.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                    && x.Kind == "employees" && employeeIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Data, ct);
            var projects = await db.Entities.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                    && x.Kind == "projects" && projectIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Data, ct);
            var names = employees.ToDictionary(x => x.Key, x => AppJson.Read<Employee>(x.Value).Name);
            var projectNames = projects.ToDictionary(x => x.Key, x => AppJson.Read<Project>(x.Value).Name);
            var result = assignments.Select(a => new AssignmentItem(
                a.Id, a.Revision, a.EmployeeId, names.GetValueOrDefault(a.EmployeeId, "Nicht verfügbar"),
                a.ProjectId, projectNames.GetValueOrDefault(a.ProjectId, "Nicht verfügbar"),
                a.Title, a.Start, a.End, a.EquipmentIds,
                PlanningWeek.DaysCovered(a.Start, a.End, days))).ToArray();
            return Results.Ok(new { weekStart = monday, timeZone = zone.Id, days, assignments = result,
                count = result.Length, maxAssignments = PlanningWeek.MaximumAssignments,
                note = "Business-local day boundaries; no route optimisation, travel-time validation or attendance approval." });
        }).WithTags("planning");
    }
}
