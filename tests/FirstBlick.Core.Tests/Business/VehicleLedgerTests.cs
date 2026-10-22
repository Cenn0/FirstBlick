using System.Text.Json;
using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class VehicleLedgerTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Posted_trip_retains_its_original_identities_decimal_values_and_digest_after_master_changes()
    {
        var vehicle = Vehicle(); var driver = Driver(); var project = Project();
        var trip = VehicleLedgerWorkflow.RecordTrip(vehicle, driver, project, Details(driver, project) with
        { OdometerStartKm = 101.125m, OdometerEndKm = 115.875m }, Actor, Now);
        var serialized = JsonSerializer.Serialize(trip);
        vehicle.Name = "Changed vehicle"; vehicle.Registration = "CHANGED"; driver.Name = "Changed person"; project.Name = "Changed project";
        vehicle.Archived = driver.Archived = project.Archived = true;
        trip.Validate();
        Assert.Equal("Service van", trip.VehicleSnapshot!.Name); Assert.Equal("Synthetic driver", trip.DriverName);
        Assert.Equal("Synthetic roof", trip.ProjectTitle); Assert.Equal(14.750m, trip.OdometerEndKm - trip.OdometerStartKm);
        Assert.Equal(serialized, JsonSerializer.Serialize(trip));
        var restored = JsonSerializer.Deserialize<VehicleTrip>(serialized)!; restored.Validate(); Assert.Equal(trip.Digest, restored.Digest);
    }

    [Theory]
    [InlineData("backwards", "vehicle_odometer")]
    [InlineData("zero", "vehicle_odometer")]
    [InlineData("precision", "vehicle_odometer_precision")]
    [InlineData("negative", "out_of_range")]
    [InlineData("too-high", "out_of_range")]
    [InlineData("future", "vehicle_trip_window")]
    [InlineData("duration", "vehicle_trip_window")]
    [InlineData("fractional-time", "vehicle_time_precision")]
    [InlineData("offset", "invalid_utc")]
    [InlineData("empty-purpose", "required")]
    [InlineData("null-notes", "vehicle_fields")]
    public void Invalid_trip_inputs_fail_before_an_original_can_be_posted(string invalid, string code)
    {
        var vehicle = Vehicle(); var driver = Driver(); var body = Details(driver);
        body = invalid switch
        {
            "backwards" => body with { OdometerEndKm = 99 }, "zero" => body with { OdometerEndKm = 100 },
            "precision" => body with { OdometerEndKm = 120.0001m }, "negative" => body with { OdometerStartKm = -1 },
            "too-high" => body with { OdometerEndKm = 10_000_000m }, "future" => body with { End = Now.AddSeconds(1) },
            "duration" => body with { Start = body.End.AddHours(-25) }, "fractional-time" => body with { End = body.End.AddTicks(1) },
            "offset" => body with { Start = body.Start.ToOffset(TimeSpan.FromHours(1)) }, "empty-purpose" => body with { Purpose = " " },
            _ => body with { Notes = null! }
        };
        Assert.Equal(code, Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.RecordTrip(vehicle, driver, null, body, Actor, Now)).Code);
    }

    [Fact]
    public void Recording_requires_a_real_current_vehicle_driver_and_project_in_one_tenant()
    {
        var vehicle = Vehicle(); var driver = Driver(); var body = Details(driver);
        vehicle.Kind = EquipmentKind.Machine;
        Assert.Equal("vehicle_required", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.RecordTrip(vehicle, driver, null, body, Actor, Now)).Code);
        vehicle.Kind = EquipmentKind.Vehicle; driver.Active = false;
        Assert.Equal("vehicle_driver", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.RecordTrip(vehicle, driver, null, body, Actor, Now)).Code);
        driver.Active = true; driver.TenantId = Guid.NewGuid();
        Assert.Equal("vehicle_driver", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.RecordTrip(vehicle, driver, null, body, Actor, Now)).Code);
        driver.TenantId = Tenant; var project = Project();
        Assert.Equal("vehicle_project", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.RecordTrip(vehicle, driver, project, body, Actor, Now)).Code);
        project.Archived = true;
        Assert.Equal("vehicle_project", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.RecordTrip(vehicle, driver, project, Details(driver, project), Actor, Now)).Code);
        project.Archived = false; driver.CreatedAt = driver.UpdatedAt = Now.AddSeconds(1);
        Assert.Equal("vehicle_record_time", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.RecordTrip(vehicle, driver, null, body, Actor, Now)).Code);
    }

    [Fact]
    public void Chronology_checks_both_neighbours_even_when_they_are_in_different_months()
    {
        var vehicle = Vehicle(); var driver = Driver();
        var previous = Trip(vehicle, driver, new(2026, 8, 31, 8, 0, 0, TimeSpan.Zero), 100, 110);
        var next = Trip(vehicle, driver, new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), 140, 150);
        var insertion = Trip(vehicle, driver, new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero), 115, 130);
        VehicleLedgerWorkflow.EnsureCompatible(insertion, previous); VehicleLedgerWorkflow.EnsureCompatible(insertion, next);
        var tooEarly = Trip(vehicle, driver, insertion.Start, 109, 120);
        Assert.Equal("vehicle_odometer_conflict", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.EnsureCompatible(tooEarly, previous)).Code);
        var tooLate = Trip(vehicle, driver, insertion.Start, 120, 141);
        Assert.Equal("vehicle_odometer_conflict", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.EnsureCompatible(tooLate, next)).Code);
        Assert.Equal("vehicle_odometer_conflict", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.EnsureCompatible(next, tooLate)).Code);
    }

    [Fact]
    public void Adjacent_journeys_and_odometer_gaps_are_allowed_but_time_overlap_is_not()
    {
        var vehicle = Vehicle(); var driver = Driver(); var otherDriver = Driver();
        var first = Trip(vehicle, driver, Now.AddDays(-2), 100, 110);
        var adjacent = Trip(vehicle, otherDriver, first.End, 110, 120);
        VehicleLedgerWorkflow.EnsureCompatible(first, adjacent);
        var gap = Trip(vehicle, otherDriver, adjacent.End.AddHours(1), 140, 150);
        VehicleLedgerWorkflow.EnsureCompatible(adjacent, gap);
        var overlap = Trip(vehicle, otherDriver, first.Start.AddMinutes(30), 110, 120);
        Assert.Equal("vehicle_trip_overlap", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.EnsureCompatible(first, overlap)).Code);
    }

    [Fact]
    public void One_driver_cannot_overlap_journeys_in_two_vehicles_but_other_drivers_can()
    {
        var driver = Driver(); var first = Trip(Vehicle(), driver, Now.AddDays(-2), 100, 110);
        var overlap = Trip(Vehicle(), driver, first.Start.AddMinutes(15), 999, 1000);
        Assert.Equal("vehicle_driver_overlap", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.EnsureCompatible(first, overlap)).Code);
        var independent = Trip(Vehicle(), Driver(), first.Start, 2, 3);
        VehicleLedgerWorkflow.EnsureCompatible(first, independent);
    }

    [Fact]
    public void Reversal_retains_the_exact_original_and_permits_a_corrected_replacement()
    {
        var vehicle = Vehicle(); var driver = Driver(); var first = Trip(vehicle, driver, Now.AddDays(-2), 100, 110);
        var original = JsonSerializer.Serialize(first);
        var reversal = VehicleLedgerWorkflow.Reverse(first, " Incorrect ending odometer ", Actor, Now.AddMinutes(1));
        Assert.Equal("Incorrect ending odometer", reversal.Reason);
        Assert.Equal(first.Digest, reversal.SourceDigest); Assert.Equal(original, JsonSerializer.Serialize(first));
        VehicleLedgerWorkflow.ValidateReversal(reversal, first);
        var replacement = Trip(vehicle, driver, first.Start, 100, 115);
        VehicleLedgerWorkflow.ValidateActiveTrips([replacement, first], [reversal]);
        Assert.Equal("vehicle_driver_overlap", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.ValidateActiveTrips([first, replacement], [])).Code);
        Assert.Equal("vehicle_already_reversed", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.ValidateActiveTrips([first], [reversal, reversal])).Code);
    }

    [Fact]
    public void Reversal_requires_reason_current_source_and_chronological_actor_evidence()
    {
        var source = Trip(Vehicle(), Driver(), Now.AddDays(-2), 100, 110);
        Assert.Equal("required", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.Reverse(source, " ", Actor, Now)).Code);
        Assert.Equal("vehicle_reversal_time", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.Reverse(source, "Correct record", Actor, Now.AddTicks(-1))).Code);
        var reversal = VehicleLedgerWorkflow.Reverse(source, "Correct record", Actor, Now);
        reversal.SourceDigest = new string('0', 64); reversal.Digest = reversal.CalculateDigest();
        Assert.Equal("vehicle_reversal_source", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.ValidateReversal(reversal, source)).Code);
        reversal.SourceDigest = source.Digest; reversal.Digest = reversal.CalculateDigest(); reversal.Reason = "Changed reason";
        Assert.Equal("vehicle_reversal_immutable", Assert.Throws<DomainException>(reversal.Validate).Code);
    }

    [Theory]
    [InlineData("amount", "invalid_money_precision")]
    [InlineData("zero", "vehicle_cost_amount")]
    [InlineData("negative", "out_of_range")]
    [InlineData("currency", "unsupported_currency")]
    [InlineData("category", "vehicle_cost_category")]
    [InlineData("future", "vehicle_cost_date")]
    [InlineData("reference", "required")]
    public void Cost_originals_require_exact_euro_amounts_dates_and_evidence(string invalid, string code)
    {
        var body = CostDetails();
        body = invalid switch
        {
            "amount" => body with { Amount = 12.345m }, "zero" => body with { Amount = 0 }, "negative" => body with { Amount = -10 },
            "currency" => body with { Currency = "USD" }, "category" => body with { Category = (VehicleCostCategory)999 },
            "future" => body with { SpentOn = DateOnly.FromDateTime(Now.Date).AddDays(1) }, _ => body with { Reference = " " }
        };
        Assert.Equal(code, Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.RecordCost(Vehicle(), null, body, DateOnly.FromDateTime(Now.Date), Actor, Now)).Code);
    }

    [Fact]
    public void Immutable_cost_original_and_reversal_reject_rewriting_archival_and_revision_changes()
    {
        var cost = VehicleLedgerWorkflow.RecordCost(Vehicle(), null, CostDetails(), DateOnly.FromDateTime(Now.Date), Actor, Now);
        cost.Validate(); Assert.Equal(123.45m, cost.Amount);
        var reversal = VehicleLedgerWorkflow.Reverse(cost, "Duplicate cost", Actor, Now.AddMinutes(1));
        Assert.Equal("vehicle-costs", reversal.SourceKind); VehicleLedgerWorkflow.ValidateReversal(reversal, cost);
        cost.Amount++; Assert.Equal("vehicle_original_immutable", Assert.Throws<DomainException>(cost.Validate).Code);
        cost.Amount--; cost.Revision++;
        Assert.Equal("vehicle_original_immutable", Assert.Throws<DomainException>(cost.Validate).Code);
        cost.Revision--; cost.Archived = true;
        Assert.Equal("vehicle_original_immutable", Assert.Throws<DomainException>(cost.Validate).Code);
        reversal.Archived = true; Assert.Equal("vehicle_reversal_immutable", Assert.Throws<DomainException>(reversal.Validate).Code);
    }

    [Fact]
    public void Full_history_validation_is_order_independent_and_compares_each_driver_across_vehicles()
    {
        var vehicle = Vehicle(); var driver = Driver();
        var first = Trip(vehicle, driver, Now.AddDays(-3), 100, 110);
        var second = Trip(vehicle, driver, Now.AddDays(-2), 110, 120);
        var third = Trip(vehicle, driver, Now.AddDays(-1), 120, 130);
        VehicleLedgerWorkflow.ValidateActiveTrips([third, first, second], []);
        var driverConflict = Trip(Vehicle(), driver, second.Start, 900, 910);
        Assert.Equal("vehicle_driver_overlap", Assert.Throws<DomainException>(() => VehicleLedgerWorkflow.ValidateActiveTrips([third, first, driverConflict, second], [])).Code);
    }

    private static Equipment Vehicle() => new() { TenantId = Tenant, Name = "Service van", InventoryNumber = Guid.NewGuid().ToString("N"),
        Kind = EquipmentKind.Vehicle, Registration = "TEST-1", CreatedAt = Now.AddYears(-1), UpdatedAt = Now.AddYears(-1) };
    private static Employee Driver() => new() { TenantId = Tenant, Name = "Synthetic driver", CreatedAt = Now.AddYears(-1), UpdatedAt = Now.AddYears(-1) };
    private static Project Project() => new() { TenantId = Tenant, CustomerId = Guid.NewGuid(), BuildingId = Guid.NewGuid(), Name = "Synthetic roof",
        CreatedAt = Now.AddYears(-1), UpdatedAt = Now.AddYears(-1) };
    private static VehicleTripDetails Details(Employee driver, Project? project = null) => new(driver.Id, project?.Id, Now.AddDays(-1).AddHours(-1),
        Now.AddDays(-1), 100, 120, "Roof inspection", "Workshop", "Roof site", "Synthetic journey");
    private static VehicleCostDetails CostDetails() => new(null, new(2026, 10, 20), VehicleCostCategory.Fuel, 123.45m, "EUR", "SYNTHETIC-RECEIPT-1", "Synthetic cost");
    private static VehicleTrip Trip(Equipment vehicle, Employee driver, DateTimeOffset start, decimal from, decimal to) =>
        VehicleLedgerWorkflow.RecordTrip(vehicle, driver, null, Details(driver) with { Start = start, End = start.AddHours(1), OdometerStartKm = from, OdometerEndKm = to }, Actor, Now);
}
