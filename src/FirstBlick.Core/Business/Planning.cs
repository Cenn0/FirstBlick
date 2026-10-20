namespace FirstBlick.Core.Business;

public sealed class Employee : TenantEntity
{
    public Guid? UserId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public bool Active { get; set; } = true;
    public bool IsApprentice { get; set; }
    public DateOnly? TrainingStart { get; set; }
    public DateOnly? TrainingEnd { get; set; }
    public Guid? MentorEmployeeId { get; set; }
    public decimal WeeklyHours { get; set; } = 40;
    public List<string> Qualifications { get; set; } = [];
    public override void Validate()
    {
        ValidateCommon(); Guard.OptionalId(UserId, nameof(UserId)); Guard.Text(Name, nameof(Name)); Guard.Email(Email);
        Guard.Text(Phone, nameof(Phone), 80, false); Guard.Range(WeeklyHours, 0, 80, nameof(WeeklyHours));
        Guard.OptionalId(MentorEmployeeId, nameof(MentorEmployeeId)); Guard.Require(MentorEmployeeId != Id, "self_mentor", "An employee cannot mentor themselves.");
        Guard.Items(Qualifications, nameof(Qualifications), 100); foreach (var qualification in Qualifications) Guard.Text(qualification, "Qualification", 120);
        Guard.Require(!TrainingStart.HasValue || !TrainingEnd.HasValue || TrainingEnd >= TrainingStart, "invalid_window", "Training end cannot precede training start.");
        Guard.Require(IsApprentice || (!TrainingStart.HasValue && !TrainingEnd.HasValue && !MentorEmployeeId.HasValue), "invalid_training", "Training dates and mentor belong to an apprentice record.");
    }
    public override IEnumerable<EntityReference> References() => MentorEmployeeId is { } id ? [BusinessRegistry.Reference<Employee>(id)] : [];
}

public sealed record TimeBreak(DateTimeOffset Start, DateTimeOffset End);
public sealed class TimeEntry : TenantEntity
{
    public Guid EmployeeId { get; set; }
    public Guid ProjectId { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public string Activity { get; set; } = "";
    public List<TimeBreak> Breaks { get; set; } = [];
    public bool Approved { get; set; }
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(EmployeeId, nameof(EmployeeId)); Guard.Id(ProjectId, nameof(ProjectId)); Guard.Window(Start, End);
        Guard.Require(End - Start <= TimeSpan.FromHours(24), "time_budget", "One time entry can cover at most 24 elapsed hours.");
        Guard.Text(Activity, nameof(Activity), 2000); Guard.Items(Breaks, nameof(Breaks), 100);
        foreach (var pause in Breaks)
        {
            Guard.Window(pause.Start, pause.End); Guard.Require(pause.Start >= Start && pause.End <= End, "break_outside_entry", "Breaks must lie within the time entry.");
        }
        var ordered = Breaks.OrderBy(x => x.Start).ToList();
        Guard.Require(!ordered.Zip(ordered.Skip(1)).Any(x => x.First.End > x.Second.Start), "overlapping_breaks", "Breaks cannot overlap.");
        Guard.Require(Breaks.Sum(x => (x.End - x.Start).Ticks) < (End - Start).Ticks, "empty_work", "Breaks cannot consume the entire time entry.");
    }
    public decimal NetHours()
    {
        Validate(); var ticks = (End - Start).Ticks - Breaks.Sum(x => (x.End - x.Start).Ticks);
        return decimal.Round((decimal)ticks / TimeSpan.TicksPerHour, 6, MidpointRounding.AwayFromZero);
    }
    public override IEnumerable<EntityReference> References() => [BusinessRegistry.Reference<Employee>(EmployeeId), BusinessRegistry.Reference<Project>(ProjectId)];
}

public enum AbsenceKind { Vacation, Sick, Training, Other }
public enum AbsenceStatus { Requested, Approved, Rejected, Cancelled }
public sealed class Absence : TenantEntity
{
    public Guid EmployeeId { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public AbsenceKind Kind { get; set; }
    public AbsenceStatus Status { get; set; }
    public string Note { get; set; } = "";
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(EmployeeId, nameof(EmployeeId)); Guard.Window(Start, End); Guard.Text(Note, nameof(Note), 2000, false);
        Guard.Require(End - Start <= TimeSpan.FromDays(366), "absence_budget", "One absence can cover at most 366 elapsed days.");
        Guard.Require(Enum.IsDefined(Kind) && Enum.IsDefined(Status), "invalid_status", "Absence kind or status is invalid.");
    }
    public override IEnumerable<EntityReference> References() => [BusinessRegistry.Reference<Employee>(EmployeeId)];
}

public sealed class ScheduleAssignment : TenantEntity
{
    public Guid ProjectId { get; set; }
    public Guid EmployeeId { get; set; }
    public List<Guid> EquipmentIds { get; set; } = [];
    public string Title { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public bool Cancelled { get; set; }
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(ProjectId, nameof(ProjectId)); Guard.Id(EmployeeId, nameof(EmployeeId)); Guard.Text(Title, nameof(Title)); Guard.Window(Start, End);
        Guard.Require(End - Start <= TimeSpan.FromDays(31), "schedule_budget", "One assignment can cover at most 31 elapsed days.");
        Guard.Items(EquipmentIds, nameof(EquipmentIds), 100); foreach (var id in EquipmentIds) Guard.Id(id, "EquipmentId");
        Guard.Require(EquipmentIds.Distinct().Count() == EquipmentIds.Count, "duplicate_equipment", "Equipment may appear only once in an assignment.");
    }
    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Project>(ProjectId); yield return BusinessRegistry.Reference<Employee>(EmployeeId);
        foreach (var id in EquipmentIds) yield return BusinessRegistry.Reference<Equipment>(id);
    }
}

public static class PlanningRules
{
    // Half-open intervals permit consecutive assignments without a false collision.
    public static bool Overlap(DateTimeOffset aStart, DateTimeOffset aEnd, DateTimeOffset bStart, DateTimeOffset bEnd) => aStart < bEnd && bStart < aEnd;

    public static void EnsureAssignment(ScheduleAssignment candidate, IEnumerable<ScheduleAssignment> assignments, IEnumerable<Absence> absences)
    {
        candidate.Validate(); if (candidate.Cancelled || candidate.Archived) return;
        foreach (var other in assignments.Where(x => x.TenantId == candidate.TenantId && x.Id != candidate.Id && !x.Cancelled && !x.Archived))
        {
            other.Validate();
            Guard.Require(!Overlap(candidate.Start, candidate.End, other.Start, other.End) ||
                (other.EmployeeId != candidate.EmployeeId && !other.EquipmentIds.Intersect(candidate.EquipmentIds).Any()),
                "planning_conflict", "An employee or equipment resource is already assigned in this time window.");
        }
        foreach (var absence in absences.Where(x => x.TenantId == candidate.TenantId && x.EmployeeId == candidate.EmployeeId && x.Status == AbsenceStatus.Approved && !x.Archived))
        {
            absence.Validate();
            Guard.Require(!Overlap(candidate.Start, candidate.End, absence.Start, absence.End), "absence_conflict", "The employee has an approved absence in this time window.");
        }
    }

    public static void EnsureAbsence(Absence candidate, IEnumerable<ScheduleAssignment> assignments, IEnumerable<Absence> absences)
    {
        candidate.Validate(); if (candidate.Status != AbsenceStatus.Approved || candidate.Archived) return;
        foreach (var assignment in assignments.Where(x => x.TenantId == candidate.TenantId && x.EmployeeId == candidate.EmployeeId && !x.Cancelled && !x.Archived))
        {
            assignment.Validate();
            Guard.Require(!Overlap(candidate.Start, candidate.End, assignment.Start, assignment.End), "planning_conflict", "Reschedule the existing assignment before approving this absence.");
        }
        foreach (var other in absences.Where(x => x.TenantId == candidate.TenantId && x.EmployeeId == candidate.EmployeeId && x.Id != candidate.Id && x.Status == AbsenceStatus.Approved && !x.Archived))
        {
            other.Validate();
            Guard.Require(!Overlap(candidate.Start, candidate.End, other.Start, other.End), "absence_conflict", "Approved absence windows cannot overlap.");
        }
    }

    public static void EnsureTimeEntry(TimeEntry candidate, IEnumerable<TimeEntry> entries)
    {
        candidate.Validate();
        Guard.Require(!entries.Any(x => x.TenantId == candidate.TenantId && x.EmployeeId == candidate.EmployeeId && x.Id != candidate.Id &&
            !x.Archived && Overlap(candidate.Start, candidate.End, x.Start, x.End)), "time_conflict", "The employee has another time entry in this window.");
    }
}
