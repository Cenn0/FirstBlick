using System.Globalization;
using System.Xml.Linq;
using FirstBlick.Core.Business;

namespace FirstBlick.Core.Integrations;

public sealed record ElectronicTaxBreakdown(string Category, decimal? Rate, decimal TaxableAmount,
    decimal TaxAmount, string ExemptionReason, string ExemptionReasonCode);
public sealed record ElectronicPostingFacts(string SourceSha256, string Syntax, string DocumentTypeCode,
    string InvoiceNumber, DateOnly IssueDate, DateOnly? DueDate, string Currency,
    string SellerName, string BuyerName, string SellerTaxIdentifier, string BuyerTaxIdentifier,
    SupplierInvoiceKind Kind, string? OriginalInvoiceNumber, decimal Net, decimal Tax, decimal Gross,
    decimal Payable, decimal Prepaid, decimal PayableRounding,
    IReadOnlyList<ElectronicTaxBreakdown> TaxBreakdown, IReadOnlyList<string> PostingBlockers);

/// <summary>
/// Projects an externally validated invoice into the existing accounts-payable model.
/// Parsing is not validation: callers must require the original validator's acceptance
/// for these exact bytes before constructing any business record.
/// </summary>
public static class ElectronicInvoicePosting
{
    private static readonly XNamespace B = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace A = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace R = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    private static readonly XNamespace S = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    private static readonly XNamespace U = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100";
    public static ElectronicPostingFacts Extract(byte[] bytes)
    {
        var preview = ElectronicInvoiceXml.Preview(bytes);
        var (document, _) = TradeXml.Load(bytes); var root = document.Root!;
        var ubl = root.Name.NamespaceName is "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2" or "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2";
        var credit = preview.DocumentKind is "CreditNote";
        string code; DateOnly? due; string sellerTax; string buyerTax; string? original;
        decimal prepaid; decimal rounding; ElectronicTaxBreakdown[] tax;
        if (ubl)
        {
            code = root.Element(B + (credit ? "CreditNoteTypeCode" : "InvoiceTypeCode"))?.Value.Trim() ?? "";
            due = Date(root.Element(B + "DueDate")?.Value, "yyyy-MM-dd");
            XElement? Party(string name) => root.Element(A + name)?.Element(A + "Party");
            string TaxId(XElement? party) => party?.Elements(A + "PartyTaxScheme")
                .Where(x => x.Element(A + "TaxScheme")?.Element(B + "ID")?.Value == "VAT")
                .Select(x => x.Element(B + "CompanyID")?.Value.Trim() ?? "").FirstOrDefault() ?? "";
            sellerTax = TaxId(Party("AccountingSupplierParty")); buyerTax = TaxId(Party("AccountingCustomerParty"));
            var references = root.Elements(A + "BillingReference").Select(x => x.Element(A + "InvoiceDocumentReference")?.Element(B + "ID")?.Value.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
            Guard.Require(references.Length <= 1, "multiple_invoice_references", "The payable correction model requires one original invoice, not an implicitly chosen reference from several originals.");
            original = references.SingleOrDefault();
            var money = root.Element(A + "LegalMonetaryTotal")!;
            prepaid = Amount(money.Element(B + "PrepaidAmount")); rounding = Amount(money.Element(B + "PayableRoundingAmount"));
            tax = root.Elements(A + "TaxTotal").Where(x => (string?)x.Element(B + "TaxAmount")?.Attribute("currencyID") == "EUR")
                .SelectMany(x => x.Elements(A + "TaxSubtotal")).Select(x =>
                {
                    var category = x.Element(A + "TaxCategory")!;
                    return new ElectronicTaxBreakdown(category.Element(B + "ID")?.Value.Trim() ?? "",
                        category.Element(B + "Percent") is { } rate ? Amount(rate) : null,
                        Amount(x.Element(B + "TaxableAmount")), Amount(x.Element(B + "TaxAmount")),
                        category.Element(B + "TaxExemptionReason")?.Value.Trim() ?? "", category.Element(B + "TaxExemptionReasonCode")?.Value.Trim() ?? "");
                }).ToArray();
        }
        else
        {
            Guard.Require(root.Name == S + "CrossIndustryInvoice", "unsupported_invoice_syntax", "A supported UBL or CII invoice is required.");
            var transaction = root.Element(S + "SupplyChainTradeTransaction")!;
            var settlement = transaction.Element(R + "ApplicableHeaderTradeSettlement")!;
            var agreement = transaction.Element(R + "ApplicableHeaderTradeAgreement")!;
            code = root.Element(S + "ExchangedDocument")?.Element(R + "TypeCode")?.Value.Trim() ?? "";
            var dueElements = settlement.Elements(R + "SpecifiedTradePaymentTerms").Select(x => x.Element(R + "DueDateDateTime")?.Element(U + "DateTimeString")).Where(x => x is not null).ToArray();
            Guard.Require(dueElements.Length <= 1 && dueElements.All(x => (string?)x!.Attribute("format") == "102"), "unsupported_due_date", "The payable model requires one unambiguous calendar due date.");
            due = Date(dueElements.SingleOrDefault()?.Value, "yyyyMMdd");
            string TaxId(XElement? party) => party?.Elements(R + "SpecifiedTaxRegistration")
                .Select(x => x.Element(R + "ID")).Where(x => (string?)x?.Attribute("schemeID") == "VA")
                .Select(x => x!.Value.Trim()).FirstOrDefault() ?? "";
            sellerTax = TaxId(agreement.Element(R + "SellerTradeParty")); buyerTax = TaxId(agreement.Element(R + "BuyerTradeParty"));
            var references = settlement.Elements(R + "InvoiceReferencedDocument").Select(x => x.Element(R + "IssuerAssignedID")?.Value.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
            Guard.Require(references.Length <= 1, "multiple_invoice_references", "The payable correction model requires one original invoice.");
            original = references.SingleOrDefault();
            var totals = settlement.Element(R + "SpecifiedTradeSettlementHeaderMonetarySummation")!;
            prepaid = Amount(totals.Element(R + "TotalPrepaidAmount")); rounding = Amount(totals.Element(R + "RoundingAmount"));
            tax = settlement.Elements(R + "ApplicableTradeTax").Select(x => new ElectronicTaxBreakdown(
                x.Element(R + "CategoryCode")?.Value.Trim() ?? "", x.Element(R + "RateApplicablePercent") is { } rate ? Amount(rate) : null,
                Amount(x.Element(R + "BasisAmount")), Amount(x.Element(R + "CalculatedAmount")),
                x.Element(R + "ExemptionReason")?.Value.Trim() ?? "", x.Element(R + "ExemptionReasonCode")?.Value.Trim() ?? "")).ToArray();
        }
        var blockers = new List<string>();
        if (code != (credit ? "381" : "380")) blockers.Add("document_type_requires_an_explicit_different_booking_workflow");
        if (preview.InvoiceNumber.Length > 80) blockers.Add("invoice_identifier_exceeds_internal_80_character_limit");
        if (preview.NetAmount < 0 || preview.TaxAmount < 0 || preview.GrossAmount <= 0) blockers.Add("negative_or_zero_amount_requires_a_different_ledger_document");
        if (prepaid != 0) blockers.Add("declared_prepayment_requires_separate_verified_payment_reconciliation");
        if (rounding != 0) blockers.Add("payable_rounding_requires_explicit_ledger_adjustment");
        if (preview.PayableAmount != preview.GrossAmount) blockers.Add("declared_payable_does_not_equal_the_unsettled_gross_document");
        if (tax.Length is 0 or > 100 || tax.Sum(x => x.TaxAmount) != preview.TaxAmount || tax.Sum(x => x.TaxableAmount) != preview.NetAmount)
            blockers.Add("tax_breakdown_does_not_reconcile_with_the_payable_header");
        if (new[] { preview.NetAmount, preview.TaxAmount, preview.GrossAmount }.Any(value => Money.Round(value) != value))
            blockers.Add("unsupported_subcent_ledger_totals");
        return new(preview.SourceSha256, preview.Syntax, code, preview.InvoiceNumber, preview.IssueDate, due, preview.Currency,
            preview.SellerName, preview.BuyerName, sellerTax, buyerTax,
            credit ? SupplierInvoiceKind.CreditNote : SupplierInvoiceKind.Invoice, original,
            preview.NetAmount, preview.TaxAmount, preview.GrossAmount, preview.PayableAmount, prepaid, rounding,
            Array.AsReadOnly(tax), blockers.AsReadOnly());
    }
    private static decimal Amount(XElement? value) => value is null ? 0 : TradeXml.DecimalValue(value.Value, value.Name.LocalName);
    private static DateOnly? Date(string? value, string format)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        Guard.Require(DateOnly.TryParseExact(value.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result),
            "invalid_invoice_date", "The source date is not a supported complete calendar date.");
        return result;
    }
}
