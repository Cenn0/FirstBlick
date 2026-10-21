namespace FirstBlick.Core.Business;

public sealed class DiaryEntry : TenantEntity
{
    public Guid ProjectId { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly Date { get; set; }
    public string Work { get; set; } = "";
    public string Weather { get; set; } = "";
    public string Obstructions { get; set; } = "";
    public string SafetyNotes { get; set; } = "";
    public DiaryStatus Status { get; set; }
    public DiarySubmission? Submission { get; set; }
    public DiaryReview? Review { get; set; }
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(ProjectId, nameof(ProjectId)); Guard.Id(EmployeeId, nameof(EmployeeId)); Guard.Require(Date != default, "required", "Diary date is required.");
        Guard.Text(Work, nameof(Work), 20_000); Guard.Text(Weather, nameof(Weather), 2000, false);
        Guard.Text(Obstructions, nameof(Obstructions), 8000, false); Guard.Text(SafetyNotes, nameof(SafetyNotes), 8000, false);
        DiaryWorkflow.ValidateState(this);
    }
    public override IEnumerable<EntityReference> References() => [BusinessRegistry.Reference<Project>(ProjectId), BusinessRegistry.Reference<Employee>(EmployeeId)];
}

public enum DefectStatus { Open, InProgress, Resolved, Accepted }
public sealed class Defect : TenantEntity
{
    public Guid ProjectId { get; set; }
    public Guid? AssignedEmployeeId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateOnly? DueDate { get; set; }
    public DefectStatus Status { get; set; }
    public string Resolution { get; set; } = "";
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(ProjectId, nameof(ProjectId)); Guard.OptionalId(AssignedEmployeeId, nameof(AssignedEmployeeId));
        Guard.Text(Title, nameof(Title)); Guard.Text(Description, nameof(Description), 8000); Guard.Require(Enum.IsDefined(Status), "invalid_status", "Defect status is invalid.");
        Guard.Text(Resolution, nameof(Resolution), 8000, Status is DefectStatus.Resolved or DefectStatus.Accepted);
    }
    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Project>(ProjectId);
        if (AssignedEmployeeId is { } id) yield return BusinessRegistry.Reference<Employee>(id);
    }
}

public sealed class Acceptance : TenantEntity
{
    public Guid ProjectId { get; set; }
    public string Title { get; set; } = "";
    public string CustomerRepresentative { get; set; } = "";
    public string ContractorRepresentative { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public bool WithReservations { get; set; }
    public List<Guid> DefectIds { get; set; } = [];
    public string Notes { get; set; } = "";
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(ProjectId, nameof(ProjectId)); Guard.Text(Title, nameof(Title)); Guard.Utc(OccurredAt, nameof(OccurredAt));
        Guard.Text(CustomerRepresentative, nameof(CustomerRepresentative)); Guard.Text(ContractorRepresentative, nameof(ContractorRepresentative));
        Guard.Items(DefectIds, nameof(DefectIds), 1000); foreach (var id in DefectIds) Guard.Id(id, "DefectId");
        Guard.Require(DefectIds.Distinct().Count() == DefectIds.Count, "duplicate_defect", "Defect references must be unique.");
        Guard.Text(Notes, nameof(Notes), 20_000, WithReservations);
        Guard.Require(WithReservations || DefectIds.Count == 0, "unacknowledged_defects", "Referenced defects require an acceptance with reservations.");
    }
    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Project>(ProjectId);
        foreach (var id in DefectIds) yield return BusinessRegistry.Reference<Defect>(id);
    }
}

public sealed class MaintenanceContract : TenantEntity
{
    public List<MaintenanceCheckTemplate> ChecklistTemplate { get; set; } = [];
    public bool AutomaticPlanning { get; set; }
    public int PlanningLeadDays { get; set; } = 14;
    public Guid CustomerId { get; set; }
    public Guid BuildingId { get; set; }
    public string Title { get; set; } = "";
    public string Scope { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int IntervalMonths { get; set; } = 12;
    public DateOnly NextDueDate { get; set; }
    public int CompletedOccurrences { get; set; }
    public bool Active { get; set; } = true;
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(CustomerId, nameof(CustomerId)); Guard.Id(BuildingId, nameof(BuildingId)); Guard.Text(Title, nameof(Title)); Guard.Text(Scope, nameof(Scope), 20_000);
        Guard.Require(StartDate != default && NextDueDate >= StartDate && (!EndDate.HasValue || EndDate >= StartDate), "invalid_window", "Maintenance dates are invalid.");
        MaintenanceWorkflow.ValidateTemplate(ChecklistTemplate, AutomaticPlanning);
        Guard.Require(PlanningLeadDays is >= 0 and <= 365, "invalid_lead_time", "The planning lead time must be between zero and 365 days.");
        Guard.Require(IntervalMonths is >= 1 and <= 120 && CompletedOccurrences is >= 0 and <= 10_000, "invalid_recurrence", "Maintenance recurrence is out of range.");
        DateOnly expected;
        try { expected = StartDate.AddMonths(checked(CompletedOccurrences * IntervalMonths)); }
        catch (ArgumentOutOfRangeException) { throw new DomainException("recurrence_overflow", "The maintenance recurrence exceeds the supported date range."); }
        Guard.Require(NextDueDate == expected, "invalid_recurrence", "Next due date must match the original anchor and completed occurrence count.");
    }
    public override IEnumerable<EntityReference> References() => [BusinessRegistry.Reference<Customer>(CustomerId), BusinessRegistry.Reference<Building>(BuildingId)];
}

public sealed class NotificationPreference : TenantEntity
{
    public Guid UserId { get; set; }
    public bool MorningBriefing { get; set; } = true;
    public TimeOnly LocalTime { get; set; } = new(7, 0);
    public string TimeZone { get; set; } = "Europe/Berlin";
    public List<CommunicationChannel> Channels { get; set; } = [];
    public List<DayOfWeek> Days { get; set; } = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    public bool IncludeYesterday { get; set; } = true;
    public bool IncludeToday { get; set; } = true;
    public bool IncludeMaintenance { get; set; } = true;
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(UserId, nameof(UserId)); Guard.Text(TimeZone, nameof(TimeZone), 100);
        Guard.Require(TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out _), "unknown_timezone", "The notification timezone is not recognized.");
        Guard.Items(Channels, nameof(Channels), 3); Guard.Items(Days, nameof(Days), 7);
        Guard.Require(Channels.All(Enum.IsDefined) && Channels.Distinct().Count() == Channels.Count && Days.All(Enum.IsDefined) && Days.Distinct().Count() == Days.Count,
            "invalid_notification_preferences", "Notification channels and weekdays must be valid and unique.");
    }
}

public static class OperationsRules
{
    public static void EnsureAcceptance(Acceptance acceptance, IEnumerable<Defect> projectDefects)
    {
        acceptance.Validate();
        var defects = projectDefects.Where(x => x.TenantId == acceptance.TenantId && x.ProjectId == acceptance.ProjectId && !x.Archived).ToList();
        Guard.Require(acceptance.DefectIds.All(id => defects.Any(x => x.Id == id)), "wrong_project_defect", "An acceptance references a defect outside its project.");
        var open = defects.Where(x => x.Status is DefectStatus.Open or DefectStatus.InProgress or DefectStatus.Resolved).Select(x => x.Id).ToHashSet();
        Guard.Require(open.Count == 0 || (acceptance.WithReservations && open.IsSubsetOf(acceptance.DefectIds)),
            "unacknowledged_defects", "All unresolved or unaccepted project defects must be recorded as reservations.");
    }

    public static string MaintenanceOccurrenceKey(MaintenanceContract contract) => $"maintenance:{contract.Id:N}:{contract.NextDueDate:yyyy-MM-dd}";

    /// <summary>Advance from the original anchor to avoid February/month-end drift. Persist occurrence and advance atomically.</summary>
    public static DateOnly CompleteMaintenance(MaintenanceContract contract, DateOnly expectedDueDate)
    {
        contract.Validate(); Guard.Require(contract.Active && contract.NextDueDate == expectedDueDate, "stale_occurrence", "The maintenance occurrence is no longer current.");
        Guard.Require(!contract.EndDate.HasValue || expectedDueDate <= contract.EndDate, "contract_expired", "The maintenance contract has ended.");
        var nextCount = checked(contract.CompletedOccurrences + 1);
        DateOnly next;
        try { next = contract.StartDate.AddMonths(checked(nextCount * contract.IntervalMonths)); }
        catch (ArgumentOutOfRangeException) { throw new DomainException("recurrence_overflow", "The next maintenance date exceeds the supported date range."); }
        Guard.Require(next > contract.NextDueDate, "invalid_recurrence", "The recurrence anchor and occurrence counter do not agree.");
        contract.CompletedOccurrences = nextCount; contract.NextDueDate = next;
        if (contract.EndDate.HasValue && next > contract.EndDate) contract.Active = false;
        return next;
    }

    public static bool BriefingDue(NotificationPreference preference, DateTimeOffset utcNow)
    {
        preference.Validate(); Guard.Utc(utcNow, nameof(utcNow));
        if (!preference.MorningBriefing || preference.Channels.Count == 0) return false;
        var local = TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.FindSystemTimeZoneById(preference.TimeZone));
        return preference.Days.Contains(local.DayOfWeek) && TimeOnly.FromDateTime(local.DateTime) >= preference.LocalTime;
    }

    /// <summary>Unique per user, tenant and local date: the worker must insert this key with a database uniqueness constraint.</summary>
    public static string BriefingOccurrenceKey(NotificationPreference preference, DateTimeOffset utcNow)
    {
        preference.Validate(); Guard.Utc(utcNow, nameof(utcNow));
        var local = TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.FindSystemTimeZoneById(preference.TimeZone));
        return $"briefing:{preference.TenantId:N}:{preference.UserId:N}:{local:yyyy-MM-dd}";
    }
}
