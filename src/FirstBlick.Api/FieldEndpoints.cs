using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FirstBlick.Api;

public sealed record PurchaseReceiptRequest(Guid LineId, decimal Quantity, string Location = "Hauptlager");
public sealed record MaterialReserveRequest(Guid ProjectId, decimal Quantity, string Location = "Hauptlager", DateOnly? RequiredOn = null);
public sealed record MaterialConsumeRequest(decimal Quantity, decimal UnitCost);
public sealed record MaterialReleaseRequest(decimal Quantity);
public sealed record InventoryCountRequest(string Location, decimal ExpectedOnHand, decimal CountedOnHand, decimal UnitCost, string Reason, string EvidenceReference);
public sealed record MaterialTransferRequest(string FromLocation, string ToLocation, decimal Quantity, decimal UnitCost, decimal ExpectedFromOnHand, string Reason);
public sealed record MaterialReturnRequest(decimal Quantity, string Reason);
public sealed record MaterialSupplierReturnRequest(decimal Quantity, string Reason, Guid? ReturnTransferId = null);
public sealed record DefectResolutionRequest(string Resolution);
public sealed record TrainingReviewRequest(string MentorFeedback);
public sealed record FormSubmitRequest(Guid ProjectId, Dictionary<string, string> Values);
public sealed record MaintenanceCompletionRequest(DateOnly ExpectedDueDate, Guid ProjectId);

/// <summary>Audited field, inventory and recurrence transitions share the tenant command lock and optimistic revision checks.</summary>
public static class FieldEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        MapInventory(api); MapPeople(api); MapForms(api); MapMaintenance(api); DiaryReportEndpoints.Map(api);
    }

    private static void MapInventory(RouteGroupBuilder api)
    {
        api.MapPost("/purchase-orders/{id:guid}/issue", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("purchase-orders", "purchase.issue", body, async ct =>
            {
                var purchase = await c.Current<PurchaseOrder>(id, ct);
                await GraphRules.Validate(purchase, c.Store, c.Db, c.Scope, ct);
                await c.Approvals.Require(c.Http, "purchase.issue", "purchase-orders", id, purchase.Revision, "{}", ct);
                InventoryRules.Order(purchase, await c.Store.NextNumber("BE", await c.BusinessDate(ct), ct));
                await c.Store.Save(purchase, "purchase.issue", false, ct); return purchase;
            })).Produces<PurchaseOrder>();
        api.MapPost("/purchase-orders/{id:guid}/receive", (Guid id, PurchaseReceiptRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("purchase-orders", "purchase.receive", body, async ct =>
            {
                var purchase = await c.Current<PurchaseOrder>(id, ct);
                var receipt = InventoryRules.Receive(purchase, body.LineId, body.Quantity, await c.Store.All<StockMovement>(ct), body.Location, DateTimeOffset.UtcNow);
                await c.SaveNew(receipt, "stock.purchase_receipt", ct);
                await c.Store.Save(purchase, "purchase.receive", false, ct);
                return new { order = purchase, receipt };
            }));
        api.MapGet("/materials/{id:guid}/stock", async (Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct, string location = "Hauptlager") =>
        {
            await c.Commands.Authorize("stock-movements", false, ct); await c.Current<Material>(id, ct, false);
            Guard.Text(location, nameof(location), 120);
            return Results.Ok(InventoryRules.Balance(c.Scope.TenantId, id, location, await c.Store.All<StockMovement>(ct), await c.Store.All<MaterialReservation>(ct)));
        });
        api.MapGet("/materials/{id:guid}/movements", async (Guid id, [AsParameters] BusinessCommandServices c,
            CancellationToken ct, string location = "", int page = 1, string kind = "") =>
        {
            await c.Commands.Authorize("stock-movements", false, ct);
            await c.Current<Material>(id, ct, false);
            Guard.Text(location, nameof(location), 120, false);
            Guard.Require(kind.Length == 0 || Enum.GetNames<StockMovementKind>().Contains(kind),
                "invalid_kind", "The requested stock movement kind is not supported.");
            Guard.Require(page is >= 1 and <= 100_000, "invalid_page", "Page must be between 1 and 100000.");
            const int pageSize = 25;
            NpgsqlParameter[] Parameters() => [
                new("tenant", c.Scope.TenantId), new("material", id.ToString("D")),
                new("location", location), new("movementKind", kind)
            ];
            const string predicate = "\"" + "TenantId\"=@tenant AND \"Kind\"='stock-movements' AND NOT \"Archived\" AND \"Data\"->>'materialId'=@material AND (@location='' OR \"Data\"->>'location'=@location) AND (@movementKind='' OR \"Data\"->>'kind'=@movementKind)";
#pragma warning disable EF1002
            var total = await c.Db.Database.SqlQueryRaw<long>(
                $"SELECT count(*) AS \"Value\" FROM \"Entities\" WHERE {predicate}", Parameters()).SingleAsync(ct);
            var paged = Parameters().Concat([new NpgsqlParameter("limit", pageSize),
                new NpgsqlParameter("offset", checked((page - 1) * pageSize))]).ToArray();
            var entries = await c.Db.Database.SqlQueryRaw<string>(
                $"SELECT \"Data\"::text AS \"Value\" FROM \"Entities\" WHERE {predicate} ORDER BY \"CreatedAt\" DESC, \"Id\" DESC LIMIT @limit OFFSET @offset",
                paged).ToListAsync(ct);
#pragma warning restore EF1002
            return Results.Ok(new { items = entries.Select(AppJson.Read<StockMovement>), total, page, pageSize });
        }).WithTags("inventory");

        api.MapPost("/materials/{id:guid}/inventory-count", (Guid id, InventoryCountRequest body, [AsParameters] BusinessCommandServices c) =>
            ExecuteInventory(c, "stock.inventory_count", body, async ct =>
            {
                await c.Current<Material>(id, ct, false);
                var movements = await c.Store.All<StockMovement>(ct);
                var reservations = await c.Store.All<MaterialReservation>(ct);
                var movement = InventoryRules.CountAdjustment(c.Scope.TenantId, id, body.Location,
                    body.ExpectedOnHand, body.CountedOnHand, body.UnitCost, body.Reason, body.EvidenceReference,
                    movements, reservations, DateTimeOffset.UtcNow);
                var balance = InventoryRules.Balance(c.Scope.TenantId, id, body.Location,
                    movements.Append(movement), reservations);
                await c.SaveNew(movement, "stock.inventory_count", ct);
                return new { movement, balance };
            }));
        api.MapPost("/materials/{id:guid}/transfer", (Guid id, MaterialTransferRequest body, [AsParameters] BusinessCommandServices c) =>
            ExecuteInventory(c, "stock.transfer", body, async ct =>
            {
                await c.Current<Material>(id, ct, false);
                var movements = await c.Store.All<StockMovement>(ct);
                var reservations = await c.Store.All<MaterialReservation>(ct);
                var (outgoing, incoming) = InventoryRules.Transfer(c.Scope.TenantId, id,
                    body.FromLocation, body.ToLocation, body.Quantity, body.UnitCost, body.ExpectedFromOnHand, body.Reason,
                    movements, reservations, DateTimeOffset.UtcNow);
                await c.SaveNew(outgoing, "stock.transfer_out", ct);
                await c.SaveNew(incoming, "stock.transfer_in", ct);
                return new { outgoing, incoming };
            }));
        api.MapPost("/stock-movements/{id:guid}/supplier-return", (Guid id, MaterialSupplierReturnRequest body, [AsParameters] BusinessCommandServices c) =>
            ExecuteInventory(c, "stock.supplier_return", body, async ct =>
            {
                var source = await c.Current<StockMovement>(id, ct);
                Guard.Require(source.PurchaseOrderId.HasValue, "invalid_supplier_return", "The source is not a supplier receipt.");
                var order = await c.Current<PurchaseOrder>(source.PurchaseOrderId.Value, ct, false);
                var returned = InventoryRules.SupplierReturn(order, source, body.Quantity, body.Reason,
                    await c.Store.All<StockMovement>(ct), await c.Store.All<MaterialReservation>(ct), DateTimeOffset.UtcNow, body.ReturnTransferId);
                await c.SaveNew(returned, "stock.supplier_return", ct);
                await c.Store.Save(order, "purchase.return", false, ct);
                return new { order, movement = returned };
            }, allowOffice: true));
        api.MapPost("/stock-movements/{id:guid}/project-return", (Guid id, MaterialReturnRequest body, [AsParameters] BusinessCommandServices c) =>
            ExecuteInventory(c, "stock.project_return", body, async ct =>
            {
                var source = await c.Current<StockMovement>(id, ct);
                Guard.Require(source.ReservationId.HasValue, "invalid_project_return", "The source is not a reservation consumption.");
                var reservation = await c.Current<MaterialReservation>(source.ReservationId.Value, ct, false);
                var returned = InventoryRules.ProjectReturn(reservation, source, body.Quantity, body.Reason,
                    await c.Store.All<StockMovement>(ct), DateTimeOffset.UtcNow);
                await c.SaveNew(returned, "stock.project_return", ct);
                await c.Store.Save(reservation, "material.return", false, ct);
                return returned;
            })).Produces<StockMovement>();
        api.MapPost("/materials/{id:guid}/reserve", (Guid id, MaterialReserveRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("material-reservations", "material.reserve", body, async ct =>
            {
                await c.Current<Material>(id, ct); await c.Current<Project>(body.ProjectId, ct, false);
                var reservation = new MaterialReservation { TenantId = c.Scope.TenantId, MaterialId = id, ProjectId = body.ProjectId,
                    Quantity = body.Quantity, Location = body.Location, RequiredOn = body.RequiredOn };
                InventoryRules.Reserve(reservation, await c.Store.All<StockMovement>(ct), await c.Store.All<MaterialReservation>(ct));
                return await c.SaveNew(reservation, "material.reserve", ct);
            })).Produces<MaterialReservation>();
        api.MapPost("/material-reservations/{id:guid}/consume", (Guid id, MaterialConsumeRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("material-reservations", "material.consume", body, async ct =>
            {
                var reservation = await c.Current<MaterialReservation>(id, ct);
                var movement = InventoryRules.Consume(reservation, body.Quantity, body.UnitCost, DateTimeOffset.UtcNow);
                await c.SaveNew(movement, "stock.reservation_consumption", ct);
                await c.Store.Save(reservation, "material.consume", false, ct);
                return new { reservation, movement };
            }));
        api.MapPost("/material-reservations/{id:guid}/release", (Guid id, MaterialReleaseRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("material-reservations", "material.release", body, async ct =>
            {
                var reservation = await c.Current<MaterialReservation>(id, ct); InventoryRules.Release(reservation, body.Quantity);
                await c.Store.Save(reservation, "material.release", false, ct); return reservation;
            })).Produces<MaterialReservation>();
    }

    private static Task<IResult> ExecuteInventory(BusinessCommandServices c, string action, object input,
        Func<CancellationToken, Task<object>> mutation, bool allowOffice = false) =>
        c.Commands.Execute(c.Http, "stock-movements", action, input, mutation, _ =>
        {
            // Run after the tenant lock and current membership reload, including on
            // idempotent replay: an old receipt does not preserve a revoked stock role.
            Guard.Require(c.Scope.Role is BusinessRole.ManagingDirector or BusinessRole.SiteManager
                || allowOffice && c.Scope.Role == BusinessRole.Office,
                "forbidden", "Your current role does not allow this stock correction.");
            return Task.CompletedTask;
        });

    private static void MapPeople(RouteGroupBuilder api)
    {
        api.MapPost("/time-entries/{id:guid}/approve", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("time-entries", "time.approve", body, async ct =>
            {
                var entry = await c.Current<TimeEntry>(id, ct);
                Guard.Require(c.Scope.Role is BusinessRole.ManagingDirector or BusinessRole.SiteManager, "forbidden", "Time approval requires a managing director or site manager.");
                Guard.Require(!entry.Approved, "invalid_transition", "This time entry is already approved.");
                PlanningRules.EnsureTimeEntry(entry, await c.Store.All<TimeEntry>(ct)); entry.Approved = true;
                await c.Store.Save(entry, "time.approve", false, ct); return entry;
            })).Produces<TimeEntry>();
        api.MapPost("/absences/{id:guid}/approve", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("absences", "absence.approve", body, async ct =>
            {
                var absence = await c.Current<Absence>(id, ct);
                Guard.Require(absence.Status == AbsenceStatus.Requested, "invalid_transition", "Only requested absence can be approved.");
                absence.Status = AbsenceStatus.Approved;
                PlanningRules.EnsureAbsence(absence, await c.Store.All<ScheduleAssignment>(ct), await c.Store.All<Absence>(ct));
                await c.Store.Save(absence, "absence.approve", false, ct); return absence;
            })).Produces<Absence>();
        api.MapPost("/absences/{id:guid}/reject", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("absences", "absence.reject", body, async ct =>
            {
                var absence = await c.Current<Absence>(id, ct);
                Guard.Require(absence.Status == AbsenceStatus.Requested, "invalid_transition", "Only requested absence can be rejected.");
                absence.Status = AbsenceStatus.Rejected; await c.Store.Save(absence, "absence.reject", false, ct); return absence;
            })).Produces<Absence>();
        api.MapPost("/defects/{id:guid}/resolve", (Guid id, DefectResolutionRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("defects", "defect.resolve", body, async ct =>
            {
                var defect = await c.Current<Defect>(id, ct);
                Guard.Require(defect.Status is DefectStatus.Open or DefectStatus.InProgress, "invalid_transition", "Only an open defect can be resolved.");
                defect.Resolution = body.Resolution; defect.Status = DefectStatus.Resolved;
                await RecordPolicy.EnsureWrite(defect, c.Scope, c.Store, c.Db, ct);
                await c.Store.Save(defect, "defect.resolve", false, ct); return defect;
            })).Produces<Defect>();
        api.MapPost("/defects/{id:guid}/accept", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("defects", "defect.accept", body, async ct =>
            {
                var defect = await c.Current<Defect>(id, ct);
                Guard.Require(c.Scope.Role is BusinessRole.ManagingDirector or BusinessRole.SiteManager, "forbidden", "Final defect acceptance requires site management.");
                Guard.Require(defect.Status == DefectStatus.Resolved, "invalid_transition", "Resolve a defect before accepting its repair.");
                defect.Status = DefectStatus.Accepted; await c.Store.Save(defect, "defect.accept", false, ct); return defect;
            })).Produces<Defect>();
        api.MapPost("/training-records/{id:guid}/submit", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("training-records", "training.submit", body, async ct =>
            {
                var record = await c.Current<TrainingRecord>(id, ct);
                Guard.Require(record.Status == TrainingRecordStatus.Draft, "invalid_transition", "Only a draft training record can be submitted.");
                var apprentice = await c.Store.Get<Employee>(record.ApprenticeEmployeeId, ct);
                Guard.Require(apprentice.UserId == c.Scope.UserId || c.Scope.Role == BusinessRole.ManagingDirector, "forbidden", "The apprentice or managing director must submit the record.");
                record.Status = TrainingRecordStatus.Submitted;
                await RecordPolicy.EnsureWrite(record, c.Scope, c.Store, c.Db, ct);
                await c.Store.Save(record, "training.submit", false, ct); return record;
            })).Produces<TrainingRecord>();
        api.MapPost("/training-records/{id:guid}/review", (Guid id, TrainingReviewRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("training-records", "training.review", body, async ct =>
            {
                var record = await c.Current<TrainingRecord>(id, ct);
                Guard.Require(record.Status == TrainingRecordStatus.Submitted, "invalid_transition", "Only a submitted training record can be reviewed.");
                record.MentorFeedback = body.MentorFeedback; record.Status = TrainingRecordStatus.Reviewed; record.ReviewedAt = DateTimeOffset.UtcNow;
                await RecordPolicy.EnsureWrite(record, c.Scope, c.Store, c.Db, ct);
                await c.Store.Save(record, "training.review", false, ct); return record;
            })).Produces<TrainingRecord>();
    }

    private static void MapForms(RouteGroupBuilder api)
    {
        api.MapPost("/form-definitions/{id:guid}/publish", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("form-definitions", "form.publish", body, async ct =>
            {
                var definition = await c.Current<FormDefinition>(id, ct); FormRules.Publish(definition);
                await c.Store.Save(definition, "form.publish", false, ct); return definition;
            })).Produces<FormDefinition>();
        api.MapPost("/form-definitions/{id:guid}/new-version", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("form-definitions", "form.new_version", body, async ct =>
            {
                var definition = await c.Current<FormDefinition>(id, ct); definition.Validate();
                Guard.Require(definition.Published, "invalid_transition", "Publish a definition before creating its next version.");
                Guard.Require(definition.Version < int.MaxValue, "version_exhausted", "The form version counter is exhausted.");
                var revisionAction = "form.revision_from:" + definition.Id;
                Guard.Require(!await c.Db.Audit.AnyAsync(x => x.Kind == "form-definitions" && x.Action == revisionAction, ct), "duplicate", "A successor draft already exists for this form version.");
                var next = new FormDefinition { TenantId = c.Scope.TenantId, Name = definition.Name, Description = definition.Description,
                    Version = checked(definition.Version + 1), Fields = definition.Fields.Select(x => x with { Choices = x.Choices.ToList() }).ToList() };
                await c.SaveNew(next, revisionAction, ct); return new { definition = next, previousDefinitionId = definition.Id };
            }));
        api.MapPost("/form-definitions/{id:guid}/submit", (Guid id, FormSubmitRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("form-submissions", "form.submit", body, async ct =>
            {
                var definition = await c.Current<FormDefinition>(id, ct); await c.Current<Project>(body.ProjectId, ct, false);
                var submission = FormRules.Submit(definition, body.ProjectId, c.Scope.UserId, body.Values, DateTimeOffset.UtcNow);
                await RecordPolicy.EnsureWrite(submission, c.Scope, c.Store, c.Db, ct);
                return await c.SaveNew(submission, "form.submit", ct);
            })).Produces<FormSubmission>();
    }

    private static void MapMaintenance(RouteGroupBuilder api)
    {
        api.MapPost("/maintenance-contracts/{id:guid}/complete", (Guid id, MaintenanceCompletionRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("maintenance-contracts", "maintenance.complete", body, async ct =>
            {
                var contract = await c.Current<MaintenanceContract>(id, ct); var project = await c.Current<Project>(body.ProjectId, ct, false);
                var occurrenceKey = $"contract:{contract.Id:N}:due:{body.ExpectedDueDate:yyyy-MM-dd}";
                Guard.Require(!await c.Db.Entities.AnyAsync(x => x.Kind == "maintenance-visits" && x.UniqueKey == occurrenceKey, ct)
                    && !await c.Db.Links.AnyAsync(x => x.SourceKind == "maintenance-visits" && x.TargetKind == "projects" && x.TargetId == project.Id, ct),
                    "maintenance_visit_required", "This occurrence has a planned checklist; complete it through its maintenance visit.");
                Guard.Require(project.Type == ProjectType.Maintenance && project.Status == ProjectStatus.Completed
                    && project.CustomerId == contract.CustomerId && project.BuildingId == contract.BuildingId,
                    "maintenance_project_mismatch", "Completion requires a completed maintenance project for this contract's customer and building.");
                var key = "maintenance-completion:" + project.Id.ToString("N");
                Guard.Require(!await c.Db.Receipts.AnyAsync(x => x.ActorId == Guid.Empty && x.Key == key, ct), "maintenance_project_reused", "This maintenance project has already completed an occurrence.");
                var completedAt = DateTimeOffset.UtcNow;
                OperationsRules.CompleteMaintenance(contract, body.ExpectedDueDate);
                var evidence = new { contractId = contract.Id, projectId = project.Id, dueDate = body.ExpectedDueDate, completedBy = c.Scope.UserId, completedAt };
                // Retain a unique completion marker and the occurrence evidence in the same transaction as the advancement.
                c.Db.Receipts.Add(new CommandReceipt { TenantId = c.Scope.TenantId, ActorId = Guid.Empty, Key = key,
                    RequestDigest = ContentDigest.Of(evidence), Response = AppJson.Serialize(evidence), CreatedAt = completedAt });
                await c.Store.Save(contract, "maintenance.complete", false, ct); return new { contract, completion = evidence };
            }));
    }
}
