using System.Text;
using System.Xml.Linq;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;
using FirstBlick.Infrastructure.Integrations;
using Xunit;

namespace FirstBlick.Api.Tests;

public sealed class OfficialGaebExchangeTests
{
    private static void Valid(GaebSchemaValidator validator, byte[] bytes)
    {
        var validation = validator.Validate(bytes);
        Assert.True(validation.Valid, string.Join("\n", validation.Issues.Select(x => x.Message)) + "\n" + Encoding.UTF8.GetString(bytes));
    }
    [Fact]
    public void Original_X83_and_generated_X84_both_pass_the_unmodified_official_schemas()
    {
        var validator = new GaebSchemaValidator(OfficialXmlValidationTests.Bundle());
        var source = OfficialGaebFixtures.Tender(); Valid(validator, source);
        var result = GaebBidWriter.Render(source, OfficialGaebFixtures.Prices(source)); Valid(validator, result.Xml);
        Assert.Equal(310m, result.TotalNet); Assert.Equal(new[] { 250m, 60m }, result.Lines.Select(x => x.LineNet));
        var output = TradeXml.Load(result.Xml).Document;
        XNamespace ns = "http://www.gaeb.de/GAEB_DA_XML/DA84/3.3";
        Assert.Equal("84", output.Root!.Element(ns + "Award")!.Element(ns + "DP")!.Value);
        Assert.Equal(new[] { "Item001", "Item002" }, output.Descendants(ns + "Item").Select(x => (string)x.Attribute("ID")!));
        Assert.Equal(3, output.Descendants(ns + "p").Count());
        Assert.Contains(output.Descendants(ns + "span"), x => x.Value == "Synthetic roof insulation & installation");
        Assert.Contains(result.PhaseExcludedSourceFields, x => x.Contains("/QU"));
        Assert.Contains(result.PhaseExcludedSourceFields, x => x.Contains("LblBoQ"));
    }
    [Fact]
    public void X84_roundtrip_repricing_preserves_hierarchy_and_rich_text_without_silently_keeping_old_totals()
    {
        var validator = new GaebSchemaValidator(OfficialXmlValidationTests.Bundle());
        var source = OfficialGaebFixtures.Tender(); var first = GaebBidWriter.Render(source, OfficialGaebFixtures.Prices(source)); Valid(validator, first.Xml);
        var input = OfficialGaebFixtures.Prices(first.Xml);
        var next = GaebBidWriter.Render(first.Xml, input with { Prices = input.Prices.Select(x => x with { UnitPrice = 10m }).ToArray() });
        Valid(validator, next.Xml); Assert.Equal(145m, next.TotalNet); Assert.Empty(next.PhaseExcludedSourceFields);
        XNamespace ns = "http://www.gaeb.de/GAEB_DA_XML/DA84/3.3";
        Assert.Equal(3, TradeXml.Load(next.Xml).Document.Descendants(ns + "p").Count());
        Assert.All(TradeXml.Load(next.Xml).Document.Descendants(ns + "Totals"), x => Assert.Equal("145.00", x.Element(ns + "Total")!.Value));
        Assert.Equal("FirstBlick", TradeXml.Load(next.Xml).Document.Root!.Element(ns + "GAEBInfo")!.Element(ns + "ProgSystem")!.Value);
    }
    [Fact]
    public void A_not_offered_decision_is_explicit_and_cannot_also_carry_a_price()
    {
        var source = OfficialGaebFixtures.Tender(); var input = OfficialGaebFixtures.Prices(source);
        var refused = input.Prices.Select((x, i) => i == 0 ? x with { NotOffered = true, UnitPrice = null } : x).ToArray();
        var output = GaebBidWriter.Render(source, input with { Prices = refused });
        Valid(new GaebSchemaValidator(OfficialXmlValidationTests.Bundle()), output.Xml); Assert.Equal(60m, output.TotalNet);
        Assert.Throws<DomainException>(() => GaebBidWriter.Render(source, input with { Prices = input.Prices.Select(x => x with { NotOffered = true }).ToArray() }));
    }
    [Fact]
    public void A_different_original_or_missing_price_cannot_reuse_a_reviewed_item_mapping()
    {
        var source = OfficialGaebFixtures.Tender(); var input = OfficialGaebFixtures.Prices(source);
        Assert.Throws<DomainException>(() => GaebBidWriter.Render(source, input with { ExpectedSourceSha256 = new('0', 64) }));
        Assert.Throws<DomainException>(() => GaebBidWriter.Render(source, input with { Prices = input.Prices.Take(1).ToArray() }));
        Assert.Throws<DomainException>(() => GaebBidWriter.Render(source, input with { Prices = input.Prices.Select(x => x with { UnitPrice = null }).ToArray() }));
        Assert.Throws<DomainException>(() => GaebBidWriter.Render(source, input with { Prices = input.Prices.Select(x => x with { ExpectedItemDigest = new('0', 64) }).ToArray() }));
    }
    [Fact]
    public void Schema_errors_and_unsupported_price_semantics_are_not_passed_off_as_ordinary_items()
    {
        var source = OfficialGaebFixtures.Tender(); var xml = TradeXml.Load(source).Document; var ns = xml.Root!.Name.Namespace;
        xml.Descendants(ns + "Item").First().AddFirst(new XElement(ns + "Optional", "Yes"));
        var changed = Encoding.UTF8.GetBytes(xml.ToString()); var current = OfficialGaebFixtures.Prices(changed);
        Assert.Equal("gaeb_special_pricing", Assert.Throws<DomainException>(() => GaebBidWriter.Render(changed, current)).Code);
        xml.Descendants(ns + "GAEBInfo").Single().Element(ns + "VersDate")!.Value = "2023-01";
        Assert.False(new GaebSchemaValidator(OfficialXmlValidationTests.Bundle()).Validate(Encoding.UTF8.GetBytes(xml.ToString())).Valid);
    }
}
