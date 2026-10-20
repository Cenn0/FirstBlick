using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using FirstBlick.Core.Business;

namespace FirstBlick.Core.Integrations;

/// <summary>Bounded, non-resolving XML reader for untrusted trade documents.</summary>
public static class TradeXml
{
    public const int MaximumBytes = 1_048_576;

    public static (XDocument Document, string Sha256) Load(byte[] bytes)
    {
        Guard.Require(bytes is not null && bytes.Length is > 0 and <= MaximumBytes,
            "xml_budget", "Trade XML must contain between 1 byte and 1 MiB.");
        try
        {
            using var stream = new MemoryStream(bytes!, writable: false);
            var options = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumBytes * 2L,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };
            using var reader = XmlReader.Create(stream, options);
            var document = XDocument.Load(reader, LoadOptions.None);
            Guard.Require(document.Root is not null, "xml_structure", "The XML document has no root element.");
            var nodes = 0;
            var stack = new Stack<(XElement Element, int Depth)>();
            stack.Push((document.Root!, 1));
            while (stack.Count > 0)
            {
                var (current, depth) = stack.Pop();
                Guard.Require(++nodes <= 25_000 && depth <= 40, "xml_budget", "Trade XML has too many or excessively nested elements.");
                foreach (var child in current.Elements()) stack.Push((child, depth + 1));
            }
            return (document, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (XmlException)
        {
            throw new DomainException("invalid_xml", "Malformed or prohibited XML. DTDs and external entities are not supported.");
        }
    }

    public static string Required(XElement? element, string label, int maximum = 240)
    {
        var text = element?.Value.Trim() ?? "";
        Guard.Text(text, label, maximum);
        return text;
    }

    public static string Optional(XElement? element, int maximum = 240)
    {
        var text = element?.Value.Trim() ?? "";
        Guard.Text(text, "Imported text", maximum, false);
        return text;
    }

    public static decimal Decimal(XElement? element, string name, bool signed = false)
    {
        var value = Required(element, name, 80);
        Guard.Require(decimal.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign |
            System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var amount),
            "xml_decimal", name + " must be an invariant finite decimal.");
        if (element?.Attribute("currencyID") is { } currency)
            Guard.Require(currency.Value == "EUR", "unsupported_currency", "A document monetary field uses a currency other than EUR.");
        Money.Amount(amount, name, signed);
        return amount;
    }

    public static decimal Quantity(XElement? element, string name)
    {
        var value = Required(element, name, 80);
        Guard.Require(decimal.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign |
            System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var amount),
            "xml_decimal", name + " must be an invariant decimal quantity.");
        Guard.Range(amount, 0.000001m, 1_000_000m, name);
        return amount;
    }

    public static string DistinctAttribute(XElement element, string name)
        => Optional(element.Attribute(name) is { } attribute ? new XElement("attr", attribute.Value) : null, 150);
}
