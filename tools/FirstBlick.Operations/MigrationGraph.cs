using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;

namespace FirstBlick.Operations;

internal sealed class MigrationGraph
{
    private readonly MigrationBundle bundle;
    private readonly List<TenantEntity> entities;
    private readonly Dictionary<(Guid Tenant, string Kind, Guid Id), TenantEntity> byId;
    private readonly HashSet<(Guid Tenant, Guid User)> memberships;
    private readonly Dictionary<(Guid Tenant, Type Type, string Key), int> uniqueCounts;
    private readonly Dictionary<(Guid Tenant, Type Type), object> typedLists = [];
    public MigrationGraph(MigrationBundle bundle, List<TenantEntity> entities)
    {
        this.bundle = bundle; this.entities = entities;
        byId = entities.ToDictionary(x => (x.TenantId, BusinessRegistry.Entities.Single(r => r.Value == x.GetType()).Key, x.Id));
        memberships = bundle.Memberships.Select(x => (x.TenantId, x.UserId)).ToHashSet();
        uniqueCounts = entities.Select(x => (x.TenantId, Type: x.GetType(), Key: EntityProjection.UniqueKey(x))).Where(x => x.Key is not null)
            .GroupBy(x => (x.TenantId, x.Type, Key: x.Key!)).ToDictionary(x => x.Key, x => x.Count());
    }
    public TenantEntity RequireReference(Guid tenant, string kind, Guid id) => byId.TryGetValue((tenant, kind, id), out var value)
        ? value : throw new OperationsException($"Missing tenant-qualified {kind} reference {id}.");
    private T Get<T>(Guid tenant, Guid id) where T : TenantEntity => (T)RequireReference(tenant, BusinessRegistry.KindOf<T>(), id);
    private List<T> All<T>(Guid tenant) where T : TenantEntity
    {
        var key = (tenant, typeof(T));
        if (!typedLists.TryGetValue(key, out var values)) { values = entities.OfType<T>().Where(x => x.TenantId == tenant).ToList(); typedLists.Add(key, values); }
        return (List<T>)values;
    }

    public void Validate(TenantEntity entity)
    {
        foreach (var reference in entity.References().Distinct()) RequireReference(entity.TenantId, reference.Kind, reference.Id);
        var unique = EntityProjection.UniqueKey(entity);
        Require.That(unique is null || uniqueCounts[(entity.TenantId, entity.GetType(), unique)] == 1, "The imported record violates its shared database uniqueness contract.");
        using (var json = JsonDocument.Parse(AppJson.Serialize(entity))) ValidateActors(entity.TenantId, json.RootElement);
        var tenant = entity.TenantId;
        switch (entity)
        {
            case Customer customer:
                Require.That(customer.Contacts is null || customer.Contacts.All(contact =>
                    contact.CreatedAt >= customer.CreatedAt && contact.UpdatedAt <= customer.UpdatedAt
                    && contact.UpdatedAt <= bundle.ExportedAt),
                    "Customer contact history must fit its retained customer envelope and export date.");
                break;
            case CustomerCommunication communication:
                ValidateCustomerCommunication(communication); break;
            case CustomerCommunicationVoid communicationVoid:
                var originalCommunication = Get<CustomerCommunication>(tenant, communicationVoid.CommunicationId);
                var voidCustomer = Get<Customer>(tenant, communicationVoid.CustomerId);
                Require.That(originalCommunication.CustomerId == communicationVoid.CustomerId
                    && originalCommunication.Digest == communicationVoid.SourceDigest,
                    "A communication void must retain the original customer's exact journal entry and digest.");
                Require.That(communicationVoid.VoidedAt >= originalCommunication.RecordedAt
                    && communicationVoid.VoidedAt <= voidCustomer.UpdatedAt && communicationVoid.VoidedAt <= bundle.ExportedAt,
                    "A communication void must fit its original recording, customer envelope and export date.");
                break;
            case Project project:
                Require.That(Get<Building>(tenant, project.BuildingId).CustomerId == project.CustomerId, "Project and building customer differ."); break;
            case ProjectTemplateApplication application:
                ValidateProjectTemplateApplication(application); break;
            case TaskItem task when task.TemplateOrigin is not null:
                ValidateTemplateTask(task, Get<ProjectTemplateApplication>(tenant, task.TemplateOrigin.ApplicationId)); break;
            case Quote quote:
                Require.That(Get<Project>(tenant, quote.ProjectId).CustomerId == quote.CustomerId, "Quote and project customer differ.");
                foreach (var line in quote.Lines) ValidateQuantity(tenant, quote.ProjectId, line.QuantityEvidence); break;
            case Order order:
                var accepted = Get<Quote>(tenant, order.QuoteId);
                Require.That(accepted.Status == QuoteStatus.Accepted && accepted.CustomerId == order.CustomerId && accepted.ProjectId == order.ProjectId
                    && accepted.Snapshot?.Digest == order.Contract?.Digest && accepted.Presentation == order.QuotePresentation && accepted.AcceptedAt == order.AcceptedAt,
                    "Order must retain the exact accepted quote, parties and acceptance time."); break;
            case ChangeOrder change:
                foreach (var line in change.Lines) ValidateQuantity(tenant, Get<Order>(tenant, change.OrderId).ProjectId, line.QuantityEvidence); break;
            case Invoice invoice:
                var invoiceOrder = Get<Order>(tenant, invoice.OrderId);
                Require.That(invoiceOrder.CustomerId == invoice.CustomerId, "Invoice and order customer differ.");
                if (invoice.OriginalInvoiceId is { } originalId)
                {
                    var original = Get<Invoice>(tenant, originalId);
                    Require.That(original.Status == InvoiceStatus.Issued && original.Kind != InvoiceKind.CreditNote && original.OrderId == invoice.OrderId && original.CustomerId == invoice.CustomerId
                        && original.BuyerName == invoice.BuyerName && original.BuyerAddress == invoice.BuyerAddress && original.SellerName == invoice.SellerName && original.SellerAddress == invoice.SellerAddress,
                        "Invoice correction must preserve its original parties and contract reference.");
                }
                foreach (var line in invoice.Lines) ValidateQuantity(tenant, invoiceOrder.ProjectId, line.QuantityEvidence); break;
            case SupplierInvoice supplierInvoice:
                if (supplierInvoice.PurchaseOrderId is { } purchaseId) Require.That(Get<PurchaseOrder>(tenant, purchaseId).SupplierId == supplierInvoice.SupplierId, "Supplier invoice and purchase order differ.");
                if (supplierInvoice.OriginalInvoiceId is { } supplierOriginal)
                {
                    var original = Get<SupplierInvoice>(tenant, supplierOriginal);
                    Require.That(original.Status == SupplierInvoiceStatus.Approved && original.Kind == SupplierInvoiceKind.Invoice && original.SupplierId == supplierInvoice.SupplierId, "Supplier credit must reference its approved original invoice.");
                }
                break;
            case PaymentAllocation payment:
                var bank = Get<BankTransaction>(tenant, payment.BankTransactionId); var receiptInvoice = Get<Invoice>(tenant, payment.InvoiceId);
                Require.That(receiptInvoice.Status == InvoiceStatus.Issued && receiptInvoice.Kind != InvoiceKind.CreditNote && !string.IsNullOrEmpty(bank.ImportDigest)
                    && (payment.Direction == PaymentDirection.Receipt ? bank.Amount > 0 : bank.Amount < 0), "Customer allocation requires an issued receivable and a sealed bank entry of the correct sign.");
                if (payment.ReturnBankTransactionId is { } returned) Require.That(payment.Direction == PaymentDirection.Receipt && Get<BankTransaction>(tenant, returned).Amount < 0, "Returned customer payment requires an outgoing bank entry.");
                break;
            case SupplierPaymentAllocation payment:
                var supplierBank = Get<BankTransaction>(tenant, payment.BankTransactionId); var payable = Get<SupplierInvoice>(tenant, payment.SupplierInvoiceId);
                Require.That(payable.Status == SupplierInvoiceStatus.Approved && payable.Kind == SupplierInvoiceKind.Invoice
                    && (payment.Direction == SupplierPaymentDirection.Payment ? supplierBank.Amount < 0 : supplierBank.Amount > 0), "Supplier allocation direction or invoice status is inconsistent.");
                if (payment.ReturnBankTransactionId is { } supplierReturned) Require.That(payment.Direction == SupplierPaymentDirection.Payment && Get<BankTransaction>(tenant, supplierReturned).Amount > 0, "Returned supplier payment requires an incoming bank entry.");
                break;
            case StockMovement movement:
                ValidateStockMovement(movement); break;
            case Equipment equipment:
                ValidateEquipmentHistory(equipment); break;
            case VehicleTrip trip:
                ValidateVehicleLedgerEntry(trip);
                Require.That(Get<Employee>(tenant, trip.DriverEmployeeId).CreatedAt <= trip.RecordedAt,
                    "A vehicle trip's retained driver must exist before recording.");
                break;
            case VehicleCost vehicleCost:
                ValidateVehicleLedgerEntry(vehicleCost);
                var costZone = TimeZoneInfo.FindSystemTimeZoneById(bundle.Tenants.Single(x => x.Data.Id == tenant).Data.TimeZone);
                var costRecordedDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(vehicleCost.RecordedAt, costZone).DateTime);
                Require.That(vehicleCost.SpentOn <= costRecordedDate,
                    "A vehicle cost cannot occur after its recording's business date.");
                break;
            case VehicleLedgerReversal reversal:
                var reversedSource = RequireReference(tenant, reversal.SourceKind, reversal.SourceId);
                Require.That(reversedSource is VehicleLedgerEntry, "A vehicle reversal must retain a supported ledger source.");
                VehicleLedgerWorkflow.ValidateReversal(reversal, (VehicleLedgerEntry)reversedSource);
                Require.That(reversal.RecordedAt <= Get<Equipment>(tenant, reversal.VehicleId).UpdatedAt
                    && reversal.RecordedAt <= bundle.ExportedAt,
                    "A vehicle reversal must fit its vehicle envelope and export date.");
                break;
            case EquipmentInspection inspection:
                _ = Get<Equipment>(tenant, inspection.EquipmentId);
                if (inspection.Completion is { } completed)
                {
                    var inspectionZone = TimeZoneInfo.FindSystemTimeZoneById(bundle.Tenants.Single(x => x.Data.Id == tenant).Data.TimeZone);
                    var recordedDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(completed.RecordedAt, inspectionZone).DateTime);
                    Require.That(completed.RecordedAt <= bundle.ExportedAt && completed.PerformedOn <= recordedDate,
                        "A completed equipment inspection cannot occur after its recording or export date.");
                }
                if (inspection.Cancellation is { } cancelled)
                    Require.That(cancelled.CancelledAt <= bundle.ExportedAt, "Equipment inspection cancellation cannot occur after export.");
                break;
            case ScheduleAssignment assignment:
                PlanningRules.EnsureAssignment(assignment, All<ScheduleAssignment>(tenant), All<Absence>(tenant));
                if (!assignment.Cancelled && !assignment.Archived)
                {
                    var project = Get<Project>(tenant, assignment.ProjectId);
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(bundle.Tenants.Single(x => x.Data.Id == tenant).Data.TimeZone);
                    var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(assignment.Start, zone).DateTime);
                    var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(assignment.End.AddTicks(-1), zone).DateTime);
                    Require.That(project.Status != ProjectStatus.Cancelled && (!project.PlannedStart.HasValue || first >= project.PlannedStart) && (!project.PlannedEnd.HasValue || last <= project.PlannedEnd), "Active assignment is outside its project's business dates.");
                }
                break;
            case Absence absence: PlanningRules.EnsureAbsence(absence, All<ScheduleAssignment>(tenant), All<Absence>(tenant)); break;
            case TimeEntry time when !time.Archived: PlanningRules.EnsureTimeEntry(time, All<TimeEntry>(tenant)); break;
            case MaintenanceContract maintenance: Require.That(Get<Building>(tenant, maintenance.BuildingId).CustomerId == maintenance.CustomerId, "Maintenance and building customer differ."); break;
            case FormSubmission submission:
                var definition = Get<FormDefinition>(tenant, submission.FormDefinitionId);
                Require.That(definition.Published && definition.Version == submission.DefinitionVersion && definition.PublishedDigest == submission.DefinitionDigest
                    && MigrationJson.Digest(definition.Fields) == MigrationJson.Digest(submission.Fields), "Submission must preserve its published form version and fields."); break;
            case RoofRevision roof:
                if (roof.ProjectId is { } roofProject) Require.That(Get<Project>(tenant, roofProject).BuildingId == roof.BuildingId, "Roof and project building differ.");
                if (roof.PreviousRevisionId is { } previous)
                {
                    var predecessor = Get<RoofRevision>(tenant, previous);
                    Require.That(predecessor.Status == RoofRevisionStatus.Published && predecessor.BuildingId == roof.BuildingId && predecessor.Version + 1 == roof.Version, "Roof predecessor must be the previous published version of the same building.");
                }
                break;
            case Measurement measurement:
                var basis = Get<RoofRevision>(tenant, measurement.RoofRevisionId); var measurementProject = Get<Project>(tenant, measurement.ProjectId);
                Require.That(basis.Status == RoofRevisionStatus.Published && basis.BuildingId == measurementProject.BuildingId && (!basis.ProjectId.HasValue || basis.ProjectId == measurement.ProjectId), "Measurement roof/building/project differ.");
                if (measurement.Snapshot is { } snapshot) Require.That(snapshot.RoofDigest == basis.Snapshot?.Digest && snapshot.RoofVersion == basis.Version, "Frozen measurement has a different roof revision digest.");
                break;
            case TechnicalRecord calculation:
                Require.That(Get<Project>(tenant, calculation.ProjectId).BuildingId == calculation.BuildingId, "Technical record lost its building context.");
                if (calculation.RoofRevisionId is { } technicalRoof)
                {
                    var roof = Get<RoofRevision>(tenant, technicalRoof);
                    Require.That(roof.Status == RoofRevisionStatus.Published && roof.BuildingId == calculation.BuildingId && (!roof.ProjectId.HasValue || roof.ProjectId == calculation.ProjectId), "Technical result roof/building/project differ.");
                }
                break;
            case MaterialDemand demand:
                ValidateQuantity(tenant, demand.ProjectId, demand.Evidence);
                Require.That(Get<Material>(tenant, demand.MaterialId).Unit == demand.Evidence.TargetUnit, "Material demand unit differs from its quantity evidence.");
                if (demand.PurchaseOrderId is { } demandPurchase)
                {
                    var purchase = Get<PurchaseOrder>(tenant, demandPurchase); var line = purchase.Lines.SingleOrDefault(x => x.Id == demand.PurchaseLineId);
                    Require.That(line is not null && purchase.ProjectId == demand.ProjectId && line.MaterialId == demand.MaterialId && line.Quantity == demand.Evidence.Quantity
                        && line.MaterialDemandId == demand.Id && line.QuantityEvidence == demand.Evidence, "Converted material demand lost its purchase-line identity or quantity.");
                }
                break;
            case PurchaseOrder purchase:
                foreach (var line in purchase.Lines)
                    if (line.QuantityEvidence is not null)
                    {
                        Require.That(purchase.ProjectId.HasValue && line.MaterialDemandId.HasValue, "Measurement-linked purchase requires a project and demand.");
                        ValidateQuantity(tenant, purchase.ProjectId!.Value, line.QuantityEvidence);
                        var demand = Get<MaterialDemand>(tenant, line.MaterialDemandId!.Value);
                        Require.That(demand.Status == MaterialDemandStatus.Converted && demand.PurchaseOrderId == purchase.Id && demand.PurchaseLineId == line.Id
                            && demand.ProjectId == purchase.ProjectId && demand.MaterialId == line.MaterialId && demand.Evidence == line.QuantityEvidence,
                            "Purchase line must preserve its converted demand and exact quantity evidence.");
                    }
                break;
            case PortalShare share:
                var sharedQuote = share.SourceKind == "quotes" ? Get<Quote>(tenant, share.SourceId) : null;
                var sharedDocument = share.SourceKind == "documents" ? bundle.Documents.SingleOrDefault(x => x.Data.Id == share.SourceId && x.Data.TenantId == tenant)?.Data : null;
                Require.That((sharedQuote?.IssuedDigest ?? sharedDocument?.Sha256) == share.SourceDigest
                    && (sharedQuote?.ProjectId ?? sharedDocument?.ProjectId) == share.ProjectId, "Portal grant must retain its exact source digest and project."); break;
            case PortalFeedback feedback:
                var grant = Get<PortalShare>(tenant, feedback.ShareId);
                Require.That(grant.ProjectId == feedback.ProjectId && grant.SourceDigest == feedback.SourceDigest, "Portal feedback must retain the granted version/project."); break;
        }
    }

    private void ValidateProjectTemplateApplication(ProjectTemplateApplication application)
    {
        application.Validate();
        var project = Get<Project>(application.TenantId, application.ProjectId);
        var template = Get<ProjectTemplate>(application.TenantId, application.TemplateId);
        var preview = application.Preview!;
        var historicalCustomer = Get<Customer>(application.TenantId, preview.Project.CustomerId);
        var historicalBuilding = Get<Building>(application.TenantId, preview.Project.BuildingId);
        Require.That(historicalCustomer.CreatedAt <= application.AppliedAt && historicalBuilding.CreatedAt <= application.AppliedAt,
            "A project template application's historical customer and building must exist before adoption.");
        Require.That(application.AppliedAt <= bundle.ExportedAt && application.UpdatedAt <= bundle.ExportedAt,
            "A project template application cannot postdate the export.");
        // EntityStore reloads CreatedAt from PostgreSQL when an entity is edited.
        // Retained first-write JSON can carry 100 ns ticks, while that column uses
        // microseconds. Preserve exact snapshot bytes; compare source identity at
        // the storage precision instead of rejecting a legitimate later edit.
        Require.That(project.CreatedAt.Ticks / 10 == preview.Project.CreatedAt.Ticks / 10 && project.Revision > preview.Project.Revision
            && project.UpdatedAt >= application.AppliedAt && template.CreatedAt.Ticks / 10 == preview.Template.CreatedAt.Ticks / 10
            && template.Revision >= preview.Template.Revision,
            "A project template application must retain existing source identities and compatible revision history.");
        if (template.Revision == preview.Template.Revision)
            Require.That(ContentDigest.Of(template) == preview.TemplateDigest,
                "An unchanged template revision must equal its retained application snapshot.");
        foreach (var created in application.Tasks)
            ValidateTemplateTask(Get<TaskItem>(application.TenantId, created.TaskId), application);
    }

    private static void ValidateTemplateTask(TaskItem task, ProjectTemplateApplication application)
    {
        ProjectTemplateWorkflow.EnsureTaskOrigin(task, application);
        if (task.Revision != 1) return;
        var original = application.Preview!.Tasks.Single(x => x.Position == task.TemplateOrigin!.Position);
        Require.That(task.Title == original.Title && task.Description == original.Description && task.DueDate == original.DueDate
            && task.Status == FirstBlick.Core.Business.TaskStatus.Open && task.AssignedEmployeeId is null && !task.Archived,
            "An unedited template task must retain the original planned contents and open unassigned state.");
    }

    private void ValidateCustomerCommunication(CustomerCommunication communication)
    {
        var customer = Get<Customer>(communication.TenantId, communication.CustomerId);
        Require.That(customer.CreatedAt <= communication.RecordedAt && communication.RecordedAt <= customer.UpdatedAt
            && communication.RecordedAt <= bundle.ExportedAt && communication.OccurredAt <= communication.RecordedAt,
            "A communication must fit its customer envelope, recording and export date.");
        if (communication.Contact is { } snapshot)
        {
            var contact = customer.Contacts?.SingleOrDefault(x => x.Id == snapshot.Id);
            Require.That(contact is not null && contact.CreatedAt <= communication.RecordedAt,
                "A communication contact must retain its original customer-owned identity before recording.");
        }
        if (communication.Project is { } projectSnapshot)
        {
            var project = Get<Project>(communication.TenantId, projectSnapshot.Id);
            Require.That(project.CustomerId == communication.CustomerId && project.CreatedAt <= communication.RecordedAt,
                "A communication project must belong to its customer and exist before recording.");
        }
        // Customer, contact and project labels may have changed or been archived
        // since recording. The sealed snapshots retain those historical values.
    }

    private void ValidateVehicleLedgerEntry(VehicleLedgerEntry entry)
    {
        var vehicle = Get<Equipment>(entry.TenantId, entry.VehicleId);
        Require.That(vehicle.Kind == EquipmentKind.Vehicle && vehicle.CreatedAt <= entry.RecordedAt
            && entry.RecordedAt <= vehicle.UpdatedAt && entry.RecordedAt <= bundle.ExportedAt,
            "A vehicle ledger entry must retain its vehicle identity and fit the vehicle envelope and export date.");
        if (entry.ProjectId is { } projectId)
            Require.That(Get<Project>(entry.TenantId, projectId).CreatedAt <= entry.RecordedAt,
                "A vehicle ledger entry's retained project must exist before recording.");
    }

    private void ValidateStockMovement(StockMovement movement)
    {
        var tenant = movement.TenantId;
        var movements = All<StockMovement>(tenant);
        Require.That(!movement.Archived, "Historical inventory movements cannot be archived out of the active stock ledger.");
        Require.That(movement.OccurredAt <= bundle.ExportedAt, "Historical inventory movements cannot postdate the export.");
        Require.That(movement.PostedDigest is { Length: 64 }, "Historical inventory movements must retain their posted digest.");
        if (movement.PurchaseOrderId is { } purchaseId)
        {
            var purchase = Get<PurchaseOrder>(tenant, purchaseId);
            var line = purchase.Lines.SingleOrDefault(x => x.Id == movement.PurchaseLineId);
            Require.That(line is not null && line.MaterialId == movement.MaterialId && movement.ProjectId == purchase.ProjectId
                && movement.Kind is StockMovementKind.Receipt or StockMovementKind.SupplierReturn && movement.UnitCost == line.UnitNet,
                "Purchase receipts and supplier returns must preserve purchase line, material, project and effective cost.");
        }
        if (movement.ReservationId is { } reservationId)
        {
            var reservation = Get<MaterialReservation>(tenant, reservationId);
            Require.That(movement.MaterialId == reservation.MaterialId && movement.ProjectId == reservation.ProjectId
                && movement.Location == reservation.Location && movement.Kind is StockMovementKind.Consumption or StockMovementKind.Return,
                "Consumption and project returns must match their reserved material, project and location.");
        }
        if (movement.Kind is StockMovementKind.TransferIn or StockMovementKind.TransferOut)
        {
            var pair = movements.Where(x => x.Reference == movement.Reference && x.Kind is StockMovementKind.TransferIn or StockMovementKind.TransferOut).ToArray();
            Require.That(Guid.TryParse(movement.Reference, out var correlation) && correlation != Guid.Empty && pair.Length == 2
                && pair.Count(x => x.Kind == StockMovementKind.TransferIn) == 1 && pair.Count(x => x.Kind == StockMovementKind.TransferOut) == 1,
                "Historical warehouse transfers require exactly one incoming and outgoing posting.");
            var incoming = pair.Single(x => x.Kind == StockMovementKind.TransferIn);
            var outgoing = pair.Single(x => x.Kind == StockMovementKind.TransferOut);
            Require.That(incoming.Quantity == -outgoing.Quantity && incoming.Location != outgoing.Location
                && incoming.MaterialId == outgoing.MaterialId && incoming.UnitCost == outgoing.UnitCost
                && incoming.OccurredAt == outgoing.OccurredAt && incoming.TransferSourceId == outgoing.TransferSourceId,
                "Historical transfer pair quantities, value, source and timestamps must agree.");
            if (incoming.TransferSourceId.HasValue)
                _ = InventoryTransferChain.Inspect(incoming, movements);
        }
        if (movement.Kind == StockMovementKind.SupplierReturn)
        {
            Require.That(Guid.TryParse(movement.Reference, out var originalId), "A supplier return requires its original receipt ID.");
            var original = Get<StockMovement>(tenant, originalId);
            Require.That(original.Kind == StockMovementKind.Receipt && original.PostedDigest.Length == 64
                && original.MaterialId == movement.MaterialId && original.UnitCost == movement.UnitCost
                && original.PurchaseOrderId == movement.PurchaseOrderId && original.PurchaseLineId == movement.PurchaseLineId
                && original.ProjectId == movement.ProjectId && original.OccurredAt <= movement.OccurredAt,
                "The supplier return must preserve its original receipt, purchase, value and chronology.");
            if (movement.ReturnTransferId is { } transferId)
            {
                var transfer = Get<StockMovement>(tenant, transferId);
                Require.That(transfer.Kind == StockMovementKind.TransferIn && transfer.PostedDigest.Length == 64
                    && transfer.Location == movement.Location && transfer.MaterialId == movement.MaterialId
                    && transfer.UnitCost == movement.UnitCost && transfer.OccurredAt >= original.OccurredAt
                    && transfer.OccurredAt <= movement.OccurredAt,
                    "A supplier return must use the linked warehouse at the original receipt's value.");
                if (transfer.TransferSourceId.HasValue)
                    Require.That(InventoryTransferChain.Resolve(transfer, movements, movement.OccurredAt).Receipt.Id == original.Id,
                        "A supplier return must retain the complete chain to its own original receipt.");
                else
                {
                    var outgoing = movements.Where(x => x.Kind == StockMovementKind.TransferOut && x.Reference == transfer.Reference).ToArray();
                    Require.That(outgoing.Length == 1 && outgoing[0].Location == original.Location,
                        "A historic unlinked return transfer must originate directly at the receipt warehouse.");
                }
                Require.That(-movements.Where(x => x.Kind == StockMovementKind.SupplierReturn && x.ReturnTransferId == transferId).Sum(x => x.Quantity) <= transfer.Quantity,
                    "Supplier returns exceed the referenced warehouse transfer quantity.");
            }
            else Require.That(movement.Location == original.Location, "A supplier return from another warehouse requires its transfer evidence.");
            Require.That(-movements.Where(x => x.Kind == StockMovementKind.SupplierReturn && x.Reference == original.Id.ToString("D")).Sum(x => x.Quantity) <= original.Quantity,
                "Supplier returns exceed their original purchase receipt quantity.");
            _ = InventoryTransferChain.Remaining(original, movements);
        }
        if (movement.Kind == StockMovementKind.Return && movement.ReservationId.HasValue)
        {
            Require.That(Guid.TryParse(movement.Reference, out var originalId), "A project return requires its original consumption ID.");
            var original = Get<StockMovement>(tenant, originalId);
            Require.That(original.Kind == StockMovementKind.Consumption && original.PostedDigest.Length == 64
                && original.MaterialId == movement.MaterialId && original.ProjectId == movement.ProjectId
                && original.ReservationId == movement.ReservationId && original.Location == movement.Location
                && original.UnitCost == movement.UnitCost && original.OccurredAt <= movement.OccurredAt,
                "A project return must retain its original consumption, reservation, value and chronology.");
            Require.That(movements.Where(x => x.Kind == StockMovementKind.Return && x.Reference == original.Id.ToString("D")).Sum(x => x.Quantity) <= -original.Quantity,
                "Project returns exceed their original consumption quantity.");
        }
    }

    private void ValidateEquipmentHistory(Equipment equipment)
    {
        var history = All<EquipmentInspection>(equipment.TenantId).Where(x => x.EquipmentId == equipment.Id).ToArray();
        var pending = history.Where(x => x.Status == EquipmentInspectionStatus.Scheduled).ToArray();
        Require.That(pending.Length <= 1 && equipment.PendingInspectionId == pending.SingleOrDefault()?.Id,
            "An equipment resource must retain exactly its own outstanding inspection link.");
        if (pending.Length == 1)
            Require.That(pending[0].EquipmentSnapshot == EquipmentInspectionWorkflow.Snapshot(equipment),
                "The outstanding inspection must retain the currently planned equipment identity.");
        var completed = history.Where(x => x.Status == EquipmentInspectionStatus.Completed).ToArray();
        Require.That(completed.All(x => x.Completion is not null), "Completed equipment inspections require their sealed result evidence.");
        var chronological = completed.OrderBy(x => x.Completion!.RecordedAt).ThenBy(x => x.Completion!.PerformedOn).ToArray();
        for (var index = 1; index < chronological.Length; index++)
            Require.That(chronological[index - 1].Completion!.PerformedOn <= chronological[index].Completion!.PerformedOn,
                "A later recorded equipment result cannot predate the previously recorded performance date.");
        if (equipment.LastInspectionId is not { } lastId)
        {
            Require.That(completed.Length == 0 && equipment.LastInspectionOn is null && !equipment.InspectionBlocked,
                "Equipment result flags require their completed inspection evidence.");
            return;
        }
        var last = completed.SingleOrDefault(x => x.Id == lastId);
        Require.That(last?.Completion is not null, "The equipment's latest result must refer to its own completed inspection.");
        var evidence = last!.Completion!;
        Require.That(completed.All(x => x.Completion!.RecordedAt <= evidence.RecordedAt && x.Completion.PerformedOn <= evidence.PerformedOn)
            && equipment.LastInspectionOn == evidence.PerformedOn
            && equipment.InspectionBlocked == (evidence.Result == EquipmentInspectionResult.Failed),
            "The equipment availability projection must retain its latest recorded result.");
        if (equipment.InspectionBlocked)
            Require.That(!equipment.Available, "A failed inspection cannot be imported as available equipment.");
        else Require.That(equipment.NextInspectionOn == evidence.NextInspectionOn,
            "The next equipment inspection date must retain the latest completed result's explicit date.");
    }

    private void ValidateQuantity(Guid tenant, Guid project, QuantityEvidence? evidence)
    {
        if (evidence is null) return;
        var measurement = Get<Measurement>(tenant, evidence.MeasurementId);
        // Retained historical references may be archived; verify immutable values directly.
        Require.That(measurement.ProjectId == project && measurement.Status == MeasurementStatus.Frozen && measurement.Snapshot?.Digest == evidence.MeasurementDigest, "Quantity basis must preserve its frozen project measurement.");
        var line = measurement.Snapshot!.Lines.SingleOrDefault(x => x.Id == evidence.MeasurementLineId);
        Require.That(line is not null && line.Quantity == evidence.BaseQuantity && line.Unit == evidence.BaseUnit, "Quantity evidence no longer matches its original measured value.");
    }
    private void ValidateActors(Guid tenant, JsonElement value)
    {
        string[] names = ["userId", "allocatedBy", "reversedBy", "approvedBy", "submittedBy", "reviewedBy", "calculatedBy", "grantedBy", "recipientUserId", "authorUserId", "actorId", "plannedBy", "recordedBy", "cancelledBy", "createdBy", "updatedBy", "archivedBy", "voidedBy", "appliedBy"];
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (names.Contains(property.Name) && property.Value.ValueKind != JsonValueKind.Null)
                    Require.That(property.Value.ValueKind == JsonValueKind.String && property.Value.TryGetGuid(out var actor) && memberships.Contains((tenant, actor)), "A historical actor/user must retain a supplied tenant membership, including inactive members.");
                ValidateActors(tenant, property.Value);
            }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) ValidateActors(tenant, item);
    }
    public void ValidateDocument(MigrationDocument item)
    {
        var document = item.Data;
        Require.That(memberships.Contains((document.TenantId, document.UploadedBy)), "Document uploader must have a mapped historical membership.");
        if (document.ProjectId is { } project) _ = Get<Project>(document.TenantId, project);
        if (document.SourceId is not { } sourceId) return;
        var source = RequireReference(document.TenantId, document.SourceKind!, sourceId);
        if (source is CustomerCommunication or VehicleCost)
        {
            var originalDigest = source is CustomerCommunication communication ? communication.Digest : ((VehicleCost)source).Digest;
            var recordedAt = source is CustomerCommunication journal ? journal.RecordedAt : ((VehicleCost)source).RecordedAt;
            var renderer = source is CustomerCommunication ? "customer-communication-original-v1" : "vehicle-cost-original-v1";
            Require.That(document.SourceDigest == originalDigest,
                "A ledger original must bind to its exact retained source digest.");
            Require.That(document.ProjectId is null && document.ProjectKind is null
                && document.RendererVersion == renderer && document.ContentType is "application/pdf" or "image/png" or "image/jpeg",
                "A ledger original must retain its source-specific format and cannot acquire project-based access.");
            Require.That(!string.IsNullOrWhiteSpace(document.VersionId) && document.VersionId != "null",
                "A ledger original must retain its immutable source version.");
            Require.That(document.Revision >= 2,
                "A verified ledger original must retain its completed upload revision.");
            Require.That(document.Name.Length <= 180 && !document.Name.Contains('\\'),
                "A ledger original must retain a supported plain filename of at most 180 characters.");
            Require.That(document.CreatedAt >= recordedAt && document.CreatedAt <= bundle.ExportedAt,
                "A ledger original cannot predate its retained entry or postdate the export.");
            return;
        }
        if (source is EquipmentInspection inspection)
        {
            Require.That(inspection.Status == EquipmentInspectionStatus.Completed && inspection.Completion is not null,
                "An equipment inspection original must refer to a completed inspection.");
            Require.That(document.SourceDigest == inspection.Completion!.Digest,
                "An equipment inspection original must bind to the sealed completion digest.");
            Require.That(document.ProjectId is null && document.ProjectKind is null
                && document.RendererVersion == "equipment-inspection-original-v1"
                && document.ContentType is "application/pdf" or "image/png" or "image/jpeg",
                "An equipment inspection original must retain its supported document format and independent equipment source.");
            Require.That(!string.IsNullOrWhiteSpace(document.VersionId) && document.VersionId != "null",
                "An equipment inspection original must retain its immutable source version.");
            Require.That(document.CreatedAt >= inspection.Completion.RecordedAt && document.CreatedAt <= bundle.ExportedAt,
                "An equipment inspection original cannot predate its sealed completion or postdate the export.");
            return;
        }
        var digest = source switch { Quote quote => quote.IssuedDigest, Invoice invoice => invoice.IssuedDigest, TechnicalRecord technical => technical.Digest, RoofRevision roof => roof.Snapshot?.Digest,
            Measurement measurement => measurement.Snapshot?.Digest, SupplierInvoice supplier => supplier.ApprovedDigest, PurchaseOrder purchase => purchase.OrderedDigest, _ => null };
        Require.That(digest is { Length: 64 } && document.SourceDigest == digest, "A generated historical document must bind to its original supported frozen source.");
    }

    public void ValidateLedger(Guid tenant)
    {
        VehicleLedgerWorkflow.ValidateActiveTrips(All<VehicleTrip>(tenant), All<VehicleLedgerReversal>(tenant));
        var invoices = All<Invoice>(tenant).Where(x => x.Status == InvoiceStatus.Issued).ToList(); var payments = All<PaymentAllocation>(tenant);
        var supplierInvoices = All<SupplierInvoice>(tenant).Where(x => x.Status == SupplierInvoiceStatus.Approved).ToList(); var supplierPayments = All<SupplierPaymentAllocation>(tenant);
        foreach (var bank in All<BankTransaction>(tenant)) Require.That(PaymentWorkflow.UsedBankAmount(bank, payments, supplierPayments) <= Math.Abs(bank.Amount), "Combined customer/supplier/return allocations exceed immutable bank capacity.");
        static string LineKey(QuoteLine line) => ContentDigest.Of(line with { Quantity = 1m });
        foreach (var order in All<Order>(tenant))
        {
            var billed = invoices.Where(x => x.OrderId == order.Id).ToList();
            var changes = All<ChangeOrder>(tenant).Where(x => x.OrderId == order.Id && x.Status == ChangeOrderStatus.Accepted).ToList();
            var contracted = order.Contract!.Lines.Select(x => x.Inputs).Concat(changes.SelectMany(x => x.Snapshot!.Lines.Select(line => line.Inputs))).GroupBy(LineKey).ToDictionary(x => x.Key, x => x.Sum(line => line.Quantity));
            var quantities = billed.SelectMany(invoice => invoice.Snapshot!.Lines.Select(line => (Key: LineKey(line.Inputs), Quantity: (invoice.Kind == InvoiceKind.CreditNote ? -1m : 1m) * line.Inputs.Quantity))).GroupBy(x => x.Key);
            Require.That(quantities.All(x => contracted.TryGetValue(x.Key, out var quantity) && x.Sum(line => line.Quantity) >= 0 && x.Sum(line => line.Quantity) <= quantity), "Cumulative invoice lines exceed retained contract quantities or effective prices.");
            Require.That(billed.Sum(x => (x.Kind == InvoiceKind.CreditNote ? -1m : 1m) * x.Snapshot!.Gross) <= order.Contract.Gross + changes.Sum(x => x.Snapshot!.Gross), "Historical order is overbilled.");
        }
        foreach (var invoice in invoices.Where(x => x.Kind != InvoiceKind.CreditNote))
        {
            var credits = invoices.Where(x => x.OriginalInvoiceId == invoice.Id).ToList();
            var originalLines = invoice.Snapshot!.Lines.Select(x => x.Inputs).GroupBy(LineKey).ToDictionary(x => x.Key, x => x.Sum(line => line.Quantity));
            Require.That(credits.SelectMany(x => x.Snapshot!.Lines.Select(line => line.Inputs)).GroupBy(LineKey).All(x => originalLines.TryGetValue(x.Key, out var quantity) && x.Sum(line => line.Quantity) <= quantity), "Credit lines exceed their original quantities/prices.");
            var balance = PaymentWorkflow.Balance(invoice, payments, credits, DateOnly.FromDateTime(bundle.ExportedAt.UtcDateTime));
            var active = payments.Where(x => x.InvoiceId == invoice.Id && !x.ReversedAt.HasValue).ToList();
            var receipts = active.Where(x => x.Direction == PaymentDirection.Receipt).Sum(x => x.Amount); var refunds = active.Where(x => x.Direction == PaymentDirection.Refund).Sum(x => x.Amount);
            Require.That(receipts + active.Sum(x => x.DiscountAmount) <= invoice.Snapshot.Gross && refunds <= Math.Max(0, receipts + balance.Discount + balance.Credits - balance.Gross), "Customer receipts, discounts or refunds exceed their commercial capacity.");
        }
        foreach (var invoice in supplierInvoices.Where(x => x.Kind == SupplierInvoiceKind.Invoice))
        {
            var credits = supplierInvoices.Where(x => x.OriginalInvoiceId == invoice.Id).ToList(); var balance = PayablesWorkflow.Balance(invoice, supplierPayments, credits, DateOnly.FromDateTime(bundle.ExportedAt.UtcDateTime));
            Require.That(credits.Sum(x => x.Net) <= invoice.Net && credits.Sum(x => x.Tax) <= invoice.Tax, "Supplier credit net/tax exceed the original invoice.");
            var active = supplierPayments.Where(x => x.SupplierInvoiceId == invoice.Id && !x.ReversedAt.HasValue).ToList();
            var paid = active.Where(x => x.Direction == SupplierPaymentDirection.Payment).Sum(x => x.Amount); var refunded = active.Where(x => x.Direction == SupplierPaymentDirection.Refund).Sum(x => x.Amount);
            Require.That(paid + active.Sum(x => x.DiscountAmount) <= invoice.Gross && refunded <= Math.Max(0, paid + balance.Discount + balance.Credits - balance.Gross), "Supplier payments, discounts or refunds exceed commercial capacity.");
        }
        var movements = All<StockMovement>(tenant); var reservations = All<MaterialReservation>(tenant);
        foreach (var key in movements.Select(x => (x.MaterialId, x.Location)).Concat(reservations.Select(x => (x.MaterialId, x.Location))).Distinct())
            Require.That(InventoryRules.Balance(tenant, key.MaterialId, key.Location, movements, reservations).Available >= 0, "Imported stock or outstanding reservations have insufficient capacity.");
        foreach (var reservation in reservations) Require.That(-movements.Where(x => x.ReservationId == reservation.Id).Sum(x => x.Quantity) == reservation.ConsumedQuantity, "Reservation consumption and immutable stock movements differ.");
        foreach (var purchase in All<PurchaseOrder>(tenant))
        {
            foreach (var line in purchase.Lines) Require.That(InventoryRules.ReceivedQuantity(purchase, line.Id, movements) <= line.Quantity,
                "Historical purchase receipts exceed the ordered line.");
            if (purchase.Status is PurchaseOrderStatus.Ordered or PurchaseOrderStatus.PartiallyReceived or PurchaseOrderStatus.Received)
                Require.That(purchase.Status == InventoryRules.ReceiptStatus(purchase, movements),
                    "Imported purchase order status differs from its net purchase receipts and supplier returns.");
        }
    }
}
