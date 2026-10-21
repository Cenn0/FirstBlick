using System.Xml.Linq;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;
using Xunit;

namespace FirstBlick.Core.Tests.Integrations;

public sealed class ElectronicInvoiceOutputTests
{
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    public static Invoice Issued(decimal net = 100m, decimal quantity = 1m, decimal rate = 19m)
    {
        var invoice = new Invoice { TenantId = Guid.NewGuid(), CustomerId = Guid.NewGuid(), OrderId = Guid.NewGuid(),
            Title = "Synthetic service", SellerName = "Synthetic Seller", SellerAddress = "Teststrasse 1, 10115 Berlin", SellerTaxId = "DE123456789",
            BuyerName = "Synthetic Buyer", BuyerAddress = "Teststrasse 2, 10115 Berlin", DueDate = new(2026, 11, 20),
            ServiceFrom = new(2026, 10, 1), ServiceUntil = new(2026, 10, 20),
            Lines = [new() { Description = "Synthetic <service> & installation", Unit = "Stk", Quantity = quantity, UnitMaterialCost = net, VatPercent = rate }] };
        InvoiceWorkflow.Issue(invoice, "SYN-2026-1", new(2026, 10, 20)); return invoice;
    }
    public static ElectronicInvoiceOutputInput Mapping(Invoice invoice) => new(invoice.IssuedDigest, "SYNTHETIC-BUYER-REFERENCE",
        new(new(invoice.SellerAddress, "Teststrasse 1", "Berlin", "10115", "DE"), "EM", "seller@example.test", "VAT", invoice.SellerTaxId, "Synthetic Contact", "+49 000 000000", "seller@example.test"),
        new(new(invoice.BuyerAddress, "Teststrasse 2", "Berlin", "10115", "DE"), "EM", "buyer@example.test", "", "", "", "", ""),
        "10", "", "", "Synthetic reviewed metadata; not real business data", invoice.Lines.Select((_, i) => new ElectronicLineClassification(i + 1, "C62", "S")).ToArray());

    [Fact]
    public void Ubl_output_preserves_actual_issued_money_and_escapes_descriptions()
    {
        var invoice = Issued(33.33m, 3);
        var output = ElectronicInvoiceOutput.Render(invoice, Mapping(invoice));
        var document = TradeXml.Load(output.Xml).Document;
        Assert.Equal(invoice.Snapshot!.Gross.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), document.Root!.Element(Cac + "LegalMonetaryTotal")!.Element(Cbc + "PayableAmount")!.Value);
        Assert.Equal("99.99", document.Descendants(Cac + "Price").Single().Element(Cbc + "PriceAmount")!.Value);
        Assert.Equal("3", document.Descendants(Cbc + "BaseQuantity").Single().Value);
        Assert.Equal("Synthetic <service> & installation", document.Descendants(Cac + "Item").Single().Element(Cbc + "Name")!.Value);
        Assert.Equal(64, output.MappingDigest.Length); Assert.Equal(output.XmlSha256, TradeXml.Load(output.Xml).Sha256);
        Assert.Equal(invoice.IssuedDigest, output.SourceInvoiceDigest);
    }
    [Fact]
    public void Output_does_not_silently_repair_incompatible_category_rounding()
    {
        var invoice = Issued(); invoice.Status = InvoiceStatus.Draft; invoice.Snapshot = null; invoice.IssuedDigest = "";
        invoice.Lines = [new() { Description = "A", UnitMaterialCost = .03m, VatPercent = 19 }, new() { Description = "B", UnitMaterialCost = .03m, VatPercent = 19 }];
        InvoiceWorkflow.Issue(invoice, "SYN-ROUNDING", new(2026, 10, 20));
        var error = Assert.Throws<DomainException>(() => ElectronicInvoiceOutput.Render(invoice, Mapping(invoice)));
        Assert.Equal("einvoice_tax_rounding_conflict", error.Code);
    }
    [Fact]
    public void Changed_source_address_or_digest_and_missing_line_mapping_are_rejected()
    {
        var invoice = Issued(); var mapping = Mapping(invoice);
        Assert.Throws<DomainException>(() => ElectronicInvoiceOutput.Render(invoice, mapping with { ExpectedInvoiceDigest = new('0', 64) }));
        Assert.Throws<DomainException>(() => ElectronicInvoiceOutput.Render(invoice, mapping with { Seller = mapping.Seller with { Address = mapping.Seller.Address with { SourceAddress = "Different" } } }));
        Assert.Throws<DomainException>(() => ElectronicInvoiceOutput.Render(invoice, mapping with { Lines = [] }));
    }
    [Fact]
    public void Zero_rate_needs_an_explicit_tax_treatment()
    {
        var invoice = Issued(rate: 0); var mapping = Mapping(invoice);
        Assert.Throws<DomainException>(() => ElectronicInvoiceOutput.Render(invoice, mapping));
        var result = ElectronicInvoiceOutput.Render(invoice, mapping with { Lines = [new(1, "C62", "Z")] });
        Assert.NotEmpty(result.Xml);
        Assert.Throws<DomainException>(() => ElectronicInvoiceOutput.Render(invoice, mapping with { Lines = [new(1, "C62", "E")] }));
    }
    [Theory]
    [InlineData("DE02120300000000202051", true)]
    [InlineData("DE00120300000000202051", false)]
    [InlineData("DE02 120300000000202051", false)]
    [InlineData("", false)]
    public void Bank_account_validation_never_normalizes_or_invents_an_account(string iban, bool valid)
        => Assert.Equal(valid, ElectronicInvoiceOutput.ValidIban(iban));
}
