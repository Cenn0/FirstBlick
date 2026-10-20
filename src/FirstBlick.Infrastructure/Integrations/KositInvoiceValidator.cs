using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;

namespace FirstBlick.Infrastructure.Integrations;

public sealed record ElectronicValidationReport(string Profile, string ConfigurationVersion,
    string EngineVersion, string SourceSha256, string ConfigurationSha256,
    bool Accepted, string ReportSha256, string ReportXmlBase64, IReadOnlyList<XmlRuleIssue> Issues);

/// <summary>Executes the fixed KoSIT engine against local original XSD and Schematron rules, not a remote API.</summary>
public sealed class KositInvoiceValidator(OfficialValidationBundle bundle)
{
    public const string Profile = "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0";
    private static readonly SemaphoreSlim Capacity = new(2, 2);
    private const int MaximumReportBytes = 8 * 1024 * 1024;
    public async Task<ElectronicValidationReport> Validate(byte[] bytes, CancellationToken cancellationToken = default)
    {
        var (source, sourceSha) = TradeXml.Load(bytes);
        CheckProfile(source);
        Guard.Require(await Capacity.WaitAsync(0, cancellationToken), "validation_busy", "Both bounded invoice validation slots are occupied. Retry this request later.");
        var working = Path.Combine(Path.GetTempPath(), "firstblick-validation-" + Guid.NewGuid().ToString("N"));
        Process? process = null;
        try
        {
            Directory.CreateDirectory(working);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(working, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            bundle.MaterializeKosit(working);
            var input = Path.Combine(working, "input.xml");
            var output = Path.Combine(working, "reports"); Directory.CreateDirectory(output);
            await File.WriteAllBytesAsync(input, bytes, cancellationToken);
            var start = new ProcessStartInfo("java")
            {
                WorkingDirectory = working, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "-Xmx256m", "-Djava.awt.headless=true", "-Djavax.xml.accessExternalDTD=", "-jar", Path.Combine(working, "validator.jar"), "-s", Path.Combine(working, "xrechnung/scenarios.xml"), "-r", Path.Combine(working, "xrechnung"), "-o", output, input })
                start.ArgumentList.Add(argument);
            process = Process.Start(start) ?? throw new DomainException("validation_unavailable", "The local KoSIT process could not be started.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var stdout = Drain(process.StandardOutput, timeout.Token); var stderr = Drain(process.StandardError, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(stdout, stderr);
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                try { await Task.WhenAll(stdout, stderr); } catch (Exception) { /* logs are not business evidence */ }
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                throw new DomainException("validation_unavailable", "The isolated validator exceeded its time or output budget; no positive result is available.");
            }
            var reports = Directory.GetFiles(output, "*.xml", SearchOption.TopDirectoryOnly);
            Guard.Require(reports.Length == 1, "validation_unavailable", "The validator did not produce exactly one machine-readable report.");
            var info = new FileInfo(reports[0]);
            Guard.Require(info.Length is > 0 and <= MaximumReportBytes, "validation_unavailable", "The validation report is missing or exceeds its bounded size.");
            var reportBytes = await File.ReadAllBytesAsync(reports[0], cancellationToken);
            using var stream = new MemoryStream(reportBytes, false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumReportBytes });
            var report = XDocument.Load(reader);
            XNamespace rep = "http://www.xoev.de/de/validator/varl/1";
            Guard.Require(report.Root?.Name == rep + "report", "validation_unavailable", "The validator response is not a recognized KoSIT report.");
            var assessment = report.Root!.Element(rep + "assessment");
            var accepted = assessment?.Elements(rep + "accept").Count() == 1 && !assessment.Elements(rep + "reject").Any();
            var messages = report.Descendants().Where(x => x.Name.LocalName == "failed-assert")
                .Take(200).Select(x => new XmlRuleIssue((string?)x.Attribute("id") ?? "SCHEMATRON",
                    (string?)x.Attribute("flag") ?? "error", Limit(string.Join(" ", x.Elements().Where(y => y.Name.LocalName == "text").Select(y => y.Value))))).ToList();
            if (!accepted && messages.Count == 0) messages.Add(new("KOSIT-REJECTED", "error", "The original KoSIT report does not contain an acceptance assessment. Inspect the retained XML report."));
            return new(Profile, "2026-08-31/XRechnung-3.0.2", "1.6.3", sourceSha, OfficialValidationBundle.XRechnungSha256,
                accepted, Convert.ToHexStringLower(SHA256.HashData(reportBytes)), Convert.ToBase64String(reportBytes), messages.AsReadOnly());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new DomainException("validation_unavailable", "A supported local Java runtime is required for the pinned validator.");
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
                process.Dispose();
            }
            try { if (Directory.Exists(working)) Directory.Delete(working, recursive: true); } catch (IOException) { /* temporary directory contains no reusable authorization */ }
            Capacity.Release();
        }
    }
    private static async Task Drain(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096]; var count = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct); if (read == 0) return;
            count += read;
            if (count > 131072) throw new IOException("Validator output exceeded its budget.");
        }
    }
    private static string Limit(string text) => text.Length > 1000 ? text[..1000] : text;
    public static void CheckProfile(XDocument document)
    {
        XNamespace basic = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        XNamespace ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
        XNamespace cii = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
        var root = document.Root!;
        var profile = root.Name.NamespaceName is "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2" or "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2"
            ? root.Elements(basic + "CustomizationID").SingleOrDefault()?.Value
            : root.Name == cii + "CrossIndustryInvoice" ? root.Element(cii + "ExchangedDocumentContext")?.Element(ram + "GuidelineSpecifiedDocumentContextParameter")?.Element(ram + "ID")?.Value : null;
        Guard.Require(profile == Profile, "unsupported_einvoice_profile", "Select the supported XRechnung CIUS 3.0 profile. Other profiles, extensions and PDF containers must not be treated as validated XRechnung.");
    }
}
