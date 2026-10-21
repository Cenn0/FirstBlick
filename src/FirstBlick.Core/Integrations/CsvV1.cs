using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FirstBlick.Core.Business;

namespace FirstBlick.Core.Integrations;

public sealed record CsvRecord(int Number, IReadOnlyList<string> Fields);
public sealed record CsvArtifact(string Name, string Schema, byte[] Content, string Sha256, int Rows, IReadOnlyList<string> Columns);

/// <summary>Explicit CSV profile: UTF-8, comma separator, doubled quotes, CRLF output; LF input is also accepted.</summary>
public static class CsvV1
{
    public const int MaximumInputBytes = 2_097_152;
    public const int MaximumOutputBytes = 25 * 1024 * 1024;
    public const int MaximumExportRows = 50_000;
    public static readonly UTF8Encoding Utf8 = new(false, true);

    public static IReadOnlyList<CsvRecord> Parse(ReadOnlySpan<byte> bytes, int maximumRecords = 1001, int maximumColumns = 64, int maximumFieldChars = 4096)
    {
        Guard.Require(bytes.Length is > 0 and <= MaximumInputBytes, "csv_budget", "CSV input must contain between one byte and 2 MiB.");
        Guard.Require(maximumRecords is > 0 and <= 50_001 && maximumColumns is > 0 and <= 128 && maximumFieldChars is > 0 and <= 32_768,
            "csv_budget", "The parser budget is invalid.");
        string input;
        try { input = Utf8.GetString(bytes.StartsWith(new byte[] { 239, 187, 191 }) ? bytes[3..] : bytes); }
        catch (DecoderFallbackException) { throw new DomainException("csv_encoding", "CSV must be valid UTF-8; legacy encodings are not guessed."); }
        Guard.Require(!input.Contains('\0'), "csv_encoding", "CSV cannot contain null characters.");
        var records = new List<CsvRecord>(); var fields = new List<string>(); var value = new StringBuilder();
        var state = 0; // 0=field start, 1=unquoted, 2=quoted, 3=after closing quote
        var rowStarted = false;
        void Field()
        {
            Guard.Require(fields.Count < maximumColumns, "csv_budget", "CSV has too many columns.");
            fields.Add(value.ToString()); value.Clear(); state = 0;
        }
        void Row()
        {
            Field(); Guard.Require(records.Count < maximumRecords, "csv_budget", "CSV has too many records.");
            records.Add(new(records.Count + 1, fields.ToArray())); fields.Clear(); rowStarted = false;
        }
        for (var index = 0; index < input.Length; index++)
        {
            var current = input[index];
            if (state == 2)
            {
                if (current == '"') state = 3;
                else value.Append(current);
            }
            else if (state == 3 && current == '"') { value.Append('"'); state = 2; }
            else if (current == ',') { Field(); rowStarted = true; }
            else if (current is '\r' or '\n')
            {
                if (current == '\r')
                {
                    Guard.Require(index + 1 < input.Length && input[index + 1] == '\n', "csv_syntax", "An unquoted carriage return must be followed by a line feed.");
                    index++;
                }
                Row();
            }
            else if (current == '"')
            {
                Guard.Require(state == 0, "csv_syntax", "Quotes may begin only at the start of a CSV field.");
                state = 2; rowStarted = true;
            }
            else
            {
                Guard.Require(state != 3, "csv_syntax", "A closing quote must be followed by a separator or record ending.");
                value.Append(current); state = 1; rowStarted = true;
            }
            Guard.Require(value.Length <= maximumFieldChars, "csv_budget", "A CSV field exceeds its supported character budget.");
        }
        Guard.Require(state != 2, "csv_syntax", "A quoted CSV field is not closed.");
        if (rowStarted || fields.Count > 0 || value.Length > 0 || state == 3) Row();
        Guard.Require(records.Count > 0, "csv_header", "CSV requires a header record.");
        return records.AsReadOnly();
    }

    public static CsvArtifact Write(string name, string schema, IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<CsvCell>> rows)
    {
        Guard.Require(name.EndsWith(".csv", StringComparison.Ordinal) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'), "csv_name", "The export filename must be a simple CSV name.");
        Guard.Items(columns, nameof(columns), 128, 1);
        Guard.Require(columns.Distinct(StringComparer.Ordinal).Count() == columns.Count && columns.All(x => x.Length is > 0 and <= 80 && x.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')),
            "csv_header", "The export column contract is invalid.");
        using var output = new MemoryStream(); var count = 0;
        void Record(IEnumerable<string> fields)
        {
            // Quote every cell, including numeric scalars, so delimiters never depend on the value.
            var line = string.Join(',', fields.Select(x => "\"" + x.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")) + "\r\n";
            var bytes = Utf8.GetBytes(line);
            Guard.Require(output.Length + bytes.Length <= MaximumOutputBytes, "export_budget", "The CSV exceeds its 25 MiB output budget. Select a smaller date window.");
            output.Write(bytes);
        }
        Record(columns);
        foreach (var row in rows)
        {
            Guard.Require(++count <= MaximumExportRows && row.Count == columns.Count, "export_budget", "The export row count or column contract is exceeded.");
            Record(row.Select(x => x.Value));
        }
        var content = output.ToArray();
        return new(name, schema, content, Convert.ToHexStringLower(SHA256.HashData(content)), count, columns.ToArray());
    }
}

/// <summary>Untrusted text always has a reversible literal prefix; numeric fields can only originate from typed values.</summary>
public readonly record struct CsvCell
{
    private CsvCell(string value) => Value = value;
    public string Value { get; }
    public static CsvCell Text(string? value) => new("text:" + (value ?? ""));
    public static CsvCell Id(Guid? value) => new(value?.ToString("D") ?? "");
    public static CsvCell Integer(long? value) => new(value?.ToString(CultureInfo.InvariantCulture) ?? "");
    public static CsvCell Money(decimal? value)
    {
        if (value.HasValue) Business.Money.Amount(value.Value, "CSV amount", true);
        return new(value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "");
    }
    public static CsvCell Number(decimal? value) => new(value?.ToString("0.############################", CultureInfo.InvariantCulture) ?? "");
    public static CsvCell Date(DateOnly? value) => new(value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");
    public static CsvCell Instant(DateTimeOffset? value) => new(value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "");
    public static CsvCell Flag(bool value) => new(value ? "true" : "false");
    public static CsvCell Code(string? value)
    {
        value ??= "";
        Guard.Require(value.Length <= 240 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-' or '/'), "csv_scalar", "A scalar code contains unsupported characters.");
        Guard.Require(value.Length == 0 || char.IsAsciiLetterOrDigit(value[0]), "csv_scalar", "A scalar code must begin with an ASCII letter or digit.");
        return new(value);
    }
    public static string DecodeText(string value)
    {
        Guard.Require(value.StartsWith("text:", StringComparison.Ordinal), "csv_text_encoding", "This version requires the literal text: prefix in text columns.");
        return value[5..];
    }
}
