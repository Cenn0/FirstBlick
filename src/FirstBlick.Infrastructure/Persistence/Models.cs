using FirstBlick.Core.Security;
using Microsoft.AspNetCore.Identity;

namespace FirstBlick.Infrastructure.Persistence;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";
}

public sealed class BusinessTenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string TimeZone { get; set; } = "Europe/Berlin";
    public string[] Modules { get; set; } = AccessRules.DefaultModules.ToArray();
    public string[] ApprovalActions { get; set; } = ["invoice.issue", "purchase.issue"];
    public long PolicyRevision { get; set; } = 1;
}

public sealed class Membership
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public BusinessRole Role { get; set; }
    public bool Active { get; set; } = true;
    public long Revision { get; set; } = 1;
}

public sealed class StoredEntity
{
    public Guid TenantId { get; set; }
    public string Kind { get; set; } = "";
    public Guid Id { get; set; }
    public long Revision { get; set; } = 1;
    public string Data { get; set; } = "{}";
    public string SearchText { get; set; } = "";
    public string? UniqueKey { get; set; }
    public bool Archived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EntityLink
{
    public Guid TenantId { get; set; }
    public string SourceKind { get; set; } = "";
    public Guid SourceId { get; set; }
    public string TargetKind { get; set; } = "";
    public Guid TargetId { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ActorId { get; set; }
    public string Action { get; set; } = "";
    public string Kind { get; set; } = "";
    public Guid EntityId { get; set; }
    public long Revision { get; set; }
    public string ContentDigest { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CommandReceipt
{
    public Guid TenantId { get; set; }
    public Guid ActorId { get; set; }
    public string Key { get; set; } = "";
    public string RequestDigest { get; set; } = "";
    public string Response { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum JobState { Pending, Leased, Completed, Failed, Uncertain }
public sealed class OutboxJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ActorId { get; set; }
    public string Type { get; set; } = "";
    public string Kind { get; set; } = "";
    public Guid EntityId { get; set; }
    public long EntityRevision { get; set; }
    public string Payload { get; set; } = "{}";
    public JobState State { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset AvailableAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public int Attempts { get; set; }
    public string LastError { get; set; } = "";
    public string ProviderReference { get; set; } = "";
    public bool ExternalAttemptStarted { get; set; }
}

public sealed class ApprovalRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid RequestedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string Action { get; set; } = "";
    public string Kind { get; set; } = "";
    public Guid EntityId { get; set; }
    public long EntityRevision { get; set; }
    public long PolicyRevision { get; set; }
    public string ContentDigest { get; set; } = "";
    public string ProposedData { get; set; } = "{}";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class Invitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid InvitedBy { get; set; }
    public string Email { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public BusinessRole Role { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class DocumentObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UploadedBy { get; set; }
    public string Name { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string ObjectKey { get; set; } = "";
    public string? VersionId { get; set; }
    public string State { get; set; } = "Staged";
    public long Revision { get; set; } = 1;
    public Guid? ProjectId { get; set; }
    public string? ProjectKind { get; set; }
    public string? SourceKind { get; set; }
    public Guid? SourceId { get; set; }
    public string SourceDigest { get; set; } = "";
    public string RendererVersion { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class NumberCounter
{
    public Guid TenantId { get; set; }
    public string Kind { get; set; } = "";
    public int Year { get; set; }
    public long Value { get; set; }
}
