using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Documents;
using PdfSharp.Pdf.IO;

namespace FirstBlick.Api.Tests;

public sealed class InvoicePdfTests
{
    [Fact]
    public void Partial_invoice_pdf_preserves_full_measurement_evidence_and_actual_frozen_amounts()
    {
        var tenant = Guid.NewGuid();
        var evidence = new QuantityEvidence(Guid.NewGuid(), new string('a', 64), Guid.NewGuid(), 10m,
            "m²", "m²", 1.35m, 0m, "Synthetic explicit measured conversion", QuantityLinks.MethodVersion, 13.5m, "");
        evidence = evidence with { Digest = ContentDigest.Of(evidence) }; evidence.Validate();
        var customer = new Customer { TenantId = tenant, Name = "Synthetischer Rechnungsempfänger", Address = "Beispielstraße 1" };
        var quote = new Quote { TenantId = tenant, CustomerId = customer.Id, ProjectId = Guid.NewGuid(), Title = "Gemessene Dachfläche",
            Lines = [new QuoteLine { Description = "Unveränderte Messgrundlage", Quantity = evidence.Quantity, QuantityEvidence = evidence, UnitMaterialCost = 10m, VatPercent = 19m }] };
        CommercialWorkflow.IssueQuote(quote, "AN-2026-000001");
        CommercialWorkflow.AcceptQuote(quote, quote.Snapshot!.Digest, new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero), new(2026, 10, 20));
        var order = CommercialWorkflow.CreateOrder(quote, "AU-2026-000001");
        var invoice = InvoiceWorkflow.DraftFromOrder(order, customer, InvoiceKind.Progress);
        invoice.SellerName = "Synthetischer Handwerksbetrieb"; invoice.SellerAddress = "Werkstattstraße 2";
        invoice.ServiceFrom = new(2026, 10, 1); invoice.ServiceUntil = new(2026, 10, 20); invoice.DueDate = new(2026, 11, 3);
        invoice.Lines[0].Quantity = 5m;
        InvoiceWorkflow.IssueForOrder(invoice, order, [], [], "RE-2026-000001", new(2026, 10, 20));
        var issuedDigest = invoice.IssuedDigest; var snapshotDigest = invoice.Snapshot!.Digest;

        var bytes = BusinessPdf.Invoice(invoice);

        Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8));
        using var stream = new MemoryStream(bytes); using var pdf = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        Assert.True(pdf.PageCount > 0); Assert.Contains(issuedDigest, pdf.Info.Subject);
        Assert.Equal(50m, invoice.Snapshot.Net); Assert.Equal(9.50m, invoice.Snapshot.Tax); Assert.Equal(59.50m, invoice.Snapshot.Gross);
        Assert.Equal(issuedDigest, invoice.IssuedDigest); Assert.Equal(snapshotDigest, invoice.Snapshot.Digest);
        Assert.Equal(evidence, invoice.Snapshot.Lines[0].Inputs.QuantityEvidence); Assert.Equal(13.5m, evidence.Quantity);
        if (Environment.GetEnvironmentVariable("FIRSTBLICK_PDF_EVIDENCE") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, "partial-measurement-invoice.pdf"), bytes);
        }
    }
}
