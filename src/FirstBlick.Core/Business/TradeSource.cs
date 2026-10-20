namespace FirstBlick.Core.Business;

/// <summary>Immutable receipt for exact original XML bytes; never a compliance or posting approval.</summary>
public abstract class TradeSource : TenantEntity
{
    public const string Contract = "firstblick.trade-source/1.0.0";
    public abstract string Format { get; }
    public Guid DocumentId { get; set; }
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string ObjectVersionId { get; set; } = "";
    public Guid ReceivedBy { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string Digest { get; set; } = "";

    public override void Validate()
    {
        ValidateCommon(); Guard.Id(DocumentId, nameof(DocumentId)); Guard.Id(ReceivedBy, nameof(ReceivedBy));
        Guard.Utc(ReceivedAt, nameof(ReceivedAt));
        Guard.Require(Revision == 1 && !Archived, "trade_source_immutable", "Source receipts are retained unchanged; they cannot be edited or archived generically.");
        ValidateFileName(FileName, Format);
        Guard.Require(Size is > 0 and <= 1_048_576, "xml_budget", "Original XML sources are limited to 1 MiB.");
        Guard.Require(Sha256.Length == 64 && Sha256.All(c => char.IsAsciiHexDigit(c) && !char.IsAsciiLetterUpper(c)),
            "invalid_checksum", "A lowercase SHA-256 of the exact source bytes is required.");
        Guard.Require(!string.IsNullOrWhiteSpace(ObjectVersionId) && ObjectVersionId != "null" && ObjectVersionId.Length <= 2048,
            "storage_versioning_required", "Source receipts require an immutable object version identifier.");
        Guard.Require(Digest == ComputeDigest(), "trade_source_changed", "The source receipt no longer matches its captured metadata.");
    }

    public string ComputeDigest() => ContentDigest.Of(new
    {
        Contract, Format, TenantId, Id, DocumentId, FileName, Sha256, Size,
        ObjectVersionId, ReceivedBy, ReceivedAt
    });

    public static void ValidateFileName(string? name, string format)
    {
        Guard.Text(name, "XML filename", 180);
        Guard.Require(name == name!.Trim() && name != "." && name != ".."
            && !name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':'),
            "invalid_filename", "Use a plain filename without paths, control characters or surrounding whitespace.");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        Guard.Require(format == "gaeb" ? extension is ".xml" or ".x83" or ".x84"
            : format == "e-invoice" && extension == ".xml",
            "unsupported_file_type", "Invoice sources must be XML; GAEB sources may use XML, X83 or X84 extensions.");
    }
}

public sealed class GaebSource : TradeSource { public override string Format => "gaeb"; }
public sealed class ElectronicInvoiceSource : TradeSource { public override string Format => "e-invoice"; }
