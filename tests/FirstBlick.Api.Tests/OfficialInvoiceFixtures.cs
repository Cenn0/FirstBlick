using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;

namespace FirstBlick.Api.Tests;

/// <summary>Independently authored fictitious business records, never real customer data.</summary>
internal static class OfficialInvoiceFixtures
{
    public static Invoice Invoice()
    {
        var invoice = new Invoice
        {
            TenantId = Guid.NewGuid(), CustomerId = Guid.NewGuid(), OrderId = Guid.NewGuid(),
            Title = "Synthetic installation", SellerName = "Synthetic Seller GmbH", SellerAddress = "Teststrasse 1, 10115 Berlin", SellerTaxId = "DE123456789",
            BuyerName = "Synthetic Buyer GmbH", BuyerAddress = "Teststrasse 2, 10115 Berlin", DueDate = new(2026, 11, 20),
            ServiceFrom = new(2026, 10, 1), ServiceUntil = new(2026, 10, 20),
            Lines = [new() { Description = "Synthetic installation", Unit = "Stk", Quantity = 3, UnitMaterialCost = 33.33m, VatPercent = 19 }]
        };
        InvoiceWorkflow.Issue(invoice, "SYN-2026-001", new(2026, 10, 20)); return invoice;
    }
    public static ElectronicInvoiceOutputInput Mapping(Invoice invoice) => new(invoice.IssuedDigest, "SYNTHETIC-REFERENCE",
        new(new(invoice.SellerAddress, "Teststrasse 1", "Berlin", "10115", "DE"), "EM", "seller@example.test", "VAT", invoice.SellerTaxId, "Synthetic Contact", "+49 000 000000", "seller@example.test"),
        new(new(invoice.BuyerAddress, "Teststrasse 2", "Berlin", "10115", "DE"), "EM", "buyer@example.test", "", "", "", "", ""),
        "10", "", "", "Synthetic reviewed classification for an automated reference case",
        invoice.Lines.Select((_, i) => new ElectronicLineClassification(i + 1, "C62", "S")).ToArray());
}
