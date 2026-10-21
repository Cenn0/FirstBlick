using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FirstBlick.Core.Business;

namespace FirstBlick.Core.Integrations;

public sealed record ElectronicPostalAddress(string SourceAddress, string Street, string City, string PostalCode, string CountryCode);
public sealed record ElectronicPartyDetails(ElectronicPostalAddress Address, string EndpointScheme, string Endpoint,
    string TaxScheme, string TaxIdentifier, string ContactName, string ContactTelephone, string ContactEmail);
public sealed record ElectronicLineClassification(int Position, string UnitCode, string TaxCategory,
    string ExemptionReason = "", string ExemptionReasonCode = "");
public sealed record ElectronicInvoiceOutputInput(string ExpectedInvoiceDigest, string BuyerReference,
    ElectronicPartyDetails Seller, ElectronicPartyDetails Buyer, string PaymentMeansCode,
    string PaymentAccount, string PaymentAccountName, string ReviewEvidence,
    IReadOnlyList<ElectronicLineClassification> Lines);
public sealed record ElectronicInvoiceOutputResult(byte[] Xml, string SourceInvoiceDigest, string MappingDigest, string XmlSha256);

/// <summary>
/// UBL 2.1 output of an already-issued immutable invoice, preserving its actual cents.
/// Emitted bytes MUST subsequently pass the pinned external XSD/Schematron validator.
/// </summary>
public static class ElectronicInvoiceOutput
{
    public const string Profile = "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    public static ElectronicInvoiceOutputResult Render(Invoice invoice, ElectronicInvoiceOutputInput input, Invoice? original = null)
    {
        ArgumentNullException.ThrowIfNull(invoice); ArgumentNullException.ThrowIfNull(input);
        invoice.Validate();
        Guard.Require(invoice.Status == InvoiceStatus.Issued && invoice.Snapshot is not null && invoice.IssueDate.HasValue,
            "invoice_not_issued", "Only an already-issued invoice with immutable monetary evidence can be exported.");
        Guard.Require(input.ExpectedInvoiceDigest == invoice.IssuedDigest, "invoice_changed", "Reload the exact issued invoice before reviewing its structured output.");
        Guard.Text(input.ReviewEvidence, "Mapping review evidence", 2000); Guard.Text(input.BuyerReference, "Buyer reference", 240);
        ValidateParty(input.Seller, invoice.SellerAddress, true); ValidateParty(input.Buyer, invoice.BuyerAddress, false);
        Guard.Require(input.Seller.TaxIdentifier == invoice.SellerTaxId, "seller_tax_mismatch", "The reviewed seller tax identifier must equal the identifier on the issued invoice.");
        Guard.Items(input.Lines, "Line classifications", 1000, 1);
        Guard.Require(input.Lines.Count == invoice.Snapshot!.Lines.Count &&
            input.Lines.Select(x => x.Position).Order().SequenceEqual(Enumerable.Range(1, input.Lines.Count)),
            "invoice_line_mapping", "Supply one explicit unit and tax-category classification for every frozen invoice position.");
        var classifications = input.Lines.OrderBy(x => x.Position).ToArray();
        var lines = invoice.Snapshot.Lines;
        for (var i = 0; i < lines.Count; i++) ValidateClassification(classifications[i], lines[i].Inputs.VatPercent);
        Guard.Require(input.PaymentMeansCode is "10" or "30" or "58", "payment_means_unsupported", "Explicit cash (10), credit transfer (30), or SEPA credit transfer (58) is supported. This output does not initiate payment.");
        if (input.PaymentMeansCode != "10")
        {
            Guard.Require(ValidIban(input.PaymentAccount), "invalid_iban", "Provide an explicitly reviewed valid IBAN for bank-transfer output.");
            Guard.Text(input.PaymentAccountName, "Payment account name", 240);
        }
        else Guard.Require(input.PaymentAccount == "" && input.PaymentAccountName == "", "cash_account_mismatch", "Cash payment does not carry an automatically inferred bank account.");
        var credit = invoice.Kind == InvoiceKind.CreditNote;
        if (credit)
        {
            Guard.Require(original is not null, "credit_original_required", "A structured credit note requires its actual original issued invoice.");
            original!.Validate();
            Guard.Require(original.Status == InvoiceStatus.Issued && original.Kind != InvoiceKind.CreditNote &&
                original.Id == invoice.OriginalInvoiceId && original.TenantId == invoice.TenantId,
                "credit_original_mismatch", "The credit note original does not match the issued evidence.");
        }
        else Guard.Require(original is null, "unexpected_original_invoice", "A non-credit invoice does not accept an invented correction reference.");

        var groups = lines.Select((line, index) => new { Line = line, Classification = classifications[index] })
            .GroupBy(x => new { x.Classification.TaxCategory, Rate = x.Line.Inputs.VatPercent,
                x.Classification.ExemptionReason, x.Classification.ExemptionReasonCode }).ToArray();
        Guard.Require(groups.GroupBy(x => new { x.Key.TaxCategory, x.Key.Rate }).All(g => g.Count() == 1),
            "tax_reason_conflict", "The same VAT category/rate must use one consistent exemption explanation.");
        var taxTotal = groups.Sum(g => Money.Round(g.Sum(x => x.Line.Net) * g.Key.Rate / 100m));
        Guard.Require(taxTotal == invoice.Snapshot.Tax && lines.Sum(x => x.Net) == invoice.Snapshot.Net && invoice.Snapshot.Gross == invoice.Snapshot.Net + taxTotal,
            "einvoice_tax_rounding_conflict", "The issued cents do not satisfy EN 16931 category-level VAT rounding. Correct the commercial source through the normal correction workflow; export must not silently change an issued tax amount.");

        XNamespace ns = credit ? "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2" : "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
        var root = new XElement(ns + (credit ? "CreditNote" : "Invoice"),
            new XAttribute(XNamespace.Xmlns + "cbc", Cbc), new XAttribute(XNamespace.Xmlns + "cac", Cac),
            B("UBLVersionID", "2.1"), B("CustomizationID", Profile),
            B("ProfileID", "urn:fdc:peppol.eu:2017:poacc:billing:01:1.0"), B("ID", invoice.Number),
            B("IssueDate", Date(invoice.IssueDate!.Value)));
        if (!credit) root.Add(B("DueDate", Date(invoice.DueDate)));
        root.Add(B(credit ? "CreditNoteTypeCode" : "InvoiceTypeCode", credit ? "381" : "380"));
        if (credit) root.Add(B("Note", invoice.CorrectionReason));
        root.Add(B("DocumentCurrencyCode", "EUR"), B("BuyerReference", input.BuyerReference),
            new XElement(Cac + "InvoicePeriod", B("StartDate", Date(invoice.ServiceFrom)), B("EndDate", Date(invoice.ServiceUntil))));
        if (credit) root.Add(new XElement(Cac + "BillingReference", new XElement(Cac + "InvoiceDocumentReference", B("ID", original!.Number), B("IssueDate", Date(original.IssueDate!.Value)))));
        root.Add(new XElement(Cac + "AccountingSupplierParty", Party(invoice.SellerName, input.Seller)),
            new XElement(Cac + "AccountingCustomerParty", Party(invoice.BuyerName, input.Buyer)));
        var payment = new XElement(Cac + "PaymentMeans", B("PaymentMeansCode", input.PaymentMeansCode), B("PaymentID", invoice.Number));
        if (input.PaymentMeansCode != "10") payment.Add(new XElement(Cac + "PayeeFinancialAccount", B("ID", input.PaymentAccount), B("Name", input.PaymentAccountName)));
        root.Add(payment, new XElement(Cac + "PaymentTerms", B("Note", "Zahlbar bis " + Date(invoice.DueDate))));
        var tax = new XElement(Cac + "TaxTotal", Amount("TaxAmount", taxTotal));
        foreach (var group in groups)
        {
            var net = group.Sum(x => x.Line.Net);
            var category = new XElement(Cac + "TaxCategory", B("ID", group.Key.TaxCategory));
            if (group.Key.TaxCategory != "O") category.Add(B("Percent", Decimal(group.Key.Rate)));
            if (group.Key.ExemptionReasonCode.Length > 0) category.Add(B("TaxExemptionReasonCode", group.Key.ExemptionReasonCode));
            if (group.Key.ExemptionReason.Length > 0) category.Add(B("TaxExemptionReason", group.Key.ExemptionReason));
            category.Add(new XElement(Cac + "TaxScheme", B("ID", "VAT")));
            tax.Add(new XElement(Cac + "TaxSubtotal", Amount("TaxableAmount", net), Amount("TaxAmount", Money.Round(net * group.Key.Rate / 100m)), category));
        }
        root.Add(tax, new XElement(Cac + "LegalMonetaryTotal", Amount("LineExtensionAmount", invoice.Snapshot.Net),
            Amount("TaxExclusiveAmount", invoice.Snapshot.Net), Amount("TaxInclusiveAmount", invoice.Snapshot.Gross),
            Amount("PayableAmount", invoice.Snapshot.Gross)));
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i]; var classification = classifications[i];
            var category = new XElement(Cac + "ClassifiedTaxCategory", B("ID", classification.TaxCategory));
            if (classification.TaxCategory != "O") category.Add(B("Percent", Decimal(line.Inputs.VatPercent)));
            category.Add(new XElement(Cac + "TaxScheme", B("ID", "VAT")));
            var item = new XElement(Cac + "Item", B("Name", line.Inputs.Description), category);
            // A price per the complete invoiced base quantity preserves cumulative cent settlements exactly.
            // It is not a rewritten unit price or a recalculation of an old quote.
            var quantity = new XElement(Cbc + (credit ? "CreditedQuantity" : "InvoicedQuantity"), new XAttribute("unitCode", classification.UnitCode), Decimal(line.Inputs.Quantity));
            var price = new XElement(Cac + "Price", Amount("PriceAmount", line.Net),
                new XElement(Cbc + "BaseQuantity", new XAttribute("unitCode", classification.UnitCode), Decimal(line.Inputs.Quantity)));
            root.Add(new XElement(Cac + (credit ? "CreditNoteLine" : "InvoiceLine"), B("ID", (i + 1).ToString(CultureInfo.InvariantCulture)), quantity,
                Amount("LineExtensionAmount", line.Net), item, price));
        }
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false }))
            new XDocument(new XDeclaration("1.0", "utf-8", null), root).WriteTo(writer);
        var bytes = output.ToArray();
        Guard.Require(bytes.Length <= TradeXml.MaximumBytes, "xml_budget", "The structured invoice exceeds the supported XML budget.");
        return new(bytes, invoice.IssuedDigest, ContentDigest.Of(new { invoice.TenantId, invoice.Id, invoice.IssuedDigest, Mapping = input }),
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
    }
    private static void ValidateParty(ElectronicPartyDetails party, string expectedAddress, bool seller)
    {
        ArgumentNullException.ThrowIfNull(party); ArgumentNullException.ThrowIfNull(party.Address);
        Guard.Require(party.Address.SourceAddress == expectedAddress, "address_mapping_changed", "The structured postal mapping must explicitly reference the exact frozen source address.");
        Guard.Text(party.Address.Street, "Street", 240); Guard.Text(party.Address.City, "City", 100); Guard.Text(party.Address.PostalCode, "Postal code", 30);
        Guard.Require(party.Address.CountryCode is { Length: 2 } && party.Address.CountryCode.All(c => c is >= 'A' and <= 'Z'), "invalid_country", "Supply a two-letter uppercase country code.");
        Guard.Text(party.EndpointScheme, "Electronic address scheme", 10); Guard.Text(party.Endpoint, "Electronic address", 254);
        Guard.Require(party.TaxScheme is "" or "VAT" or "FC", "tax_identifier_scheme", "Use explicit VAT or local tax-registration (FC) identifiers, or leave both buyer tax fields empty.");
        Guard.Text(party.TaxIdentifier, "Tax identifier", 80, seller);
        Guard.Require((party.TaxScheme.Length > 0) == (party.TaxIdentifier.Length > 0), "tax_identifier_scheme", "Tax scheme and identifier must be supplied together.");
        Guard.Text(party.ContactName, "Contact name", 240, seller); Guard.Text(party.ContactTelephone, "Contact telephone", 80, seller); Guard.Text(party.ContactEmail, "Contact email", 254, seller);
        if (party.ContactEmail.Length > 0) Guard.Require(System.Net.Mail.MailAddress.TryCreate(party.ContactEmail, out _), "invalid_email", "Supply a valid contact email address.");
    }
    private static void ValidateClassification(ElectronicLineClassification value, decimal rate)
    {
        Guard.Text(value.UnitCode, "UNECE unit code", 3);
        Guard.Require(value.UnitCode.All(c => char.IsAsciiLetterOrDigit(c) && !char.IsLower(c)), "invalid_unit_code", "Supply an explicit uppercase UNECE unit code.");
        Guard.Require(value.TaxCategory is "S" or "Z" or "E" or "AE" or "O" or "G" or "K" or "L" or "M", "unsupported_tax_category", "Supply an EN 16931 VAT category; a zero rate alone does not identify a tax treatment.");
        Guard.Require(value.TaxCategory is "S" or "L" or "M" ? rate > 0 : rate == 0,
            "tax_category_rate", "The explicit VAT category and frozen percentage disagree.");
        Guard.Text(value.ExemptionReason, "Tax exemption reason", 1000, false); Guard.Text(value.ExemptionReasonCode, "Tax exemption code", 80, false);
        if (value.TaxCategory is "E" or "AE" or "O" or "G" or "K")
            Guard.Require(value.ExemptionReason.Length > 0 || value.ExemptionReasonCode.Length > 0, "exemption_required", "This category requires its reviewed tax-exemption explanation or code.");
        else Guard.Require(value.ExemptionReason.Length == 0 && value.ExemptionReasonCode.Length == 0, "unexpected_exemption", "Do not attach an exemption explanation to a taxable or zero-rated category.");
    }
    private static XElement Party(string name, ElectronicPartyDetails details)
    {
        var party = new XElement(Cac + "Party", new XElement(Cbc + "EndpointID", new XAttribute("schemeID", details.EndpointScheme), details.Endpoint),
            new XElement(Cac + "PartyName", B("Name", name)),
            new XElement(Cac + "PostalAddress", B("StreetName", details.Address.Street), B("CityName", details.Address.City), B("PostalZone", details.Address.PostalCode),
                new XElement(Cac + "Country", B("IdentificationCode", details.Address.CountryCode))));
        if (details.TaxIdentifier.Length > 0) party.Add(new XElement(Cac + "PartyTaxScheme", B("CompanyID", details.TaxIdentifier), new XElement(Cac + "TaxScheme", B("ID", details.TaxScheme))));
        party.Add(new XElement(Cac + "PartyLegalEntity", B("RegistrationName", name)));
        if (details.ContactName.Length > 0 || details.ContactTelephone.Length > 0 || details.ContactEmail.Length > 0)
            party.Add(new XElement(Cac + "Contact", details.ContactName.Length > 0 ? B("Name", details.ContactName) : null,
                details.ContactTelephone.Length > 0 ? B("Telephone", details.ContactTelephone) : null,
                details.ContactEmail.Length > 0 ? B("ElectronicMail", details.ContactEmail) : null));
        return party;
    }
    private static XElement B(string name, string value) => new(Cbc + name, value);
    private static XElement Amount(string name, decimal value) => new(Cbc + name, new XAttribute("currencyID", "EUR"), value.ToString("0.00", CultureInfo.InvariantCulture));
    private static string Decimal(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static bool ValidIban(string? value)
    {
        if (value is not { Length: >= 15 and <= 34 } || !value.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9') ||
            !value[..2].All(c => c is >= 'A' and <= 'Z') || !value.Substring(2, 2).All(char.IsAsciiDigit)) return false;
        var remainder = 0;
        foreach (var c in value[4..] + value[..4])
        {
            if (c is >= '0' and <= '9') remainder = (remainder * 10 + c - '0') % 97;
            else remainder = (remainder * 100 + c - 'A' + 10) % 97;
        }
        return remainder == 1;
    }
}
