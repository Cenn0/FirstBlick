using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

internal static class RecordPolicy
{
    public static async Task<Guid[]?> VisibleIds(string kind, TenantScope scope, EntityStore store, CancellationToken ct)
    {
        if (kind == "notification-preferences") return (await store.All<NotificationPreference>(ct)).Where(x => x.UserId == scope.UserId).Select(x => x.Id).ToArray();
        if (scope.Role is not BusinessRole.Worker and not BusinessRole.Trainer) return null;
        var employees = await store.All<Employee>(ct);
        var own = employees.Where(x => x.UserId == scope.UserId && x.Active).Select(x => x.Id).ToHashSet();
        if (kind == "employees") return employees.Where(x => own.Contains(x.Id) || (scope.Role == BusinessRole.Trainer && x.MentorEmployeeId.HasValue && own.Contains(x.MentorEmployeeId.Value))).Select(x => x.Id).ToArray();
        if (kind == "training-records") return (await store.All<TrainingRecord>(ct)).Where(x => own.Contains(x.ApprenticeEmployeeId) || own.Contains(x.MentorEmployeeId)).Select(x => x.Id).ToArray();
        if (kind == "time-entries") return (await store.All<TimeEntry>(ct)).Where(x => own.Contains(x.EmployeeId)).Select(x => x.Id).ToArray();
        if (kind == "diary-entries") return (await store.All<DiaryEntry>(ct)).Where(x => own.Contains(x.EmployeeId)).Select(x => x.Id).ToArray();
        if (kind == "form-submissions") return (await store.All<FormSubmission>(ct)).Where(x => x.SubmittedBy == scope.UserId).Select(x => x.Id).ToArray();
        var assignments = (await store.All<ScheduleAssignment>(ct)).Where(x => own.Contains(x.EmployeeId) && !x.Cancelled).ToList();
        var projectIds = assignments.Select(x => x.ProjectId).ToHashSet();
        if (kind == "schedule-assignments") return assignments.Select(x => x.Id).ToArray();
        if (kind == "projects") return projectIds.ToArray();
        if (kind == "buildings") return (await store.All<Project>(ct)).Where(x => projectIds.Contains(x.Id)).Select(x => x.BuildingId).ToArray();
        if (kind == "tasks") return (await store.All<TaskItem>(ct)).Where(x => projectIds.Contains(x.ProjectId) || (x.AssignedEmployeeId.HasValue && own.Contains(x.AssignedEmployeeId.Value))).Select(x => x.Id).ToArray();
        if (kind == "defects") return (await store.All<Defect>(ct)).Where(x => projectIds.Contains(x.ProjectId)).Select(x => x.Id).ToArray();
        return kind is "materials" or "equipment" or "form-definitions" ? null : [];
    }

    public static async Task EnsureVisible(string kind, Guid id, TenantScope scope, EntityStore store, CancellationToken ct)
    {
        var visible = await VisibleIds(kind, scope, store, ct);
        Guard.Require(visible is null || visible.Contains(id), "not_found", "The record does not exist or is outside your assigned work.");
    }

    public static async Task EnsureWrite(TenantEntity entity, TenantScope scope, EntityStore store, FirstBlickDbContext db, CancellationToken ct)
    {
        if (entity is Defect { Status: DefectStatus.Accepted })
            Guard.Require(scope.Role is BusinessRole.ManagingDirector or BusinessRole.SiteManager, "forbidden", "Final defect acceptance requires site management.");
        if (entity is NotificationPreference pref)
        {
            Guard.Require(pref.UserId == scope.UserId, "forbidden", "Notification preferences belong to your personal account.");
            return;
        }
        if (entity is Employee e && e.UserId.HasValue)
        {
            Guard.Require(scope.Role == BusinessRole.ManagingDirector, "forbidden", "Only a managing director may link a personal account to staff records.");
            Guard.Require(await db.Memberships.AnyAsync(x => x.TenantId == scope.TenantId && x.UserId == e.UserId && x.Active, ct), "invalid_reference", "The personal account must be an active member of this business.");
        }
        if (entity is TimeEntry { Approved: true } time)
            Guard.Require(scope.Role is BusinessRole.ManagingDirector or BusinessRole.SiteManager, "forbidden", "Time approval requires a managing director or site manager.");
        if (entity is TrainingRecord record)
        {
            var apprentice = await store.Get<Employee>(record.ApprenticeEmployeeId, ct);
            var mentor = await store.Get<Employee>(record.MentorEmployeeId, ct);
            if (record.Status == TrainingRecordStatus.Reviewed)
            {
                Guard.Require(mentor.UserId == scope.UserId, "forbidden", "Only the assigned mentor may review this record.");
                record.ReviewedAt = DateTimeOffset.UtcNow;
            }
            else Guard.Require(apprentice.UserId == scope.UserId || mentor.UserId == scope.UserId || scope.Role == BusinessRole.ManagingDirector,
                "forbidden", "The training record belongs to another apprentice and mentor.");
            TrainingRules.EnsurePeople(record, apprentice, mentor);
        }
        if (scope.Role != BusinessRole.Worker) return;
        var employee = (await store.All<Employee>(ct)).SingleOrDefault(x => x.UserId == scope.UserId && x.Active);
        Guard.Require(employee is not null, "forbidden", "An active staff record is required for field work.");
        var employeeId = entity switch { TimeEntry t => t.EmployeeId, DiaryEntry d => d.EmployeeId, _ => employee!.Id };
        Guard.Require(employeeId == employee!.Id, "forbidden", "Field workers may record only their own work.");
        if (entity is TimeEntry ownTime) Guard.Require(!ownTime.Approved, "forbidden", "Field workers cannot approve their own time.");
        if (entity is Defect defect) Guard.Require(defect.Status is DefectStatus.Open or DefectStatus.InProgress or DefectStatus.Resolved, "forbidden", "Final defect acceptance requires site management.");
        var projectId = entity switch { TimeEntry t => t.ProjectId, DiaryEntry d => d.ProjectId, Defect d => d.ProjectId, FormSubmission f => f.ProjectId, _ => Guid.Empty };
        if (projectId != Guid.Empty) await EnsureVisible("projects", projectId, scope, store, ct);
    }
}

internal static class GraphRules
{
    public static async Task Validate(TenantEntity entity, EntityStore store, FirstBlickDbContext db, TenantScope scope, CancellationToken ct)
    {
        entity.Validate();
        switch (entity)
        {
            case Building building:
                Guard.Require((await store.All<Project>(ct)).All(p => p.BuildingId != building.Id || p.CustomerId == building.CustomerId), "customer_mismatch", "Existing projects prevent changing this building to another customer."); break;
            case Project project:
                var projectBuilding = await store.Get<Building>(project.BuildingId, ct);
                Guard.Require(projectBuilding.CustomerId == project.CustomerId, "customer_mismatch", "The project customer must own its building record.");
                Guard.Require((await store.All<Quote>(ct)).All(q => q.ProjectId != project.Id || q.CustomerId == project.CustomerId), "customer_mismatch", "Existing quotes prevent this customer change.");
                foreach (var assignment in (await store.All<ScheduleAssignment>(ct)).Where(x => x.ProjectId == project.Id && !x.Cancelled))
                    await ValidateWindow(project, assignment, db, scope, ct);
                break;
            case Quote quote:
                Guard.Require((await store.Get<Project>(quote.ProjectId, ct)).CustomerId == quote.CustomerId, "customer_mismatch", "Quote and project customers must match."); break;
            case Invoice invoice:
                var order = await store.Get<Order>(invoice.OrderId, ct);
                Guard.Require(order.CustomerId == invoice.CustomerId, "customer_mismatch", "Invoice and order customers must match."); break;
            case ScheduleAssignment assignment:
                Guard.Require((await store.Get<Employee>(assignment.EmployeeId, ct)).Active, "inactive_employee", "An inactive employee cannot be scheduled.");
                foreach (var equipmentId in assignment.EquipmentIds) Guard.Require((await store.Get<Equipment>(equipmentId, ct)).Available, "equipment_unavailable", "A selected resource is unavailable.");
                await ValidateWindow(await store.Get<Project>(assignment.ProjectId, ct), assignment, db, scope, ct);
                PlanningRules.EnsureAssignment(assignment, await store.All<ScheduleAssignment>(ct), await store.All<Absence>(ct)); break;
            case Absence absence:
                PlanningRules.EnsureAbsence(absence, await store.All<ScheduleAssignment>(ct), await store.All<Absence>(ct)); break;
            case TimeEntry entry:
                Guard.Require((await store.Get<Employee>(entry.EmployeeId, ct)).Active, "inactive_employee", "An inactive employee cannot record new time.");
                PlanningRules.EnsureTimeEntry(entry, await store.All<TimeEntry>(ct)); break;
            case MaterialReservation reservation:
                InventoryRules.Reserve(reservation, await store.All<StockMovement>(ct), await store.All<MaterialReservation>(ct)); break;
            case Acceptance acceptance:
                OperationsRules.EnsureAcceptance(acceptance, await store.All<Defect>(ct)); break;
            case MaintenanceContract contract:
                Guard.Require((await store.Get<Building>(contract.BuildingId, ct)).CustomerId == contract.CustomerId, "customer_mismatch", "Maintenance customer and building must match."); break;
        }
    }

    private static async Task ValidateWindow(Project project, ScheduleAssignment assignment, FirstBlickDbContext db, TenantScope scope, CancellationToken ct)
    {
        if (assignment.Cancelled || assignment.Archived) return;
        Guard.Require(project.Status != ProjectStatus.Cancelled, "planning_conflict", "Cancel or move existing assignments before cancelling a project.");
        var zoneId = await db.Tenants.Where(x => x.Id == scope.TenantId).Select(x => x.TimeZone).SingleAsync(ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(assignment.Start, zone).DateTime);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(assignment.End.AddTicks(-1), zone).DateTime);
        Guard.Require((!project.PlannedStart.HasValue || first >= project.PlannedStart) && (!project.PlannedEnd.HasValue || last <= project.PlannedEnd),
            "planning_conflict", $"Assignment {assignment.Id} lies outside the project dates. Resolve it explicitly before changing the project window.");
    }
}
