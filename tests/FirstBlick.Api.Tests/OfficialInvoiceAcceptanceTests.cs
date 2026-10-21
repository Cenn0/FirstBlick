using System.Text;
using System.Xml.Linq;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;
using FirstBlick.Infrastructure.Integrations;
using Xunit;

namespace FirstBlick.Api.Tests;

public sealed class OfficialInvoiceAcceptanceTests
{
    private static string Explain(ElectronicValidationReport report)
        => string.Join("\n", report.Issues.Select(x => $"{x.Code}: {x.Message}")) + "\n" + Encoding.UTF8.GetString(Convert.FromBase64String(report.ReportXmlBase64));

    [Fact]
    public async Task Generated_UBL_invoice_passes_original_XRechnung_XSD_and_Schematron_rules()
    {
        var invoice = OfficialInvoiceFixtures.Invoice();
        var xml = ElectronicInvoiceOutput.Render(invoice, OfficialInvoiceFixtures.Mapping(invoice));
        var result = await new KositInvoiceValidator(OfficialXmlValidationTests.Bundle()).Validate(xml.Xml);
        Assert.True(result.Accepted, Explain(result)); Assert.Equal(xml.XmlSha256, result.SourceSha256);
        Assert.DoesNotContain(result.Issues, x => x.Severity is "fatal" or "error");
    }
    [Fact]
    public async Task Generated_referenced_credit_note_passes_original_rules()
    {
        var original = OfficialInvoiceFixtures.Invoice();
        var credit = InvoiceWorkflow.DraftCreditNote(original, "Synthetic full correction");
        credit.DueDate = new(2026, 11, 20);
        InvoiceWorkflow.IssueCreditNote(credit, original, [], "SYN-CREDIT-001", new(2026, 10, 21));
        var xml = ElectronicInvoiceOutput.Render(credit, OfficialInvoiceFixtures.Mapping(credit), original);
        var result = await new KositInvoiceValidator(OfficialXmlValidationTests.Bundle()).Validate(xml.Xml);
        Assert.True(result.Accepted, Explain(result)); Assert.Equal(xml.XmlSha256, result.SourceSha256);
    }
    [Fact]
    public async Task Schema_correct_but_semantically_inconsistent_invoice_is_rejected()
    {
        var invoice = OfficialInvoiceFixtures.Invoice();
        var xml = ElectronicInvoiceOutput.Render(invoice, OfficialInvoiceFixtures.Mapping(invoice));
        var document = TradeXml.Load(xml.Xml).Document;
        XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        document.Descendants(cbc + "PayableAmount").Single().Value = "999.00";
        var bytes = Encoding.UTF8.GetBytes(document.ToString());
        var result = await new KositInvoiceValidator(OfficialXmlValidationTests.Bundle()).Validate(bytes);
        Assert.False(result.Accepted); Assert.NotEmpty(result.Issues);
        Assert.NotEqual(xml.XmlSha256, result.SourceSha256);
    }
}
