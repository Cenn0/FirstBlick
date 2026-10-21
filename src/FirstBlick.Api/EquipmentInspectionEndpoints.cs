using System.Data;
using System.Globalization;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FirstBlick.Api;

public sealed record EquipmentInspectionPlanRequest(EquipmentInspectionKind Kind, DateOnly ScheduledOn, string Title, string Notes);
public sealed record EquipmentInspectionCompleteRequest(Guid InspectionId, long InspectionRevision, DateOnly PerformedOn,
    EquipmentInspectionResult Result, string Inspector, string EvidenceReference, string Notes, DateOnly? NextInspectionOn, bool MakeAvailable);
public sealed record EquipmentInspectionCancelRequest(Guid InspectionId, long InspectionRevision, string Reason);

// These views deliberately emit nullable/default fields: legacy stored Equipment JSON remains byte-compatible,
// while the dedicated workplace has an explicit and stable response contract.
public sealed record EquipmentInspectionEquipmentView(Guid Id, Guid TenantId, long Revision, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, bool Archived, string Name, string InventoryNumber, EquipmentKind Kind, string Registration,
    string Location, DateOnly? NextInspectionOn, bool Available, Guid? PendingInspectionId, Guid? LastInspectionId,
    DateOnly? LastInspectionOn, bool InspectionBlocked);
public sealed record EquipmentInspectionSummary(Guid Id, long Revision, string Name, string InventoryNumber, EquipmentKind Kind,
    string Registration, string Location, DateOnly? NextInspectionOn, bool Available, bool InspectionBlocked,
    EquipmentInspectionDueStatus DueStatus, Guid? PendingInspectionId, Guid? LastInspectionId);
public sealed record EquipmentInspectionChange(EquipmentInspectionEquipmentView Equipment, EquipmentInspection Inspection, long AffectedAssignmentCount);

public static class EquipmentInspectionEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/equipment/inspection-worklist", Worklist).WithTags("equipment-inspections");
        api.MapGet("/equipment/{id:guid}/inspection-history", History).WithTags("equipment-inspections");
        api.MapGet("/equipment-inspections/{id:guid}", async (Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await c.Commands.Authorize("equipment-inspections", false, ct);
            var inspection = await c.Current<EquipmentInspection>(id, ct, false); inspection.Validate();
            c.Http.Response.Headers.ETag = $"\"{inspection.Revision}\"";
            return Results.Ok(inspection);
        }).Produces<EquipmentInspection>();

        api.MapPost("/equipment/{id:guid}/inspection-plan", (Guid id, EquipmentInspectionPlanRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("equipment-inspections", "equipment.inspection_plan", body, async ct =>
            {
                var equipment = await Current(c, id, ct);
                var inspection = EquipmentInspectionWorkflow.Plan(equipment, body.Kind, body.ScheduledOn, body.Title, body.Notes,
                    c.Scope.UserId, DateTimeOffset.UtcNow);
                await c.Store.Save(inspection, "equipment.inspection_planned", true, ct);
                await c.Store.Save(equipment, "equipment.inspection_plan", false, ct);
                return new EquipmentInspectionChange(View(equipment), inspection, await AffectedAssignments(c, equipment, ct));
            })).Produces<EquipmentInspectionChange>();

        api.MapPost("/equipment/{id:guid}/inspection-complete", (Guid id, EquipmentInspectionCompleteRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("equipment-inspections", "equipment.inspection_complete", body, async ct =>
            {
                var equipment = await Current(c, id, ct);
                var inspection = await Pending(c, equipment, body.InspectionId, body.InspectionRevision, ct);
                var (_, businessDate) = await BusinessClock(c, ct);
                EquipmentInspectionWorkflow.Complete(equipment, inspection, body.PerformedOn, body.Result, body.Inspector,
                    body.EvidenceReference, body.Notes, body.NextInspectionOn, body.MakeAvailable, businessDate,
                    c.Scope.UserId, DateTimeOffset.UtcNow);
                await c.Store.Save(inspection, "equipment.inspection_completed", false, ct);
                await c.Store.Save(equipment, "equipment.inspection_complete", false, ct);
                return new EquipmentInspectionChange(View(equipment), inspection, await AffectedAssignments(c, equipment, ct));
            })).Produces<EquipmentInspectionChange>();

        api.MapPost("/equipment/{id:guid}/inspection-cancel", (Guid id, EquipmentInspectionCancelRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("equipment-inspections", "equipment.inspection_cancel", body, async ct =>
            {
                var equipment = await Current(c, id, ct);
                var inspection = await Pending(c, equipment, body.InspectionId, body.InspectionRevision, ct);
                EquipmentInspectionWorkflow.Cancel(equipment, inspection, body.Reason, c.Scope.UserId, DateTimeOffset.UtcNow);
                await c.Store.Save(inspection, "equipment.inspection_cancelled", false, ct);
                await c.Store.Save(equipment, "equipment.inspection_cancel", false, ct);
                return new EquipmentInspectionChange(View(equipment), inspection, await AffectedAssignments(c, equipment, ct));
            })).Produces<EquipmentInspectionChange>();
    }

    private static async Task<IResult> Worklist([AsParameters] BusinessCommandServices c, CancellationToken ct,
        string status = "all", string search = "", int page = 1)
    {
        await c.Commands.Authorize("equipment-inspections", false, ct);
        Guard.Require(status is "all" or "due" or "blocked" or "planned", "invalid_inspection_filter", "Choose all, due, blocked or planned equipment.");
        Guard.Text(search, nameof(search), 200, false); search = (search ?? "").Trim().ToLowerInvariant();
        page = Math.Clamp(page, 1, 100_000); const int pageSize = 25;
        var (zone, date) = await BusinessClock(c, ct);
        const string predicate = """
            "TenantId"=@tenant AND "Kind"='equipment' AND NOT "Archived"
            AND (@search='' OR position(@search in "SearchText")>0)
            AND (@status='all'
              OR (@status='due' AND "Data"->>'nextInspectionOn'<=@today)
              OR (@status='blocked' AND COALESCE("Data"->>'inspectionBlocked','false')='true')
              OR (@status='planned' AND "Data"->>'pendingInspectionId' IS NOT NULL))
            """;
        NpgsqlParameter[] Parameters() => [new("tenant", c.Scope.TenantId), new("search", search), new("status", status), new("today", date.ToString("yyyy-MM-dd"))];
#pragma warning disable EF1002
        var total = await c.Db.Database.SqlQueryRaw<long>($"SELECT count(*) AS \"Value\" FROM \"Entities\" WHERE {predicate}", Parameters()).SingleAsync(ct);
        var parameters = Parameters().Concat([new NpgsqlParameter("offset", (page - 1) * pageSize)]).ToArray();
        var data = await c.Db.Database.SqlQueryRaw<string>($"""
            SELECT "Data"::text AS "Value" FROM "Entities" WHERE {predicate}
            ORDER BY (COALESCE("Data"->>'inspectionBlocked','false')='true') DESC,
              "Data"->>'nextInspectionOn' ASC NULLS LAST, "Data"->>'name', "Id"
            LIMIT 25 OFFSET @offset
            """, parameters).ToListAsync(ct);
#pragma warning restore EF1002
        var items = data.Select(AppJson.Read<Equipment>).Select(e =>
        {
            e.Validate(); return new EquipmentInspectionSummary(e.Id, e.Revision, e.Name, e.InventoryNumber, e.Kind,
                e.Registration, e.Location, e.NextInspectionOn, e.Available, e.InspectionBlocked,
                EquipmentInspectionWorkflow.DueStatus(e, date), e.PendingInspectionId, e.LastInspectionId);
        }).ToArray();
        return Results.Ok(new { businessDate = date, timeZone = zone.Id, items, total, page, pageSize });
    }

    private static async Task<IResult> History(Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct, int page = 1)
    {
        // The equipment pointers and paginated history must come from the same database snapshot.
        await using var transaction = await c.Db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await c.Commands.Authorize("equipment-inspections", false, ct);
        var equipment = await Current(c, id, ct, false);
        EquipmentInspection? pending = null;
        if (equipment.PendingInspectionId is { } pendingId)
        {
            pending = await c.Store.Get<EquipmentInspection>(pendingId, ct); pending.Validate();
            Guard.Require(pending.EquipmentId == equipment.Id && pending.Status == EquipmentInspectionStatus.Scheduled,
                "inspection_state", "The outstanding inspection does not match the selected equipment.");
        }
        var links = c.Db.Links.Where(x => x.SourceKind == "equipment-inspections" && x.TargetKind == "equipment" && x.TargetId == id);
        var query = c.Db.Entities.AsNoTracking().Where(x => x.Kind == "equipment-inspections" && links.Any(l => l.SourceId == x.Id));
        page = Math.Clamp(page, 1, 100_000); const int pageSize = 20;
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(x => x.Data).ToListAsync(ct);
        var items = rows.Select(AppJson.Read<EquipmentInspection>).ToArray(); foreach (var inspection in items) inspection.Validate();
        var (zone, date) = await BusinessClock(c, ct);
        var affectedAssignmentCount = await AffectedAssignments(c, equipment, ct);
        await transaction.CommitAsync(ct);
        c.Http.Response.Headers.ETag = $"\"{equipment.Revision}\"";
        return Results.Ok(new { equipment = View(equipment), pendingInspection = pending, items, total, page, pageSize,
            businessDate = date, timeZone = zone.Id, affectedAssignmentCount });
    }

    private static async Task<Equipment> Current(BusinessCommandServices c, Guid id, CancellationToken ct, bool match = true)
    {
        var equipment = await c.Current<Equipment>(id, ct, match); equipment.Validate();
        Guard.Require(!equipment.Archived, "not_found", "The equipment is archived.");
        if (equipment.LastInspectionId is { } latestId)
        {
            var latest = await c.Store.Get<EquipmentInspection>(latestId, ct); latest.Validate();
            Guard.Require(latest.EquipmentId == equipment.Id && latest.Status == EquipmentInspectionStatus.Completed
                && latest.Completion!.PerformedOn == equipment.LastInspectionOn
                && equipment.InspectionBlocked == (latest.Completion.Result == EquipmentInspectionResult.Failed)
                && (latest.Completion.Result != EquipmentInspectionResult.Passed || latest.Completion.NextInspectionOn == equipment.NextInspectionOn),
                "inspection_state", "The equipment state does not match its retained inspection evidence.");
        }
        return equipment;
    }

    private static async Task<EquipmentInspection> Pending(BusinessCommandServices c, Equipment equipment, Guid id, long revision, CancellationToken ct)
    {
        Guard.Id(id, nameof(id)); Guard.Require(revision > 0, "revision_required", "Supply the inspection revision.");
        var inspection = await c.Store.Get<EquipmentInspection>(id, ct);
        Guard.Require(inspection.EquipmentId == equipment.Id, "inspection_equipment_mismatch", "The inspection belongs to a different resource.");
        Guard.Require(inspection.Revision == revision, "revision_conflict", "The inspection changed. Reload it before continuing.");
        return inspection;
    }

    private static async Task<(TimeZoneInfo Zone, DateOnly Date)> BusinessClock(BusinessCommandServices c, CancellationToken ct)
    {
        var zoneId = await c.Db.Tenants.Where(x => x.Id == c.Scope.TenantId).Select(x => x.TimeZone).SingleAsync(ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        return (zone, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime));
    }

    private static async Task<long> AffectedAssignments(BusinessCommandServices c, Equipment equipment, CancellationToken ct)
    {
        if (equipment.Available && !equipment.InspectionBlocked && equipment.NextInspectionOn is null) return 0;
        var (zone, _) = await BusinessClock(c, ct);
        var now = DateTimeOffset.UtcNow;
        // PostgreSQL timestamps have microsecond precision, while the retained JSON/assignment
        // contract accepts 100 ns ticks. Stream only candidate end times and apply the same exact
        // exclusive-end rule as dispatch; casting before subtracting would misclassify midnight + 1 tick.
        var ends = c.Db.Database.SqlQueryRaw<string>("""
            SELECT "Data"->>'end' AS "Value" FROM "Entities"
            WHERE "TenantId"=@tenant AND "Kind"='schedule-assignments' AND NOT "Archived"
              AND COALESCE("Data"->>'cancelled','false')='false'
              AND ("Data"->'equipmentIds') ? @equipment
              AND ("Data"->>'end')::timestamptz >= @earliest
            """, new NpgsqlParameter("tenant", c.Scope.TenantId), new NpgsqlParameter("equipment", equipment.Id.ToString("D")),
            new NpgsqlParameter("earliest", now.AddTicks(-10))).AsAsyncEnumerable();
        long count = 0;
        await foreach (var text in ends.WithCancellation(ct))
        {
            var end = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            Guard.Utc(end, "Assignment.End");
            if (end <= now) continue;
            var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end.AddTicks(-1), zone).DateTime);
            if (!equipment.Available || equipment.InspectionBlocked || equipment.NextInspectionOn is { } due && lastDay > due) count++;
        }
        return count;
    }

    private static EquipmentInspectionEquipmentView View(Equipment e) => new(e.Id, e.TenantId, e.Revision, e.CreatedAt, e.UpdatedAt, e.Archived,
        e.Name, e.InventoryNumber, e.Kind, e.Registration, e.Location, e.NextInspectionOn, e.Available,
        e.PendingInspectionId, e.LastInspectionId, e.LastInspectionOn, e.InspectionBlocked);
}
