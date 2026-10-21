using System.Text;
using FirstBlick.Core.Integrations;
using Xunit;

namespace FirstBlick.Core.Tests.Integrations;

public sealed class ElectronicInvoiceXmlTests
{
    private const string Ubl = """
        <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"
                 xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
                 xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
          <cbc:CustomizationID>urn:cen.eu:en16931:2017</cbc:CustomizationID>
          <cbc:ProfileID>urn:fdc:peppol.eu:2017:poacc:billing:01:1.0</cbc:ProfileID>
          <cbc:ID>TEST-1</cbc:ID><cbc:IssueDate>2026-10-20</cbc:IssueDate>
          <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
          <cac:AccountingSupplierParty><cac:Party><cbc:EndpointID>supplier@example.test</cbc:EndpointID><cac:PartyLegalEntity><cbc:RegistrationName>Supplier Test</cbc:RegistrationName></cac:PartyLegalEntity></cac:Party></cac:AccountingSupplierParty>
          <cac:AccountingCustomerParty><cac:Party><cbc:EndpointID>buyer@example.test</cbc:EndpointID><cac:PartyLegalEntity><cbc:RegistrationName>Buyer Test</cbc:RegistrationName></cac:PartyLegalEntity></cac:Party></cac:AccountingCustomerParty>
          <cac:LegalMonetaryTotal><cbc:TaxExclusiveAmount currencyID="EUR">100.00</cbc:TaxExclusiveAmount><cbc:TaxInclusiveAmount currencyID="EUR">119.00</cbc:TaxInclusiveAmount><cbc:PayableAmount currencyID="EUR">119.00</cbc:PayableAmount></cac:LegalMonetaryTotal>
          <cac:InvoiceLine><cbc:ID>1</cbc:ID><cbc:InvoicedQuantity unitCode="C62">2</cbc:InvoicedQuantity><cbc:LineExtensionAmount currencyID="EUR">100.00</cbc:LineExtensionAmount><cac:Item><cbc:Name>Roof repair</cbc:Name></cac:Item></cac:InvoiceLine>
        </Invoice>
        """;

    [Fact]
    public void PlainUblPreviewPreservesMoneyAndCannotBook()
    {
        var p = ElectronicInvoiceXml.Preview(Encoding.UTF8.GetBytes(Ubl));
        Assert.Equal("UBL-2.1-Invoice", p.Syntax);
        Assert.Equal("TEST-1", p.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 10, 20), p.IssueDate);
        Assert.Equal((100m, 19m, 119m), (p.NetAmount, p.TaxAmount, p.PayableAmount));
        Assert.Equal(("1", 2m, "C62"), (p.Lines[0].Reference, p.Lines[0].Quantity, p.Lines[0].UnitCode));
        Assert.False(p.ExternalValidationPerformed); Assert.False(p.MayPostAutomatically);
        Assert.Contains("external_xsd_schematron_and_tax_profile_validation_not_performed", p.ReviewIssues);
        Assert.Equal(64, p.SourceSha256.Length);
    }

    [Fact]
    public void DtdAndUnknownDocumentsAreRejectedNotTransformed()
    {
        var dtd = "<!DOCTYPE Invoice [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><Invoice>&xxe;</Invoice>";
        Assert.Equal("invalid_xml", Assert.Throws<FirstBlick.Core.Business.DomainException>(() =>
            ElectronicInvoiceXml.Preview(Encoding.UTF8.GetBytes(dtd))).Code);
        Assert.Equal("unsupported_einvoice_syntax", Assert.Throws<FirstBlick.Core.Business.DomainException>(() =>
            ElectronicInvoiceXml.Preview(Encoding.UTF8.GetBytes("<Invoice/>"))).Code);
        Assert.Equal("xml_budget", Assert.Throws<FirstBlick.Core.Business.DomainException>(() =>
            ElectronicInvoiceXml.Preview(new byte[TradeXml.MaximumBytes + 1])).Code);
    }

    [Fact]
    public void MissingUBLIdentityOrAmbiguousLinesAreNotAcceptedAsAPreview()
    {
        var duplicate = Ubl.Replace("</Invoice>", """
            <cac:InvoiceLine><cbc:ID>1</cbc:ID><cbc:InvoicedQuantity unitCode="C62">1</cbc:InvoicedQuantity><cbc:LineExtensionAmount currencyID="EUR">0.00</cbc:LineExtensionAmount><cac:Item><cbc:Name>Duplicate</cbc:Name></cac:Item></cac:InvoiceLine>
            </Invoice>
            """, StringComparison.Ordinal);
        Assert.Equal("xml_duplicate_line", Assert.Throws<FirstBlick.Core.Business.DomainException>(() =>
            ElectronicInvoiceXml.Preview(Encoding.UTF8.GetBytes(duplicate))).Code);
        Assert.Equal("required", Assert.Throws<FirstBlick.Core.Business.DomainException>(() =>
            ElectronicInvoiceXml.Preview(Encoding.UTF8.GetBytes(Ubl.Replace("<cbc:ID>TEST-1</cbc:ID>", "", StringComparison.Ordinal)))).Code);
    }

    [Fact]
    public void CiiMonetarySummaryCanBeReviewedWithoutImplyingEn16931Validity()
    {
        var xml = """
            <rsm:CrossIndustryInvoice xmlns:rsm="urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100"
             xmlns:ram="urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100"
             xmlns:udt="urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100">
              <rsm:ExchangedDocument><ram:ID>CII-1</ram:ID><ram:TypeCode>380</ram:TypeCode><ram:IssueDateTime><udt:DateTimeString format="102">20261020</udt:DateTimeString></ram:IssueDateTime></rsm:ExchangedDocument>
              <rsm:SupplyChainTradeTransaction>
                <ram:IncludedSupplyChainTradeLineItem>
                  <ram:AssociatedDocumentLineDocument><ram:LineID>1</ram:LineID></ram:AssociatedDocumentLineDocument>
                  <ram:SpecifiedTradeProduct><ram:Name>Test roof</ram:Name></ram:SpecifiedTradeProduct>
                  <ram:SpecifiedLineTradeDelivery><ram:BilledQuantity unitCode="C62">1</ram:BilledQuantity></ram:SpecifiedLineTradeDelivery>
                  <ram:SpecifiedLineTradeSettlement><ram:SpecifiedTradeSettlementLineMonetarySummation><ram:LineTotalAmount>50.00</ram:LineTotalAmount></ram:SpecifiedTradeSettlementLineMonetarySummation></ram:SpecifiedLineTradeSettlement>
                </ram:IncludedSupplyChainTradeLineItem>
                <ram:ApplicableHeaderTradeAgreement><ram:SellerTradeParty><ram:Name>Seller</ram:Name></ram:SellerTradeParty><ram:BuyerTradeParty><ram:Name>Buyer</ram:Name></ram:BuyerTradeParty></ram:ApplicableHeaderTradeAgreement>
                <ram:ApplicableHeaderTradeSettlement><ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode><ram:SpecifiedTradeSettlementHeaderMonetarySummation><ram:TaxBasisTotalAmount>50.00</ram:TaxBasisTotalAmount><ram:GrandTotalAmount>59.50</ram:GrandTotalAmount><ram:DuePayableAmount>59.50</ram:DuePayableAmount></ram:SpecifiedTradeSettlementHeaderMonetarySummation></ram:ApplicableHeaderTradeSettlement>
              </rsm:SupplyChainTradeTransaction>
            </rsm:CrossIndustryInvoice>
            """;
        var result = ElectronicInvoiceXml.Preview(Encoding.UTF8.GetBytes(xml));
        Assert.Equal("CII-D16B-structure", result.Syntax);
        Assert.Equal("CII-1", result.InvoiceNumber);
        Assert.Equal(9.50m, result.TaxAmount);
        Assert.False(result.MayPostAutomatically);
    }
}
