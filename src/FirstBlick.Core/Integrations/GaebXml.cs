using System.Xml.Linq;
using FirstBlick.Core.Business;

namespace FirstBlick.Core.Integrations;

public sealed record GaebItemPreview(string PositionKey, string PositionId, string Description,
    decimal? TenderQuantity, string Unit, decimal? OfferedUnitPrice, string ItemDigest,
    IReadOnlyList<string> ReviewIssues);

public sealed record GaebPreview(string Contract, string SourceSha256, string ExchangePhase,
    string SchemaVersion, string SchemaEdition, string ScheduleName, string Currency,
    IReadOnlyList<GaebItemPreview> Items, IReadOnlyList<string> ReviewIssues,
    bool ExternalSchemaValidationPerformed);

/// <summary>Structural preview of GAEB DA XML 3.3 X83/X84 for human mapping; not an XSD or GAEB certification.</summary>
public static class GaebXml
{
    public const string Contract = "firstblick.gaeb-structural/1.0.0";

    public static GaebPreview Preview(byte[] bytes)
    {
        var (xml, sourceSha) = TradeXml.Load(bytes);
        var root = xml.Root!;
        Guard.Require(root.Name.LocalName == "GAEB"
            && (root.Name.NamespaceName == "http://www.gaeb.de/GAEB_DA_XML/DA83/3.3"
                || root.Name.NamespaceName == "http://www.gaeb.de/GAEB_DA_XML/DA84/3.3"),
            "unsupported_gaeb_syntax", "Only namespaced GAEB DA XML 3.3 phase X83 or X84 can be previewed.");
        var n = root.Name.Namespace;
        var version = TradeXml.Required(root.Element(n + "GAEBInfo")?.Element(n + "Version"), "GAEB version", 10);
        var edition = TradeXml.Required(root.Element(n + "GAEBInfo")?.Element(n + "VersDate"), "GAEB schema edition", 10);
        var award = root.Element(n + "Award");
        var phase = TradeXml.Required(award?.Element(n + "DP"), "GAEB phase", 3);
        Guard.Require(version == "3.3" && edition == "2021-05" && (phase == "83" || phase == "84")
            && root.Name.NamespaceName.EndsWith($"/DA{phase}/3.3", StringComparison.Ordinal),
            "unsupported_gaeb_version", "GAEB namespace, phase and schema edition must match supported X83/X84 version 3.3 (2021-05).");
        var boq = award?.Element(n + "BoQ");
        Guard.Require(boq is not null, "gaeb_structure", "An Award/BoQ element is required.");
        var scheduleName = TradeXml.Optional(boq!.Element(n + "BoQInfo")?.Element(n + "LblBoQ")
            ?? boq.Element(n + "BoQInfo")?.Element(n + "Name"), 240);
        var currency = TradeXml.Optional(root.Element(n + "PrjInfo")?.Element(n + "Cur")
            ?? award?.Element(n + "AwardInfo")?.Element(n + "Cur"), 20);
        Guard.Require(currency is "" or "EUR" or "EURO" or "€", "unsupported_currency",
            "Only the EUR currency is currently supported for internal catalogue transfer.");
        var raw = boq.Element(n + "BoQBody")?.Descendants(n + "Item")
            .Where(x => x.Parent?.Name == n + "Itemlist").ToArray() ?? [];
        Guard.Require(raw.Length is >= 1 and <= 1000, "gaeb_items", "A GAEB preview must contain 1 to 1000 regular Item positions.");
        var items = new List<GaebItemPreview>(raw.Length);
        foreach (var element in raw)
        {
            var parts = element.Ancestors(n + "BoQCtgy").Reverse().Select(x => (string?)x.Attribute("RNoPart") ?? "").ToList();
            parts.Add((string?)element.Attribute("RNoPart") ?? "");
            Guard.Require(parts.All(p => p.Length is > 0 and <= 32 && !p.Any(char.IsControl)),
                "gaeb_position", "Every hierarchy and item requires a bounded RNoPart identifier.");
            var key = string.Join(".", parts);
            Guard.Text(key, "PositionKey", 240);
            var itemId = TradeXml.Optional(element.Attribute("ID") is { } attr
                ? new XElement("identifier", attr.Value) : null, 150);
            var description = element.Element(n + "Description");
            var outline = description?.Descendants(n + "OutlineText").FirstOrDefault();
            var detail = description?.Descendants(n + "DetailTxt").FirstOrDefault();
            var text = outline ?? detail;
            var label = text is null ? "" : string.Join(" ", text.DescendantNodes()
                .OfType<XText>().Select(x => x.Value.Trim()).Where(x => x.Length > 0));
            if (label.Length > 4000) label = label[..4000]; // preview truncation is explicit below
            var unit = TradeXml.Optional(element.Element(n + "QU"), 32);
            decimal? quantity = element.Element(n + "Qty") is { } qty ? TradeXml.Quantity(qty, "GAEB Qty") : null;
            decimal? price = element.Element(n + "UP") is { } unitPrice ? TradeXml.Decimal(unitPrice, "GAEB UP") : null;
            var issues = new List<string>();
            if (quantity is null) issues.Add("quantity_missing_or_to_be_determined");
            if (unit.Length == 0) issues.Add("unit_missing");
            if (text is null || label.Length == 0) issues.Add("description_missing");
            if (text is not null && string.Concat(text.DescendantNodes().OfType<XText>().Select(x => x.Value)).Length > 4000)
                issues.Add("description_truncated_for_preview");
            if (phase == "84" && price is null) issues.Add("price_missing");
            if (element.Descendants(n + "SubDescription").Any()) issues.Add("complex_subdescription_requires_full_gaeb_adapter");
            var item = new GaebItemPreview(key, itemId, label, quantity, unit, price, "",
                issues.AsReadOnly());
            items.Add(item with { ItemDigest = ContentDigest.Of(item) });
        }
        Guard.Require(items.Select(x => x.PositionKey).Distinct(StringComparer.Ordinal).Count() == items.Count,
            "gaeb_duplicate_position", "Position keys must be unique within the submitted schedule.");
        var review = new List<string>
        {
            "no_gaeb_xsd_validation_or_exchange_roundtrip_performed",
            "full_free_text_rich_formatting_and_tender_metadata_not_preserved",
            "tender_price_is_not_an_internal_material_or_labour_cost",
            "requires_explicit_human_mapping_before_catalogue_creation"
        };
        if (currency.Length == 0) review.Add("currency_not_declared");
        return new(Contract, sourceSha, "X" + phase, version, edition, scheduleName,
            currency.Length == 0 ? "unspecified" : "EUR", items.AsReadOnly(), review.AsReadOnly(), false);
    }
}
