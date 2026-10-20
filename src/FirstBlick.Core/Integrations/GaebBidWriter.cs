using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FirstBlick.Core.Business;

namespace FirstBlick.Core.Integrations;

public sealed record GaebBidder(string Name, string Street, string PostalCode, string City, string Country, string Contact, string Email);
public sealed record GaebBidPrice(string PositionKey, string ExpectedItemDigest, decimal? UnitPrice, bool NotOffered, string PriceEvidence);
public sealed record GaebBidInput(string ExpectedSourceSha256, DateOnly BidDate, GaebBidder Bidder,
    string ReviewedCurrency, string ReviewEvidence, IReadOnlyList<GaebBidPrice> Prices);
public sealed record GaebBidLineResult(string PositionKey, string OriginalItemId, decimal Quantity,
    string SourceUnit, decimal? UnitPrice, decimal LineNet, bool NotOffered, string PriceEvidence);
public sealed record GaebBidResult(string Contract, string SourceSha256, string SourcePhase,
    string TargetPhase, string SchemaVersion, string SchemaEdition, string MappingDigest,
    decimal TotalNet, IReadOnlyList<GaebBidLineResult> Lines,
    IReadOnlyList<string> PhaseExcludedSourceFields, byte[] Xml, string XmlSha256);

/// <summary>
/// Phase-aware X83-to-X84 response or reviewed X84 repricing for regular quantity/unit-price
/// items. Caller must validate both original and output with the original XSDs. Original
/// bytes are archived separately; phase-specific exclusions are always part of the review.
/// </summary>
public static class GaebBidWriter
{
    private static readonly XNamespace Target = "http://www.gaeb.de/GAEB_DA_XML/DA84/3.3";
    private static readonly HashSet<string> PlainItemElements = new(StringComparer.Ordinal)
    { "Qty", "QU", "Description", "VAT", "UP", "IT", "NotOffered" };

    public static GaebBidResult Render(byte[] originalBytes, GaebBidInput input)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(input.Bidder);
        var (source, sourceSha) = TradeXml.Load(originalBytes);
        var preview = GaebXml.Preview(originalBytes);
        Guard.Require(sourceSha == input.ExpectedSourceSha256, "gaeb_source_changed", "The selected original GAEB bytes changed since review.");
        Guard.Require(input.BidDate != default && input.ReviewedCurrency == "EUR", "gaeb_bid_currency", "Review the bid date and declare EUR explicitly; no foreign-currency conversion is inferred.");
        Guard.Text(input.ReviewEvidence, "Bid review", 2000); ValidateBidder(input.Bidder);
        Guard.Items(input.Prices, "Reviewed bid prices", 1000, 1);
        Guard.Require(input.Prices.Count == preview.Items.Count && input.Prices.Select(x => x.PositionKey).Distinct(StringComparer.Ordinal).Count() == input.Prices.Count,
            "gaeb_bid_selection", "Every source position requires exactly one explicit price or not-offered decision.");
        var sourceNamespace = source.Root!.Name.Namespace;
        var sourceAward = source.Root.Element(sourceNamespace + "Award")!;
        var sourceBoq = sourceAward.Element(sourceNamespace + "BoQ")!;
        Guard.Require(!sourceBoq.Descendants().Any(x => x.Name.LocalName is "LotGrp" or "PerfDescr" or "SumDescr" or "SubDescr" or "QtySplit" or "QtyDeterm"),
            "gaeb_special_pricing", "Lot groups, performance descriptions, item compositions and derived quantities require their own price semantics; they cannot be silently flattened into regular unit-price items.");
        var items = sourceBoq.Descendants(sourceNamespace + "Item").Where(x => x.Parent?.Name == sourceNamespace + "Itemlist").ToArray();
        var byId = new Dictionary<string, GaebBidLineResult>(StringComparer.Ordinal);
        var results = new List<GaebBidLineResult>();
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i]; var row = preview.Items[i];
            var unknown = item.Elements().Where(x => !PlainItemElements.Contains(x.Name.LocalName)).Select(x => x.Name.LocalName).ToArray();
            Guard.Require(unknown.Length == 0 && item.Attribute("RNoIndex") is null,
                "gaeb_special_pricing", $"Position {row.PositionKey} has unsupported alternative, component or special pricing fields: {string.Join(", ", unknown)}.");
            Guard.Require(row.TenderQuantity is > 0 && !string.IsNullOrWhiteSpace(row.PositionId),
                "gaeb_quantity_review_required", "A regular bid requires an explicit source quantity and stable item ID; undetermined quantities are not invented.");
            var price = input.Prices.SingleOrDefault(x => x.PositionKey == row.PositionKey);
            Guard.Require(price is not null && price.ExpectedItemDigest == row.ItemDigest,
                "gaeb_item_changed", "A reviewed price does not match the source position and its content digest.");
            Guard.Text(price!.PriceEvidence, "Price evidence", 2000);
            if (price.NotOffered) Guard.Require(price.UnitPrice is null, "gaeb_bid_price_conflict", "A not-offered position must not also carry a price.");
            else
            {
                Guard.Require(price.UnitPrice.HasValue, "gaeb_bid_price_missing", "An offered item needs an explicitly reviewed unit price; missing is not zero.");
                Guard.Range(price.UnitPrice!.Value, 0, 999_999_999m, "GAEB bid unit price");
                Guard.Require(decimal.Round(price.UnitPrice.Value, 6) == price.UnitPrice.Value,
                    "gaeb_bid_price_precision", "Unit-price precision must not exceed six decimal places; the original XSD applies its additional constraints.");
            }
            var net = price.NotOffered ? 0 : Money.Round(checked(row.TenderQuantity!.Value * price.UnitPrice!.Value));
            Money.Amount(net, "GAEB line total");
            var result = new GaebBidLineResult(row.PositionKey, row.PositionId, row.TenderQuantity!.Value,
                row.Unit, price.UnitPrice, net, price.NotOffered, price.PriceEvidence.Trim());
            Guard.Require(byId.TryAdd(row.PositionId, result), "gaeb_duplicate_id", "Source item IDs must be unique."); results.Add(result);
        }
        var total = results.Sum(x => x.LineNet); Money.Amount(total, "GAEB bid total");
        var excluded = new List<string>(); XElement root;
        if (preview.ExchangePhase == "X84")
        {
            root = new XElement(source.Root);
            var gaebInfo = root.Element(Target + "GAEBInfo")!;
            gaebInfo.SetElementValue(Target + "Date", Date(input.BidDate));
            // GAEB 3.3 requires information about the software ACTUALLY exporting this X84.
            // Copying the original tender/bid producer into GAEBInfo would misstate provenance.
            var producer = gaebInfo.Element(Target + "ProgSystem");
            Guard.Require(producer is not null, "gaeb_missing_producer", "The original X84 lacks a producer field required for a truthful re-export.");
            producer!.Value = "FirstBlick";
            var award = root.Element(Target + "Award")!;
            award.Element(Target + "CTR")!.ReplaceWith(Contractor(input.Bidder));
            UpdateBody(award.Element(Target + "BoQ")!.Element(Target + "BoQBody")!, byId);
            SetTotal(award.Element(Target + "BoQ")!.Element(Target + "BoQInfo")!, total);
        }
        else
        {
            root = new XElement(Target + "GAEB", new XElement(Target + "GAEBInfo",
                E("Version", "3.3"), E("VersDate", "2021-05"), E("Date", Date(input.BidDate)), E("ProgSystem", "FirstBlick")));
            var sourceProject = source.Root.Element(sourceNamespace + "PrjInfo");
            if (sourceProject?.Element(sourceNamespace + "NamePrj") is { } projectName)
            {
                var project = new XElement(Target + "PrjInfo", ConvertNamespace(projectName));
                foreach (var name in new[] { "PrjID", "LblPrj" }) if (sourceProject.Element(sourceNamespace + name) is { } field) project.Add(ConvertNamespace(field));
                root.Add(project);
            }
            var sourceInfo = sourceBoq.Element(sourceNamespace + "BoQInfo")!;
            var info = new XElement(Target + "BoQInfo", ConvertNamespace(sourceInfo.Element(sourceNamespace + "Name")
                ?? throw new DomainException("gaeb_boq_name", "The original BoQ name is required.")));
            foreach (var breakdown in sourceInfo.Elements(sourceNamespace + "BoQBkdn")) info.Add(ConvertNamespace(breakdown));
            info.Add(new XElement(Target + "Totals", E("Total", Amount(total))));
            var boq = new XElement(Target + "BoQ", SourceAttributes(sourceBoq), info,
                ConvertBody(sourceBoq.Element(sourceNamespace + "BoQBody")!, sourceNamespace, byId, excluded));
            root.Add(new XElement(Target + "Award", E("DP", "84"), Contractor(input.Bidder), boq));
            foreach (var child in source.Root.Elements()) if (child.Name.LocalName is not ("GAEBInfo" or "PrjInfo" or "Award")) excluded.Add("GAEB/" + child.Name.LocalName);
            foreach (var child in sourceAward.Elements()) if (child.Name.LocalName is not ("DP" or "BoQ")) excluded.Add("GAEB/Award/" + child.Name.LocalName);
            foreach (var child in sourceInfo.Elements()) if (child.Name.LocalName is not ("Name" or "BoQBkdn")) excluded.Add("GAEB/Award/BoQ/BoQInfo/" + child.Name.LocalName);
            if (sourceProject is not null)
                foreach (var child in sourceProject.Elements()) if (child.Name.LocalName is not ("NamePrj" or "PrjID" or "LblPrj")) excluded.Add("GAEB/PrjInfo/" + child.Name.LocalName);
        }
        Guard.Require(excluded.Count <= 500, "gaeb_projection_budget", "The phase-specific metadata review exceeds its supported budget.");
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false }))
            new XDocument(new XDeclaration("1.0", "utf-8", null), root).WriteTo(writer);
        var bytes = output.ToArray(); Guard.Require(bytes.Length <= TradeXml.MaximumBytes, "xml_budget", "The GAEB bid output exceeds one MiB.");
        return new("firstblick.gaeb-bid/2.0.0", sourceSha, preview.ExchangePhase, "X84", "3.3", "2021-05",
            ContentDigest.Of(new { sourceSha, Input = input, results, excluded }), total, results.AsReadOnly(),
            excluded.AsReadOnly(), bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static XElement ConvertBody(XElement source, XNamespace ns, IReadOnlyDictionary<string, GaebBidLineResult> prices, List<string> excluded)
    {
        var body = new XElement(Target + "BoQBody");
        foreach (var child in source.Elements())
        {
            if (child.Name == ns + "BoQCtgy")
            {
                var category = new XElement(Target + "BoQCtgy", SourceAttributes(child));
                if (child.Element(ns + "BoQBody") is { } inner) category.Add(ConvertBody(inner, ns, prices, excluded));
                var total = child.Descendants(ns + "Item").Sum(x => prices[(string)x.Attribute("ID")!].LineNet);
                category.Add(new XElement(Target + "Totals", E("Total", Amount(total)))); body.Add(category);
                foreach (var field in child.Elements()) if (field.Name.LocalName != "BoQBody") excluded.Add($"BoQCtgy[{(string?)child.Attribute("ID")}]/" + field.Name.LocalName);
            }
            else if (child.Name == ns + "Itemlist")
            {
                var list = new XElement(Target + "Itemlist");
                foreach (var item in child.Elements())
                {
                    Guard.Require(item.Name == ns + "Item", "gaeb_special_pricing", "This bid writer does not silently reinterpret non-item content inside Itemlist.");
                    var price = prices[(string)item.Attribute("ID")!]; var target = new XElement(Target + "Item", SourceAttributes(item));
                    if (price.NotOffered) target.Add(E("NotOffered", "Yes"));
                    target.Add(E("Qty", Decimal(price.Quantity)));
                    if (!price.NotOffered) target.Add(E("UP", Decimal(price.UnitPrice!.Value)), E("IT", Amount(price.LineNet)));
                    if (item.Element(ns + "VAT") is { } vat) target.Add(ConvertNamespace(vat));
                    var description = item.Element(ns + "Description");
                    if (description is not null)
                    {
                        var detail = description.Element(ns + "CompleteText")?.Element(ns + "DetailTxt");
                        var outline = description.Descendants(ns + "OutlineText").FirstOrDefault();
                        if (detail is not null)
                        {
                            Guard.Require(!detail.Descendants().Any(x => x.Name.LocalName == "attachment"), "gaeb_bid_attachment",
                                "X84 does not carry the original X83 attachment field. Resolve attachment delivery explicitly rather than silently discarding it.");
                            target.Add(new XElement(Target + "Description", new XElement(Target + "CompleteText", ConvertBidDescription(detail, price.OriginalItemId, excluded))));
                        }
                        else if (outline is not null)
                        {
                            var text = new XElement(Target + "Text");
                            foreach (var fragment in outline.Descendants(ns + "TextOutlTxt")) foreach (var element in fragment.Elements()) text.Add(ConvertNamespace(element));
                            Guard.Require(text.HasElements, "gaeb_bid_description", "The outline text cannot be represented as a complete X84 item description.");
                            target.Add(new XElement(Target + "Description", new XElement(Target + "CompleteText", new XElement(Target + "DetailTxt", ConvertBidDescription(text, price.OriginalItemId, excluded)))));
                        }
                        foreach (var field in description.Elements()) if (field.Name.LocalName != "CompleteText") excluded.Add($"Item[{price.OriginalItemId}]/Description/" + field.Name.LocalName);
                        if (description.Element(ns + "CompleteText")?.Element(ns + "OutlineText") is not null) excluded.Add($"Item[{price.OriginalItemId}]/Description/CompleteText/OutlineText");
                    }
                    if (item.Element(ns + "QU") is not null) excluded.Add($"Item[{price.OriginalItemId}]/QU (retained in original and bid receipt; X84 does not repeat the source unit field)");
                    list.Add(target);
                }
                body.Add(list);
            }
            else
            {
                Guard.Require(child.Name.LocalName == "Remark", "gaeb_special_structure", "The BoQ contains an unsupported structural record.");
                excluded.Add($"BoQBody/Remark[{(string?)child.Attribute("ID")}]");
            }
        }
        return body;
    }
    /// <summary>
    /// GAEB X83 can contain inline span elements in description paragraphs, whereas the
    /// pinned official X84 paragraph content model permits text and TextComplement, not span.
    /// Preserve all plain text, but explicitly report unsupported typographic span markup
    /// as phase-specific loss. Do not fake a TextComplement or silently drop nested content.
    /// </summary>
    private static XElement ConvertBidDescription(XElement source, string itemId, List<string> excluded)
    {
        var detail = ConvertNamespace(source);
        foreach (var span in detail.Descendants(Target + "span").Reverse().ToArray())
        {
            Guard.Require(span.Nodes().All(x => x is XText),
                "gaeb_inline_markup_unsupported",
                "The GAEB X83 description contains nested span markup not representable as ordinary X84 paragraph text. Resolve the original markup explicitly.");
            Guard.Require(!span.Attributes().Any(x => x.IsNamespaceDeclaration || x.Name.NamespaceName.Length > 0),
                "gaeb_inline_markup_unsupported",
                "The GAEB description has namespace-qualified span attributes that cannot be silently simplified.");
            span.ReplaceWith(new XText(span.Value));
            excluded.Add($"Item[{itemId}]/Description/inline span formatting: removed for X84 compatibility; complete literal text preserved; original X83 archived");
        }
        return detail;
    }
    private static void UpdateBody(XElement body, IReadOnlyDictionary<string, GaebBidLineResult> prices)
    {
        foreach (var item in body.Descendants(Target + "Item"))
        {
            var price = prices[(string)item.Attribute("ID")!];
            item.Elements().Where(x => x.Name.LocalName is "NotOffered" or "Qty" or "UP" or "IT").Remove();
            var first = new List<XElement>(); if (price.NotOffered) first.Add(E("NotOffered", "Yes"));
            first.Add(E("Qty", Decimal(price.Quantity)));
            if (!price.NotOffered) { first.Add(E("UP", Decimal(price.UnitPrice!.Value))); first.Add(E("IT", Amount(price.LineNet))); }
            item.AddFirst(first);
        }
        foreach (var category in body.Descendants(Target + "BoQCtgy")) SetTotal(category, category.Descendants(Target + "Item").Sum(x => prices[(string)x.Attribute("ID")!].LineNet));
    }
    private static void SetTotal(XElement parent, decimal total)
    {
        var totals = parent.Element(Target + "Totals");
        if (totals is null) { parent.Add(new XElement(Target + "Totals", E("Total", Amount(total)))); return; }
        Guard.Require(totals.Elements().All(x => x.Name.LocalName == "Total"), "gaeb_special_totals",
            "Existing discounts, VAT totals or lump-sum calculations require their own commercial rules and cannot be retained with silently changed prices.");
        totals.SetElementValue(Target + "Total", Amount(total));
    }
    private static IEnumerable<XAttribute> SourceAttributes(XElement element)
        => element.Attributes().Where(x => !x.IsNamespaceDeclaration).Select(x => new XAttribute(x));
    private static XElement ConvertNamespace(XElement source)
        => new(Target + source.Name.LocalName, SourceAttributes(source), source.Nodes().Select(node => node switch
        { XElement element => (object)ConvertNamespace(element), XText text => new XText(text.Value), _ => null! }).Where(x => x is not null));
    private static void ValidateBidder(GaebBidder bidder)
    {
        Guard.Text(bidder.Name, "Bidder name", 40); Guard.Text(bidder.Street, "Bidder street", 40);
        Guard.Text(bidder.PostalCode, "Bidder postal code", 20); Guard.Text(bidder.City, "Bidder city", 40);
        Guard.Text(bidder.Country, "Bidder country", 40); Guard.Text(bidder.Contact, "Bidder contact", 40, false); Guard.Text(bidder.Email, "Bidder email", 256, false);
        if (bidder.Email.Length > 0) Guard.Require(System.Net.Mail.MailAddress.TryCreate(bidder.Email, out _), "invalid_email", "The explicitly supplied bidder email is invalid.");
    }
    private static XElement Contractor(GaebBidder bidder)
        => new(Target + "CTR", new XElement(Target + "Address", E("Name1", bidder.Name), E("Street", bidder.Street), E("PCode", bidder.PostalCode), E("City", bidder.City), E("Country", bidder.Country),
            bidder.Contact.Length > 0 ? E("Contact", bidder.Contact) : null, bidder.Email.Length > 0 ? E("Email", bidder.Email) : null));
    private static XElement E(string name, string value) => new(Target + name, value);
    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Decimal(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
