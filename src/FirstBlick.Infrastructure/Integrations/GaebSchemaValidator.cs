using System.Net;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Schema;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;

namespace FirstBlick.Infrastructure.Integrations;

public sealed record XmlRuleIssue(string Code, string Severity, string Message);
public sealed record GaebSchemaResult(string SourceSha256, string Phase, string SchemaVersion,
    string SchemaEdition, string SchemaBundleSha256, bool Valid, IReadOnlyList<XmlRuleIssue> Issues);

/// <summary>Original GAEB schemas; instance-provided schemaLocation and external entities are never resolved.</summary>
public sealed class GaebSchemaValidator
{
    private readonly Dictionary<string, XmlSchemaSet> schemas = new(StringComparer.Ordinal);
    public GaebSchemaValidator(OfficialValidationBundle bundle)
    {
        foreach (var phase in new[] { "83", "84" })
        {
            var file = $"GAEB_DA_XML_{phase}_3.3_2021-05.xsd";
            var ns = $"http://www.gaeb.de/GAEB_DA_XML/DA{phase}/3.3";
            var set = new XmlSchemaSet { XmlResolver = new BundleResolver(bundle) };
            using var source = bundle.Open("gaeb/" + file);
            using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }, "https://firstblick.invalid/validation/gaeb/" + file);
            set.Add(ns, reader); set.Compile(); schemas.Add(ns, set);
        }
    }
    public GaebSchemaResult Validate(byte[] bytes)
    {
        var (document, digest) = TradeXml.Load(bytes);
        var ns = document.Root!.Name.NamespaceName;
        Guard.Require(document.Root.Name.LocalName == "GAEB" && schemas.ContainsKey(ns),
            "unsupported_gaeb_schema", "Only original GAEB DA XML 3.3/2021-05 X83 or X84 schemas are configured.");
        var issues = new List<XmlRuleIssue>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, ValidationType = ValidationType.Schema,
            Schemas = schemas[ns], MaxCharactersInDocument = TradeXml.MaximumBytes * 2L,
            ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings | XmlSchemaValidationFlags.ProcessIdentityConstraints
        };
        settings.ValidationEventHandler += (_, e) =>
        {
            if (issues.Count == 200) throw new DomainException("xml_validation_budget", "The document produces too many schema errors.");
            issues.Add(new("GAEB-XSD", e.Severity == XmlSeverityType.Error ? "error" : "warning", e.Message.Length <= 1000 ? e.Message : e.Message[..1000]));
        };
        using var stream = new MemoryStream(bytes, false); using var reader = XmlReader.Create(stream, settings);
        while (reader.Read()) { }
        return new(digest, ns.Contains("DA83", StringComparison.Ordinal) ? "X83" : "X84", "3.3", "2021-05",
            OfficialValidationBundle.GaebSha256, issues.Count == 0, issues.AsReadOnly());
    }
    public void RequireValid(byte[] bytes)
    {
        var report = Validate(bytes);
        Guard.Require(report.Valid, "gaeb_schema_invalid", "The document does not pass the original GAEB XSD. Read the validation report before processing it.");
    }
    private sealed class BundleResolver(OfficialValidationBundle bundle) : XmlResolver
    {
        public override ICredentials? Credentials { set { } }
        public override object GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            const string prefix = "https://firstblick.invalid/validation/";
            if (!absoluteUri.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal) || absoluteUri.Query.Length != 0 || absoluteUri.Fragment.Length != 0)
                throw new XmlException("Only pinned local schema resources may be resolved.");
            return bundle.Open(Uri.UnescapeDataString(absoluteUri.AbsoluteUri[prefix.Length..]));
        }
    }
}
