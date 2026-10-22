using FirstBlick.Core.Business;

namespace FirstBlick.Operations;

internal static partial class MigrationDrill
{
    private static void SeedContactHistory(Customer customer, Guid actor, DateTimeOffset at, List<TenantEntity> entities)
    {
        void Retain(IReadOnlyList<CustomerContact> before, CustomerContactChangeKind kind, Guid target, DateTimeOffset timestamp)
        {
            entities.AddRange(CustomerContactHistory.Capture(customer, before, kind, target, actor, "Historical contact editor", timestamp));
            customer.Revision++; customer.UpdatedAt = timestamp;
        }
        var originalContacts = customer.Contacts?.ToArray() ?? [];
        var contact = CustomerContactWorkflow.Add(customer, new("Historical history contact", "Site coordination", "history@example.test", "0421 123", "Original retained note", false), actor, at);
        Retain(originalContacts, CustomerContactChangeKind.Created, contact.Id, at);
        var beforeEdit = customer.Contacts!.ToArray();
        CustomerContactWorkflow.Update(customer, contact.Id, new("Current history contact", "Site coordination", "history@example.test", "0421 456", "Updated retained note", false), actor, at.AddSeconds(1));
        Retain(beforeEdit, CustomerContactChangeKind.Updated, contact.Id, at.AddSeconds(1));
        var beforeArchive = customer.Contacts.ToArray(); CustomerContactWorkflow.Archive(customer, contact.Id, actor, at.AddSeconds(2));
        Retain(beforeArchive, CustomerContactChangeKind.Archived, contact.Id, at.AddSeconds(2));
        var beforeRestore = customer.Contacts.ToArray(); CustomerContactWorkflow.Restore(customer, contact.Id, actor, at.AddSeconds(3));
        Retain(beforeRestore, CustomerContactChangeKind.Restored, contact.Id, at.AddSeconds(3));
    }

    private static void VerifyContactHistory(IEnumerable<TenantEntity> source)
    {
        var entities = source.ToArray(); var changes = entities.OfType<CustomerContactChange>().ToArray();
        Require.That(changes.Length == 4, "Migration lost retained contact-history records.");
        foreach (var group in changes.GroupBy(x => (x.TenantId, x.CustomerId)))
        {
            var customer = entities.OfType<Customer>().Single(x => x.TenantId == group.Key.TenantId && x.Id == group.Key.CustomerId);
            CustomerContactHistory.EnsureCompleteHistory(customer, group.ToArray());
            var creation = group.Single(x => x.ChangeKind == CustomerContactChangeKind.Created);
            var update = group.Single(x => x.ChangeKind == CustomerContactChangeKind.Updated);
            Require.That(creation.Before is null && creation.After.Name == "Historical history contact"
                && update.Before?.Phone == "0421 123" && update.After.Phone == "0421 456"
                && update.RecordedByName == "Historical contact editor",
                "Migration changed historical contact values or the recorded actor label.");
        }
    }

    private static async Task RejectContactHistorySources(MigrationBundle bundle,
        Func<string, MigrationBundle, Func<MigrationIssue, bool>?, Task> reject)
    {
        Task Reject(string name, MigrationBundle altered, Func<MigrationIssue, bool>? expected = null) =>
            reject("contact-history-" + name, altered, expected);
        await Reject("changed-original", Change<CustomerContactChange>(bundle, entry => entry.RecordedByName = "Changed historic actor"));
        await Reject("wrong-customer-revision", Change<CustomerContactChange>(bundle, entry =>
        { entry.CustomerRevision = long.MaxValue; entry.Digest = CustomerContactHistory.Digest(entry); }));
        await Reject("wrong-contact", Change<CustomerContactChange>(bundle, entry =>
        {
            entry.ContactId = Guid.NewGuid(); entry.After = entry.After with { Id = entry.ContactId };
            if (entry.Before is not null) entry.Before = entry.Before with { Id = entry.ContactId };
            entry.Digest = CustomerContactHistory.Digest(entry);
        }));
        await Reject("broken-chain", Change<CustomerContactChange>(bundle, entry =>
        { entry.Before = entry.Before! with { Phone = "Forged prior value" }; entry.Digest = CustomerContactHistory.Digest(entry); },
            entry => entry.ChangeKind == CustomerContactChangeKind.Updated));
        await Reject("duplicate-customer-revision", Duplicate<CustomerContactChange>(bundle, entry => entry.Digest = CustomerContactHistory.Digest(entry)),
            issue => issue.Message == "The imported record violates its shared database uniqueness contract.");
    }
}
