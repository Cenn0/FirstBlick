using FirstBlick.Core.Business;
using static FirstBlick.Core.Tests.Business.BusinessFixture;

namespace FirstBlick.Core.Tests.Business;

public sealed class CustomerContactHistoryTests
{
    private static Customer Customer() => new() { TenantId = Tenant, Name = "Hausverwaltung", CreatedAt = Now, UpdatedAt = Now };
    private static CustomerContactDetails Details(string name = "Objektleitung", bool primary = false) =>
        new(name, "Verwaltung", "kontakt@example.test", "0421 123", "Am Vormittag", primary);

    private static IReadOnlyList<CustomerContactChange> Change(Customer customer, CustomerContactChangeKind action,
        DateTimeOffset at, Guid? id = null, CustomerContactDetails? details = null)
    {
        var before = customer.Contacts?.ToArray() ?? [];
        if (action == CustomerContactChangeKind.Created) id = CustomerContactWorkflow.Add(customer, details ?? Details(), Actor, at).Id;
        else if (action == CustomerContactChangeKind.Updated) CustomerContactWorkflow.Update(customer, id!.Value, details ?? Details(), Actor, at);
        else if (action == CustomerContactChangeKind.Archived) CustomerContactWorkflow.Archive(customer, id!.Value, Actor, at);
        else CustomerContactWorkflow.Restore(customer, id!.Value, Actor, at);
        var history = CustomerContactHistory.Capture(customer, before, action, id!.Value, Actor, "Musterperson", at);
        customer.Revision++; customer.UpdatedAt = at;
        return history;
    }

    [Fact]
    public void Creation_edit_archive_and_restore_retain_exact_original_values_and_form_a_chain()
    {
        var customer = Customer(); var created = Assert.Single(Change(customer, CustomerContactChangeKind.Created, Now));
        var edited = Assert.Single(Change(customer, CustomerContactChangeKind.Updated, Now.AddMinutes(1), created.ContactId,
            Details("Buchhaltung") with { Email = "rechnung@example.test", Phone = "0421 456" }));
        var archived = Assert.Single(Change(customer, CustomerContactChangeKind.Archived, Now.AddMinutes(2), created.ContactId));
        var restored = Assert.Single(Change(customer, CustomerContactChangeKind.Restored, Now.AddMinutes(3), created.ContactId));
        Assert.Null(created.Before); Assert.Equal(created.After, edited.Before);
        Assert.Equal("kontakt@example.test", edited.Before!.Email); Assert.Equal("rechnung@example.test", edited.After.Email);
        Assert.Equal(edited.After, archived.Before); Assert.Equal(archived.After, restored.Before);
        Assert.True(archived.After.Archived); Assert.False(restored.After.Archived);
        Assert.All(new[] { created, edited, archived, restored }, entry => { Assert.Equal(Actor, entry.RecordedBy); Assert.Equal("Musterperson", entry.RecordedByName); });
        CustomerContactHistory.EnsureCompleteHistory(customer, [created, edited, archived, restored]);
        customer.Archived = true; CustomerContactHistory.EnsureCompleteHistory(customer, [created, edited, archived, restored]);
    }

    [Fact]
    public void Choosing_a_new_primary_records_both_the_new_contact_and_prior_primary_demotion()
    {
        var customer = Customer(); var first = Assert.Single(Change(customer, CustomerContactChangeKind.Created, Now, details: Details(primary: true)));
        var second = Change(customer, CustomerContactChangeKind.Created, Now.AddMinutes(1), details: Details("Buchhaltung", true));
        var demotion = Assert.Single(second, x => x.ChangeKind == CustomerContactChangeKind.PrimaryCleared);
        Assert.Equal(first.ContactId, demotion.ContactId); Assert.True(demotion.Before!.IsPrimary); Assert.False(demotion.After.IsPrimary);
        Assert.Equal(demotion.Before.Name, demotion.After.Name); Assert.Equal(demotion.Before.Email, demotion.After.Email);
        Assert.Single(second, x => x.ChangeKind == CustomerContactChangeKind.Created);
        CustomerContactHistory.EnsureCompleteHistory(customer, [first, .. second]);
    }

    [Fact]
    public void First_recorded_edit_of_a_legacy_contact_keeps_only_the_known_prior_snapshot()
    {
        var customer = Customer(); var legacy = CustomerContactWorkflow.Add(customer, Details(), Actor, Now);
        customer.Revision = 8;
        var first = Assert.Single(Change(customer, CustomerContactChangeKind.Updated, Now.AddDays(1), legacy.Id, Details("Neuer Name")));
        Assert.Equal(legacy, first.Before); Assert.Equal(CustomerContactChangeKind.Updated, first.ChangeKind); Assert.Equal(9, first.CustomerRevision);
        CustomerContactHistory.EnsureCompleteHistory(customer, [first]);
    }

    [Fact]
    public void Digest_identity_and_transition_validation_rejects_corrupted_or_relabelled_history()
    {
        var customer = Customer(); var created = Assert.Single(Change(customer, CustomerContactChangeKind.Created, Now));
        var update = Assert.Single(Change(customer, CustomerContactChangeKind.Updated, Now.AddMinutes(1), created.ContactId, Details("Buchhaltung")));
        update.RecordedByName = "Andere Person";
        Assert.Equal("contact_history_digest", Assert.Throws<DomainException>(update.Validate).Code);
        update.Digest = CustomerContactHistory.Digest(update); update.Revision = 2;
        Assert.Equal("contact_history_immutable", Assert.Throws<DomainException>(update.Validate).Code);
        update.Revision = 1; update.ChangeKind = CustomerContactChangeKind.Archived; update.Digest = CustomerContactHistory.Digest(update);
        Assert.Equal("contact_history_transition", Assert.Throws<DomainException>(update.Validate).Code);
        update.ChangeKind = CustomerContactChangeKind.Updated; update.Before = update.Before! with { CreatedBy = Guid.NewGuid() }; update.Digest = CustomerContactHistory.Digest(update);
        Assert.Equal("contact_history_identity", Assert.Throws<DomainException>(update.Validate).Code);
    }

    [Fact]
    public void Complete_history_rejects_missing_intermediate_changes_wrong_sources_and_changed_current_values()
    {
        var customer = Customer(); var created = Assert.Single(Change(customer, CustomerContactChangeKind.Created, Now));
        var first = Assert.Single(Change(customer, CustomerContactChangeKind.Updated, Now.AddMinutes(1), created.ContactId, Details("Zwischenstand")));
        var final = Assert.Single(Change(customer, CustomerContactChangeKind.Updated, Now.AddMinutes(2), created.ContactId, Details("Endstand")));
        Assert.Equal("contact_history_chain", Assert.Throws<DomainException>(() => CustomerContactHistory.EnsureCompleteHistory(customer, [created, final])).Code);
        var other = Customer(); Assert.Equal("contact_history_source", Assert.Throws<DomainException>(() => CustomerContactHistory.EnsureSource(other, created)).Code);
        customer.Contacts![0] = customer.Contacts[0] with { Phone = "Anderer Wert" };
        Assert.Equal("contact_history_chain", Assert.Throws<DomainException>(() => CustomerContactHistory.EnsureCompleteHistory(customer, [created, first, final])).Code);
    }

    [Fact]
    public void Untargeted_contact_changes_cannot_be_disguised_as_primary_reassignment()
    {
        var customer = Customer(); var first = CustomerContactWorkflow.Add(customer, Details(primary: true), Actor, Now);
        var second = CustomerContactWorkflow.Add(customer, Details("Zweiter Kontakt"), Actor, Now);
        var before = customer.Contacts!.ToArray();
        CustomerContactWorkflow.Update(customer, second.Id, Details("Zweiter Kontakt", true), Actor, Now.AddMinutes(1));
        customer.Contacts[0] = customer.Contacts[0] with { Email = "unerwartet@example.test" };
        Assert.Equal("contact_history_transition", Assert.Throws<DomainException>(() => CustomerContactHistory.Capture(customer, before,
            CustomerContactChangeKind.Updated, second.Id, Actor, "Musterperson", Now.AddMinutes(1))).Code);
    }
}
