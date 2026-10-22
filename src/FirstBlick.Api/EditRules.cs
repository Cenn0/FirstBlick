using FirstBlick.Core.Business;

namespace FirstBlick.Api;

internal static class EditRules
{
    public static void EnsureGenericCreate(TenantEntity e, bool retainPurchaseLinks = false, bool retainElectronicOrigin = false)
    {
        Guard.Require(e is not ProjectTemplate and not ProjectTemplateApplication && e is not TaskItem { TemplateOrigin: not null },
            "command_required", "Template applications and task provenance require their reviewed command.");
        Guard.Require(e is not Order and not BankTransaction and not PaymentAllocation and not SupplierPaymentAllocation and not StockMovement and not FormSubmission and not TechnicalRecord and not PortalShare and not PortalFeedback and not MaterialDemand and not CollectionCase and not MaintenanceVisit and not EquipmentInspection and not CustomerContactChange and not CustomerCommunication and not CustomerCommunicationVoid and not VehicleTrip and not VehicleCost and not VehicleLedgerReversal,
            "command_required", "This record can only be created through its dedicated business command.");
        Guard.Require(e is not TaskItem { PredecessorTaskIds.Count: > 0 }, "task_dependency_command_required", "Task dependencies use their dedicated command.");
        switch (e)
        {
            case Customer customer:
                Guard.Require(customer.Contacts is null || customer.Contacts.Count == 0,
                    "command_required", "Customer contacts are created through their dedicated commands."); break;
            case Equipment equipment:
                Guard.Require(equipment.PendingInspectionId is null && equipment.LastInspectionId is null && equipment.LastInspectionOn is null && !equipment.InspectionBlocked,
                    "command_required", "Equipment inspection evidence must be created by its dedicated workflow."); break;
            case CatalogItem { GaebOrigin: not null }: throw new DomainException("command_required", "GAEB origins may only be created by reviewed catalogue import.");
            case Quote q: Guard.Require(q.Status == QuoteStatus.Draft && q.Snapshot is null && q.Presentation is null && q.Deliveries.Count == 0 && q.AcceptedAt is null && q.Number == "", "command_required", "Create a draft quote; issue and delivery are separate commands."); break;
            case Invoice i: Guard.Require(i.Status == InvoiceStatus.Draft && i.Snapshot is null && i.Number == "" && i.Kind != InvoiceKind.CreditNote && i.OriginalInvoiceId is null, "command_required", "Create a draft receivable; corrections and issuance use dedicated commands."); break;
            case ChangeOrder c: Guard.Require(c.Status == ChangeOrderStatus.Draft && c.Snapshot is null && c.AcceptedAt is null, "command_required", "Create a draft change order."); break;
            case PurchaseOrder p:
                Guard.Require(p.Status == PurchaseOrderStatus.Draft && p.OrderedDigest == "" && p.Number == "", "command_required", "Create a draft purchase order.");
                Guard.Require(retainPurchaseLinks || p.Lines.All(x => x.MaterialDemandId is null), "command_required", "Demand links require the reviewed demand-to-purchase command."); break;
            case MaterialReservation r: Guard.Require(r.ConsumedQuantity == 0 && r.ReleasedQuantity == 0, "command_required", "A new reservation cannot contain previous movements."); break;
            case FormDefinition f: Guard.Require(!f.Published && f.PublishedDigest == "", "command_required", "Create an unpublished form definition."); break;
            case SupplierInvoice i:
                Guard.Require(i.Status == SupplierInvoiceStatus.Draft && i.ApprovedAt is null && i.ApprovedBy is null && i.ApprovedDigest == "", "command_required", "Create an unapproved supplier invoice.");
                Guard.Require(retainElectronicOrigin || i.ElectronicSource is null, "command_required", "Electronic source evidence requires original validation, review and archival through the dedicated import command."); break;
            case TimeEntry t: Guard.Require(!t.Approved, "command_required", "Create unapproved time; approval uses its dedicated command."); break;
            case TrainingRecord t: Guard.Require(t.Status == TrainingRecordStatus.Draft && t.ReviewedAt is null && t.MentorFeedback == "", "command_required", "Create a draft training record without a review."); break;
            case DiaryEntry d: Guard.Require(d.Status == DiaryStatus.Draft && d.Submission is null && d.Review is null, "command_required", "Create a draft diary report; submission and review use dedicated commands."); break;
            case MaintenanceContract m: Guard.Require(m.Workdays is null, "maintenance_workdays_command_required", "Configure workdays explicitly after creating the contract."); Guard.Require(m.CompletedOccurrences == 0, "command_required", "A new maintenance contract cannot invent completed occurrences."); break;
            case RoofRevision r: Guard.Require(r.Status == RoofRevisionStatus.Draft && r.Snapshot is null && r.PublishedAt is null && r.Version == 1 && r.PreviousRevisionId is null, "command_required", "Create a first draft roof; subsequent versions use the revision command."); break;
            case Measurement m: Guard.Require(m.Status == MeasurementStatus.Draft && m.Snapshot is null && m.FrozenAt is null, "command_required", "Create a draft measurement; freezing uses its dedicated command."); break;
        }
    }
    public static void EnsureGenericUpdate(TenantEntity old, TenantEntity next)
    {
        Guard.Require(old is not ProjectTemplate and not ProjectTemplateApplication, "command_required", "Use the dedicated project-template command.");
        Guard.Require(old.GetType() == next.GetType() && !old.Archived, "record_immutable", "This record cannot be edited.");
        Guard.Require(old is not Order and not BankTransaction and not PaymentAllocation and not SupplierPaymentAllocation and not StockMovement and not FormSubmission and not MaterialReservation and not Acceptance and not TechnicalRecord and not PortalShare and not PortalFeedback and not MaterialDemand and not CollectionCase and not MaintenanceVisit and not EquipmentInspection and not CustomerContactChange and not CustomerCommunication and not CustomerCommunicationVoid and not VehicleTrip and not VehicleCost and not VehicleLedgerReversal,
            "command_required", "Use the dedicated auditable command for this record.");
        switch (old)
        {
            case TaskItem task:
                TaskDependencies.PreserveGenericEdit(task, (TaskItem)next);
                Guard.Require(((TaskItem)next).TemplateOrigin == task.TemplateOrigin,
                    "project_template_origin", "The original task provenance cannot be removed or replaced.");
                break;
            case Customer customer:
                var nextCustomer = (Customer)next;
                if (nextCustomer.Contacts is null) nextCustomer.Contacts = customer.Contacts;
                else Guard.Require(ContentDigest.Of(nextCustomer.Contacts) == ContentDigest.Of(customer.Contacts ?? []),
                    "contact_command_required", "Customer contacts cannot be replaced or removed through generic edits.");
                // Preserve an absent historical field even if a generic client sends an empty list.
                if (customer.Contacts is null && nextCustomer.Contacts?.Count == 0) nextCustomer.Contacts = null;
                break;
            case Equipment equipment:
                var changedEquipment = (Equipment)next;
                Guard.Require(changedEquipment.PendingInspectionId == equipment.PendingInspectionId && changedEquipment.LastInspectionId == equipment.LastInspectionId
                    && changedEquipment.LastInspectionOn == equipment.LastInspectionOn && changedEquipment.InspectionBlocked == equipment.InspectionBlocked,
                    "inspection_evidence_immutable", "Equipment inspection history cannot be removed, replaced or fabricated by a generic edit.");
                Guard.Require(!equipment.LastInspectionId.HasValue || changedEquipment.NextInspectionOn == equipment.NextInspectionOn,
                    "command_required", "The next inspection date of inspected equipment is changed through a new recorded inspection.");
                Guard.Require(!equipment.InspectionBlocked || !changedEquipment.Available,
                    "inspection_blocked", "A failed inspection requires a new successful inspection before equipment can be made available.");
                Guard.Require(!equipment.PendingInspectionId.HasValue
                    || (EquipmentInspectionWorkflow.Snapshot(changedEquipment) with { Id = equipment.Id }) == EquipmentInspectionWorkflow.Snapshot(equipment),
                    "inspection_equipment_changed", "Cancel the outstanding inspection before changing its equipment identity."); break;
            case CatalogItem catalog:
                Guard.Require(((CatalogItem)next).GaebOrigin == catalog.GaebOrigin,
                    "gaeb_origin_immutable", "GAEB source evidence cannot be changed by editing a catalogue item."); break;
            case Quote q:
                Guard.Require(q.Status == QuoteStatus.Draft, "quote_immutable", "An issued quote is immutable; create a new version.");
                EnsureGenericCreate(next); break;
            case Invoice i:
                Guard.Require(i.Status == InvoiceStatus.Draft && ((Invoice)next).Status == InvoiceStatus.Draft && ((Invoice)next).Snapshot is null && ((Invoice)next).Kind == i.Kind && ((Invoice)next).OriginalInvoiceId == i.OriginalInvoiceId,
                    "invoice_immutable", "Issued invoices require a separate correction document."); break;
            case ChangeOrder c: Guard.Require(c.Status == ChangeOrderStatus.Draft, "change_immutable", "Accepted changes are immutable."); EnsureGenericCreate(next); break;
            case PurchaseOrder p: Guard.Require(p.Status == PurchaseOrderStatus.Draft, "purchase_immutable", "Ordered quantities and prices are immutable."); EnsureGenericCreate(next, true); break;
            case FormDefinition f: Guard.Require(!f.Published, "form_immutable", "Published forms require a new version."); EnsureGenericCreate(next); break;
            case TimeEntry t: Guard.Require(!t.Approved && !((TimeEntry)next).Approved, "time_immutable", "Approved time requires an explicit correction; approval uses its dedicated command."); break;
            case TrainingRecord t: Guard.Require(t.Status == TrainingRecordStatus.Draft, "training_immutable", "Submitted training records are sealed; review uses its dedicated command."); EnsureGenericCreate(next); break;
            case DiaryEntry diary:
                var changedDiary = (DiaryEntry)next;
                Guard.Require(diary.Status is DiaryStatus.Draft or DiaryStatus.Returned, "diary_immutable", "Submitted and approved diary reports retain their exact contents.");
                Guard.Require(changedDiary.Status == diary.Status && changedDiary.Submission == diary.Submission && changedDiary.Review == diary.Review,
                    "command_required", "Diary submission and review evidence cannot be altered by an edit."); break;
            case SupplierInvoice i:
                Guard.Require(i.Status == SupplierInvoiceStatus.Draft, "supplier_invoice_immutable", "Approved supplier invoices require separate corrections.");
                Guard.Require(i.ElectronicSource == ((SupplierInvoice)next).ElectronicSource,
                    "einvoice_evidence_changed", "The original electronic invoice, validation and booking evidence cannot be removed, replaced or fabricated by a generic edit.");
                EnsureGenericCreate(next, retainElectronicOrigin: true); break;
            case MaintenanceContract m:
                var changedContract = (MaintenanceContract)next;
                if (changedContract.Workdays is null) changedContract.Workdays = m.Workdays;
                else Guard.Require(ContentDigest.Of(changedContract.Workdays) == ContentDigest.Of(m.Workdays), "maintenance_workdays_command_required", "Configure workdays through the dedicated command.");
                Guard.Require(((MaintenanceContract)next).CompletedOccurrences == m.CompletedOccurrences && (m.CompletedOccurrences == 0 || ((MaintenanceContract)next).StartDate == m.StartDate), "command_required", "Maintenance occurrence history uses the completion command."); break;
            case RoofRevision r:
                var nextRoof = (RoofRevision)next;
                Guard.Require(r.Status == RoofRevisionStatus.Draft && nextRoof.Status == RoofRevisionStatus.Draft && nextRoof.Snapshot is null && nextRoof.PublishedAt is null && r.Version == nextRoof.Version && r.PreviousRevisionId == nextRoof.PreviousRevisionId,
                    "roof_immutable", "Published roofs require a new draft revision; predecessor and version cannot be edited."); break;
            case Measurement m: Guard.Require(m.Status == MeasurementStatus.Draft, "measurement_immutable", "Frozen measurements retain their input evidence."); EnsureGenericCreate(next); break;
        }
    }
    public static void EnsureArchivable(TenantEntity e)
    {
        Guard.Require(e is not ProjectTemplate and not ProjectTemplateApplication, "record_immutable", "Template history is retained; change a template's availability through its dedicated command.");
        Guard.Require(e is not Order and not Invoice and not SupplierInvoice and not PaymentAllocation and not SupplierPaymentAllocation and not BankTransaction and not StockMovement and not MaterialReservation and not FormSubmission and not Acceptance and not TechnicalRecord and not PortalShare and not PortalFeedback and not MaterialDemand and not CollectionCase and not MaintenanceVisit and not EquipmentInspection and not CustomerContactChange and not CustomerCommunication and not CustomerCommunicationVoid and not VehicleTrip and not VehicleCost and not VehicleLedgerReversal,
            "record_immutable", "Business evidence is retained; use a correction or reversal.");
        if (e is Quote q) Guard.Require(q.Status == QuoteStatus.Draft, "quote_immutable", "Issued quote versions are retained.");
        if (e is ChangeOrder c) Guard.Require(c.Status != ChangeOrderStatus.Accepted, "change_immutable", "Accepted changes remain part of the contract and its financial history.");
        if (e is PurchaseOrder p) Guard.Require(p.Status == PurchaseOrderStatus.Draft, "purchase_immutable", "Issued purchase evidence is retained.");
        if (e is MaintenanceContract contract) Guard.Require(contract.CompletedOccurrences == 0, "maintenance_immutable", "Completed maintenance history is retained; deactivate the contract to stop future work.");
        if (e is TimeEntry t) Guard.Require(!t.Approved, "time_immutable", "Approved time is retained.");
        if (e is DiaryEntry d) Guard.Require(d.Status == DiaryStatus.Draft, "diary_immutable", "Submitted and reviewed diary reports are retained.");
        if (e is RoofRevision r) Guard.Require(r.Status == RoofRevisionStatus.Draft, "roof_immutable", "Published roof evidence is retained.");
        if (e is Measurement m) Guard.Require(m.Status == MeasurementStatus.Draft, "measurement_immutable", "Frozen measurement evidence is retained.");
    }
}
