namespace FirstBlick.Core.Business;

public enum CustomerContactChangeKind { Created, Updated, PrimaryCleared, Archived, Restored }

/// <summary>An immutable contact change. Older customers acquire history only when an explicit contact command runs.</summary>
public sealed class CustomerContactChange : TenantEntity
{
    public Guid CustomerId { get; set; }
    public Guid ContactId { get; set; }
    public long CustomerRevision { get; set; }
    public CustomerContactChangeKind ChangeKind { get; set; }
    public CustomerContact? Before { get; set; }
    public CustomerContact After { get; set; } = null!;
    public Guid RecordedBy { get; set; }
    public string RecordedByName { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
    public string Digest { get; set; } = "";

    public override void Validate()
    {
        ValidateCommon(); Guard.Id(CustomerId, nameof(CustomerId)); Guard.Id(ContactId, nameof(ContactId));
        Guard.Id(RecordedBy, nameof(RecordedBy)); Guard.Text(RecordedByName, nameof(RecordedByName), 160);
        Guard.Utc(RecordedAt, nameof(RecordedAt));
        Guard.Require(Revision == 1 && !Archived, "contact_history_immutable", "Contact history cannot be overwritten or archived.");
        Guard.Require(CustomerRevision > 1 && RecordedAt == CreatedAt && Enum.IsDefined(ChangeKind),
            "contact_history_source", "Contact history requires a customer revision, change kind and original recording time.");
        Guard.Require(After is not null && After.Id == ContactId && After.UpdatedBy == RecordedBy && After.UpdatedAt == RecordedAt,
            "contact_history_source", "The changed contact must match the recorded actor, identity and time.");
        CustomerContactWorkflow.Validate([After]);
        if (ChangeKind == CustomerContactChangeKind.Created)
        {
            Guard.Require(Before is null && !After.Archived && After.CreatedBy == RecordedBy && After.CreatedAt == RecordedAt,
                "contact_history_transition", "A newly created contact has no prior snapshot.");
        }
        else
        {
            Guard.Require(Before is not null, "contact_history_transition", "A contact change must retain its prior values.");
            CustomerContactWorkflow.Validate([Before]);
            Guard.Require(Before.Id == After.Id && Before.CreatedBy == After.CreatedBy && Before.CreatedAt == After.CreatedAt
                && Before.UpdatedAt <= RecordedAt, "contact_history_identity", "A contact change must preserve its original identity and chronology.");
            var changed = Before with { UpdatedBy = RecordedBy, UpdatedAt = RecordedAt };
            var valid = ChangeKind switch
            {
                CustomerContactChangeKind.Updated => !Before.Archived && !After.Archived,
                CustomerContactChangeKind.PrimaryCleared => Before.IsPrimary && !Before.Archived && After == (changed with { IsPrimary = false }),
                CustomerContactChangeKind.Archived => !Before.Archived && After == (changed with { Archived = true, IsPrimary = false, ArchivedAt = RecordedAt, ArchivedBy = RecordedBy }),
                CustomerContactChangeKind.Restored => Before.Archived && After == (changed with { Archived = false, IsPrimary = false, ArchivedAt = null, ArchivedBy = null }),
                _ => false
            };
            Guard.Require(valid, "contact_history_transition", "The before and after snapshots do not describe the recorded contact action.");
        }
        Guard.Require(Digest == CustomerContactHistory.Digest(this), "contact_history_digest", "The retained contact change no longer matches its digest.");
    }

    public override IEnumerable<EntityReference> References() => [BusinessRegistry.Reference<Customer>(CustomerId)];
}

public static class CustomerContactHistory
{
    /// <summary>Call before saving the changed customer; the history names its next revision.</summary>
    public static IReadOnlyList<CustomerContactChange> Capture(Customer changed, IReadOnlyList<CustomerContact> previous,
        CustomerContactChangeKind action, Guid target, Guid actor, string actorName, DateTimeOffset now)
    {
        changed.Validate(); CustomerContactWorkflow.Validate(previous); Guard.Id(target, nameof(target));
        Guard.Require(action != CustomerContactChangeKind.PrimaryCleared && Enum.IsDefined(action),
            "contact_history_transition", "Primary reassignment is captured as a consequence of an explicit contact command.");
        var current = changed.Contacts ?? [];
        Guard.Require(current.Any(x => x.Id == target) && previous.All(old => current.Any(x => x.Id == old.Id))
            && current.Count == previous.Count + (action == CustomerContactChangeKind.Created ? 1 : 0),
            "contact_history_source", "Contact commands must retain existing identities and describe one target.");
        var entries = new List<CustomerContactChange>();
        foreach (var contact in current)
        {
            var before = previous.SingleOrDefault(x => x.Id == contact.Id);
            if (contact.Id != target && before == contact) continue;
            var entry = new CustomerContactChange
            {
                TenantId = changed.TenantId, CustomerId = changed.Id, ContactId = contact.Id,
                CustomerRevision = checked(changed.Revision + 1), ChangeKind = contact.Id == target ? action : CustomerContactChangeKind.PrimaryCleared,
                Before = before, After = contact, RecordedBy = actor, RecordedByName = actorName, RecordedAt = now,
                CreatedAt = now, UpdatedAt = now
            };
            entry.Digest = Digest(entry); entry.Validate(); entries.Add(entry);
        }
        return entries;
    }

    public static void EnsureSource(Customer customer, CustomerContactChange change)
    {
        customer.Validate(); change.Validate();
        var contact = customer.Contacts?.SingleOrDefault(x => x.Id == change.ContactId);
        Guard.Require(change.CustomerId == customer.Id && change.TenantId == customer.TenantId
            && change.CustomerRevision <= customer.Revision && contact is not null
            && contact.CreatedBy == change.After.CreatedBy && contact.CreatedAt == change.After.CreatedAt
            && change.RecordedAt >= customer.CreatedAt && change.RecordedAt <= contact.UpdatedAt,
            "contact_history_source", "Contact history must refer to an existing retained contact and customer revision.");
    }

    /// <summary>Used for complete retained histories during migration; no earlier history is synthesized.</summary>
    public static void EnsureCompleteHistory(Customer customer, IReadOnlyList<CustomerContactChange> changes)
    {
        foreach (var change in changes) EnsureSource(customer, change);
        foreach (var group in changes.GroupBy(x => x.ContactId))
        {
            var ordered = group.OrderBy(x => x.CustomerRevision).ToArray();
            for (var i = 1; i < ordered.Length; i++)
                Guard.Require(ordered[i - 1].CustomerRevision < ordered[i].CustomerRevision
                    && ordered[i - 1].After == ordered[i].Before && ordered[i - 1].RecordedAt <= ordered[i].RecordedAt,
                    "contact_history_chain", "The retained contact snapshots must form an ordered, uninterrupted history.");
            Guard.Require(customer.Contacts!.Single(x => x.Id == group.Key) == ordered[^1].After,
                "contact_history_chain", "The final retained contact change must match the current contact values.");
        }
    }

    public static string Digest(CustomerContactChange entry) => ContentDigest.Of(new
    {
        entry.Id, entry.TenantId, entry.CustomerId, entry.ContactId, entry.CustomerRevision, entry.ChangeKind,
        entry.Before, entry.After, entry.RecordedBy, entry.RecordedByName, entry.RecordedAt
    });
}
