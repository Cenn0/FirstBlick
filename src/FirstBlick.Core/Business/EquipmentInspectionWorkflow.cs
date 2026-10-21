namespace FirstBlick.Core.Business;

public enum EquipmentInspectionKind { Inspection, Maintenance, Repair }
public enum EquipmentInspectionStatus { Scheduled, Completed, Cancelled }
public enum EquipmentInspectionResult { Passed, Failed }
public enum EquipmentInspectionDueStatus { Unscheduled, Current, Due, Overdue }

public sealed record EquipmentInspectionSnapshot(Guid Id, string Name, string InventoryNumber, EquipmentKind Kind, string Registration);
public sealed record EquipmentInspectionCompletion(DateOnly PerformedOn, EquipmentInspectionResult Result, string Inspector,
    string EvidenceReference, string Notes, DateOnly? NextInspectionOn, bool MakeAvailable, Guid RecordedBy,
    DateTimeOffset RecordedAt, string Digest);
public sealed record EquipmentInspectionCancellation(string Reason, Guid CancelledBy, DateTimeOffset CancelledAt, string Digest);

/// <summary>A scheduled inspection, maintenance or repair with an immutable equipment identity and sealed outcome.</summary>
public sealed class EquipmentInspection : TenantEntity
{
    public Guid EquipmentId { get; set; }
    public EquipmentInspectionKind Kind { get; set; }
    public string Title { get; set; } = "";
    public DateOnly ScheduledOn { get; set; }
    public string Notes { get; set; } = "";
    public EquipmentInspectionStatus Status { get; set; }
    public EquipmentInspectionSnapshot? EquipmentSnapshot { get; set; }
    public Guid PlannedBy { get; set; }
    public string ScopeDigest { get; set; } = "";
    public EquipmentInspectionCompletion? Completion { get; set; }
    public EquipmentInspectionCancellation? Cancellation { get; set; }

    public override void Validate()
    {
        ValidateCommon(); Guard.Id(EquipmentId, nameof(EquipmentId)); Guard.Id(PlannedBy, nameof(PlannedBy));
        Guard.Require(!Archived, "inspection_immutable", "Inspection history is retained; cancel an outstanding appointment instead.");
        Guard.Require(Enum.IsDefined(Kind) && Enum.IsDefined(Status), "invalid_status", "The inspection kind or status is invalid.");
        Guard.Text(Title, nameof(Title)); Guard.Text(Notes, nameof(Notes), 4000, false);
        EquipmentInspectionWorkflow.Date(ScheduledOn, nameof(ScheduledOn));
        Guard.Require(EquipmentSnapshot is not null && EquipmentSnapshot.Id == EquipmentId && Enum.IsDefined(EquipmentSnapshot.Kind),
            "inspection_snapshot", "The inspection must retain its equipment identity.");
        Guard.Text(EquipmentSnapshot.Name, nameof(EquipmentSnapshot.Name));
        Guard.Text(EquipmentSnapshot.InventoryNumber, nameof(EquipmentSnapshot.InventoryNumber), 100);
        Guard.Text(EquipmentSnapshot.Registration, nameof(EquipmentSnapshot.Registration), 80, false);
        Guard.Require(ScopeDigest == EquipmentInspectionWorkflow.ScopeDigest(this), "inspection_immutable", "The scheduled inspection scope has changed.");
        Guard.Require(Status switch
        {
            EquipmentInspectionStatus.Scheduled => Completion is null && Cancellation is null,
            EquipmentInspectionStatus.Completed => Completion is not null && Cancellation is null,
            EquipmentInspectionStatus.Cancelled => Completion is null && Cancellation is not null,
            _ => false
        }, "inspection_evidence", "The inspection status does not match its completion or cancellation evidence.");
        if (Completion is { } completed)
        {
            EquipmentInspectionWorkflow.ValidateCompletion(completed);
            Guard.Require(completed.RecordedAt >= CreatedAt && completed.Digest == EquipmentInspectionWorkflow.CompletionDigest(this, completed),
                "inspection_immutable", "Completed inspection evidence cannot be rewritten.");
        }
        if (Cancellation is { } cancelled)
        {
            Guard.Text(cancelled.Reason, nameof(cancelled.Reason), 2000); Guard.Id(cancelled.CancelledBy, nameof(cancelled.CancelledBy));
            Guard.Utc(cancelled.CancelledAt, nameof(cancelled.CancelledAt));
            Guard.Require(cancelled.CancelledAt >= CreatedAt && cancelled.Digest == EquipmentInspectionWorkflow.CancellationDigest(this, cancelled),
                "inspection_immutable", "Inspection cancellation evidence cannot be rewritten.");
        }
    }

    public override IEnumerable<EntityReference> References() => [BusinessRegistry.Reference<Equipment>(EquipmentId)];
}

public static class EquipmentInspectionWorkflow
{
    public static void Date(DateOnly value, string field) =>
        Guard.Require(value.Year is >= 1900 and <= 9998, "invalid_inspection_date", $"{field} must be a date from 1900 through 9998.");

    public static EquipmentInspectionSnapshot Snapshot(Equipment equipment) =>
        new(equipment.Id, equipment.Name, equipment.InventoryNumber, equipment.Kind, equipment.Registration);

    public static string ScopeDigest(EquipmentInspection inspection) => ContentDigest.Of(new
    {
        inspection.TenantId, inspection.Id, inspection.EquipmentId, inspection.Kind, inspection.Title,
        inspection.ScheduledOn, inspection.Notes, inspection.EquipmentSnapshot, inspection.PlannedBy
    });

    public static string CompletionDigest(EquipmentInspection inspection, EquipmentInspectionCompletion evidence) => ContentDigest.Of(new
    {
        inspection.TenantId, inspection.Id, inspection.ScopeDigest, evidence.PerformedOn, evidence.Result,
        evidence.Inspector, evidence.EvidenceReference, evidence.Notes, evidence.NextInspectionOn,
        evidence.MakeAvailable, evidence.RecordedBy, evidence.RecordedAt
    });

    public static string CancellationDigest(EquipmentInspection inspection, EquipmentInspectionCancellation evidence) => ContentDigest.Of(new
    { inspection.TenantId, inspection.Id, inspection.ScopeDigest, evidence.Reason, evidence.CancelledBy, evidence.CancelledAt });

    public static EquipmentInspection Plan(Equipment equipment, EquipmentInspectionKind kind, DateOnly scheduledOn,
        string title, string notes, Guid actor, DateTimeOffset now)
    {
        equipment.Validate(); Guard.Require(!equipment.Archived, "not_found", "The equipment is archived.");
        Guard.Require(equipment.PendingInspectionId is null, "inspection_already_planned", "Complete or cancel the outstanding inspection before planning another.");
        Guard.Id(actor, nameof(actor)); Guard.Utc(now, nameof(now)); Date(scheduledOn, nameof(scheduledOn));
        Guard.Require(Enum.IsDefined(kind), "invalid_status", "The inspection kind is invalid.");
        Guard.Text(title, nameof(title)); Guard.Text(notes, nameof(notes), 4000, false);
        var inspection = new EquipmentInspection
        {
            TenantId = equipment.TenantId, EquipmentId = equipment.Id, Kind = kind, Title = title.Trim(),
            ScheduledOn = scheduledOn, Notes = notes ?? "", EquipmentSnapshot = Snapshot(equipment),
            PlannedBy = actor, CreatedAt = now, UpdatedAt = now
        };
        inspection.ScopeDigest = ScopeDigest(inspection); inspection.Validate();
        equipment.PendingInspectionId = inspection.Id;
        return inspection;
    }

    public static void Complete(Equipment equipment, EquipmentInspection inspection, DateOnly performedOn,
        EquipmentInspectionResult result, string inspector, string evidenceReference, string notes,
        DateOnly? nextInspectionOn, bool makeAvailable, DateOnly businessDate, Guid actor, DateTimeOffset now)
    {
        EnsurePending(equipment, inspection); Date(businessDate, nameof(businessDate)); Guard.Utc(now, nameof(now));
        Guard.Require(now >= inspection.CreatedAt, "invalid_time", "Recording time cannot precede the planned record.");
        Guard.Require(performedOn <= businessDate, "inspection_future_result", "A future inspection cannot be recorded as performed.");
        Guard.Require(!equipment.LastInspectionOn.HasValue || performedOn >= equipment.LastInspectionOn,
            "inspection_out_of_order", "A new result cannot predate the latest recorded inspection.");
        var completion = new EquipmentInspectionCompletion(performedOn, result, inspector, evidenceReference, notes ?? "",
            nextInspectionOn, makeAvailable, actor, now, "");
        ValidateCompletion(completion);
        completion = completion with { Digest = CompletionDigest(inspection, completion) };
        inspection.Completion = completion; inspection.Status = EquipmentInspectionStatus.Completed;
        equipment.PendingInspectionId = null; equipment.LastInspectionId = inspection.Id; equipment.LastInspectionOn = performedOn;
        equipment.InspectionBlocked = result == EquipmentInspectionResult.Failed;
        if (result == EquipmentInspectionResult.Failed) equipment.Available = false;
        else
        {
            equipment.NextInspectionOn = nextInspectionOn;
            // A successful result does not silently undo a manual unavailability decision.
            if (makeAvailable) equipment.Available = true;
        }
        equipment.Validate(); inspection.Validate();
    }

    public static void Cancel(Equipment equipment, EquipmentInspection inspection, string reason, Guid actor, DateTimeOffset now)
    {
        EnsurePending(equipment, inspection); Guard.Text(reason, nameof(reason), 2000); Guard.Id(actor, nameof(actor)); Guard.Utc(now, nameof(now));
        Guard.Require(now >= inspection.CreatedAt, "invalid_time", "Cancellation time cannot precede the planned record.");
        var cancellation = new EquipmentInspectionCancellation(reason, actor, now, "");
        inspection.Cancellation = cancellation with { Digest = CancellationDigest(inspection, cancellation) };
        inspection.Status = EquipmentInspectionStatus.Cancelled; equipment.PendingInspectionId = null;
        inspection.Validate(); equipment.Validate();
    }

    public static void ValidateCompletion(EquipmentInspectionCompletion evidence)
    {
        Date(evidence.PerformedOn, nameof(evidence.PerformedOn)); Guard.Require(Enum.IsDefined(evidence.Result), "invalid_status", "The inspection result is invalid.");
        Guard.Text(evidence.Inspector, nameof(evidence.Inspector), 240); Guard.Text(evidence.EvidenceReference, nameof(evidence.EvidenceReference), 500);
        Guard.Text(evidence.Notes, nameof(evidence.Notes), 4000, evidence.Result == EquipmentInspectionResult.Failed);
        Guard.Id(evidence.RecordedBy, nameof(evidence.RecordedBy)); Guard.Utc(evidence.RecordedAt, nameof(evidence.RecordedAt));
        if (evidence.Result == EquipmentInspectionResult.Passed)
        {
            Guard.Require(evidence.NextInspectionOn.HasValue && evidence.NextInspectionOn > evidence.PerformedOn,
                "inspection_next_date", "A successful result requires an explicitly chosen next date after the performed date.");
            Date(evidence.NextInspectionOn.Value, nameof(evidence.NextInspectionOn));
        }
        else Guard.Require(evidence.NextInspectionOn is null && !evidence.MakeAvailable, "inspection_failed_release",
            "A failed inspection cannot release equipment or declare a renewed inspection date.");
    }

    public static EquipmentInspectionDueStatus DueStatus(Equipment equipment, DateOnly businessDate) => equipment.NextInspectionOn switch
    {
        null => EquipmentInspectionDueStatus.Unscheduled,
        { } due when due < businessDate => EquipmentInspectionDueStatus.Overdue,
        { } due when due == businessDate => EquipmentInspectionDueStatus.Due,
        _ => EquipmentInspectionDueStatus.Current
    };

    public static void EnsureAssignable(Equipment equipment, DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone)
    {
        equipment.Validate(); Guard.Window(start, end);
        Guard.Require(!equipment.Archived && equipment.Available && !equipment.InspectionBlocked,
            "equipment_unavailable", "A selected resource is unavailable or blocked by an inspection result.");
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end.AddTicks(-1), zone).DateTime);
        Guard.Require(!equipment.NextInspectionOn.HasValue || lastDay <= equipment.NextInspectionOn,
            "equipment_inspection_due", "The assignment extends beyond the selected resource's explicitly recorded inspection date.");
    }

    private static void EnsurePending(Equipment equipment, EquipmentInspection inspection)
    {
        equipment.Validate(); inspection.Validate();
        Guard.Require(!equipment.Archived && equipment.TenantId == inspection.TenantId && equipment.Id == inspection.EquipmentId,
            "inspection_equipment_mismatch", "The inspection and equipment must belong to the same business and resource.");
        Guard.Require(inspection.Status == EquipmentInspectionStatus.Scheduled && equipment.PendingInspectionId == inspection.Id,
            "inspection_sealed", "Only the equipment's outstanding inspection can be completed or cancelled.");
        Guard.Require(inspection.EquipmentSnapshot == Snapshot(equipment), "inspection_equipment_changed", "The equipment identity changed after the inspection was planned.");
    }
}
