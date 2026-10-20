using System.Text;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Integrations;
using Xunit;

namespace FirstBlick.Api.Tests;

public sealed class OfficialXmlValidationTests
{
    public static OfficialValidationBundle Bundle() => new(Environment.GetEnvironmentVariable("FIRSTBLICK_STANDARDS_PATH")
        ?? throw new InvalidOperationException("Set FIRSTBLICK_STANDARDS_PATH after running python3 scripts/standards-assets.py. These tests never simulate official validation."));

    [Fact]
    public void Official_gaeb_schema_rejects_a_structurally_plausible_but_incomplete_document()
    {
        var validator = new GaebSchemaValidator(Bundle());
        var result = validator.Validate(Encoding.UTF8.GetBytes("<GAEB xmlns=\"http://www.gaeb.de/GAEB_DA_XML/DA83/3.3\"><Award><DP>83</DP></Award></GAEB>"));
        Assert.False(result.Valid); Assert.NotEmpty(result.Issues);
        Assert.Equal(OfficialValidationBundle.GaebSha256, result.SchemaBundleSha256);
    }
    [Fact]
    public void Original_assets_reject_unknown_resolution_and_changed_jar()
    {
        Assert.Throws<DomainException>(() => Bundle().Open("https://example.test/schema.xsd"));
        var directory = Path.Combine(Path.GetTempPath(), "firstblick-invalid-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { File.WriteAllText(Path.Combine(directory, "xrechnung.zip"), "not an original archive"); Assert.Throws<DomainException>(() => new OfficialValidationBundle(directory)); }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task Kosit_executes_original_rules_and_does_not_equate_parseable_XML_with_acceptance()
    {
        var input = Encoding.UTF8.GetBytes($"<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\" xmlns:cbc=\"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2\"><cbc:CustomizationID>{KositInvoiceValidator.Profile}</cbc:CustomizationID></Invoice>");
        var result = await new KositInvoiceValidator(Bundle()).Validate(input);
        Assert.False(result.Accepted); Assert.NotEmpty(result.Issues); Assert.Equal(64, result.ReportSha256.Length);
        Assert.NotEmpty(Convert.FromBase64String(result.ReportXmlBase64)); Assert.Equal("1.6.3", result.EngineVersion);
    }
    [Fact]
    public async Task Unsupported_invoice_profiles_and_DTDs_are_rejected_before_external_processing()
    {
        var validator = new KositInvoiceValidator(Bundle());
        await Assert.ThrowsAsync<DomainException>(() => validator.Validate(Encoding.UTF8.GetBytes("<!DOCTYPE Invoice [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><Invoice>&x;</Invoice>")));
        await Assert.ThrowsAsync<DomainException>(() => validator.Validate(Encoding.UTF8.GetBytes("<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\"/>")));
    }
}
