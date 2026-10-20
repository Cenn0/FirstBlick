using System.Text;
using FirstBlick.Core.Integrations;

namespace FirstBlick.Api.Tests;

internal static class OfficialGaebFixtures
{
    public static byte[] Tender() => Encoding.UTF8.GetBytes("""
        <?xml version="1.0" encoding="UTF-8"?>
        <GAEB xmlns="http://www.gaeb.de/GAEB_DA_XML/DA83/3.3">
          <GAEBInfo><Version>3.3</Version><VersDate>2021-05</VersDate><Date>2026-10-20</Date><ProgSystem>Independent synthetic reference</ProgSystem></GAEBInfo>
          <PrjInfo><NamePrj>Synthetic roof</NamePrj><Cur>EUR</Cur></PrjInfo>
          <Award><DP>83</DP><BoQ ID="BoQ01">
            <BoQInfo><Name>SYN-001</Name><LblBoQ>Synthetic roofing bill</LblBoQ><OutlCompl>AllTxt</OutlCompl>
              <BoQBkdn><Type>BoQLevel</Type><Length>2</Length><Num>Yes</Num></BoQBkdn>
              <BoQBkdn><Type>Item</Type><Length>3</Length><Num>Yes</Num></BoQBkdn>
            </BoQInfo>
            <BoQBody><BoQCtgy ID="Category01" RNoPart="01"><LblTx><span>Roof works</span></LblTx><BoQBody><Itemlist>
              <Item ID="Item001" RNoPart="001"><Qty>12.500</Qty><QU>m2</QU><Description><CompleteText><DetailTxt><Text><p><span>Synthetic roof insulation &amp; installation</span></p><p><span>Second paragraph retained as markup.</span></p></Text></DetailTxt><OutlineText><OutlTxt><TextOutlTxt><span>Synthetic insulation</span></TextOutlTxt></OutlTxt></OutlineText></CompleteText></Description></Item>
              <Item ID="Item002" RNoPart="002"><Qty>2.000</Qty><QU>m</QU><Description><CompleteText><DetailTxt><Text><p><span>Synthetic edge work</span></p></Text></DetailTxt></CompleteText></Description></Item>
            </Itemlist></BoQBody></BoQCtgy></BoQBody>
          </BoQ></Award>
        </GAEB>
        """);
    public static GaebBidInput Prices(byte[] source)
    {
        var preview = GaebXml.Preview(source);
        return new(preview.SourceSha256, new(2026, 10, 21), new("Synthetic Contractor", "Teststrasse 1", "10115", "Berlin", "DE", "Synthetic Contact", "bidder@example.test"),
            "EUR", "Synthetic explicit bidder and pricing review",
            preview.Items.Select((item, i) => new GaebBidPrice(item.PositionKey, item.ItemDigest, i == 0 ? 20m : 30m, false, "Synthetic independently reviewed unit price")).ToArray());
    }
}
