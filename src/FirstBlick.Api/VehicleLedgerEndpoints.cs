using System.Data;
using System.Globalization;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FirstBlick.Api;

public sealed record VehicleLedgerVehicleView(Guid Id, Guid TenantId, long Revision, string Name, string InventoryNumber,
    EquipmentKind Kind, string Registration, bool Archived, bool Available);
public sealed record VehicleTripItem(VehicleTrip Entry, decimal DistanceKm, VehicleLedgerReversal? Reversal);
public sealed record VehicleCostItem(VehicleCost Entry, VehicleLedgerReversal? Reversal);
public sealed record VehicleTripChange(VehicleLedgerVehicleView Vehicle, VehicleTripItem Trip);
public sealed record VehicleCostChange(VehicleLedgerVehicleView Vehicle, VehicleCostItem Cost);
public sealed record VehicleReverseRequest(string SourceDigest, string Reason);
public sealed record VehicleLedgerPage<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize);
public sealed record VehicleLedgerSummary(long TripCount, long ReversedTripCount, decimal DistanceKm, long CostCount,
    long ReversedCostCount, decimal CostAmount, decimal? CostPerKm);
public sealed record VehicleLedgerView(VehicleLedgerVehicleView Vehicle, DateOnly BusinessDate, string TimeZone, string Month,
    VehicleLedgerSummary Summary, VehicleLedgerPage<VehicleTripItem> Trips, VehicleLedgerPage<VehicleCostItem> Costs);

/// <summary>Operational journey/cost evidence; deliberately independent of accounting and invoice allocations.</summary>
public static class VehicleLedgerEndpoints
{
    private const int PageSize = 20;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/vehicle-ledger/vehicles", Vehicles).WithTags("vehicle-ledger");
        api.MapGet("/equipment/{id:guid}/vehicle-ledger", Ledger).WithTags("vehicle-ledger");
        api.MapPost("/equipment/{id:guid}/vehicle-trips", (Guid id, VehicleTripDetails body, [AsParameters] BusinessCommandServices c) =>
            c.Commands.Execute(c.Http, "vehicle-trips", "vehicle.trip_record", body, async ct =>
            {
                var vehicle = await Current(c, id, true, ct);
                var driver = await c.Store.Get<Employee>(body.DriverEmployeeId, ct);
                var project = body.ProjectId is { } projectId ? await c.Store.Get<Project>(projectId, ct) : null;
                var trip = VehicleLedgerWorkflow.RecordTrip(vehicle, driver, project, body, c.Scope.UserId, DateTimeOffset.UtcNow);
                await CheckHistory(c, trip, ct);
                await c.Store.Save(trip, "vehicle.trip_record", true, ct);
                await c.Store.Save(vehicle, "vehicle.ledger_advance", false, ct);
                return new VehicleTripChange(View(vehicle), new(trip, trip.OdometerEndKm - trip.OdometerStartKm, null));
            }, async ct => { _ = await Current(c, id, false, ct, true); })).WithTags("vehicle-ledger");

        api.MapPost("/equipment/{id:guid}/vehicle-costs", (Guid id, VehicleCostDetails body, [AsParameters] BusinessCommandServices c) =>
            c.Commands.Execute(c.Http, "vehicle-costs", "vehicle.cost_record", body, async ct =>
            {
                var vehicle = await Current(c, id, true, ct);
                var project = body.ProjectId is { } projectId ? await c.Store.Get<Project>(projectId, ct) : null;
                var (_, _, businessDate) = await Clock(c, ct);
                var cost = VehicleLedgerWorkflow.RecordCost(vehicle, project, body, businessDate, c.Scope.UserId, DateTimeOffset.UtcNow);
                await c.Store.Save(cost, "vehicle.cost_record", true, ct);
                await c.Store.Save(vehicle, "vehicle.ledger_advance", false, ct);
                return new VehicleCostChange(View(vehicle), new(cost, null));
            }, async ct => { _ = await Current(c, id, false, ct, true); })).WithTags("vehicle-ledger");

        api.MapPost("/equipment/{id:guid}/vehicle-trips/{entryId:guid}/reverse", (Guid id, Guid entryId, VehicleReverseRequest body,
            [AsParameters] BusinessCommandServices c) => Reverse<VehicleTrip>(c, id, entryId, body)).WithTags("vehicle-ledger");
        api.MapPost("/equipment/{id:guid}/vehicle-costs/{entryId:guid}/reverse", (Guid id, Guid entryId, VehicleReverseRequest body,
            [AsParameters] BusinessCommandServices c) => Reverse<VehicleCost>(c, id, entryId, body)).WithTags("vehicle-ledger");
    }

    private static Task<IResult> Reverse<T>(BusinessCommandServices c, Guid vehicleId, Guid entryId, VehicleReverseRequest body) where T : VehicleLedgerEntry
    {
        var kind = BusinessRegistry.KindOf<T>();
        return c.Commands.Execute(c.Http, kind, "vehicle.entry_reverse", body, async ct =>
        {
            var vehicle = await Current(c, vehicleId, true, ct);
            var source = await Source<T>(c, vehicleId, entryId, body.SourceDigest, ct);
            var links = c.Db.Links.Where(x => x.SourceKind == "vehicle-ledger-reversals" && x.TargetKind == kind && x.TargetId == source.Id);
            Guard.Require(!await links.AnyAsync(ct), "vehicle_already_reversed", "This vehicle entry already has a retained reversal.");
            var reversal = VehicleLedgerWorkflow.Reverse(source, body.Reason, c.Scope.UserId, DateTimeOffset.UtcNow);
            await c.Store.Save(reversal, "vehicle.entry_reverse", true, ct);
            await c.Store.Save(vehicle, "vehicle.ledger_advance", false, ct);
            return source is VehicleTrip trip
                ? (object)new VehicleTripChange(View(vehicle), new(trip, trip.OdometerEndKm - trip.OdometerStartKm, reversal))
                : new VehicleCostChange(View(vehicle), new((VehicleCost)(VehicleLedgerEntry)source, reversal));
        }, async ct =>
        {
            _ = await Current(c, vehicleId, false, ct, true);
            _ = await Source<T>(c, vehicleId, entryId, body.SourceDigest, ct);
        });
    }

    private static async Task<IResult> Vehicles([AsParameters] BusinessCommandServices c, CancellationToken ct, string search = "", int page = 1)
    {
        await using var transaction = await c.Db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await c.Commands.Authorize("vehicle-trips", false, ct); ValidatePage(page);
        Guard.Text(search, nameof(search), 200, false); search = (search ?? "").Trim().ToLowerInvariant();
        const string predicate = "\"TenantId\"=@tenant AND \"Kind\"='equipment' AND NOT \"Archived\" AND \"Data\"->>'kind'='Vehicle' AND (@search='' OR position(@search in \"SearchText\")>0)";
        NpgsqlParameter[] Parameters() => [new("tenant", c.Scope.TenantId), new("search", search)];
#pragma warning disable EF1002
        var total = await c.Db.Database.SqlQueryRaw<long>($"SELECT count(*) AS \"Value\" FROM \"Entities\" WHERE {predicate}", Parameters()).SingleAsync(ct);
        var rows = await c.Db.Database.SqlQueryRaw<string>($"SELECT \"Data\"::text AS \"Value\" FROM \"Entities\" WHERE {predicate} ORDER BY \"Data\"->>'name', \"Id\" LIMIT 25 OFFSET @offset",
            Parameters().Concat([new NpgsqlParameter("offset", (page - 1) * 25)]).ToArray()).ToListAsync(ct);
#pragma warning restore EF1002
        var vehicles = rows.Select(AppJson.Read<Equipment>).ToArray(); foreach (var vehicle in vehicles) vehicle.Validate();
        var (_, timeZone, businessDate) = await Clock(c, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { items = vehicles.Select(View), total, page, pageSize = 25, businessDate, timeZone });
    }

    private static async Task<IResult> Ledger(Guid id, string month, [AsParameters] BusinessCommandServices c, CancellationToken ct,
        int tripPage = 1, int costPage = 1)
    {
        ValidatePage(tripPage); ValidatePage(costPage);
        Guard.Require(month is { Length: 7 } && DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "vehicle_month", "Choose a month in YYYY-MM format.");
        var first = DateOnly.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Guard.Require(first.Year is >= 1900 and <= 9998, "vehicle_month", "Choose a month from 1900 through 9998.");
        var next = first.AddMonths(1);
        await using var transaction = await c.Db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await c.Commands.Authorize("vehicle-trips", false, ct); await c.Commands.Authorize("vehicle-costs", false, ct);
        var vehicle = await Current(c, id, false, ct);
        var (zone, timeZone, businessDate) = await Clock(c, ct);
        var tripItems = new List<VehicleTripItem>(); var costItems = new List<VehicleCostItem>();
        long trips = 0, costs = 0, activeTrips = 0, activeCosts = 0, reversedTrips = 0, reversedCosts = 0;
        decimal distance = 0, amount = 0;
        // A one-day UTC margin contains every supported TimeZoneInfo offset. Convert
        // each candidate in C# to preserve the tenant's actual zone, including DST and
        // Windows zone identifiers; do not infer local months from UTC timestamps.
        var from = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
        var until = new DateTimeOffset(next.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(1);
        await foreach (var row in TripRows(c, id, from, until).WithCancellation(ct))
        {
            var trip = AppJson.Read<VehicleTrip>(row.EntryJson); trip.Validate();
            var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(trip.Start, zone).DateTime);
            if (local < first || local >= next) continue;
            var reversal = ReadReversal(row, trip);
            var kilometres = trip.OdometerEndKm - trip.OdometerStartKm;
            if (reversal is null) { activeTrips++; distance += kilometres; } else reversedTrips++;
            if (trips >= (tripPage - 1L) * PageSize && trips < tripPage * (long)PageSize) tripItems.Add(new(trip, kilometres, reversal));
            trips++;
        }
        await foreach (var row in CostRows(c, id, first, next).WithCancellation(ct))
        {
            var cost = AppJson.Read<VehicleCost>(row.EntryJson); cost.Validate();
            var reversal = ReadReversal(row, cost);
            if (reversal is null) { activeCosts++; amount += cost.Amount; } else reversedCosts++;
            if (costs >= (costPage - 1L) * PageSize && costs < costPage * (long)PageSize) costItems.Add(new(cost, reversal));
            costs++;
        }
        var summary = new VehicleLedgerSummary(activeTrips, reversedTrips, distance, activeCosts, reversedCosts, amount,
            distance == 0 ? null : decimal.Round(amount / distance, 4, MidpointRounding.AwayFromZero));
        await transaction.CommitAsync(ct);
        c.Http.Response.Headers.CacheControl = "private, no-store";
        c.Http.Response.Headers.ETag = $"\"{vehicle.Revision}\"";
        return Results.Ok(new VehicleLedgerView(View(vehicle), businessDate, timeZone, month, summary,
            new(tripItems, trips, tripPage, PageSize), new(costItems, costs, costPage, PageSize)));
    }

    private static IAsyncEnumerable<LedgerRow> TripRows(BusinessCommandServices c, Guid id, DateTimeOffset from, DateTimeOffset until) =>
        c.Db.Database.SqlQueryRaw<LedgerRow>("""
            SELECT original."Data"::text AS "EntryJson", reversal."Data"::text AS "ReversalJson"
            FROM "Entities" original
            LEFT JOIN "Entities" reversal ON reversal."TenantId"=original."TenantId" AND reversal."Kind"='vehicle-ledger-reversals'
              AND reversal."Data"->>'sourceKind'='vehicle-trips' AND reversal."Data"->>'sourceId'=original."Id"::text
            WHERE original."TenantId"=@tenant AND original."Kind"='vehicle-trips' AND original."Data"->>'vehicleId'=@vehicle
              AND (original."Data"->>'start')::timestamptz >= @from AND (original."Data"->>'start')::timestamptz < @until
            ORDER BY (original."Data"->>'start')::timestamptz DESC, original."CreatedAt" DESC, original."Id"
            """, new NpgsqlParameter("tenant", c.Scope.TenantId), new NpgsqlParameter("vehicle", id.ToString("D")),
            new NpgsqlParameter("from", from), new NpgsqlParameter("until", until)).AsAsyncEnumerable();

    private static IAsyncEnumerable<LedgerRow> CostRows(BusinessCommandServices c, Guid id, DateOnly from, DateOnly until) =>
        c.Db.Database.SqlQueryRaw<LedgerRow>("""
            SELECT original."Data"::text AS "EntryJson", reversal."Data"::text AS "ReversalJson"
            FROM "Entities" original
            LEFT JOIN "Entities" reversal ON reversal."TenantId"=original."TenantId" AND reversal."Kind"='vehicle-ledger-reversals'
              AND reversal."Data"->>'sourceKind'='vehicle-costs' AND reversal."Data"->>'sourceId'=original."Id"::text
            WHERE original."TenantId"=@tenant AND original."Kind"='vehicle-costs' AND original."Data"->>'vehicleId'=@vehicle
              AND original."Data"->>'spentOn' >= @from AND original."Data"->>'spentOn' < @until
            ORDER BY original."Data"->>'spentOn' DESC, original."CreatedAt" DESC, original."Id"
            """, new NpgsqlParameter("tenant", c.Scope.TenantId), new NpgsqlParameter("vehicle", id.ToString("D")),
            new NpgsqlParameter("from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new NpgsqlParameter("until", until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).AsAsyncEnumerable();

    private static async Task CheckHistory(BusinessCommandServices c, VehicleTrip candidate, CancellationToken ct)
    {
        // Validate the complete active history for the vehicle OR driver. A bounded
        // screen page or the chosen month must never be used as a correctness limit.
        var rows = c.Db.Database.SqlQueryRaw<string>("""
            SELECT original."Data"::text AS "Value" FROM "Entities" original
            WHERE original."TenantId"=@tenant AND original."Kind"='vehicle-trips'
              AND (original."Data"->>'vehicleId'=@vehicle OR original."Data"->>'driverEmployeeId'=@driver)
              AND NOT EXISTS (SELECT 1 FROM "Entities" reversal WHERE reversal."TenantId"=original."TenantId"
                AND reversal."Kind"='vehicle-ledger-reversals' AND reversal."Data"->>'sourceKind'='vehicle-trips'
                AND reversal."Data"->>'sourceId'=original."Id"::text)
            """, new NpgsqlParameter("tenant", c.Scope.TenantId), new NpgsqlParameter("vehicle", candidate.VehicleId.ToString("D")),
            new NpgsqlParameter("driver", candidate.DriverEmployeeId.ToString("D"))).AsAsyncEnumerable();
        await foreach (var row in rows.WithCancellation(ct)) VehicleLedgerWorkflow.EnsureCompatible(candidate, AppJson.Read<VehicleTrip>(row));
    }

    private static VehicleLedgerReversal? ReadReversal(LedgerRow row, VehicleLedgerEntry source)
    {
        if (row.ReversalJson is null) return null;
        var reversal = AppJson.Read<VehicleLedgerReversal>(row.ReversalJson); VehicleLedgerWorkflow.ValidateReversal(reversal, source); return reversal;
    }

    private static async Task<Equipment> Current(BusinessCommandServices c, Guid id, bool match, CancellationToken ct, bool requireActive = false)
    {
        var vehicle = await c.Store.Get<Equipment>(id, ct); vehicle.Validate();
        Guard.Require(vehicle.Kind == EquipmentKind.Vehicle, "vehicle_required", "This equipment is not a vehicle.");
        Guard.Require(!(match || requireActive) || !vehicle.Archived, "not_found", "The vehicle record is archived.");
        if (match) CommandExecutor.Match(c.Http, vehicle.Revision);
        return vehicle;
    }

    private static async Task<T> Source<T>(BusinessCommandServices c, Guid vehicleId, Guid sourceId, string digest, CancellationToken ct) where T : VehicleLedgerEntry
    {
        var entry = await c.Store.Get<T>(sourceId, ct); entry.Validate();
        Guard.Require(entry.VehicleId == vehicleId && entry.Digest == digest, "vehicle_source_mismatch", "The selected original and digest do not belong to this vehicle.");
        return entry;
    }

    private static async Task<(TimeZoneInfo Zone, string TimeZone, DateOnly Date)> Clock(BusinessCommandServices c, CancellationToken ct)
    {
        var id = await c.Db.Tenants.Where(x => x.Id == c.Scope.TenantId).Select(x => x.TimeZone).SingleAsync(ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
        var browserId = TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : id;
        return (zone, browserId, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime));
    }

    private static void ValidatePage(int page) => Guard.Require(page is >= 1 and <= 100_000, "vehicle_page", "Choose a page from 1 through 100000.");
    private static VehicleLedgerVehicleView View(Equipment e) => new(e.Id, e.TenantId, e.Revision, e.Name, e.InventoryNumber, e.Kind, e.Registration ?? "", e.Archived, e.Available);
    private sealed class LedgerRow { public string EntryJson { get; set; } = ""; public string? ReversalJson { get; set; } }
}
