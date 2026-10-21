using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;
using FirstBlick.Infrastructure.Integrations;
using FirstBlick.Infrastructure.Persistence;
using FirstBlick.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record ElectronicImportRequest(
    [property: JsonRequired] string SourceBase64,
    [property: JsonRequired] string ExpectedSourceSha256,
    [property: JsonRequired] Guid SupplierId,
    [property: JsonRequired] long ExpectedSupplierRevision,
    [property: JsonRequired] string ReviewedSourceSellerName,
    [property: JsonRequired] string ReviewedSourceBuyerName,
    [property: JsonRequired] string ReviewEvidence,
    DateOnly? SelectedDueDate = null, Guid? PurchaseOrderId = null, long? ExpectedPurchaseOrderRevision = null,
    Guid? OriginalInvoiceId = null, long? ExpectedOriginalInvoiceRevision = null, string? PreviewDigest = null);
public sealed record ElectronicOutputRequest([property: JsonRequired] ElectronicInvoiceOutputInput Mapping, string? PreviewDigest = null);
public sealed record ElectronicValidationSummary(string Profile, string ConfigurationVersion, string EngineVersion,
    string SourceSha256, string ConfigurationSha256, bool Accepted, string ReportSha256, IReadOnlyList<XmlRuleIssue> Issues);
public sealed record ElectronicImportPreview(string Contract, Guid TenantId, Guid SupplierId, long SupplierRevision,
    string SupplierName, ElectronicValidationSummary Validation, ElectronicPostingFacts? Facts,
    DateOnly? EffectiveDueDate, string? Digest, bool CanImport, bool AutomaticallyApproved,
    IReadOnlyList<string> BlockingReasons);
public sealed record ElectronicOutputPreview(string Contract, Guid InvoiceId, long InvoiceRevision, string InvoiceDigest,
    string MappingDigest, string XmlSha256, ElectronicValidationSummary Validation, string Digest, bool CanExport);

/// <summary>
/// Original rule execution, explicit party/date mapping, immutable archival and existing
/// approval/reconciliation workflows. No financial approval, email or payment is initiated
/// merely because XML passed its formal validation.
/// </summary>
public static class OfficialInvoiceEndpoints
{
    private sealed record ImportWork(ElectronicImportPreview Preview, byte[] Bytes,
        ElectronicValidationReport Validation, Supplier Supplier, ElectronicPostingFacts? Facts,
        DateOnly? DueDate, PurchaseOrder? PurchaseOrder, SupplierInvoice? Original);
    private sealed record OutputWork(ElectronicOutputPreview Preview, ElectronicInvoiceOutputResult Rendered,
        ElectronicValidationReport Validation, Invoice Invoice, Guid ProjectId);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/integrations/v2/e-invoice/validate", async (HttpContext http,
            [AsParameters] BusinessCommandServices c, StandardsRuntime standards) =>
        {
            await c.Commands.Authorize("supplier-invoices", false, http.RequestAborted);
            var bytes = await TradePreviewEndpoints.ReadXml(http);
            var validation = await standards.Invoices.Validate(bytes, http.RequestAborted);
            await c.Commands.Authorize("supplier-invoices", false, http.RequestAborted);
            return Results.Ok(new { contract = "firstblick.einvoice-validation/2.0.0", validation = Summary(validation),
                posting = validation.Accepted ? ElectronicInvoicePosting.Extract(bytes) : null,
                automaticallyPosted = false });
        }).WithTags("electronic-invoices");

        api.MapPost("/integrations/v2/e-invoice/import-preview", async (ElectronicImportRequest body,
            [AsParameters] BusinessCommandServices c, StandardsRuntime standards, CancellationToken ct) =>
        {
            await c.Commands.Authorize("supplier-invoices", true, ct);
            var work = await PrepareImport(body, c, standards, ct);
            await c.Commands.Authorize("supplier-invoices", true, ct);
            return Results.Ok(work.Preview);
        }).WithTags("electronic-invoices");

        api.MapPost("/integrations/v2/e-invoice/import", (ElectronicImportRequest body,
            [AsParameters] BusinessCommandServices c, StandardsRuntime standards, IObjectStore objects) =>
            c.Execute("supplier-invoices", "einvoice.import", body, async ct =>
            {
                var work = await PrepareImport(body, c, standards, ct);
                Guard.Require(work.Preview.CanImport && work.Facts is not null && work.DueDate.HasValue,
                    "einvoice_not_bookable", "The original rules or the explicit payable mapping contain unresolved blocking reasons. No document has been booked.");
                Guard.Require(body.PreviewDigest == work.Preview.Digest && body.PreviewDigest is { Length: 64 },
                    "einvoice_preview_changed", "Original XML, destination business, source records or reviewed mapping changed. Generate a new preview before importing.");
                var facts = work.Facts!;
                var invoice = new SupplierInvoice
                {
                    TenantId = c.Scope.TenantId, SupplierId = work.Supplier.Id, PurchaseOrderId = work.PurchaseOrder?.Id,
                    OriginalInvoiceId = work.Original?.Id, Number = facts.InvoiceNumber,
                    Title = $"E-Rechnung {facts.InvoiceNumber}", SupplierName = work.Supplier.Name, SupplierAddress = work.Supplier.Address,
                    Currency = facts.Currency, InvoiceDate = facts.IssueDate, DueDate = work.DueDate!.Value,
                    Kind = facts.Kind, Net = facts.Net, Tax = facts.Tax, Gross = facts.Gross,
                    ReviewNote = body.ReviewEvidence.Trim(), Status = SupplierInvoiceStatus.Draft
                };
                var originalId = Guid.NewGuid(); var validationId = Guid.NewGuid(); var receiptId = Guid.NewGuid();
                invoice.ElectronicSource = new ElectronicSupplierInvoiceEvidence(work.Validation.Profile,
                    work.Validation.ConfigurationVersion, work.Validation.ConfigurationSha256,
                    work.Validation.SourceSha256, work.Validation.ReportSha256,
                    originalId, validationId, receiptId, facts.SellerName, facts.BuyerName,
                    facts.SellerTaxIdentifier, facts.BuyerTaxIdentifier, body.ReviewEvidence.Trim(), c.Scope.UserId,
                    DateTimeOffset.UtcNow, "", "").Seal(invoice);
                await GraphRules.Validate(invoice, c.Store, c.Db, c.Scope, ct);
                await c.SaveNew(invoice, "einvoice.import", ct);
                var receipt = new
                {
                    contract = "firstblick.einvoice-booking-receipt/2.0.0", invoice.TenantId,
                    supplierInvoiceId = invoice.Id, invoice.Revision, invoice.Status,
                    reviewedPreviewDigest = work.Preview.Digest, evidence = invoice.ElectronicSource,
                    validation = Summary(work.Validation), facts,
                    selectedSupplier = new { work.Supplier.Id, work.Supplier.Revision, work.Supplier.Name, work.Supplier.Address },
                    purchaseOrderId = work.PurchaseOrder?.Id, originalInvoiceId = work.Original?.Id,
                    dueDate = invoice.DueDate, approvalRequired = true, paymentInitiated = false
                };
                var digest = invoice.ElectronicSource.EvidenceDigest;
                var original = await ValidatedExchangeDocuments.Save(c.Db, c.Scope, objects, originalId,
                    work.Bytes, $"Eingangsrechnung-{invoice.Id:N}.xml", "application/xml", "supplier-invoices", invoice.Id,
                    digest, work.PurchaseOrder?.ProjectId, "einvoice-original/2.0.0", ct);
                var validation = await ValidatedExchangeDocuments.Save(c.Db, c.Scope, objects, validationId,
                    Convert.FromBase64String(work.Validation.ReportXmlBase64), $"Pruefbericht-{invoice.Id:N}.xml", "application/xml",
                    "supplier-invoices", invoice.Id, digest, work.PurchaseOrder?.ProjectId, "kosit-1.6.3/xrechnung-2026-08-31", ct);
                var evidence = await ValidatedExchangeDocuments.Save(c.Db, c.Scope, objects, receiptId,
                    Encoding.UTF8.GetBytes(AppJson.Serialize(receipt)), $"Importbeleg-{invoice.Id:N}.json", "application/json",
                    "supplier-invoices", invoice.Id, digest, work.PurchaseOrder?.ProjectId, "einvoice-booking/2.0.0", ct);
                return new { contract = "firstblick.einvoice-import/2.0.0", invoice,
                    documents = new[] { DocumentEndpoints.View(original), DocumentEndpoints.View(validation), DocumentEndpoints.View(evidence) },
                    reviewedPreviewDigest = work.Preview.Digest, automaticallyApproved = false, paymentInitiated = false };
            })).WithTags("electronic-invoices");

        api.MapPost("/invoices/{id:guid}/e-invoice/preview", async (Guid id, ElectronicOutputRequest body,
            [AsParameters] BusinessCommandServices c, StandardsRuntime standards, CancellationToken ct) =>
        {
            await c.Commands.Authorize("invoices", true, ct);
            var work = await PrepareOutput(id, body, c, standards, ct);
            await c.Commands.Authorize("invoices", true, ct);
            return Results.Ok(work.Preview);
        }).WithTags("electronic-invoices");

        api.MapPost("/invoices/{id:guid}/e-invoice/export", (Guid id, ElectronicOutputRequest body,
            [AsParameters] BusinessCommandServices c, StandardsRuntime standards, IObjectStore objects) =>
            c.Execute("invoices", "einvoice.export", new { id, body }, async ct =>
            {
                var work = await PrepareOutput(id, body, c, standards, ct);
                Guard.Require(work.Preview.CanExport, "einvoice_invalid", "The generated structured invoice did not pass the original XRechnung rules. No approved export is available.");
                Guard.Require(body.PreviewDigest == work.Preview.Digest, "einvoice_preview_changed", "The reviewed structured output no longer matches the immutable invoice and mapping.");
                var xmlId = Guid.NewGuid(); var validationId = Guid.NewGuid(); var receiptId = Guid.NewGuid();
                var receipt = new
                {
                    contract = "firstblick.einvoice-output-receipt/2.0.0", c.Scope.TenantId, invoiceId = id,
                    invoiceRevision = work.Invoice.Revision, work.Invoice.IssuedDigest,
                    reviewedPreviewDigest = work.Preview.Digest, work.Rendered.MappingDigest,
                    validation = Summary(work.Validation), mapping = body.Mapping,
                    originalDocumentId = xmlId, validationDocumentId = validationId, receiptDocumentId = receiptId,
                    reviewedBy = c.Scope.UserId, reviewedAt = DateTimeOffset.UtcNow, invoiceMoneyChanged = false,
                    externallySent = false, paymentInitiated = false
                };
                var xml = await ValidatedExchangeDocuments.Save(c.Db, c.Scope, objects, xmlId,
                    work.Rendered.Xml, $"Rechnung-{id:N}.xml", "application/xml", "invoices", id,
                    work.Invoice.IssuedDigest, work.ProjectId, "xrechnung-ubl/2.0.0", ct);
                var validation = await ValidatedExchangeDocuments.Save(c.Db, c.Scope, objects, validationId,
                    Convert.FromBase64String(work.Validation.ReportXmlBase64), $"Pruefbericht-{id:N}.xml", "application/xml",
                    "invoices", id, work.Invoice.IssuedDigest, work.ProjectId, "kosit-1.6.3/xrechnung-2026-08-31", ct);
                var evidence = await ValidatedExchangeDocuments.Save(c.Db, c.Scope, objects, receiptId,
                    Encoding.UTF8.GetBytes(AppJson.Serialize(receipt)), $"Ausgabebeleg-{id:N}.json", "application/json",
                    "invoices", id, work.Invoice.IssuedDigest, work.ProjectId, "einvoice-output/2.0.0", ct);
                return new { contract = "firstblick.einvoice-output/2.0.0", preview = work.Preview,
                    documents = new[] { DocumentEndpoints.View(xml), DocumentEndpoints.View(validation), DocumentEndpoints.View(evidence) },
                    externallySent = false, invoiceMoneyChanged = false };
            })).WithTags("electronic-invoices");
    }

    internal static ElectronicValidationSummary Summary(ElectronicValidationReport report) => new(report.Profile,
        report.ConfigurationVersion, report.EngineVersion, report.SourceSha256, report.ConfigurationSha256,
        report.Accepted, report.ReportSha256, report.Issues);

    private static async Task<ImportWork> PrepareImport(ElectronicImportRequest body, BusinessCommandServices c,
        StandardsRuntime standards, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guard.Require(body.SourceBase64 is { Length: > 0 and <= 1_400_000 }, "xml_budget", "Provide at most one MiB of original XML bytes as Base64.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(body.SourceBase64); }
        catch (FormatException) { throw new DomainException("invalid_base64", "The original XML is not valid Base64."); }
        var (_, sourceSha) = TradeXml.Load(bytes);
        Guard.Require(body.ExpectedSourceSha256 == sourceSha, "source_hash_mismatch", "The original XML bytes no longer match the selected file.");
        Guard.Id(body.SupplierId, nameof(body.SupplierId));
        Guard.Require(body.ExpectedSupplierRevision > 0, "supplier_revision_required", "Select the current supplier revision explicitly.");
        Guard.Text(body.ReviewedSourceSellerName, nameof(body.ReviewedSourceSellerName), 300);
        Guard.Text(body.ReviewedSourceBuyerName, nameof(body.ReviewedSourceBuyerName), 300);
        Guard.Text(body.ReviewEvidence, nameof(body.ReviewEvidence), 2000);
        var supplier = await c.Current<Supplier>(body.SupplierId, ct, false);
        Guard.Require(supplier.Revision == body.ExpectedSupplierRevision, "supplier_changed", "The supplier changed since the reviewed mapping.");
        var tenant = await c.Db.Tenants.AsNoTracking().SingleAsync(x => x.Id == c.Scope.TenantId, ct);
        var validation = await standards.Invoices.Validate(bytes, ct);
        if (!validation.Accepted)
        {
            var denied = new ElectronicImportPreview("firstblick.einvoice-import-preview/2.0.0", c.Scope.TenantId,
                supplier.Id, supplier.Revision, supplier.Name, Summary(validation), null, null, null, false, false,
                Array.AsReadOnly(new[] { "original_xrechnung_validation_rejected" }));
            return new(denied, bytes, validation, supplier, null, null, null, null);
        }
        var facts = ElectronicInvoicePosting.Extract(bytes);
        Guard.Require(body.ReviewedSourceSellerName == facts.SellerName && body.ReviewedSourceBuyerName == facts.BuyerName,
            "party_review_mismatch", "Explicitly acknowledge the seller and buyer names extracted from the original file. The business display name is not automatically a verified legal recipient identity.");
        var reasons = facts.PostingBlockers.ToList();
        DateOnly? due = facts.DueDate ?? body.SelectedDueDate;
        if (facts.DueDate is { } sourceDue && body.SelectedDueDate is { } selected && sourceDue != selected)
            reasons.Add("declared_source_due_date_must_not_be_overwritten");
        if (!due.HasValue || due.Value < facts.IssueDate) reasons.Add("explicit_due_date_required_or_inconsistent");
        PurchaseOrder? purchase = null; SupplierInvoice? original = null;
        if (body.PurchaseOrderId is { } purchaseId)
        {
            purchase = await c.Current<PurchaseOrder>(purchaseId, ct, false);
            Guard.Require(purchase.SupplierId == supplier.Id && purchase.Revision == body.ExpectedPurchaseOrderRevision,
                "purchase_mapping_changed", "The purchase order must belong to the selected supplier and reviewed revision.");
        }
        else Guard.Require(body.ExpectedPurchaseOrderRevision is null, "invalid_purchase_mapping", "A purchase-order revision without a selected order is ambiguous.");
        if (facts.Kind == SupplierInvoiceKind.CreditNote)
        {
            Guard.Require(body.OriginalInvoiceId.HasValue, "credit_original_required", "Select the actual original payable before importing a supplier credit note.");
            original = await c.Current<SupplierInvoice>(body.OriginalInvoiceId!.Value, ct, false);
            Guard.Require(original.Status == SupplierInvoiceStatus.Approved && original.Kind == SupplierInvoiceKind.Invoice
                && original.SupplierId == supplier.Id && original.Revision == body.ExpectedOriginalInvoiceRevision,
                "credit_original_changed", "The approved original payable must match the supplier and reviewed revision.");
            if (facts.OriginalInvoiceNumber is not null && facts.OriginalInvoiceNumber != original.Number)
                reasons.Add("source_credit_reference_does_not_match_the_selected_original");
        }
        else Guard.Require(body.OriginalInvoiceId is null && body.ExpectedOriginalInvoiceRevision is null,
            "unexpected_credit_reference", "A standard incoming invoice cannot silently become a correction.");
        if (await c.Db.Documents.AnyAsync(x => x.SourceKind == "supplier-invoices" && x.Sha256 == sourceSha, ct))
            reasons.Add("identical_original_xml_already_retained_for_a_payable");
        var unique = $"supplier:{supplier.Id}:number:{facts.InvoiceNumber.Trim().ToUpperInvariant()}";
        if (await c.Db.Entities.AnyAsync(x => x.Kind == "supplier-invoices" && x.UniqueKey == unique, ct))
            reasons.Add("supplier_invoice_number_already_exists");
        var digest = ContentDigest.Of(new
        {
            contract = "firstblick.einvoice-import-preview/2.0.0", c.Scope.TenantId,
            tenantName = tenant.Name, tenantPolicyRevision = tenant.PolicyRevision,
            supplier = new { supplier.Id, supplier.Revision, supplier.Name, supplier.Address },
            purchase = purchase is null ? null : new { purchase.Id, purchase.Revision },
            original = original is null ? null : new { original.Id, original.Revision, original.ApprovedDigest },
            validation.ConfigurationSha256, facts, due, body.ReviewedSourceSellerName,
            body.ReviewedSourceBuyerName, reviewEvidence = body.ReviewEvidence.Trim()
        });
        var preview = new ElectronicImportPreview("firstblick.einvoice-import-preview/2.0.0", c.Scope.TenantId,
            supplier.Id, supplier.Revision, supplier.Name, Summary(validation), facts, due, digest,
            reasons.Count == 0, false, reasons.AsReadOnly());
        return new(preview, bytes, validation, supplier, facts, due, purchase, original);
    }

    private static async Task<OutputWork> PrepareOutput(Guid id, ElectronicOutputRequest body, BusinessCommandServices c,
        StandardsRuntime standards, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body); ArgumentNullException.ThrowIfNull(body.Mapping);
        var invoice = await c.Current<Invoice>(id, ct);
        Invoice? original = invoice.OriginalInvoiceId is { } originalId ? await c.Current<Invoice>(originalId, ct, false) : null;
        var rendered = ElectronicInvoiceOutput.Render(invoice, body.Mapping, original);
        var order = await c.Current<Order>(invoice.OrderId, ct, false);
        var validation = await standards.Invoices.Validate(rendered.Xml, ct);
        var digest = ContentDigest.Of(new { contract = "firstblick.einvoice-output-preview/2.0.0", c.Scope.TenantId,
            invoice.Id, invoice.Revision, invoice.IssuedDigest, rendered.MappingDigest, rendered.XmlSha256,
            validation.ConfigurationSha256, validation.Accepted });
        var preview = new ElectronicOutputPreview("firstblick.einvoice-output-preview/2.0.0", invoice.Id,
            invoice.Revision, invoice.IssuedDigest, rendered.MappingDigest, rendered.XmlSha256, Summary(validation), digest, validation.Accepted);
        return new(preview, rendered, validation, invoice, order.ProjectId);
    }
}
