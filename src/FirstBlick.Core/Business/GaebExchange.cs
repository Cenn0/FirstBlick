namespace FirstBlick.Core.Business;

/// <summary>
/// Internal immutable parent for one reviewed X84 output and its three original/
/// generated/evidence objects. A real same-tenant parent is required for the
/// document FK; the generic API never grants access to this priced record.
/// </summary>
public sealed class GaebExchange : TenantEntity
{
    public Guid ProjectId { get; set; }
    public long ProjectRevision { get; set; }
    public string SourceSha256 { get; set; } = "";
    public string OutputSha256 { get; set; } = "";
    public string PreviewDigest { get; set; } = "";
    public decimal TotalNet { get; set; }
    public Guid ApprovedBy { get; set; }
    public DateTimeOffset ApprovedAtUtc { get; set; }

    public override void Validate()
    {
        ValidateCommon();
        Guard.Require(Revision == 1 && !Archived, "gaeb_exchange_immutable",
            "Approved GAEB exchanges are never generically revised or archived.");
        Guard.Id(ProjectId, nameof(ProjectId));
        Guard.Require(ProjectRevision > 0, "invalid_project_revision", "A current project revision must be reviewed.");
        foreach (var hash in new[] { SourceSha256, OutputSha256, PreviewDigest })
            Guard.Require(hash is { Length: 64 } &&
                hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
                "invalid_checksum", "An approved exchange requires complete lowercase SHA-256 references.");
        Money.Amount(TotalNet, "GAEB exchange net total");
        Guard.Id(ApprovedBy, nameof(ApprovedBy));
        Guard.Utc(ApprovedAtUtc, nameof(ApprovedAtUtc));
    }

    public override IEnumerable<EntityReference> References() =>
        [BusinessRegistry.Reference<Project>(ProjectId)];
}
