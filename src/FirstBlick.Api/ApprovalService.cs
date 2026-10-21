using System.Text.Json;
using System.Text.Json.Nodes;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed class ApprovalService(FirstBlickDbContext db, TenantScope scope, EntityStore store)
{
    public static readonly string[] Actions = ["invoice.issue", "purchase.issue", "supplier-invoice.approve", "settings.change", "membership.change", "membership.invite"];
    public async Task<(long Revision, string Digest)> Fingerprint(string kind, Guid id, string proposed, CancellationToken ct)
    {
        string current; long revision;
        if (kind == "settings")
        {
            Guard.Require(id == scope.TenantId, "forbidden", "The approval targets another business.");
            var tenant = await db.Tenants.SingleAsync(x => x.Id == id, ct);
            current = AppJson.Serialize(tenant); revision = tenant.PolicyRevision;
        }
        else if (kind == "memberships")
        {
            var member = await db.Memberships.SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.UserId == id, ct)
                ?? throw new DomainException("not_found", "The membership does not exist.");
            current = AppJson.Serialize(member); revision = member.Revision;
        }
        else
        {
            Guard.Require(kind is "invoices" or "purchase-orders" or "supplier-invoices", "invalid_approval", "This approval target is unsupported.");
            var row = await store.Find(kind, id, ct); current = row.Data; revision = row.Revision;
        }
        return (revision, ContentDigest.Of(new { current = Canonical(current), proposed = Canonical(proposed) }));
    }

    public async Task Require(HttpContext http, string action, string kind, Guid id, long revision, string proposed, CancellationToken ct)
    {
        var tenant = await db.Tenants.SingleAsync(x => x.Id == scope.TenantId, ct);
        var directors = await db.Memberships.CountAsync(x => x.TenantId == scope.TenantId && x.Active && x.Role == BusinessRole.ManagingDirector, ct);
        // A sole director can operate. Once there are two, governance changes themselves always need a second director.
        if (directors < 2 || !(tenant.ApprovalActions.Contains(action) || action is "settings.change" or "membership.change" or "membership.invite")) return;
        Guard.Require(Guid.TryParse(http.Request.Headers["X-Approval-Id"], out var approvalId), "approval_required", "A different managing director must approve this exact operation before it can be executed.");
        var approval = await db.Approvals.SingleOrDefaultAsync(x => x.Id == approvalId, ct);
        var fingerprint = await Fingerprint(kind, id, proposed, ct);
        Guard.Require(approval is not null && approval.RequestedBy == scope.UserId && approval.ApprovedBy.HasValue && approval.ApprovedBy != scope.UserId && approval.ApprovedAt.HasValue && approval.ConsumedAt is null && approval.ExpiresAt > DateTimeOffset.UtcNow &&
            approval.Action == action && approval.Kind == kind && approval.EntityId == id && approval.EntityRevision == revision && approval.PolicyRevision == tenant.PolicyRevision && approval.ContentDigest == fingerprint.Digest,
            "approval_invalid", "The approval is missing, expired, already used, or no longer matches the object, content, actor or policy.");
        Guard.Require(await db.Memberships.AnyAsync(x => x.TenantId == scope.TenantId && x.UserId == approval!.ApprovedBy && x.Active && x.Role == BusinessRole.ManagingDirector, ct),
            "approval_invalid", "The approver is no longer an active managing director.");
        approval!.ConsumedAt = DateTimeOffset.UtcNow; approval.Revision++;
    }

    public static string Canonical(string json)
    {
        static JsonNode? Sort(JsonNode? node) => node switch
        {
            JsonObject o => new JsonObject(o.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => KeyValuePair.Create(x.Key, Sort(x.Value)))),
            JsonArray a => new JsonArray(a.Select(Sort).ToArray()),
            _ => node?.DeepClone()
        };
        return Sort(JsonNode.Parse(json))?.ToJsonString() ?? "null";
    }
}
