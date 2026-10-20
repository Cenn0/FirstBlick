using System.Security.Cryptography;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;

namespace FirstBlick.Core.Tests.Integrations;

public sealed class CsvAdapterTests
{
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static string Header => string.Join(',', BankCsvV1.Header) + "\r\n";
    private static byte[] Bytes(string source) => CsvV1.Utf8.GetBytes(source);

    [Fact]
    public void Bank_profile_preserves_quoted_unicode_multiline_text_and_hashes_exact_bytes()
    {
        var bytes = new byte[] { 239, 187, 191 }.Concat(Bytes(Header + "account-a,ext-1,2026-10-20,-1234.56,EUR,\"Dach, Müller\",\"He said \"\"yes\"\"\r\nsecond line\"\r\n")).ToArray();
        var batch = BankCsvV1.Read(bytes); var row = Assert.Single(batch.Rows);
        Assert.Equal(2, row.Record); Assert.Equal(-1234.56m, row.Amount); Assert.Equal(new DateOnly(2026, 10, 20), row.BookedOn);
        Assert.Equal("Dach, Müller", row.Counterparty); Assert.Equal("He said \"yes\"\r\nsecond line", row.Reference);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), batch.SourceSha256);
        Assert.NotEqual(batch.SourceSha256, BankCsvV1.Read(bytes[3..]).SourceSha256);
        Assert.Equal(row.ToEntity(Tenant).ImportDigest, BankCsvV1.Read(bytes[3..]).Rows[0].ToEntity(Tenant).ImportDigest);
    }

    [Theory]
    [InlineData("1")][InlineData("1.0")][InlineData("1.001")][InlineData("1,00")][InlineData("+1.00")]
    [InlineData("01.00")][InlineData(" 1.00")][InlineData("1.00 ")][InlineData("1e2")][InlineData("1E+02")]
    [InlineData("0.00")][InlineData("-0.00")][InlineData("1000000000001.00")][InlineData("99999999999999999999999999999.99")]
    public void Bank_profile_never_guesses_or_rounds_invalid_money(string amount)
        => Assert.Throws<DomainException>(() => BankCsvV1.Read(Bytes(Header + $"a,b,2026-10-20,\"{amount}\",EUR,Name,Reference")));

    [Theory]
    [InlineData("account,ext,2026-02-30,1.00,EUR,Name,Reference")]
    [InlineData("account,ext,20.10.2026,1.00,EUR,Name,Reference")]
    [InlineData("account,ext,2026-10-20,1.00,USD,Name,Reference")]
    [InlineData(" account,ext,2026-10-20,1.00,EUR,Name,Reference")]
    [InlineData("account,ext ,2026-10-20,1.00,EUR,Name,Reference")]
    [InlineData("account,ext,2026-10-20,1.00,EUR,Name")]
    [InlineData("account,ext,2026-10-20,1.00,EUR,Name,Reference,Extra")]
    public void Bank_profile_rejects_ambiguous_keys_dates_currency_and_column_counts(string row)
        => Assert.Throws<DomainException>(() => BankCsvV1.Read(Bytes(Header + row)));

    [Theory]
    [InlineData("a,\"unfinished")][InlineData("un\"quoted,b")][InlineData("\"a\" garbage,b")][InlineData("a,b\rc,d")]
    public void Csv_rejects_malformed_quoting_and_record_endings(string source)
        => Assert.Equal("csv_syntax", Assert.Throws<DomainException>(() => CsvV1.Parse(Bytes(source))).Code);

    [Fact]
    public void Encoding_header_and_input_budgets_fail_closed()
    {
        Assert.Equal("csv_encoding", Assert.Throws<DomainException>(() => CsvV1.Parse([0xc3, 0x28])).Code);
        Assert.Equal("csv_encoding", Assert.Throws<DomainException>(() => CsvV1.Parse(Bytes("a,\0b"))).Code);
        Assert.Equal("csv_header", Assert.Throws<DomainException>(() => BankCsvV1.Read(Bytes(Header.ToUpperInvariant() + "a,b,2026-10-20,1.00,EUR,Name,Ref"))).Code);
        Assert.Equal("csv_empty", Assert.Throws<DomainException>(() => BankCsvV1.Read(Bytes(Header))).Code);
        var row = "a,b,2026-10-20,1.00,EUR,Name,Ref\n";
        Assert.Equal(1000, BankCsvV1.Read(Bytes(Header + string.Concat(Enumerable.Repeat(row, 1000)))).Rows.Count);
        Assert.Equal("csv_budget", Assert.Throws<DomainException>(() => BankCsvV1.Read(Bytes(Header + string.Concat(Enumerable.Repeat(row, 1001))))).Code);
        Assert.Equal("csv_budget", Assert.Throws<DomainException>(() => CsvV1.Parse(new byte[CsvV1.MaximumInputBytes + 1])).Code);
        Assert.Equal("csv_budget", Assert.Throws<DomainException>(() => CsvV1.Parse(Bytes(new string('a', 4097)))).Code);
    }

    [Fact]
    public void Preview_detects_same_file_and_stored_duplicate_conflicts_without_replacing_evidence()
    {
        var first = "a,ext-1,2026-10-20,10.00,EUR,Name,Ref\n";
        var batch = BankCsvV1.Read(Bytes(Header + first + first + "a,ext-1,2026-10-20,11.00,EUR,Name,Ref\n"));
        var preview = BankCsvV1.Preview(batch, Tenant, []);
        Assert.False(preview.CanImport); Assert.Equal(1, preview.New); Assert.Equal(1, preview.Reused); Assert.Equal(1, preview.Conflicts);
        Assert.Equal(new[] { "New", "Reused", "Conflict" }, preview.Rows.Select(x => x.State));
        var bank = batch.Rows[0].ToEntity(Tenant); var digest = bank.ImportDigest;
        preview = BankCsvV1.Preview(batch, Tenant, [bank]);
        Assert.Equal(0, preview.New); Assert.Equal(2, preview.Reused); Assert.Equal(bank.Id, preview.Rows[0].ExistingId);
        Assert.Equal(digest, bank.ImportDigest); Assert.Equal(10m, bank.Amount);
        Assert.NotEqual(digest, batch.Rows[0].ToEntity(Guid.NewGuid()).ImportDigest);
        Assert.Equal("invalid_import_evidence", Assert.Throws<DomainException>(() => BankCsvV1.Preview(batch, Guid.NewGuid(), [bank])).Code);
        var otherAccount = BankCsvV1.Read(Bytes(Header + first + "different,ext-1,2026-10-20,11.00,EUR,Name,Ref"));
        Assert.Equal(2, BankCsvV1.Preview(otherAccount, Tenant, []).New);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://example.test\")")][InlineData("+1+2")][InlineData("-1+2")][InlineData("@SUM(A1:A2)")]
    [InlineData("\t=1+1")][InlineData(" \r\n=1+1")][InlineData("＝1+1")][InlineData("＋1+1")]
    [InlineData("text:already prefixed")][InlineData("00123")][InlineData("Normal, \"quoted\"\r\ntext")]
    public void Every_untrusted_text_cell_uses_an_exact_reversible_literal_prefix(string value)
    {
        var file = CsvV1.Write("example.csv", "test/1", ["user_text", "money", "quantity"], [[CsvCell.Text(value), CsvCell.Money(-12.50m), CsvCell.Number(0.000001m)]]);
        var records = CsvV1.Parse(file.Content);
        Assert.Equal(2, records.Count); Assert.Equal(3, records[1].Fields.Count);
        Assert.Equal("text:" + value, records[1].Fields[0]); Assert.Equal(value, CsvCell.DecodeText(records[1].Fields[0]));
        Assert.Equal("-12.50", records[1].Fields[1]); Assert.Equal("0.000001", records[1].Fields[2]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(file.Content)), file.Sha256);
        Assert.False(file.Content.AsSpan().StartsWith(new byte[] { 239, 187, 191 }));
    }

    [Fact]
    public void Export_scalars_reject_arbitrary_formula_text_and_subcent_money()
    {
        Assert.Throws<DomainException>(() => CsvCell.Code("=1+1")); Assert.Throws<DomainException>(() => CsvCell.Code("safe\n=1+1"));
        Assert.Throws<DomainException>(() => CsvCell.Money(1.001m)); Assert.Throws<DomainException>(() => CsvCell.DecodeText("=1+1"));
    }
}
