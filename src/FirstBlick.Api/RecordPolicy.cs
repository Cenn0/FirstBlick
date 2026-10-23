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
            case TaskItem task:
                if (task.TemplateOrigin is not null)
                    ProjectTemplateWorkflow.EnsureTaskOrigin(task, await store.Get<ProjectTemplateApplication>(task.TemplateOrigin.ApplicationId, ct));
                await TaskDependencyEndpoints.ValidateMutation(task, store, db, scope, ct);
                break;
            case Equipment equipment:
                Guard.Require(equipment.Kind == EquipmentKind.Vehicle || !await db.Links.AnyAsync(x => x.TargetKind == "equipment"
                    && x.TargetId == equipment.Id && (x.SourceKind == "vehicle-trips" || x.SourceKind == "vehicle-costs" || x.SourceKind == "vehicle-ledger-reversals"), ct),
                    "vehicle_kind_in_use", "Equipment with retained vehicle records must remain a vehicle.");
                break;
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
                Guard.Require((await Related<Project>(db, scope, "buildings", building.Id, ct)).All(p => p.BuildingId != building.Id || p.CustomerId == building.CustomerId), "customer_mismatch", "Existing projects prevent changing this building to another customer."); break;
            case Project project:
                var communicationCustomerMismatch = await db.Database.SqlQueryRaw<bool>("""
                    SELECT EXISTS (SELECT 1 FROM "Entities" e
                      JOIN "Links" l ON l."TenantId"=e."TenantId" AND l."SourceKind"=e."Kind" AND l."SourceId"=e."Id"
                      WHERE e."TenantId"=@tenant AND e."Kind"='customer-communications'
                        AND l."TargetKind"='projects' AND l."TargetId"=@project
                        AND e."Data"->>'customerId'<>@customer) AS "Value"
                    """, new Npgsql.NpgsqlParameter("tenant", scope.TenantId), new Npgsql.NpgsqlParameter("project", project.Id),
                    new Npgsql.NpgsqlParameter("customer", project.CustomerId.ToString("D"))).SingleAsync(ct);
                Guard.Require(!communicationCustomerMismatch, "communication_project", "Recorded customer conversations preserve this project's customer association.");
                var maintenanceProjectRows = await EntitySources.Read<MaintenanceVisit>(db, scope.TenantId, ct,
                    [BusinessRegistry.Reference<Project>(project.Id)], includeArchived: true);
                foreach (var visit in maintenanceProjectRows)
                {
                    visit.Validate();
                    Guard.Require(project.Type == ProjectType.Maintenance && project.CustomerId == visit.Terms!.CustomerId && project.BuildingId == visit.Terms.BuildingId
                        && (visit.Status != MaintenanceVisitStatus.Completed || project.Status == ProjectStatus.Completed),
                        "maintenance_project_mismatch", "Maintenance projects retain the original occurrence and completed history.");
                }
                var projectBuilding = await store.Get<Building>(project.BuildingId, ct);
                Guard.Require(projectBuilding.CustomerId == project.CustomerId, "customer_mismatch", "The project customer must own its building record.");
                Guard.Require((await Related<Quote>(db, scope, "projects", project.Id, ct)).All(q => q.ProjectId != project.Id || q.CustomerId == project.CustomerId), "customer_mismatch", "Existing quotes prevent this customer change.");
                Guard.Require((await Related<RoofRevision>(db, scope, "projects", project.Id, ct)).All(r => r.ProjectId != project.Id || r.BuildingId == project.BuildingId), "project_roof_mismatch", "Existing roof revisions prevent moving this project to another building.");
                foreach (var existingMeasurement in (await Related<Measurement>(db, scope, "projects", project.Id, ct)).Where(m => m.ProjectId == project.Id))
                    Guard.Require((await store.Get<RoofRevision>(existingMeasurement.RoofRevisionId, ct)).BuildingId == project.BuildingId, "project_roof_mismatch", "Existing measurements preserve this project's building context.");
                Guard.Require((await Related<TechnicalRecord>(db, scope, "projects", project.Id, ct)).All(c => c.ProjectId != project.Id || c.BuildingId == project.BuildingId), "project_roof_mismatch", "Stored technical results preserve this project's building context.");
                foreach (var assignment in (await Related<ScheduleAssignment>(db, scope, "projects", project.Id, ct)).Where(x => x.ProjectId == project.Id && !x.Cancelled))
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
                foreach (var linked in (await Related<MaterialDemand>(db, scope, "purchase-orders", purchase.Id, ct)).Where(x => x.PurchaseOrderId == purchase.Id))
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
                if (!assignment.Cancelled)
                {
                    var equipmentZoneId = await db.Tenants.Where(x => x.Id == scope.TenantId).Select(x => x.TimeZone).SingleAsync(ct);
                    var equipmentZone = TimeZoneInfo.FindSystemTimeZoneById(equipmentZoneId);
                    foreach (var equipmentId in assignment.EquipmentIds)
                        EquipmentInspectionWorkflow.EnsureAssignable(await store.Get<Equipment>(equipmentId, ct), assignment.Start, assignment.End, equipmentZone);
                }
                await ValidateWindow(await store.Get<Project>(assignment.ProjectId, ct), assignment, db, scope, ct);
                var employeeAssignments = await Related<ScheduleAssignment>(db, scope, "employees", assignment.EmployeeId, ct);
                var equipmentAssignments = await EntitySources.Read<ScheduleAssignment>(db, scope.TenantId, ct,
                    assignment.EquipmentIds.Select(BusinessRegistry.Reference<Equipment>).ToArray());
                PlanningRules.EnsureAssignment(assignment, employeeAssignments.Concat(equipmentAssignments).DistinctBy(x => x.Id),
                    await Related<Absence>(db, scope, "employees", assignment.EmployeeId, ct)); break;
            case Absence absence:
                PlanningRules.EnsureAbsence(absence, await Related<ScheduleAssignment>(db, scope, "employees", absence.EmployeeId, ct),
                    await Related<Absence>(db, scope, "employees", absence.EmployeeId, ct)); break;
            case DiaryEntry diary:
                Guard.Require((await store.Get<Employee>(diary.EmployeeId, ct)).Active, "inactive_employee", "An inactive employee cannot record new diary work."); break;
            case TimeEntry entry:
                Guard.Require((await store.Get<Employee>(entry.EmployeeId, ct)).Active, "inactive_employee", "An inactive employee cannot record new time.");
                PlanningRules.EnsureTimeEntry(entry, await Related<TimeEntry>(db, scope, "employees", entry.EmployeeId, ct)); break;
            case MaterialReservation reservation:
                InventoryRules.Reserve(reservation, await Related<StockMovement>(db, scope, "materials", reservation.MaterialId, ct), await Related<MaterialReservation>(db, scope, "materials", reservation.MaterialId, ct)); break;
            case Acceptance acceptance:
                OperationsRules.EnsureAcceptance(acceptance, await Related<Defect>(db, scope, "projects", acceptance.ProjectId, ct)); break;
            case MaintenanceContract contract:
                Guard.Require((await store.Get<Building>(contract.BuildingId, ct)).CustomerId == contract.CustomerId, "customer_mismatch", "Maintenance customer and building must match.");
                var contractRows = await EntitySources.Read<MaintenanceVisit>(db, scope.TenantId, ct,
                    [BusinessRegistry.Reference<MaintenanceContract>(contract.Id)], includeArchived: true);
                foreach (var visit in contractRows)
                {
                    visit.Validate();
                    Guard.Require(contract.CustomerId == visit.Terms!.CustomerId && contract.BuildingId == visit.Terms.BuildingId
                        && contract.StartDate == visit.Terms.StartDate && contract.IntervalMonths == visit.Terms.IntervalMonths,
                        "maintenance_recurrence_in_use", "Planned maintenance retains its customer, building and recurrence anchor. Create a new contract for changed terms.");
                }
                break;
        }
    }

    private static Task<List<T>> Related<T>(FirstBlickDbContext db, TenantScope scope, string kind, Guid id, CancellationToken ct)
        where T : TenantEntity => EntitySources.Read<T>(db, scope.TenantId, ct, [new(kind, id)]);

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
