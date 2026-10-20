using System.Text;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;
using Xunit;

namespace FirstBlick.Core.Tests.Integrations;

public sealed class GaebXmlTests
{
    internal const string X83 = """
        <GAEB xmlns="http://www.gaeb.de/GAEB_DA_XML/DA83/3.3">
          <GAEBInfo><Version>3.3</Version><VersDate>2021-05</VersDate></GAEBInfo>
          <PrjInfo><Cur>EUR</Cur></PrjInfo>
          <Award><DP>83</DP><BoQ><BoQInfo><LblBoQ>Roof Tender</LblBoQ></BoQInfo>
          <BoQBody>
            <BoQCtgy RNoPart="01"><BoQBody><Itemlist>
              <Item ID="uuid-a" RNoPart="001"><Qty>12.500</Qty><QU>m²</QU>
                <Description><CompleteText><OutlineText><OutlTxt><TextOutlTxt><span>Roof membrane</span></TextOutlTxt></OutlTxt></OutlineText></CompleteText></Description>
              </Item>
            </Itemlist></BoQBody></BoQCtgy>
            <BoQCtgy RNoPart="02"><BoQBody><Itemlist>
              <Item ID="uuid-b" RNoPart="001"><QtyTBD>Yes</QtyTBD><QU>St</QU>
                <Description><CompleteText><OutlineText><OutlTxt><TextOutlTxt><span>Inspection</span></TextOutlTxt></OutlTxt></OutlineText></CompleteText></Description>
              </Item>
            </Itemlist></BoQBody></BoQCtgy>
          </BoQBody></BoQ></Award>
        </GAEB>
        """;

    [Fact]
    public void HierarchyAndUnpricedTenderRemainReviewableAndUnvalidated()
    {
        var p = GaebXml.Preview(Encoding.UTF8.GetBytes(X83));
        Assert.Equal("X83", p.ExchangePhase);
        Assert.Equal("Roof Tender", p.ScheduleName);
        Assert.Equal(["01.001", "02.001"], p.Items.Select(x => x.PositionKey).ToArray());
        Assert.Equal(12.500m, p.Items[0].TenderQuantity);
        Assert.Null(p.Items[0].OfferedUnitPrice);
        Assert.Null(p.Items[1].TenderQuantity);
        Assert.Contains("quantity_missing_or_to_be_determined", p.Items[1].ReviewIssues);
        Assert.False(p.ExternalSchemaValidationPerformed);
    }

    [Fact]
    public void PricedX84IsNotMappedIntoActualInternalCostsWithoutReview()
    {
        var priceXml = X83.Replace("/DA83/", "/DA84/", StringComparison.Ordinal)
            .Replace("<DP>83</DP>", "<DP>84</DP>", StringComparison.Ordinal)
            .Replace("<QU>m²</QU>", "<QU>m²</QU><UP>18.40</UP>", StringComparison.Ordinal);
        var p = GaebXml.Preview(Encoding.UTF8.GetBytes(priceXml));
        Assert.Equal("X84", p.ExchangePhase);
        Assert.Equal(18.40m, p.Items[0].OfferedUnitPrice);
        Assert.Contains("tender_price_is_not_an_internal_material_or_labour_cost", p.ReviewIssues);
    }

    [Fact]
    public void PhaseNamespaceMismatchAndDuplicatePositionsAreRejected()
    {
        var bad = X83.Replace("<DP>83</DP>", "<DP>84</DP>", StringComparison.Ordinal);
        Assert.Equal("unsupported_gaeb_version", Assert.Throws<DomainException>(() =>
            GaebXml.Preview(Encoding.UTF8.GetBytes(bad))).Code);
        var duplicate = X83.Replace("RNoPart=\"02\"", "RNoPart=\"01\"", StringComparison.Ordinal);
        Assert.Equal("gaeb_duplicate_position", Assert.Throws<DomainException>(() =>
            GaebXml.Preview(Encoding.UTF8.GetBytes(duplicate))).Code);
    }

    [Fact]
    public void DtdBasedEntityResolutionIsNotSupported()
    {
        var xml = "<!DOCTYPE GAEB [<!ENTITY x SYSTEM 'file:///etc/hostname'>]>" + X83;
        Assert.Equal("invalid_xml", Assert.Throws<DomainException>(() =>
            GaebXml.Preview(Encoding.UTF8.GetBytes(xml))).Code);
    }
}
