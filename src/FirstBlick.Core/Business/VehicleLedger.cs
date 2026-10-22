namespace FirstBlick.Core.Business;

public enum VehicleCostCategory { Fuel, Maintenance, Repair, Insurance, Tax, Toll, Parking, Other }

public sealed record VehicleTripDetails(Guid DriverEmployeeId, Guid? ProjectId, DateTimeOffset Start, DateTimeOffset End,
    decimal OdometerStartKm, decimal OdometerEndKm, string Purpose, string Origin, string Destination, string Notes);
public sealed record VehicleCostDetails(Guid? ProjectId, DateOnly SpentOn, VehicleCostCategory Category, decimal Amount,
    string Currency, string Reference, string Notes);

/// <summary>A posted operational original. Corrections are separate, retained reversal records.</summary>
public abstract class VehicleLedgerEntry : TenantEntity
{
    public Guid VehicleId { get; set; }
    public EquipmentInspectionSnapshot? VehicleSnapshot { get; set; }
    public Guid? ProjectId { get; set; }
    public string? ProjectTitle { get; set; }
    public Guid RecordedBy { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string Digest { get; set; } = "";
    public abstract string CalculateDigest();

    protected void ValidateOriginal()
    {
        ValidateCommon(); Guard.Id(VehicleId, nameof(VehicleId)); Guard.Id(RecordedBy, nameof(RecordedBy));
        Guard.Utc(RecordedAt, nameof(RecordedAt)); Guard.OptionalId(ProjectId, nameof(ProjectId));
        Guard.Require(!Archived && Revision == 1 && CreatedAt == RecordedAt && UpdatedAt >= RecordedAt,
            "vehicle_original_immutable", "A posted vehicle original is retained unchanged; use its reversal command.");
        Guard.Require(VehicleSnapshot is not null && VehicleSnapshot.Id == VehicleId && VehicleSnapshot.Kind == EquipmentKind.Vehicle,
            "vehicle_snapshot", "Vehicle evidence must retain the original vehicle identity.");
        Guard.Text(VehicleSnapshot.Name, nameof(VehicleSnapshot.Name)); Guard.Text(VehicleSnapshot.InventoryNumber, nameof(VehicleSnapshot.InventoryNumber), 100);
        VehicleLedgerWorkflow.Text(VehicleSnapshot.Registration, nameof(VehicleSnapshot.Registration), 80, false);
        Guard.Require(ProjectId.HasValue ? !string.IsNullOrWhiteSpace(ProjectTitle) : ProjectTitle is null,
            "vehicle_project", "A project reference must retain its original name.");
        Guard.Text(ProjectTitle, nameof(ProjectTitle), required: false);
        Guard.Require(Digest is { Length: 64 } && Digest == CalculateDigest(), "vehicle_original_immutable", "The recorded vehicle original does not match its sealed digest.");
    }

    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Equipment>(VehicleId);
        if (ProjectId is { } project) yield return BusinessRegistry.Reference<Project>(project);
    }
}

public sealed class VehicleTrip : VehicleLedgerEntry
{
    public Guid DriverEmployeeId { get; set; }
    public string DriverName { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public decimal OdometerStartKm { get; set; }
    public decimal OdometerEndKm { get; set; }
    public string Purpose { get; set; } = "";
    public string Origin { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Notes { get; set; } = "";

    public override void Validate()
    {
        ValidateOriginal(); Guard.Id(DriverEmployeeId, nameof(DriverEmployeeId)); Guard.Text(DriverName, nameof(DriverName));
        Guard.Window(Start, End);
        Guard.Require(Start.Year >= 1900 && End <= RecordedAt && End - Start <= TimeSpan.FromHours(24),
            "vehicle_trip_window", "A recorded journey must be completed and cover at most 24 hours, from 1900 onward.");
        Guard.Require(Start.Ticks % TimeSpan.TicksPerSecond == 0 && End.Ticks % TimeSpan.TicksPerSecond == 0,
            "vehicle_time_precision", "Journey timestamps support whole seconds.");
        VehicleLedgerWorkflow.Odometer(OdometerStartKm); VehicleLedgerWorkflow.Odometer(OdometerEndKm);
        Guard.Require(OdometerEndKm > OdometerStartKm, "vehicle_odometer", "The final odometer must exceed the starting odometer.");
        VehicleLedgerWorkflow.Text(Purpose, nameof(Purpose), 1000); VehicleLedgerWorkflow.Text(Origin, nameof(Origin), 240);
        VehicleLedgerWorkflow.Text(Destination, nameof(Destination), 240); VehicleLedgerWorkflow.Text(Notes, nameof(Notes), 4000, false);
    }

    public override string CalculateDigest() => ContentDigest.Of(new
    {
        format = "vehicle-trip-v1", TenantId, Id, VehicleId, VehicleSnapshot, DriverEmployeeId, DriverName, ProjectId, ProjectTitle,
        Start, End, OdometerStartKm, OdometerEndKm, Purpose, Origin, Destination, Notes, RecordedBy, RecordedAt
    });

    public override IEnumerable<EntityReference> References() => base.References().Append(BusinessRegistry.Reference<Employee>(DriverEmployeeId));
}

public sealed class VehicleCost : VehicleLedgerEntry
{
    public DateOnly SpentOn { get; set; }
    public VehicleCostCategory Category { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Reference { get; set; } = "";
    public string Notes { get; set; } = "";

    public override void Validate()
    {
        ValidateOriginal(); Guard.Require(SpentOn.Year is >= 1900 and <= 9998, "vehicle_cost_date", "Use a cost date from 1900 through 9998.");
        Guard.Require(Enum.IsDefined(Category), "vehicle_cost_category", "Choose a supported vehicle cost category.");
        Guard.Currency(Currency); Money.Amount(Amount, nameof(Amount)); Guard.Require(Amount > 0, "vehicle_cost_amount", "Record a positive cost; reverse an incorrect original separately.");
        VehicleLedgerWorkflow.Text(Reference, nameof(Reference), 240); VehicleLedgerWorkflow.Text(Notes, nameof(Notes), 4000, false);
    }

    public override string CalculateDigest() => ContentDigest.Of(new
    {
        format = "vehicle-cost-v1", TenantId, Id, VehicleId, VehicleSnapshot, ProjectId, ProjectTitle,
        SpentOn, Category, Amount, Currency, Reference, Notes, RecordedBy, RecordedAt
    });
}

public sealed class VehicleLedgerReversal : TenantEntity
{
    public Guid VehicleId { get; set; }
    public string SourceKind { get; set; } = "";
    public Guid SourceId { get; set; }
    public string SourceDigest { get; set; } = "";
    public string Reason { get; set; } = "";
    public Guid RecordedBy { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string Digest { get; set; } = "";

    public override void Validate()
    {
        ValidateCommon(); Guard.Id(VehicleId, nameof(VehicleId)); Guard.Id(SourceId, nameof(SourceId)); Guard.Id(RecordedBy, nameof(RecordedBy));
        Guard.Utc(RecordedAt, nameof(RecordedAt)); VehicleLedgerWorkflow.Text(Reason, nameof(Reason), 2000);
        Guard.Require(!Archived && Revision == 1 && CreatedAt == RecordedAt && UpdatedAt >= RecordedAt,
            "vehicle_reversal_immutable", "A vehicle reversal is a retained immutable record.");
        Guard.Require(SourceKind is "vehicle-trips" or "vehicle-costs" && SourceDigest is { Length: 64 } && SourceDigest.All(char.IsAsciiHexDigit),
            "vehicle_reversal_source", "The reversal must identify its original vehicle entry and digest.");
        Guard.Require(Digest is { Length: 64 } && Digest == CalculateDigest(), "vehicle_reversal_immutable", "The vehicle reversal evidence has changed.");
    }

    public string CalculateDigest() => ContentDigest.Of(new
    { format = "vehicle-reversal-v1", TenantId, Id, VehicleId, SourceKind, SourceId, SourceDigest, Reason, RecordedBy, RecordedAt });

    public override IEnumerable<EntityReference> References() => [BusinessRegistry.Reference<Equipment>(VehicleId), new(SourceKind, SourceId)];
}

public static class VehicleLedgerWorkflow
{
    public static VehicleTrip RecordTrip(Equipment vehicle, Employee driver, Project? project, VehicleTripDetails details, Guid actor, DateTimeOffset now)
    {
        BeforeRecord(vehicle, project, actor, now); driver.Validate();
        Guard.Require(driver.TenantId == vehicle.TenantId && driver.Id == details.DriverEmployeeId && driver.Active && !driver.Archived,
            "vehicle_driver", "Choose an active employee in this business as the driver.");
        Guard.Require(now >= driver.CreatedAt, "vehicle_record_time", "The driver record cannot postdate the recording.");
        Guard.Require(details.ProjectId == project?.Id, "vehicle_project", "The selected project does not match the resolved project.");
        var trip = new VehicleTrip
        {
            TenantId = vehicle.TenantId, VehicleId = vehicle.Id, VehicleSnapshot = EquipmentInspectionWorkflow.Snapshot(vehicle) with { Registration = vehicle.Registration ?? "" },
            DriverEmployeeId = driver.Id, DriverName = driver.Name, ProjectId = project?.Id, ProjectTitle = project?.Name,
            Start = details.Start, End = details.End, OdometerStartKm = details.OdometerStartKm, OdometerEndKm = details.OdometerEndKm,
            Purpose = details.Purpose, Origin = details.Origin, Destination = details.Destination, Notes = details.Notes,
            RecordedBy = actor, RecordedAt = now, CreatedAt = now, UpdatedAt = now
        };
        trip.Digest = trip.CalculateDigest(); trip.Validate(); return trip;
    }

    public static VehicleCost RecordCost(Equipment vehicle, Project? project, VehicleCostDetails details, DateOnly businessDate, Guid actor, DateTimeOffset now)
    {
        BeforeRecord(vehicle, project, actor, now);
        Guard.Require(details.ProjectId == project?.Id, "vehicle_project", "The selected project does not match the resolved project.");
        Guard.Require(details.SpentOn <= businessDate, "vehicle_cost_date", "A recorded cost cannot have a future business date.");
        var cost = new VehicleCost
        {
            TenantId = vehicle.TenantId, VehicleId = vehicle.Id, VehicleSnapshot = EquipmentInspectionWorkflow.Snapshot(vehicle) with { Registration = vehicle.Registration ?? "" },
            ProjectId = project?.Id, ProjectTitle = project?.Name, SpentOn = details.SpentOn, Category = details.Category,
            Amount = details.Amount, Currency = details.Currency, Reference = details.Reference, Notes = details.Notes,
            RecordedBy = actor, RecordedAt = now, CreatedAt = now, UpdatedAt = now
        };
        cost.Digest = cost.CalculateDigest(); cost.Validate(); return cost;
    }

    public static VehicleLedgerReversal Reverse(VehicleLedgerEntry source, string reason, Guid actor, DateTimeOffset now)
    {
        source.Validate(); Guard.Id(actor, nameof(actor)); Guard.Utc(now, nameof(now)); Text(reason, nameof(reason), 2000);
        Guard.Require(now >= source.RecordedAt, "vehicle_reversal_time", "A reversal cannot precede its original.");
        var reversal = new VehicleLedgerReversal
        {
            TenantId = source.TenantId, VehicleId = source.VehicleId, SourceKind = source is VehicleTrip ? "vehicle-trips" : "vehicle-costs",
            SourceId = source.Id, SourceDigest = source.Digest, Reason = reason.Trim(), RecordedBy = actor, RecordedAt = now,
            CreatedAt = now, UpdatedAt = now
        };
        reversal.Digest = reversal.CalculateDigest(); ValidateReversal(reversal, source); return reversal;
    }

    public static void ValidateReversal(VehicleLedgerReversal reversal, VehicleLedgerEntry source)
    {
        reversal.Validate(); source.Validate();
        Guard.Require(reversal.TenantId == source.TenantId && reversal.VehicleId == source.VehicleId && reversal.SourceId == source.Id
            && reversal.SourceKind == (source is VehicleTrip ? "vehicle-trips" : "vehicle-costs") && reversal.SourceDigest == source.Digest
            && reversal.RecordedAt >= source.RecordedAt, "vehicle_reversal_source", "The reversal must retain its exact original vehicle entry and chronological evidence.");
    }

    /// <summary>Compare against retained active history, including records before and after the new entry's month.</summary>
    public static void EnsureCompatible(VehicleTrip candidate, VehicleTrip other)
    {
        candidate.Validate(); other.Validate();
        Guard.Require(candidate.TenantId == other.TenantId, "vehicle_tenant", "Compare vehicle history only within one business.");
        if (candidate.Id == other.Id) return;
        var overlaps = candidate.Start < other.End && candidate.End > other.Start;
        if (candidate.DriverEmployeeId == other.DriverEmployeeId)
            Guard.Require(!overlaps, "vehicle_driver_overlap", "This driver already has a recorded journey in this time window.");
        if (candidate.VehicleId != other.VehicleId) return;
        Guard.Require(!overlaps, "vehicle_trip_overlap", "The vehicle already has a recorded journey in this time window.");
        Guard.Require(candidate.End <= other.Start ? candidate.OdometerEndKm <= other.OdometerStartKm : other.OdometerEndKm <= candidate.OdometerStartKm,
            "vehicle_odometer_conflict", "The odometer conflicts with an earlier or later recorded journey. Reverse the incorrect original before recording a correction.");
    }

    public static void ValidateActiveTrips(IEnumerable<VehicleTrip> trips, IEnumerable<VehicleLedgerReversal> reversals)
    {
        var originals = trips.ToArray(); foreach (var trip in originals) trip.Validate();
        var removed = new HashSet<(Guid Tenant, Guid Id)>();
        foreach (var reversal in reversals.Where(x => x.SourceKind == "vehicle-trips"))
        {
            reversal.Validate();
            var source = originals.SingleOrDefault(x => x.TenantId == reversal.TenantId && x.Id == reversal.SourceId)
                ?? throw new DomainException("vehicle_reversal_source", "The reversed journey is missing from retained history.");
            ValidateReversal(reversal, source);
            Guard.Require(removed.Add((reversal.TenantId, reversal.SourceId)), "vehicle_already_reversed", "A journey can be reversed only once.");
        }
        var active = originals.Where(x => !removed.Contains((x.TenantId, x.Id))).ToArray();
        foreach (var group in active.GroupBy(x => (x.TenantId, x.VehicleId)).Concat(active.GroupBy(x => (x.TenantId, x.DriverEmployeeId))))
        {
            VehicleTrip? previous = null;
            foreach (var trip in group.OrderBy(x => x.Start).ThenBy(x => x.End))
            {
                if (previous is not null) EnsureCompatible(previous, trip);
                previous = trip;
            }
        }
    }

    internal static void Text(string? value, string field, int max, bool required = true)
    {
        Guard.Text(value, field, max, required);
        Guard.Require(value is not null, "vehicle_fields", "Optional vehicle fields must be strings; use an empty string if unknown.");
    }

    internal static void Odometer(decimal value)
    {
        Guard.Range(value, 0, 9_999_999.999m, "OdometerKm");
        Guard.Require(value == decimal.Round(value, 3), "vehicle_odometer_precision", "Odometer readings support at most three decimal places in kilometres.");
    }

    private static void BeforeRecord(Equipment vehicle, Project? project, Guid actor, DateTimeOffset now)
    {
        vehicle.Validate(); Guard.Id(actor, nameof(actor)); Guard.Utc(now, nameof(now));
        Guard.Require(vehicle.Kind == EquipmentKind.Vehicle && !vehicle.Archived, "vehicle_required", "Choose an active vehicle record.");
        Guard.Require(now >= vehicle.CreatedAt, "vehicle_record_time", "The vehicle record cannot postdate the recording.");
        if (project is null) return;
        project.Validate(); Guard.Require(project.TenantId == vehicle.TenantId && !project.Archived,
            "vehicle_project", "Choose a project in this business which is not archived.");
        Guard.Require(now >= project.CreatedAt, "vehicle_record_time", "The project record cannot postdate the recording.");
    }
}
