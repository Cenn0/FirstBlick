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
            case Project project:
                Require.That(Get<Building>(tenant, project.BuildingId).CustomerId == project.CustomerId, "Project and building customer differ."); break;
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
                if (movement.PurchaseOrderId is { } movementPurchase)
                {
                    var purchase = Get<PurchaseOrder>(tenant, movementPurchase); var line = purchase.Lines.SingleOrDefault(x => x.Id == movement.PurchaseLineId);
                    Require.That(line is not null && line.MaterialId == movement.MaterialId && movement.ProjectId == purchase.ProjectId && movement.Kind == StockMovementKind.Receipt && movement.UnitCost == line.UnitNet, "Receipt must preserve purchase line, material, project and effective cost.");
                }
                if (movement.ReservationId is { } reservationId)
                {
                    var reservation = Get<MaterialReservation>(tenant, reservationId);
                    Require.That(movement.MaterialId == reservation.MaterialId && movement.ProjectId == reservation.ProjectId && movement.Location == reservation.Location && movement.Kind == StockMovementKind.Consumption, "Consumption must match its reserved material, project and location.");
                }
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
        string[] names = ["userId", "allocatedBy", "reversedBy", "approvedBy", "submittedBy", "reviewedBy", "calculatedBy", "grantedBy", "recipientUserId", "authorUserId", "actorId"];
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
        var digest = source switch { Quote quote => quote.IssuedDigest, Invoice invoice => invoice.IssuedDigest, TechnicalRecord technical => technical.Digest, RoofRevision roof => roof.Snapshot?.Digest,
            Measurement measurement => measurement.Snapshot?.Digest, SupplierInvoice supplier => supplier.ApprovedDigest, PurchaseOrder purchase => purchase.OrderedDigest, _ => null };
        Require.That(digest is { Length: 64 } && document.SourceDigest == digest, "A generated historical document must bind to its original supported frozen source.");
    }

    public void ValidateLedger(Guid tenant)
    {
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
            foreach (var line in purchase.Lines) Require.That(movements.Where(x => x.PurchaseOrderId == purchase.Id && x.PurchaseLineId == line.Id).Sum(x => x.Quantity) <= line.Quantity, "Historical purchase receipts exceed the ordered line.");
    }
}
