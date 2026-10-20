using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;
using Xunit;

namespace FirstBlick.Core.Tests.Integrations;

public sealed class XmlDecimalPrecisionTests
{
    [Theory]
    [InlineData("00012.3000", "12.3")]
    [InlineData("+.50", "0.50")]
    [InlineData("-0.000", "0")]
    [InlineData("9007199254740993.01", "9007199254740993.01")]
    [InlineData("19.600001", "19.600001")]
    public void Source_precision_is_preserved_without_binary_float_or_cent_rounding(string value, string expected)
        => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), TradeXml.DecimalValue(value, "reference"));

    [Theory]
    [InlineData("1e2")]
    [InlineData("1,000.00")]
    [InlineData("NaN")]
    [InlineData("0.123456789012345678901234567891")]
    [InlineData("79228162514264337593543950335.1")]
    public void Ambiguous_or_unrepresentable_xml_numbers_are_not_silently_rounded(string value)
        => Assert.Throws<DomainException>(() => TradeXml.DecimalValue(value, "reference"));
}
