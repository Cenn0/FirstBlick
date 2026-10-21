using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using FirstBlick.Core.Business;

namespace FirstBlick.Core.Integrations;

public sealed record ExportWindow(DateOnly From, DateOnly To, string TimeZone)
{
    public void Validate()
    {
        Guard.Require(From != default && To >= From && To < DateOnly.MaxValue && To.DayNumber - From.DayNumber < 366,
            "export_window", "Use an inclusive date window of at most 366 days.");
        Guard.Require(TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out _), "invalid_timezone", "The export timezone is unavailable.");
    }
    public bool Includes(DateOnly day) => day >= From && day <= To;
    public bool Includes(DateTimeOffset instant) => Includes(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime));
    [JsonIgnore] public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
    public DateTimeOffset Start => Boundary(From);
    public DateTimeOffset EndExclusive => Boundary(To.AddDays(1));
    public DateTimeOffset Boundary(DateOnly date)
    {
        Validate(); var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified); var zone = Zone;
        Guard.Require(!zone.IsInvalidTime(local) && !zone.IsAmbiguousTime(local), "unsupported_time_boundary", "This local date has an invalid or ambiguous midnight. A reviewed timezone-specific adapter is required.");
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }
}

public sealed record ExportContext(Guid Id, Guid TenantId, Guid ActorId, DateTimeOffset CapturedAt, ExportWindow Window)
{
    public void Validate()
    {
        Guard.Id(Id, nameof(Id)); Guard.Id(TenantId, nameof(TenantId)); Guard.Id(ActorId, nameof(ActorId)); Guard.Utc(CapturedAt, nameof(CapturedAt)); Window.Validate();
    }
}
public sealed record ExportFileManifest(string Name, string Schema, string Sha256, int Rows, IReadOnlyList<string> Columns, int Bytes);
public sealed record ExportManifest(string Schema, ExportContext Capture, string Selection, string StateSemantics, string TextEncoding,
    bool InitiatesPayments, bool CertifiedProviderFormat, IReadOnlyList<ExportFileManifest> Files);
public sealed record ExportBundle(ExportManifest Manifest, IReadOnlyList<CsvArtifact> Files)
{
    public byte[] ToZip()
    {
        Guard.Require(Files.Sum(x => (long)x.Content.Length) <= CsvV1.MaximumOutputBytes, "export_budget", "The full export exceeds 25 MiB before compression. Select a smaller window.");
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void File(string name, byte[] bytes)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var content = entry.Open(); content.Write(bytes);
            }
            File("manifest.json", JsonSerializer.SerializeToUtf8Bytes(Manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            foreach (var file in Files) File(file.Name, file.Content);
        }
        Guard.Require(output.Length <= CsvV1.MaximumOutputBytes + 1_048_576, "export_budget", "The archive exceeds its output budget.");
        return output.ToArray();
    }
}

public static class ExportLimits
{
    public const int MaximumSources = 10_000;
    public const long MaximumSourceBytes = 12 * 1024 * 1024;
}

internal static class ExportContract
{
    public const int MaximumSources = ExportLimits.MaximumSources;
    public static readonly string[] PrefixColumns = ["schema_version", "tenant_id", "capture_id", "captured_at_utc"];
    public static IReadOnlyList<CsvCell> Row(ExportContext context, params CsvCell[] cells)
        => new[] { CsvCell.Code("1.0.0"), CsvCell.Id(context.TenantId), CsvCell.Id(context.Id), CsvCell.Instant(context.CapturedAt) }.Concat(cells).ToArray();
    public static CsvArtifact Table(ExportContext context, string name, string schema, string[] columns, IEnumerable<IReadOnlyList<CsvCell>> rows)
        => CsvV1.Write(name, schema, PrefixColumns.Concat(columns).ToArray(), rows);
    public static ExportBundle Bundle(string schema, ExportContext context, string selection, IEnumerable<CsvArtifact> files)
    {
        context.Validate(); var array = files.ToArray();
        Guard.Require(array.Sum(x => (long)x.Content.Length) <= CsvV1.MaximumOutputBytes, "export_budget", "The full export exceeds its 25 MiB output budget.");
        var manifest = new ExportManifest(schema, context, selection,
            "Current validated records captured in one tenant transaction; date windows select documents/events, not a historical accounting cutoff.",
            "Every user text cell has a literal text: prefix. Remove exactly one prefix only in a trusted typed importer; numeric/date/id/code columns have no prefix.",
            false, false, array.Select(x => new ExportFileManifest(x.Name, x.Schema, x.Sha256, x.Rows, x.Columns, x.Content.Length)).ToArray());
        return new(manifest, array);
    }
    public static void ValidateSources<T>(IReadOnlyCollection<T> records, Guid tenantId) where T : TenantEntity
    {
        Guard.Items(records, nameof(records), MaximumSources);
        Guard.Require(records.All(x => x.TenantId == tenantId) && records.Select(x => x.Id).Distinct().Count() == records.Count,
            "export_tenant", "Export sources must be unique records from the selected business.");
        foreach (var record in records) record.Validate();
    }
}
