namespace FirstBlick.Core.Business;

/// <summary>A named person's time-limited access to one immutable document or issued quote.</summary>
public sealed class PortalShare : TenantEntity
{
    public Guid RecipientUserId { get; set; }
    public Guid GrantedBy { get; set; }
    public Guid ProjectId { get; set; }
    public string Title { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public Guid SourceId { get; set; }
    public string SourceDigest { get; set; } = "";
    public bool AllowAcceptance { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(RecipientUserId, nameof(RecipientUserId)); Guard.Id(GrantedBy, nameof(GrantedBy));
        Guard.Id(ProjectId, nameof(ProjectId)); Guard.Id(SourceId, nameof(SourceId)); Guard.Text(Title, nameof(Title));
        Guard.Require(SourceKind is "quotes" or "documents", "unsupported_share", "Only an issued quote or verified document can be explicitly shared.");
        Guard.Require(SourceDigest.Length == 64 && SourceDigest.All(char.IsAsciiHexDigit), "invalid_digest", "The grant must bind to the exact source content.");
        Guard.Require(!AllowAcceptance || SourceKind == "quotes", "invalid_share", "Acceptance is available only for an explicitly shared quote.");
        Guard.Utc(ExpiresAt, nameof(ExpiresAt));
        Guard.Require(ExpiresAt > CreatedAt && ExpiresAt <= CreatedAt.AddDays(90), "invalid_expiry", "A portal grant may last at most 90 days.");
        if (RevokedAt is { } revoked) { Guard.Utc(revoked, nameof(RevokedAt)); Guard.Require(revoked >= CreatedAt, "invalid_time", "Revocation cannot precede the grant."); }
    }
    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Project>(ProjectId);
        if (SourceKind == "quotes") yield return BusinessRegistry.Reference<Quote>(SourceId);
        // Documents are retained independently; document tenant, state and version are
        // checked under the same tenant lock on grant creation and on every access.
    }
}

public sealed class PortalFeedback : TenantEntity
{
    public Guid ShareId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid AuthorUserId { get; set; }
    public bool FromBusiness { get; set; }
    public string Message { get; set; } = "";
    public string SourceDigest { get; set; } = "";
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(ShareId, nameof(ShareId)); Guard.Id(ProjectId, nameof(ProjectId)); Guard.Id(AuthorUserId, nameof(AuthorUserId));
        Guard.Text(Message, nameof(Message), 4000);
        Guard.Require(SourceDigest.Length == 64 && SourceDigest.All(char.IsAsciiHexDigit), "invalid_digest", "Feedback must refer to the released source version.");
    }
    public override IEnumerable<EntityReference> References() => [BusinessRegistry.Reference<PortalShare>(ShareId), BusinessRegistry.Reference<Project>(ProjectId)];
}
