using FirstBlick.Core.Business;

namespace FirstBlick.Api;

internal static class EditRules
{
    public static void EnsureGenericCreate(TenantEntity e, bool retainPurchaseLinks = false, bool retainElectronicOrigin = false)
    {
        Guard.Require(e is not Order and not BankTransaction and not PaymentAllocation and not SupplierPaymentAllocation and not StockMovement and not FormSubmission and not TechnicalRecord and not PortalShare and not PortalFeedback and not MaterialDemand and not CollectionCase,
            "command_required", "This record can only be created through its dedicated business command.");
        switch (e)
        {
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
            case MaintenanceContract m: Guard.Require(m.CompletedOccurrences == 0, "command_required", "A new maintenance contract cannot invent completed occurrences."); break;
            case RoofRevision r: Guard.Require(r.Status == RoofRevisionStatus.Draft && r.Snapshot is null && r.PublishedAt is null && r.Version == 1 && r.PreviousRevisionId is null, "command_required", "Create a first draft roof; subsequent versions use the revision command."); break;
            case Measurement m: Guard.Require(m.Status == MeasurementStatus.Draft && m.Snapshot is null && m.FrozenAt is null, "command_required", "Create a draft measurement; freezing uses its dedicated command."); break;
        }
    }
    public static void EnsureGenericUpdate(TenantEntity old, TenantEntity next)
    {
        Guard.Require(old.GetType() == next.GetType() && !old.Archived, "record_immutable", "This record cannot be edited.");
        Guard.Require(old is not Order and not BankTransaction and not PaymentAllocation and not SupplierPaymentAllocation and not StockMovement and not FormSubmission and not MaterialReservation and not Acceptance and not TechnicalRecord and not PortalShare and not PortalFeedback and not MaterialDemand and not CollectionCase,
            "command_required", "Use the dedicated auditable command for this record.");
        switch (old)
        {
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
            case SupplierInvoice i:
                Guard.Require(i.Status == SupplierInvoiceStatus.Draft, "supplier_invoice_immutable", "Approved supplier invoices require separate corrections.");
                Guard.Require(i.ElectronicSource == ((SupplierInvoice)next).ElectronicSource,
                    "einvoice_evidence_changed", "The original electronic invoice, validation and booking evidence cannot be removed, replaced or fabricated by a generic edit.");
                EnsureGenericCreate(next, retainElectronicOrigin: true); break;
            case MaintenanceContract m: Guard.Require(((MaintenanceContract)next).CompletedOccurrences == m.CompletedOccurrences && (m.CompletedOccurrences == 0 || ((MaintenanceContract)next).StartDate == m.StartDate), "command_required", "Maintenance occurrence history uses the completion command."); break;
            case RoofRevision r:
                var nextRoof = (RoofRevision)next;
                Guard.Require(r.Status == RoofRevisionStatus.Draft && nextRoof.Status == RoofRevisionStatus.Draft && nextRoof.Snapshot is null && nextRoof.PublishedAt is null && r.Version == nextRoof.Version && r.PreviousRevisionId == nextRoof.PreviousRevisionId,
                    "roof_immutable", "Published roofs require a new draft revision; predecessor and version cannot be edited."); break;
            case Measurement m: Guard.Require(m.Status == MeasurementStatus.Draft, "measurement_immutable", "Frozen measurements retain their input evidence."); EnsureGenericCreate(next); break;
        }
    }
    public static void EnsureArchivable(TenantEntity e)
    {
        Guard.Require(e is not Order and not Invoice and not SupplierInvoice and not PaymentAllocation and not SupplierPaymentAllocation and not BankTransaction and not StockMovement and not MaterialReservation and not FormSubmission and not Acceptance and not TechnicalRecord and not PortalShare and not PortalFeedback and not MaterialDemand and not CollectionCase,
            "record_immutable", "Business evidence is retained; use a correction or reversal.");
        if (e is Quote q) Guard.Require(q.Status == QuoteStatus.Draft, "quote_immutable", "Issued quote versions are retained.");
        if (e is ChangeOrder c) Guard.Require(c.Status != ChangeOrderStatus.Accepted, "change_immutable", "Accepted changes remain part of the contract and its financial history.");
        if (e is PurchaseOrder p) Guard.Require(p.Status == PurchaseOrderStatus.Draft, "purchase_immutable", "Issued purchase evidence is retained.");
        if (e is MaintenanceContract contract) Guard.Require(contract.CompletedOccurrences == 0, "maintenance_immutable", "Completed maintenance history is retained; deactivate the contract to stop future work.");
        if (e is TimeEntry t) Guard.Require(!t.Approved, "time_immutable", "Approved time is retained.");
        if (e is RoofRevision r) Guard.Require(r.Status == RoofRevisionStatus.Draft, "roof_immutable", "Published roof evidence is retained.");
        if (e is Measurement m) Guard.Require(m.Status == MeasurementStatus.Draft, "measurement_immutable", "Frozen measurement evidence is retained.");
    }
}
