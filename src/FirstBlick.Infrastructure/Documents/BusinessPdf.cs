using System.Globalization;
using FirstBlick.Core.Business;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace FirstBlick.Infrastructure.Documents;

/// <summary>Versioned PDF renderer. Financial amounts are read exclusively from validated, frozen snapshots.</summary>
public static class BusinessPdf
{
    public const string Version = "firstblick-business-pdf/1.0";
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    static BusinessPdf() => GlobalFontSettings.FontResolver = new InstalledFontResolver();

    public static byte[] Quote(Quote quote)
    {
        quote.Validate();
        Guard.Require(quote.Snapshot is not null && quote.Presentation is not null, "legacy_evidence_incomplete", "A frozen quote and party presentation are required for PDF generation. Historical missing evidence cannot be inferred from today's master data.");
        using var writer = new Report("ANGEBOT", quote.Number, quote.IssuedDigest);
        writer.Text(quote.Presentation.SellerName, true, 15);
        writer.Space(12); writer.Text(quote.Presentation.BuyerName, true); writer.Text(quote.Presentation.BuyerAddress);
        writer.Space(16); writer.Text(quote.Title, true, 14);
        writer.Text($"Angebotsversion {quote.Version} · {quote.Number}");
        writer.Text($"Ausgestellt am {quote.Presentation.IssuedAt:dd.MM.yyyy}");
        if (quote.ValidUntil.HasValue) writer.Text($"Gültig bis {quote.ValidUntil.Value:dd.MM.yyyy}");
        writer.Space(18); writer.Lines(quote.Snapshot);
        writer.Space(14); writer.Text("Dieses Dokument enthält die eingefrorenen Preise und Mengen dieser Angebotsversion.", size: 9);
        return writer.Finish();
    }

    public static byte[] Invoice(Invoice invoice)
    {
        invoice.Validate();
        Guard.Require(invoice.Status == InvoiceStatus.Issued && invoice.Snapshot is not null, "invalid_transition", "An issued invoice is required for PDF generation.");
        var title = invoice.Kind switch { InvoiceKind.CreditNote => "RECHNUNGSKORREKTUR", InvoiceKind.Progress => "ABSCHLAGSRECHNUNG", _ => "RECHNUNG" };
        using var writer = new Report(title, invoice.Number, invoice.IssuedDigest);
        writer.Text(invoice.SellerName, true, 15); writer.Text(invoice.SellerAddress);
        if (invoice.SellerTaxId.Length > 0) writer.Text($"Steuerkennung: {invoice.SellerTaxId}", size: 9);
        writer.Space(16); writer.Text(invoice.BuyerName, true); writer.Text(invoice.BuyerAddress);
        writer.Space(16); writer.Text(invoice.Title, true, 14);
        writer.Text($"{invoice.Number} · Rechnungsdatum {invoice.IssueDate:dd.MM.yyyy}");
        writer.Text($"Leistungszeitraum {invoice.ServiceFrom:dd.MM.yyyy} bis {invoice.ServiceUntil:dd.MM.yyyy}");
        writer.Text($"Fällig am {invoice.DueDate:dd.MM.yyyy}");
        if (invoice.Kind == InvoiceKind.CreditNote)
        {
            writer.Text($"Korrektur zu Beleg-ID {invoice.OriginalInvoiceId}", size: 9);
            writer.Text(invoice.CorrectionReason);
            writer.Text("Die ausgewiesenen positiven Beträge mindern die Forderung der ursprünglichen Rechnung.", true, 9);
        }
        // Invoice.Validate already checks the invoice-specific frozen contract,
        // including partial quantities with their complete measurement evidence.
        writer.Space(18); writer.Lines(invoice.Snapshot, validateQuote: false);
        return writer.Finish();
    }

    public static byte[] Technical(string title, string digest, IEnumerable<string> lines)
    {
        using var writer = new Report("TECHNISCHER RECHENBERICHT", "Ref. " + digest[..12], digest);
        writer.Text(title, true, 15); writer.Space(12);
        writer.Text("Rechenhilfe – kein normativer oder baurechtlicher Nachweis", true, 11);
        writer.Space(12); foreach (var line in lines) writer.Text(line, size: 9);
        return writer.Finish();
    }

    private sealed class InstalledFontResolver : IFontResolver
    {
        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) => familyName == "FirstBlick" ? new FontResolverInfo(isBold ? "FirstBlick-Bold" : "FirstBlick-Regular") : null;
        public byte[]? GetFont(string faceName)
        {
            var directory = Environment.GetEnvironmentVariable("FIRSTBLICK_FONT_DIRECTORY") ?? "/usr/share/fonts/truetype/dejavu";
            var path = Path.Combine(directory, faceName == "FirstBlick-Bold" ? "DejaVuSans-Bold.ttf" : "DejaVuSans.ttf");
            Guard.Require(File.Exists(path), "provider_unconfigured", "Install the documented DejaVu Sans fonts or set FIRSTBLICK_FONT_DIRECTORY before generating PDFs.");
            return File.ReadAllBytes(path);
        }
    }

    private sealed class Report : IDisposable
    {
        private readonly PdfDocument document = new();
        private readonly string title;
        private readonly string number;
        private readonly string digest;
        private XGraphics graphics = null!;
        private double y;
        private const double Left = 45;
        private const double Width = 505;
        private static readonly XColor Ink = XColor.FromArgb(30, 45, 58);
        private static readonly XColor Accent = XColor.FromArgb(22, 101, 84);

        public Report(string title, string number, string digest)
        {
            this.title = title; this.number = number; this.digest = digest;
            document.Info.Title = $"{title} {number}"; document.Info.Creator = Version;
            document.Info.Subject = $"Frozen source SHA-256: {digest}";
            NewPage();
        }

        private void NewPage()
        {
            graphics?.Dispose();
            var page = document.AddPage(); page.Size = PdfSharp.PageSize.A4;
            graphics = XGraphics.FromPdfPage(page);
            graphics.DrawRectangle(new XSolidBrush(Accent), Left, 30, Width, 4);
            graphics.DrawString(title, Font(9, true), new XSolidBrush(Accent), new XPoint(Left, 52));
            graphics.DrawString(number, Font(9), new XSolidBrush(Ink), new XRect(Left, 40, Width, 16), XStringFormats.TopRight);
            y = 86;
        }

        private static XFont Font(double size, bool bold = false) => new("FirstBlick", size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
        public void Space(double height) { y += height; if (y > 747) NewPage(); }

        public void Text(string? text, bool bold = false, double size = 10)
        {
            var font = Font(size, bold);
            foreach (var paragraph in (text ?? "").Replace("\r", "", StringComparison.Ordinal).Split('\n'))
            {
                foreach (var line in Wrap(paragraph, font, Width))
                {
                    if (y + size * 1.45 > 752) NewPage();
                    graphics.DrawString(line, font, new XSolidBrush(Ink), new XPoint(Left, y));
                    y += size * 1.45;
                }
            }
        }

        private List<string> Wrap(string value, XFont font, double width)
        {
            var lines = new List<string>(); var current = "";
            // Split on Unicode text elements so wrapping never cuts a surrogate pair or combining sequence.
            var elements = StringInfo.GetTextElementEnumerator(value);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                if (graphics.MeasureString(current + element, font).Width > width && current.Length > 0)
                {
                    var space = current.LastIndexOf(' ');
                    if (space > current.Length / 2) { lines.Add(current[..space]); current = current[(space + 1)..]; }
                    else { lines.Add(current); current = ""; }
                }
                current += element;
            }
            lines.Add(current); return lines;
        }

        public void Lines(QuoteSnapshot snapshot, bool validateQuote = true)
        {
            if (validateQuote) snapshot.Validate();
            var index = 0;
            foreach (var line in snapshot.Lines)
            {
                // Keep the heading and price row together; long descriptions may continue on subsequent pages.
                if (y > 692) NewPage();
                Text($"{++index:D3}   {line.Inputs.Description}", true);
                Text($"{line.Inputs.Quantity.ToString("0.######", German)} {line.Inputs.Unit} × {line.UnitNet.ToString("N2", German)} EUR = {line.Net.ToString("N2", German)} EUR netto", size: 9);
                Text($"USt {line.Inputs.VatPercent.ToString("0.######", German)} %: {line.Tax.ToString("N2", German)} EUR · Brutto {line.Gross.ToString("N2", German)} EUR", size: 9);
                Space(9);
            }
            if (y > 647) NewPage();
            graphics.DrawLine(new XPen(Accent, 1), Left, y, Left + Width, y); Space(18);
            Text($"Nettosumme  {snapshot.Net.ToString("N2", German)} EUR", true);
            foreach (var tax in snapshot.Lines.GroupBy(x => x.Inputs.VatPercent))
                Text($"USt {tax.Key.ToString("0.######", German)} % auf {tax.Sum(x => x.Net).ToString("N2", German)} EUR: {tax.Sum(x => x.Tax).ToString("N2", German)} EUR");
            Text($"Gesamtbetrag  {snapshot.Gross.ToString("N2", German)} EUR", true, 13);
        }

        public byte[] Finish()
        {
            graphics.Dispose();
            for (var i = 0; i < document.PageCount; i++)
            {
                using var footer = XGraphics.FromPdfPage(document.Pages[i], XGraphicsPdfPageOptions.Append);
                footer.DrawLine(new XPen(XColors.LightGray, .5), Left, 779, Left + Width, 779);
                footer.DrawString($"{number} · Seite {i + 1} von {document.PageCount}", Font(8), new XSolidBrush(Ink), new XPoint(Left, 794));
                footer.DrawString($"SHA-256 {digest}", Font(6), new XSolidBrush(Ink), new XPoint(Left, 808));
            }
            using var output = new MemoryStream(); document.Save(output, false); return output.ToArray();
        }

        public void Dispose() { graphics.Dispose(); document.Dispose(); }
    }
}
