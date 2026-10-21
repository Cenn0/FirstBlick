using System.Globalization;
using System.Xml.Linq;
using FirstBlick.Core.Business;

namespace FirstBlick.Core.Integrations;

public sealed record ElectronicInvoiceLine(string Reference, string Description, decimal Quantity,
    string UnitCode, decimal NetAmount);
public sealed record ElectronicInvoicePreview(string Contract, string SourceSha256, string Syntax,
    string ProfileIdentifier, string InvoiceNumber, string DocumentKind, DateOnly IssueDate,
    string Currency, string SellerName, string BuyerName, decimal NetAmount,
    decimal TaxAmount, decimal GrossAmount, decimal PayableAmount,
    IReadOnlyList<ElectronicInvoiceLine> Lines, IReadOnlyList<string> ReviewIssues,
    bool ExternalValidationPerformed, bool MayPostAutomatically);

/// <summary>
/// Strict read-only field extraction from untrusted UBL 2.1 and CII D16B-style XML.
/// Not an EN16931/CIUS validator and never a posting/transport authority.
/// </summary>
public static class ElectronicInvoiceXml
{
    public const string Contract = "firstblick.einvoice-preview/1.0.0";
    private static readonly XNamespace UblInvoice = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    private static readonly XNamespace UblCredit = "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cii = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    private static readonly XNamespace Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    private static readonly XNamespace Udt = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100";

    public static ElectronicInvoicePreview Preview(byte[] bytes)
    {
        var (xml, digest) = TradeXml.Load(bytes);
        var root = xml.Root!;
        if (root.Name == UblInvoice + "Invoice") return FromUbl(root, digest, false);
        if (root.Name == UblCredit + "CreditNote") return FromUbl(root, digest, true);
        if (root.Name == Cii + "CrossIndustryInvoice") return FromCii(root, digest);
        throw new DomainException("unsupported_einvoice_syntax",
            "Only plain UBL 2.1 Invoice/CreditNote or CII CrossIndustryInvoice XML can be previewed. PDF/A-3 attachments and other syntaxes require separate adapters.");
    }

    private static ElectronicInvoicePreview FromUbl(XElement root, string digest, bool credit)
    {
        var issueDateString = TradeXml.Required(root.Element(Cbc + "IssueDate"), "IssueDate", 10);
        Guard.Require(DateOnly.TryParseExact(issueDateString, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var issueDate), "xml_date", "UBL issue date must use YYYY-MM-DD.");
        var number = TradeXml.Required(root.Element(Cbc + "ID"), "Invoice ID", 150);
        var currency = TradeXml.Required(root.Element(Cbc + "DocumentCurrencyCode"), "Currency", 3);
        Guard.Currency(currency);
        var seller = PartyName(root.Element(Cac + "AccountingSupplierParty"), "Seller");
        var buyer = PartyName(root.Element(Cac + "AccountingCustomerParty"), "Buyer");
        var totals = root.Element(Cac + "LegalMonetaryTotal");
        Guard.Require(totals is not null, "xml_structure", "The UBL monetary totals are required.");
        var net = TradeXml.Decimal(totals!.Element(Cbc + "TaxExclusiveAmount"), "TaxExclusiveAmount");
        var gross = TradeXml.Decimal(totals.Element(Cbc + "TaxInclusiveAmount"), "TaxInclusiveAmount");
        var payable = TradeXml.Decimal(totals.Element(Cbc + "PayableAmount"), "PayableAmount");
        var lines = root.Elements(Cac + (credit ? "CreditNoteLine" : "InvoiceLine")).ToArray();
        Guard.Require(lines.Length is >= 1 and <= 1000, "xml_budget", "The UBL document must include 1 to 1000 lines.");
        var parsed = lines.Select(l => new ElectronicInvoiceLine(
            TradeXml.Required(l.Element(Cbc + "ID"), "Line ID", 150),
            TradeXml.Required(l.Element(Cac + "Item")?.Element(Cbc + "Name"), "Item name", 4000),
            TradeXml.Quantity(l.Element(Cbc + (credit ? "CreditedQuantity" : "InvoicedQuantity")), "Line quantity"),
            TradeXml.Optional(l.Element(Cbc + (credit ? "CreditedQuantity" : "InvoicedQuantity"))?.Attribute("unitCode")
                is { } unit ? new XElement("unit", unit.Value) : null, 32),
            TradeXml.Decimal(l.Element(Cbc + "LineExtensionAmount"), "LineExtensionAmount"))).ToArray();
        Guard.Require(parsed.Select(x => x.Reference).Distinct(StringComparer.Ordinal).Count() == parsed.Length,
            "xml_duplicate_line", "Invoice line references cannot repeat in the supported preview.");
        var profile = TradeXml.Optional(root.Element(Cbc + "CustomizationID"), 500);
        var issues = new List<string>();
        if (profile.Length == 0) issues.Add("missing_customization_id");
        if (root.Element(Cbc + "ProfileID") is null) issues.Add("missing_business_process_id");
        if (root.Element(Cac + "AccountingSupplierParty")?.Element(Cac + "Party")?.Element(Cbc + "EndpointID") is null)
            issues.Add("missing_seller_electronic_address");
        if (root.Element(Cac + "AccountingCustomerParty")?.Element(Cac + "Party")?.Element(Cbc + "EndpointID") is null)
            issues.Add("missing_buyer_electronic_address");
        if (parsed.Sum(x => x.NetAmount) != net) issues.Add("line_net_differs_from_tax_basis_allowances_or_charges_require_manual_review");
        if (gross < net) issues.Add("gross_below_net_manual_review");
        issues.Add("external_xsd_schematron_and_tax_profile_validation_not_performed");
        issues.Add("no_accounting_import_or_supplier_matching");
        return new(Contract, digest, credit ? "UBL-2.1-CreditNote" : "UBL-2.1-Invoice", profile, number,
            credit ? "CreditNote" : "Invoice", issueDate, currency, seller, buyer,
            net, gross - net, gross, payable, Array.AsReadOnly(parsed), issues.AsReadOnly(), false, false);
    }

    private static string PartyName(XElement? accountingParty, string label)
    {
        var party = accountingParty?.Element(Cac + "Party");
        var name = party?.Element(Cac + "PartyLegalEntity")?.Element(Cbc + "RegistrationName")
            ?? party?.Element(Cac + "PartyName")?.Element(Cbc + "Name");
        return TradeXml.Required(name, label, 240);
    }

    private static ElectronicInvoicePreview FromCii(XElement root, string digest)
    {
        var document = root.Element(Cii + "ExchangedDocument");
        var number = TradeXml.Required(document?.Element(Ram + "ID"), "ExchangedDocument.ID", 150);
        var kind = TradeXml.Required(document?.Element(Ram + "TypeCode"), "TypeCode", 8);
        Guard.Require(kind is "380" or "381", "unsupported_einvoice_type",
            "This preview supports only CII invoice type 380 and credit-note type 381.");
        var date = TradeXml.Required(document?.Element(Ram + "IssueDateTime")?.Element(Udt + "DateTimeString"), "IssueDate", 8);
        Guard.Require(date.Length == 8 && date.All(char.IsAsciiDigit), "xml_date", "CII issue date must use YYYYMMDD for this preview.");
        Guard.Require(DateOnly.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var issueDate), "xml_date", "CII issue date is not a valid YYYYMMDD calendar date.");
        var tx = root.Element(Cii + "SupplyChainTradeTransaction");
        var agreement = tx?.Element(Ram + "ApplicableHeaderTradeAgreement");
        var settlement = tx?.Element(Ram + "ApplicableHeaderTradeSettlement");
        var totals = settlement?.Element(Ram + "SpecifiedTradeSettlementHeaderMonetarySummation");
        var currency = TradeXml.Required(settlement?.Element(Ram + "InvoiceCurrencyCode"), "Currency", 3);
        Guard.Currency(currency);
        var seller = TradeXml.Required(agreement?.Element(Ram + "SellerTradeParty")?.Element(Ram + "Name"), "Seller", 240);
        var buyer = TradeXml.Required(agreement?.Element(Ram + "BuyerTradeParty")?.Element(Ram + "Name"), "Buyer", 240);
        var net = TradeXml.Decimal(totals?.Element(Ram + "TaxBasisTotalAmount"), "TaxBasisTotalAmount");
        var gross = TradeXml.Decimal(totals?.Element(Ram + "GrandTotalAmount"), "GrandTotalAmount");
        var payable = TradeXml.Decimal(totals?.Element(Ram + "DuePayableAmount"), "DuePayableAmount");
        var elements = tx?.Elements(Ram + "IncludedSupplyChainTradeLineItem").ToArray() ?? [];
        Guard.Require(elements.Length is >= 1 and <= 1000, "xml_budget", "The CII document must include 1 to 1000 lines.");
        var parsed = elements.Select(l => new ElectronicInvoiceLine(
            TradeXml.Required(l.Element(Ram + "AssociatedDocumentLineDocument")?.Element(Ram + "LineID"), "LineID", 150),
            TradeXml.Required(l.Element(Ram + "SpecifiedTradeProduct")?.Element(Ram + "Name"), "Product name", 4000),
            TradeXml.Quantity(l.Element(Ram + "SpecifiedLineTradeDelivery")?.Element(Ram + "BilledQuantity"), "BilledQuantity"),
            TradeXml.Optional(l.Element(Ram + "SpecifiedLineTradeDelivery")?.Element(Ram + "BilledQuantity")?.Attribute("unitCode")
                is { } unit ? new XElement("unit", unit.Value) : null, 32),
            TradeXml.Decimal(l.Element(Ram + "SpecifiedLineTradeSettlement")?
                .Element(Ram + "SpecifiedTradeSettlementLineMonetarySummation")?.Element(Ram + "LineTotalAmount"), "LineTotalAmount"))).ToArray();
        Guard.Require(parsed.Select(x => x.Reference).Distinct(StringComparer.Ordinal).Count() == parsed.Length,
            "xml_duplicate_line", "CII line references cannot repeat in the supported preview.");
        var profile = TradeXml.Optional(root.Element(Cii + "ExchangedDocumentContext")?
            .Element(Ram + "GuidelineSpecifiedDocumentContextParameter")?.Element(Ram + "ID"), 500);
        var issues = new List<string>();
        if (profile.Length == 0) issues.Add("missing_guideline_id");
        if (parsed.Sum(l => l.NetAmount) != net) issues.Add("line_net_differs_from_tax_basis_allowances_or_charges_require_manual_review");
        issues.Add("external_xsd_schematron_and_tax_profile_validation_not_performed");
        issues.Add("no_accounting_import_or_supplier_matching");
        return new(Contract, digest, "CII-D16B-structure", profile, number,
            kind == "381" ? "CreditNote" : "Invoice", issueDate, currency, seller, buyer,
            net, gross - net, gross, payable, Array.AsReadOnly(parsed), issues.AsReadOnly(), false, false);
    }
}
