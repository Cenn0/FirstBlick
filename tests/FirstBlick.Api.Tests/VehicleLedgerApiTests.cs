using System.Globalization;
using System.Net;
using System.Text.Json;
using FirstBlick.Api;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class VehicleLedgerApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Actual_http_records_immutable_trip_and_cost_originals_with_exact_monthly_amounts()
    {
        var s = await Setup(); var body = TripBody(s) with { OdometerStartKm = 100.125m, OdometerEndKm = 120.250m };
        var trip = await RecordTrip(s, body); var cost = await RecordCost(s, CostBody() with { ProjectId = s.Project.Id });
        Assert.Equal(1, trip.Trip.Entry.Revision); Assert.Equal(20.125m, trip.Trip.DistanceKm);
        Assert.Equal(s.Owner.UserId, trip.Trip.Entry.RecordedBy); Assert.Equal(s.Driver.Name, trip.Trip.Entry.DriverName);
        Assert.Equal(s.Project.Name, trip.Trip.Entry.ProjectTitle); Assert.Equal(trip.Vehicle.Revision + 1, cost.Vehicle.Revision);
        Assert.Equal(s.Vehicle.Id, trip.Trip.Entry.VehicleSnapshot!.Id);
        var month = body.Start.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        using var response = await s.Client.GetAsync(LedgerPath(s.Vehicle.Id, month));
        var text = await response.Content.ReadAsStringAsync(); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ledger = AppJson.Read<VehicleLedgerView>(text);
        Assert.Equal(1, ledger.Summary.TripCount); Assert.Equal(1, ledger.Summary.CostCount); Assert.Equal(20.125m, ledger.Summary.DistanceKm);
        Assert.Equal(123.45m, ledger.Summary.CostAmount); Assert.Equal(decimal.Round(123.45m / 20.125m, 4, MidpointRounding.AwayFromZero), ledger.Summary.CostPerKm);
        Assert.Null(Assert.Single(ledger.Trips.Items).Reversal); Assert.Null(Assert.Single(ledger.Costs.Items).Reversal);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("summary").GetProperty("distanceKm").ValueKind);
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("summary").GetProperty("costAmount").ValueKind);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal($"\"{cost.Vehicle.Revision}\"", response.Headers.ETag!.Tag);
        var storedTrip = await fixture.Read<VehicleTrip>(s.Owner.TenantId, trip.Trip.Entry.Id); storedTrip.Validate();
        Assert.Equal(trip.Trip.Entry.Digest, storedTrip.CalculateDigest());
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(0, await db.Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == s.Owner.TenantId && (x.Kind == "supplier-invoices" || x.Kind == "supplier-cost-allocations" || x.Kind == "cost-forecasts")));
        });
    }

    [PostgresApiFact]
    public async Task Same_request_replays_once_and_stale_or_competing_vehicle_revisions_cannot_record_twice()
    {
        var s = await Setup(); var body = TripBody(s); var key = Guid.NewGuid().ToString("N");
        var first = await Ok<VehicleTripChange>(Send(s.Client, HttpMethod.Post, TripsPath(s.Vehicle.Id), body, s.Vehicle.Revision, key));
        using var replay = await Send(s.Client, HttpMethod.Post, TripsPath(s.Vehicle.Id), body, s.Vehicle.Revision, key);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        var replayed = AppJson.Read<VehicleTripChange>(await replay.Content.ReadAsStringAsync()); Assert.Equal(first.Trip.Entry.Id, replayed.Trip.Entry.Id);
        await Error(Send(s.Client, HttpMethod.Post, TripsPath(s.Vehicle.Id), body with { Purpose = "Changed request" }, s.Vehicle.Revision, key), "idempotency_conflict", HttpStatusCode.Conflict);
        await Error(Send(s.Client, HttpMethod.Post, CostsPath(s.Vehicle.Id), CostBody(), s.Vehicle.Revision), "revision_conflict", HttpStatusCode.Conflict);
        var results = await Task.WhenAll(Enumerable.Range(1, 2).Select(i => Send(s.Client, HttpMethod.Post, CostsPath(s.Vehicle.Id), CostBody() with { Reference = "Concurrent-" + i }, first.Vehicle.Revision)));
        Assert.Single(results, x => x.IsSuccessStatusCode); Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        foreach (var result in results) result.Dispose();
        var ledger = await ReadLedger(s, body.Start);
        Assert.Equal(1, ledger.Summary.TripCount); Assert.Equal(1, ledger.Summary.CostCount);
    }

    [PostgresApiFact]
    public async Task Full_history_odometer_constraints_include_preceding_and_later_months()
    {
        var s = await Setup(); var anchor = MonthStart(DateTimeOffset.UtcNow.AddMonths(-3));
        await RecordTrip(s, TripBody(s, anchor.AddDays(-1), 100, 110));
        await RecordTrip(s, TripBody(s, anchor.AddMonths(1), 140, 150));
        var current = await fixture.Read<Equipment>(s.Owner.TenantId, s.Vehicle.Id);
        await Error(Send(s.Client, HttpMethod.Post, TripsPath(s.Vehicle.Id), TripBody(s, anchor.AddDays(10), 109, 130), current.Revision), "vehicle_odometer_conflict");
        await Error(Send(s.Client, HttpMethod.Post, TripsPath(s.Vehicle.Id), TripBody(s, anchor.AddDays(10), 115, 141), current.Revision), "vehicle_odometer_conflict");
        var valid = await RecordTrip(s, TripBody(s, anchor.AddDays(10), 115, 130));
        Assert.Equal(15, valid.Trip.DistanceKm);
        var month = await ReadLedger(s, anchor); Assert.Single(month.Trips.Items); Assert.Equal(15, month.Summary.DistanceKm);
    }

    [PostgresApiFact]
    public async Task Driver_overlap_is_rejected_across_vehicles_even_when_the_current_vehicle_has_no_history()
    {
        var s = await Setup(); var first = await RecordTrip(s, TripBody(s));
        var secondVehicle = await fixture.Save(new Equipment { TenantId = s.Owner.TenantId, Name = "Second van", Kind = EquipmentKind.Vehicle,
            InventoryNumber = "SECOND-" + Guid.NewGuid().ToString("N") }, s.Owner.UserId);
        await Error(Send(s.Client, HttpMethod.Post, TripsPath(secondVehicle.Id), TripBody(s) with { OdometerStartKm = 500, OdometerEndKm = 510 }, secondVehicle.Revision), "vehicle_driver_overlap");
        var body = TripBody(s, first.Trip.Entry.End, 500, 510);
        await Ok<VehicleTripChange>(Send(s.Client, HttpMethod.Post, TripsPath(secondVehicle.Id), body, secondVehicle.Revision));
    }

    [PostgresApiFact]
    public async Task Separate_reversals_preserve_original_bytes_and_months_and_allow_corrected_entries()
    {
        var s = await Setup(); var body = TripBody(s); var trip = await RecordTrip(s, body); var cost = await RecordCost(s, CostBody());
        var tripJson = await Stored(s.Owner.TenantId, "vehicle-trips", trip.Trip.Entry.Id);
        var costJson = await Stored(s.Owner.TenantId, "vehicle-costs", cost.Cost.Entry.Id);
        await Error(Send(s.Client, HttpMethod.Post, ReversePath(s.Vehicle.Id, "vehicle-trips", trip.Trip.Entry.Id), new VehicleReverseRequest(trip.Trip.Entry.Digest, " "), cost.Vehicle.Revision), "required");
        await Error(Send(s.Client, HttpMethod.Post, ReversePath(s.Vehicle.Id, "vehicle-trips", trip.Trip.Entry.Id), new VehicleReverseRequest(new string('0', 64), "Wrong source"), cost.Vehicle.Revision), "vehicle_source_mismatch");
        var reversedTrip = await Ok<VehicleTripChange>(Send(s.Client, HttpMethod.Post, ReversePath(s.Vehicle.Id, "vehicle-trips", trip.Trip.Entry.Id),
            new VehicleReverseRequest(trip.Trip.Entry.Digest, "Incorrect distance"), cost.Vehicle.Revision));
        var reverseKey = Guid.NewGuid().ToString("N"); var reverseCostBody = new VehicleReverseRequest(cost.Cost.Entry.Digest, "Duplicate cost");
        var reversedCost = await Ok<VehicleCostChange>(Send(s.Client, HttpMethod.Post, ReversePath(s.Vehicle.Id, "vehicle-costs", cost.Cost.Entry.Id), reverseCostBody, reversedTrip.Vehicle.Revision, reverseKey));
        using var replay = await Send(s.Client, HttpMethod.Post, ReversePath(s.Vehicle.Id, "vehicle-costs", cost.Cost.Entry.Id), reverseCostBody, reversedTrip.Vehicle.Revision, reverseKey);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        await Error(Send(s.Client, HttpMethod.Post, ReversePath(s.Vehicle.Id, "vehicle-costs", cost.Cost.Entry.Id), reverseCostBody, reversedCost.Vehicle.Revision), "vehicle_already_reversed");
        Assert.Equal(tripJson, await Stored(s.Owner.TenantId, "vehicle-trips", trip.Trip.Entry.Id));
        Assert.Equal(costJson, await Stored(s.Owner.TenantId, "vehicle-costs", cost.Cost.Entry.Id));
        var ledger = await ReadLedger(s, body.Start);
        Assert.Equal(0, ledger.Summary.TripCount); Assert.Equal(0, ledger.Summary.CostCount); Assert.Equal(0, ledger.Summary.CostAmount);
        Assert.Equal(1, ledger.Summary.ReversedTripCount); Assert.Equal(1, ledger.Summary.ReversedCostCount); Assert.Null(ledger.Summary.CostPerKm);
        Assert.NotNull(Assert.Single(ledger.Trips.Items).Reversal); Assert.NotNull(Assert.Single(ledger.Costs.Items).Reversal);
        await RecordTrip(s, body with { OdometerEndKm = 125 });
        ledger = await ReadLedger(s, body.Start); Assert.Equal(2, ledger.Trips.Total); Assert.Equal(25, ledger.Summary.DistanceKm);
    }

    [PostgresApiFact]
    public async Task Retained_labels_and_reversals_still_work_after_historical_driver_and_project_are_archived()
    {
        var s = await Setup(); var trip = await RecordTrip(s, TripBody(s));
        await ChangeMaster<Employee>(s.Owner.TenantId, s.Driver.Id, x => { x.Name = "Changed former driver"; x.Active = false; x.Archived = true; });
        await ChangeMaster<Project>(s.Owner.TenantId, s.Project.Id, x => { x.Name = "Changed former project"; x.Archived = true; });
        var ledger = await ReadLedger(s, trip.Trip.Entry.Start); var entry = Assert.Single(ledger.Trips.Items).Entry;
        Assert.Equal("Synthetic driver", entry.DriverName); Assert.Equal("Synthetic project", entry.ProjectTitle);
        var reversal = await Ok<VehicleTripChange>(Send(s.Client, HttpMethod.Post, ReversePath(s.Vehicle.Id, "vehicle-trips", entry.Id),
            new VehicleReverseRequest(entry.Digest, "Historical correction"), ledger.Vehicle.Revision));
        Assert.NotNull(reversal.Trip.Reversal);
        await Error(Send(s.Client, HttpMethod.Post, TripsPath(s.Vehicle.Id), TripBody(s) with { ProjectId = null }, reversal.Vehicle.Revision), "vehicle_driver");
    }

    [PostgresApiFact]
    public async Task All_ledger_reads_and_mutations_are_tenant_and_role_scoped_and_replays_use_current_rights()
    {
        var s = await Setup(); var body = TripBody(s); var key = Guid.NewGuid().ToString("N");
        var posted = await Ok<VehicleTripChange>(Send(s.Client, HttpMethod.Post, TripsPath(s.Vehicle.Id), body, s.Vehicle.Revision, key));
        var other = await Setup();
        await Error(other.Client.GetAsync(LedgerPath(s.Vehicle.Id, body.Start.ToString("yyyy-MM", CultureInfo.InvariantCulture))), "not_found", HttpStatusCode.NotFound);
        await Error(Send(other.Client, HttpMethod.Post, TripsPath(s.Vehicle.Id), TripBody(other), posted.Vehicle.Revision), "not_found", HttpStatusCode.NotFound);
        await Error(Send(other.Client, HttpMethod.Post, ReversePath(other.Vehicle.Id, "vehicle-trips", posted.Trip.Entry.Id), new VehicleReverseRequest(posted.Trip.Entry.Digest, "Wrong tenant"), other.Vehicle.Revision), "not_found", HttpStatusCode.NotFound);
        var accountant = await fixture.CreateUser(s.Owner.TenantId, BusinessRole.Accounting); var accounting = await fixture.Login(accountant);
        await Ok<VehicleLedgerView>(accounting.GetAsync(LedgerPath(s.Vehicle.Id, body.Start.ToString("yyyy-MM", CultureInfo.InvariantCulture))));
        await Error(Send(accounting, HttpMethod.Post, CostsPath(s.Vehicle.Id), CostBody(), posted.Vehicle.Revision), "forbidden", HttpStatusCode.Forbidden);
        var worker = await fixture.Login(await fixture.CreateUser(s.Owner.TenantId, BusinessRole.Worker));
        await Error(worker.GetAsync(LedgerPath(s.Vehicle.Id, body.Start.ToString("yyyy-MM", CultureInfo.InvariantCulture))), "forbidden", HttpStatusCode.Forbidden);
        await ChangeRole(s.Owner, BusinessRole.Accounting);
        await Error(Send(s.Client, HttpMethod.Post, TripsPath(s.Vehicle.Id), body, s.Vehicle.Revision, key), "forbidden", HttpStatusCode.Forbidden);
        await ChangeRole(s.Owner, BusinessRole.ManagingDirector);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(x => x.Id == s.Owner.TenantId);
            tenant.Modules = tenant.Modules.Where(x => x != "purchasing").ToArray(); await db.SaveChangesAsync();
        });
        await Error(Send(s.Client, HttpMethod.Post, TripsPath(s.Vehicle.Id), body, s.Vehicle.Revision, key), "module_disabled", HttpStatusCode.Forbidden);
        await ReadLedger(s, body.Start);
    }

    [PostgresApiFact]
    public async Task Dedicated_commands_prevent_generic_rewriting_or_archive_and_keep_vehicle_kind()
    {
        var s = await Setup(); var posted = await RecordTrip(s, TripBody(s)); var current = await fixture.Read<Equipment>(s.Owner.TenantId, s.Vehicle.Id);
        current.Kind = EquipmentKind.Machine;
        using var edit = await Send(s.Client, HttpMethod.Put, $"/api/v1/equipment/{current.Id}", current, current.Revision);
        Assert.False(edit.IsSuccessStatusCode);
        await Error(Send(s.Client, HttpMethod.Delete, $"/api/v1/equipment/{current.Id}", revision: current.Revision), "record_in_use");
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            var path = "/api/v1/vehicle-trips" + (method == HttpMethod.Post ? "" : "/" + posted.Trip.Entry.Id);
            using var bypass = await Send(s.Client, method, path, method == HttpMethod.Delete ? null : posted.Trip.Entry, posted.Trip.Entry.Revision);
            Assert.False(bypass.IsSuccessStatusCode);
        }
        Assert.Equal(EquipmentKind.Vehicle, (await fixture.Read<Equipment>(s.Owner.TenantId, current.Id)).Kind);
        Assert.Equal(1, (await fixture.Read<VehicleTrip>(s.Owner.TenantId, posted.Trip.Entry.Id)).Revision);
    }

    [PostgresApiFact]
    public async Task Monthly_pagination_is_bounded_but_summary_counts_every_original_with_exact_decimal_arithmetic()
    {
        var s = await Setup(); var anchor = MonthStart(DateTimeOffset.UtcNow.AddMonths(-2));
        for (var i = 0; i < 25; i++)
        {
            var trip = VehicleLedgerWorkflow.RecordTrip(s.Vehicle, s.Driver, null, TripBody(s, anchor.AddDays(i), i, i + 1) with { ProjectId = null }, s.Owner.UserId, DateTimeOffset.UtcNow);
            await fixture.Save(trip, s.Owner.UserId);
            var cost = VehicleLedgerWorkflow.RecordCost(s.Vehicle, null, CostBody() with { Amount = 0.01m, SpentOn = DateOnly.FromDateTime(anchor.AddDays(i).Date), Reference = "ONE-CENT-" + i }, DateOnly.FromDateTime(DateTime.UtcNow), s.Owner.UserId, DateTimeOffset.UtcNow);
            await fixture.Save(cost, s.Owner.UserId);
        }
        var month = anchor.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var first = await Ok<VehicleLedgerView>(s.Client.GetAsync(LedgerPath(s.Vehicle.Id, month)));
        var second = await Ok<VehicleLedgerView>(s.Client.GetAsync(LedgerPath(s.Vehicle.Id, month) + "&tripPage=2&costPage=2"));
        Assert.Equal(20, first.Trips.Items.Count); Assert.Equal(5, second.Trips.Items.Count);
        Assert.Equal(20, first.Costs.Items.Count); Assert.Equal(5, second.Costs.Items.Count);
        Assert.Equal(25, first.Trips.Total); Assert.Equal(25, first.Costs.Total); Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(25, first.Summary.DistanceKm); Assert.Equal(0.25m, first.Summary.CostAmount); Assert.Equal(0.01m, first.Summary.CostPerKm);
        Assert.Empty(first.Trips.Items.Select(x => x.Entry.Id).Intersect(second.Trips.Items.Select(x => x.Entry.Id)));
        Assert.Empty(first.Costs.Items.Select(x => x.Entry.Id).Intersect(second.Costs.Items.Select(x => x.Entry.Id)));
        await Error(s.Client.GetAsync(LedgerPath(s.Vehicle.Id, month) + "&tripPage=0"), "vehicle_page");
        await Error(s.Client.GetAsync(LedgerPath(s.Vehicle.Id, "2026-13")), "vehicle_month");
    }

    [PostgresApiFact]
    public async Task Business_timezone_and_dst_determine_start_month_and_windows_ids_are_normalized_for_browsers()
    {
        var s = await Setup();
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(x => x.Id == s.Owner.TenantId);
            tenant.TimeZone = "W. Europe Standard Time"; await db.SaveChangesAsync();
        });
        await RecordTrip(s, TripBody(s, new(2025, 3, 30, 0, 30, 0, TimeSpan.Zero), 100, 110)); // Spans the spring DST change.
        await RecordTrip(s, TripBody(s, new(2025, 3, 31, 21, 30, 0, TimeSpan.Zero), 110, 120)); // Starts in March, ends in April locally.
        await RecordTrip(s, TripBody(s, new(2025, 3, 31, 22, 30, 0, TimeSpan.Zero), 120, 130)); // UTC March, local April.
        var march = await Ok<VehicleLedgerView>(s.Client.GetAsync(LedgerPath(s.Vehicle.Id, "2025-03")));
        var april = await Ok<VehicleLedgerView>(s.Client.GetAsync(LedgerPath(s.Vehicle.Id, "2025-04")));
        Assert.Equal("Europe/Berlin", march.TimeZone); Assert.Equal(2, march.Summary.TripCount); Assert.Equal(1, april.Summary.TripCount);
        Assert.Equal(20, march.Summary.DistanceKm); Assert.Equal(10, april.Summary.DistanceKm);
        Assert.Equal(new DateTimeOffset(2025, 3, 31, 22, 30, 0, TimeSpan.Zero), Assert.Single(april.Trips.Items).Entry.Start);
    }

    [PostgresApiFact]
    public async Task Invalid_payloads_missing_revision_and_nonvehicle_resources_never_commit_and_legacy_null_registration_is_supported()
    {
        var s = await Setup();
        await Error(Send(s.Client, HttpMethod.Post, TripsPath(s.Vehicle.Id), TripBody(s)), "revision_required", HttpStatusCode.PreconditionRequired);
        await Error(Send(s.Client, HttpMethod.Post, TripsPath(s.Vehicle.Id), TripBody(s) with { End = DateTimeOffset.UtcNow.AddDays(1) }, s.Vehicle.Revision), "vehicle_trip_window");
        await Error(Send(s.Client, HttpMethod.Post, TripsPath(s.Vehicle.Id), TripBody(s) with { OdometerEndKm = 120.0001m }, s.Vehicle.Revision), "vehicle_odometer_precision");
        await Error(Send(s.Client, HttpMethod.Post, CostsPath(s.Vehicle.Id), CostBody() with { Amount = 1.001m }, s.Vehicle.Revision), "invalid_money_precision");
        await Error(Send(s.Client, HttpMethod.Post, CostsPath(s.Vehicle.Id), CostBody() with { SpentOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2) }, s.Vehicle.Revision), "vehicle_cost_date");
        var machine = await fixture.Save(new Equipment { TenantId = s.Owner.TenantId, Name = "Machine", InventoryNumber = "MACHINE", Kind = EquipmentKind.Machine }, s.Owner.UserId);
        await Error(Send(s.Client, HttpMethod.Post, TripsPath(machine.Id), TripBody(s), machine.Revision), "vehicle_required");
        var worklist = await Ok<JsonElement>(s.Client.GetAsync("/api/v1/vehicle-ledger/vehicles?search=Synthetic&page=1"));
        Assert.Equal(25, worklist.GetProperty("pageSize").GetInt32());
        Assert.Equal(s.Vehicle.Id, Assert.Single(worklist.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        var unmatched = await Ok<JsonElement>(s.Client.GetAsync("/api/v1/vehicle-ledger/vehicles?search=does-not-exist"));
        Assert.Equal(0, unmatched.GetProperty("total").GetInt64());
        await Error(s.Client.GetAsync("/api/v1/vehicle-ledger/vehicles?page=0"), "vehicle_page");
        var empty = await ReadLedger(s, TripBody(s).Start); Assert.Empty(empty.Trips.Items); Assert.Empty(empty.Costs.Items);
        await ChangeMaster<Equipment>(s.Owner.TenantId, s.Vehicle.Id, x => x.Registration = null!);
        var posted = await RecordTrip(s, TripBody(s)); Assert.Equal("", posted.Vehicle.Registration); Assert.Equal("", posted.Trip.Entry.VehicleSnapshot!.Registration);
    }

    private sealed record Context(ApiTestUser Owner, HttpClient Client, Equipment Vehicle, Employee Driver, Project Project);
    private async Task<Context> Setup()
    {
        var tenant = await fixture.CreateTenant(); var owner = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(owner);
        var created = DateTimeOffset.UtcNow.AddYears(-3);
        var vehicle = await fixture.Save(new Equipment { TenantId = tenant.Id, Name = "Synthetic van", InventoryNumber = Guid.NewGuid().ToString("N"),
            Kind = EquipmentKind.Vehicle, Registration = "TEST-1", CreatedAt = created, UpdatedAt = created }, owner.UserId);
        var driver = await fixture.Save(new Employee { TenantId = tenant.Id, Name = "Synthetic driver", CreatedAt = created, UpdatedAt = created }, owner.UserId);
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Synthetic customer", CreatedAt = created, UpdatedAt = created }, owner.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id, Name = "Synthetic building", Address = "Synthetic address",
            CreatedAt = created, UpdatedAt = created }, owner.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id, BuildingId = building.Id, Name = "Synthetic project",
            CreatedAt = created, UpdatedAt = created }, owner.UserId);
        return new(owner, client, vehicle, driver, project);
    }

    private async Task<VehicleTripChange> RecordTrip(Context s, VehicleTripDetails body)
    {
        var current = await fixture.Read<Equipment>(s.Owner.TenantId, s.Vehicle.Id);
        return await Ok<VehicleTripChange>(Send(s.Client, HttpMethod.Post, TripsPath(s.Vehicle.Id), body, current.Revision));
    }
    private async Task<VehicleCostChange> RecordCost(Context s, VehicleCostDetails body)
    {
        var current = await fixture.Read<Equipment>(s.Owner.TenantId, s.Vehicle.Id);
        return await Ok<VehicleCostChange>(Send(s.Client, HttpMethod.Post, CostsPath(s.Vehicle.Id), body, current.Revision));
    }
    private Task<VehicleLedgerView> ReadLedger(Context s, DateTimeOffset at) => Ok<VehicleLedgerView>(s.Client.GetAsync(LedgerPath(s.Vehicle.Id, at.ToString("yyyy-MM", CultureInfo.InvariantCulture))));
    private Task<string> Stored(Guid tenant, string kind, Guid id) => fixture.InScope(async services => await services.GetRequiredService<FirstBlickDbContext>().Entities.IgnoreQueryFilters()
        .Where(x => x.TenantId == tenant && x.Kind == kind && x.Id == id).Select(x => x.Data).SingleAsync());
    private Task ChangeMaster<T>(Guid tenant, Guid id, Action<T> change) where T : TenantEntity => fixture.InScope(async services =>
    {
        var db = services.GetRequiredService<FirstBlickDbContext>(); var row = await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenant && x.Kind == BusinessRegistry.KindOf<T>() && x.Id == id);
        var entity = AppJson.Read<T>(row.Data); change(entity); row.Data = AppJson.Serialize(entity); row.Archived = entity.Archived; await db.SaveChangesAsync();
    });
    private Task ChangeRole(ApiTestUser user, BusinessRole role) => fixture.InScope(async services =>
    {
        var db = services.GetRequiredService<FirstBlickDbContext>(); var membership = await db.Memberships.IgnoreQueryFilters().SingleAsync(x => x.TenantId == user.TenantId && x.UserId == user.UserId);
        membership.Role = role; membership.Revision++; await db.SaveChangesAsync();
    });
    private static VehicleTripDetails TripBody(Context s, DateTimeOffset? at = null, decimal from = 100m, decimal to = 120m)
    {
        var instant = at ?? DateTimeOffset.UtcNow.AddDays(-2); var start = new DateTimeOffset(instant.Ticks - instant.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        return new(s.Driver.Id, s.Project.Id, start, start.AddHours(1), from, to, "Synthetic site work", "Workshop", "Roof site", "Synthetic journey");
    }
    private static VehicleCostDetails CostBody() => new(null, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), VehicleCostCategory.Fuel, 123.45m, "EUR", "SYNTHETIC-RECEIPT", "Synthetic cost");
    private static DateTimeOffset MonthStart(DateTimeOffset at) => new(at.Year, at.Month, 1, 8, 0, 0, TimeSpan.Zero);
    private static string TripsPath(Guid vehicle) => $"/api/v1/equipment/{vehicle}/vehicle-trips";
    private static string CostsPath(Guid vehicle) => $"/api/v1/equipment/{vehicle}/vehicle-costs";
    private static string LedgerPath(Guid vehicle, string month) => $"/api/v1/equipment/{vehicle}/vehicle-ledger?month={month}";
    private static string ReversePath(Guid vehicle, string kind, Guid source) => $"/api/v1/equipment/{vehicle}/{kind}/{source}/reverse";
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {text}"); return AppJson.Read<T>(text);
    }
    private static async Task Error(Task<HttpResponseMessage> pending, string code, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode); Assert.Equal(code, JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
    }
}
