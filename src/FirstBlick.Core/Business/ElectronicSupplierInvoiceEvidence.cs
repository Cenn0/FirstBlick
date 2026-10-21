namespace FirstBlick.Core.Business;

/// <summary>Immutable link between an imported payable, its exact original bytes and the retained original validator report.</summary>
public sealed record ElectronicSupplierInvoiceEvidence(string Profile, string ConfigurationVersion,
    string ConfigurationSha256, string SourceSha256, string ValidationReportSha256,
    Guid OriginalDocumentId, Guid ValidationDocumentId, Guid ReceiptDocumentId,
    string SourceSellerName, string SourceBuyerName, string SourceSellerTaxIdentifier,
    string SourceBuyerTaxIdentifier, string ReviewEvidence, Guid ReviewedBy, DateTimeOffset ReviewedAt,
    string FinancialDigest, string EvidenceDigest)
{
    public static string FinancialBinding(SupplierInvoice invoice) => ContentDigest.Of(new
    {
        invoice.TenantId, invoice.Id, invoice.SupplierId, invoice.PurchaseOrderId, invoice.OriginalInvoiceId,
        invoice.Number, invoice.Currency, invoice.InvoiceDate, invoice.DueDate, invoice.Kind, invoice.Net, invoice.Tax, invoice.Gross
    });
    public ElectronicSupplierInvoiceEvidence Seal(SupplierInvoice invoice)
    {
        var bound = this with { FinancialDigest = FinancialBinding(invoice), EvidenceDigest = "" };
        return bound with { EvidenceDigest = ContentDigest.Of(bound) };
    }
    public void Validate(SupplierInvoice invoice)
    {
        foreach (var digest in new[] { ConfigurationSha256, SourceSha256, ValidationReportSha256, FinancialDigest, EvidenceDigest })
            Guard.Require(digest is { Length: 64 } && digest.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9'),
                "invalid_einvoice_evidence", "Electronic invoice evidence requires complete lowercase SHA-256 identifiers.");
        Guard.Text(Profile, nameof(Profile), 240); Guard.Text(ConfigurationVersion, nameof(ConfigurationVersion), 100);
        Guard.Id(OriginalDocumentId, nameof(OriginalDocumentId)); Guard.Id(ValidationDocumentId, nameof(ValidationDocumentId)); Guard.Id(ReceiptDocumentId, nameof(ReceiptDocumentId));
        Guard.Require(new[] { OriginalDocumentId, ValidationDocumentId, ReceiptDocumentId }.Distinct().Count() == 3,
            "invalid_einvoice_evidence", "Original invoice, validation report and booking receipt have distinct immutable document identities.");
        Guard.Text(SourceSellerName, nameof(SourceSellerName), 300); Guard.Text(SourceBuyerName, nameof(SourceBuyerName), 300);
        Guard.Text(SourceSellerTaxIdentifier, nameof(SourceSellerTaxIdentifier), 100, false); Guard.Text(SourceBuyerTaxIdentifier, nameof(SourceBuyerTaxIdentifier), 100, false);
        Guard.Text(ReviewEvidence, nameof(ReviewEvidence), 2000); Guard.Id(ReviewedBy, nameof(ReviewedBy)); Guard.Utc(ReviewedAt, nameof(ReviewedAt));
        Guard.Require(FinancialDigest == FinancialBinding(invoice) && EvidenceDigest == ContentDigest.Of(this with { EvidenceDigest = "" }),
            "einvoice_evidence_changed", "Imported source amounts, supplier/recipient mapping, dates and retained original evidence are immutable. Reject or correct the document instead of overwriting its provenance.");
    }
}
