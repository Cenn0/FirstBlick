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
        if (kind == "maintenance-visits") return (await store.All<MaintenanceVisit>(ct)).Where(x => projectIds.Contains(x.ProjectId)).Select(x => x.Id).ToArray();
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
            case RoofRevision roof:
                _ = await store.Get<Building>(roof.BuildingId, ct);
                if (roof.ProjectId is { } roofProject)
                    Guard.Require((await store.Get<Project>(roofProject, ct)).BuildingId == roof.BuildingId, "roof_building_mismatch", "The roof and project must refer to the same building.");
                if (roof.PreviousRevisionId is { } predecessor)
                {
                    var previousRoof = await store.Get<RoofRevision>(predecessor, ct);
                    Guard.Require(previousRoof.BuildingId == roof.BuildingId && previousRoof.Status == RoofRevisionStatus.Published && previousRoof.Version + 1 == roof.Version,
                        "wrong_predecessor", "The previous published revision must belong to this building and immediately precede the new version.");
                }
                break;
            case Measurement measurement:
                var measurementRoof = await store.Get<RoofRevision>(measurement.RoofRevisionId, ct);
                var measurementProject = await store.Get<Project>(measurement.ProjectId, ct);
                Guard.Require(measurementRoof.Status == RoofRevisionStatus.Published && measurementRoof.BuildingId == measurementProject.BuildingId && (!measurementRoof.ProjectId.HasValue || measurementRoof.ProjectId == measurement.ProjectId),
                    "wrong_measurement_roof", "The measurement requires a published roof belonging to this project's building."); break;
            case TechnicalRecord calculation:
                var calculationProject = await store.Get<Project>(calculation.ProjectId, ct);
                Guard.Require(calculation.BuildingId == calculationProject.BuildingId, "wrong_calculation_building", "The stored calculation preserves its project's building context.");
                if (calculation.RoofRevisionId is { } calculationRoofId)
                {
                    var calculationRoof = await store.Get<RoofRevision>(calculationRoofId, ct);
                    Guard.Require(calculationRoof.Status == RoofRevisionStatus.Published && calculationRoof.BuildingId == calculationProject.BuildingId && (!calculationRoof.ProjectId.HasValue || calculationRoof.ProjectId == calculation.ProjectId),
                        "wrong_calculation_roof", "The calculation requires a published roof of this project's building.");
                }
                break;
            case Building building:
                Guard.Require((await store.All<Project>(ct)).All(p => p.BuildingId != building.Id || p.CustomerId == building.CustomerId), "customer_mismatch", "Existing projects prevent changing this building to another customer."); break;
            case Project project:
                var maintenanceProjectLinks = db.Links.Where(x => x.SourceKind == "maintenance-visits" && x.TargetKind == "projects" && x.TargetId == project.Id);
                var maintenanceProjectRows = await db.Entities.Where(x => x.Kind == "maintenance-visits" && maintenanceProjectLinks.Any(l => l.SourceId == x.Id)).Select(x => x.Data).ToListAsync(ct);
                foreach (var row in maintenanceProjectRows)
                {
                    var visit = AppJson.Read<MaintenanceVisit>(row); visit.Validate();
                    Guard.Require(project.Type == ProjectType.Maintenance && project.CustomerId == visit.Terms!.CustomerId && project.BuildingId == visit.Terms.BuildingId
                        && (visit.Status != MaintenanceVisitStatus.Completed || project.Status == ProjectStatus.Completed),
                        "maintenance_project_mismatch", "Maintenance projects retain the original occurrence and completed history.");
                }
                var projectBuilding = await store.Get<Building>(project.BuildingId, ct);
                Guard.Require(projectBuilding.CustomerId == project.CustomerId, "customer_mismatch", "The project customer must own its building record.");
                Guard.Require((await store.All<Quote>(ct)).All(q => q.ProjectId != project.Id || q.CustomerId == project.CustomerId), "customer_mismatch", "Existing quotes prevent this customer change.");
                Guard.Require((await store.All<RoofRevision>(ct)).All(r => r.ProjectId != project.Id || r.BuildingId == project.BuildingId), "project_roof_mismatch", "Existing roof revisions prevent moving this project to another building.");
                foreach (var existingMeasurement in (await store.All<Measurement>(ct)).Where(m => m.ProjectId == project.Id))
                    Guard.Require((await store.Get<RoofRevision>(existingMeasurement.RoofRevisionId, ct)).BuildingId == project.BuildingId, "project_roof_mismatch", "Existing measurements preserve this project's building context.");
                Guard.Require((await store.All<TechnicalRecord>(ct)).All(c => c.ProjectId != project.Id || c.BuildingId == project.BuildingId), "project_roof_mismatch", "Stored technical results preserve this project's building context.");
                foreach (var assignment in (await store.All<ScheduleAssignment>(ct)).Where(x => x.ProjectId == project.Id && !x.Cancelled))
                    await ValidateWindow(project, assignment, db, scope, ct);
                break;
            case Quote quote:
                Guard.Require((await store.Get<Project>(quote.ProjectId, ct)).CustomerId == quote.CustomerId, "customer_mismatch", "Quote and project customers must match.");
                foreach (var line in quote.Lines.Where(x => x.QuantityEvidence is not null))
                    QuantityLinks.EnsureSource(line.QuantityEvidence!, await store.Get<Measurement>(line.QuantityEvidence!.MeasurementId, ct), scope.TenantId, quote.ProjectId);
                break;
            case ChangeOrder change:
                var changeProjectId = (await store.Get<Order>(change.OrderId, ct)).ProjectId;
                foreach (var line in change.Lines.Where(x => x.QuantityEvidence is not null))
                    QuantityLinks.EnsureSource(line.QuantityEvidence!, await store.Get<Measurement>(line.QuantityEvidence!.MeasurementId, ct), scope.TenantId, changeProjectId);
                break;
            case Material material:
                var storedMaterial = await db.Entities.SingleOrDefaultAsync(x => x.Kind == "materials" && x.Id == material.Id, ct);
                if (storedMaterial is not null && AppJson.Read<Material>(storedMaterial.Data).Unit != material.Unit)
                    Guard.Require(!await db.Links.AnyAsync(x => x.TargetKind == "materials" && x.TargetId == material.Id, ct),
                        "material_unit_in_use", "A material used in purchasing, stock or demand retains its unit. Create a separate item for a different unit.");
                break;
            case MaterialDemand demand:
                QuantityLinks.EnsureSource(demand.Evidence, await store.Get<Measurement>(demand.Evidence.MeasurementId, ct), scope.TenantId, demand.ProjectId);
                Guard.Require((await store.Get<Material>(demand.MaterialId, ct)).Unit == demand.Evidence.TargetUnit, "quantity_target_changed", "The demand unit must match its material.");
                break;
            case PurchaseOrder purchase:
                foreach (var purchaseLine in purchase.Lines.Where(x => x.MaterialDemandId.HasValue))
                {
                    var linked = await store.Get<MaterialDemand>(purchaseLine.MaterialDemandId!.Value, ct);
                    Guard.Require(linked.Status == MaterialDemandStatus.Converted && linked.PurchaseOrderId == purchase.Id && linked.PurchaseLineId == purchaseLine.Id
                        && linked.ProjectId == purchase.ProjectId && linked.MaterialId == purchaseLine.MaterialId && linked.Evidence == purchaseLine.QuantityEvidence,
                        "purchase_demand_changed", "The purchase line must retain its reviewed demand, project, material and quantity evidence.");
                }
                foreach (var linked in (await store.All<MaterialDemand>(ct)).Where(x => x.PurchaseOrderId == purchase.Id))
                    Guard.Require(purchase.Lines.Any(x => x.Id == linked.PurchaseLineId && x.MaterialDemandId == linked.Id),
                        "purchase_demand_changed", "A converted demand cannot be removed silently from its purchase draft.");
                break;
            case Invoice invoice:
                var order = await store.Get<Order>(invoice.OrderId, ct);
                Guard.Require(order.CustomerId == invoice.CustomerId, "customer_mismatch", "Invoice and order customers must match."); break;
            case ScheduleAssignment assignment:
                // Cancellation releases an existing slot even if a person or vehicle became unavailable.
                // Retain the references, but apply availability checks only to live assignments.
                var assignedEmployee = await store.Get<Employee>(assignment.EmployeeId, ct);
                Guard.Require(assignment.Cancelled || assignedEmployee.Active, "inactive_employee", "An inactive employee cannot be scheduled.");
                foreach (var equipmentId in assignment.EquipmentIds)
                    Guard.Require((await store.Get<Equipment>(equipmentId, ct)).Available || assignment.Cancelled,
                        "equipment_unavailable", "A selected resource is unavailable.");
                await ValidateWindow(await store.Get<Project>(assignment.ProjectId, ct), assignment, db, scope, ct);
                PlanningRules.EnsureAssignment(assignment, await store.All<ScheduleAssignment>(ct), await store.All<Absence>(ct)); break;
            case Absence absence:
                PlanningRules.EnsureAbsence(absence, await store.All<ScheduleAssignment>(ct), await store.All<Absence>(ct)); break;
            case DiaryEntry diary:
                Guard.Require((await store.Get<Employee>(diary.EmployeeId, ct)).Active, "inactive_employee", "An inactive employee cannot record new diary work."); break;
            case TimeEntry entry:
                Guard.Require((await store.Get<Employee>(entry.EmployeeId, ct)).Active, "inactive_employee", "An inactive employee cannot record new time.");
                PlanningRules.EnsureTimeEntry(entry, await store.All<TimeEntry>(ct)); break;
            case MaterialReservation reservation:
                InventoryRules.Reserve(reservation, await store.All<StockMovement>(ct), await store.All<MaterialReservation>(ct)); break;
            case Acceptance acceptance:
                OperationsRules.EnsureAcceptance(acceptance, await store.All<Defect>(ct)); break;
            case MaintenanceContract contract:
                Guard.Require((await store.Get<Building>(contract.BuildingId, ct)).CustomerId == contract.CustomerId, "customer_mismatch", "Maintenance customer and building must match.");
                var contractLinks = db.Links.Where(x => x.SourceKind == "maintenance-visits" && x.TargetKind == "maintenance-contracts" && x.TargetId == contract.Id);
                var contractRows = await db.Entities.Where(x => x.Kind == "maintenance-visits" && contractLinks.Any(l => l.SourceId == x.Id)).Select(x => x.Data).ToListAsync(ct);
                foreach (var row in contractRows)
                {
                    var visit = AppJson.Read<MaintenanceVisit>(row); visit.Validate();
                    Guard.Require(contract.CustomerId == visit.Terms!.CustomerId && contract.BuildingId == visit.Terms.BuildingId
                        && contract.StartDate == visit.Terms.StartDate && contract.IntervalMonths == visit.Terms.IntervalMonths,
                        "maintenance_recurrence_in_use", "Planned maintenance retains its customer, building and recurrence anchor. Create a new contract for changed terms.");
                }
                break;
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
