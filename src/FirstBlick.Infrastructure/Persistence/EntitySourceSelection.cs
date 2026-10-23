using FirstBlick.Core.Business;

namespace FirstBlick.Infrastructure.Persistence;

// These selectors are internal application contracts, never HTTP field names or SQL fragments.
public enum EntitySelectionMode { All, OwnEmployee, Employees, Training, EmployeeWork, Assignments, ProjectWork, Tasks, SubmittedForms, UserPreference, Ids }

public sealed class EntitySourceSelection(EntitySelectionMode mode, Guid[] ids, Guid[] projectIds, Guid userId = default)
{
    public EntitySelectionMode Mode { get; } = mode;
    public IReadOnlySet<Guid> Ids { get; } = ids.ToHashSet();
    public IReadOnlySet<Guid> ProjectIds { get; } = projectIds.ToHashSet();
    public Guid UserId { get; } = userId;
    public void EnsureKind(string kind) => Guard.Require(Mode switch
    {
        EntitySelectionMode.All or EntitySelectionMode.Ids => true,
        EntitySelectionMode.OwnEmployee or EntitySelectionMode.Employees => kind == "employees",
        EntitySelectionMode.Training => kind == "training-records",
        EntitySelectionMode.EmployeeWork => kind is "time-entries" or "diary-entries",
        EntitySelectionMode.Assignments => kind == "schedule-assignments",
        EntitySelectionMode.ProjectWork => kind is "maintenance-visits" or "defects",
        EntitySelectionMode.Tasks => kind == "tasks",
        EntitySelectionMode.SubmittedForms => kind == "form-submissions",
        EntitySelectionMode.UserPreference => kind == "notification-preferences",
        _ => false
    }, "source_selection", "The selector is not defined for this source kind.");

    public bool Matches(TenantEntity entity) => Mode switch
    {
        EntitySelectionMode.All => true,
        EntitySelectionMode.Ids => Ids.Contains(entity.Id),
        EntitySelectionMode.OwnEmployee => entity is Employee e && e.UserId == UserId && e.Active,
        EntitySelectionMode.Employees => entity is Employee e && (Ids.Contains(e.Id) || e.MentorEmployeeId is { } mentor && Ids.Contains(mentor)),
        EntitySelectionMode.Training => entity is TrainingRecord t && (Ids.Contains(t.ApprenticeEmployeeId) || Ids.Contains(t.MentorEmployeeId)),
        EntitySelectionMode.EmployeeWork => entity switch { TimeEntry t => Ids.Contains(t.EmployeeId), DiaryEntry d => Ids.Contains(d.EmployeeId), _ => false },
        EntitySelectionMode.Assignments => entity is ScheduleAssignment a && !a.Cancelled && Ids.Contains(a.EmployeeId),
        EntitySelectionMode.ProjectWork => entity switch { MaintenanceVisit v => ProjectIds.Contains(v.ProjectId), Defect d => ProjectIds.Contains(d.ProjectId), _ => false },
        EntitySelectionMode.Tasks => entity is TaskItem t && (ProjectIds.Contains(t.ProjectId) || t.AssignedEmployeeId is { } employee && Ids.Contains(employee)),
        EntitySelectionMode.SubmittedForms => entity is FormSubmission f && f.SubmittedBy == UserId,
        EntitySelectionMode.UserPreference => entity is NotificationPreference p && p.UserId == UserId,
        _ => false
    };
}

/// <summary>Counts actual source materializations across the scoped DbContext; repeated reads also consume work.</summary>
public sealed class EntitySourceBudget
{
    public int RemainingRecords { get; private set; } = EntitySources.RecordLimit;
    public long RemainingBytes { get; private set; } = EntitySources.ByteLimit;

    public void Consume(int records, long bytes)
    {
        Guard.Require(records >= 0 && bytes >= 0 && records <= RemainingRecords && bytes <= RemainingBytes,
            "source_request_budget", "Combined source reads exceed 10,000 records or 12 MiB in this operation. No partial result was used.");
        RemainingRecords -= records; RemainingBytes -= bytes;
    }
}
