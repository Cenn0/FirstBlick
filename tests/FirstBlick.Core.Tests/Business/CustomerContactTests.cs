using System.Text.Json;
using FirstBlick.Core.Business;
using static FirstBlick.Core.Tests.Business.BusinessFixture;

namespace FirstBlick.Core.Tests.Business;

public sealed class CustomerContactTests
{
    private static Customer Customer() => new() { TenantId = Tenant, Name = "Hausverwaltung", ContactName = "Bisheriger Kontakt",
        Email = "zentrale@example.test", Phone = "0123", CreatedAt = Now, UpdatedAt = Now };
    private static CustomerContactDetails Details(string name = "Kontakt", bool primary = false) => new(name, "Objektleitung", "kontakt@example.test", "0421 123", "Erreichbarkeit nach Vereinbarung", primary);

    [Fact]
    public void Legacy_customer_has_no_contacts_and_preserves_its_original_serialization()
    {
        var customer = Customer(); customer.Validate(); Assert.Null(customer.Contacts);
        Assert.DoesNotContain("Contacts", JsonSerializer.Serialize(customer), StringComparison.Ordinal);
        var restored = JsonSerializer.Deserialize<Customer>(JsonSerializer.Serialize(customer))!;
        Assert.Null(restored.Contacts); Assert.Equal(ContentDigest.Of(customer), ContentDigest.Of(restored));
    }

    [Fact]
    public void Adding_contacts_preserves_legacy_fields_and_moves_primary_only_on_explicit_choice()
    {
        var customer = Customer(); var first = CustomerContactWorkflow.Add(customer, Details("Hausmeister", true), Actor, Now);
        var second = CustomerContactWorkflow.Add(customer, Details("Buchhaltung"), Actor, Now.AddMinutes(1));
        Assert.Equal(first.Id, Assert.Single(customer.Contacts!, x => x.IsPrimary).Id);
        CustomerContactWorkflow.Update(customer, second.Id, Details("Buchhaltung", true), Actor, Now.AddMinutes(2));
        Assert.Equal(second.Id, Assert.Single(customer.Contacts!, x => x.IsPrimary).Id);
        Assert.Equal(Now.AddMinutes(2), customer.Contacts![0].UpdatedAt);
        Assert.Equal("Bisheriger Kontakt", customer.ContactName); Assert.Equal("zentrale@example.test", customer.Email); Assert.Equal("0123", customer.Phone);
    }

    [Fact]
    public void Archive_retains_data_and_restore_does_not_replace_a_different_primary()
    {
        var customer = Customer(); var contact = CustomerContactWorkflow.Add(customer, Details(primary: true), Actor, Now);
        CustomerContactWorkflow.Archive(customer, contact.Id, Actor, Now.AddMinutes(1));
        var archived = Assert.Single(customer.Contacts!); Assert.True(archived.Archived); Assert.False(archived.IsPrimary);
        Assert.Equal(contact.Name, archived.Name); Assert.Equal(contact.Email, archived.Email); Assert.Equal(contact.CreatedAt, archived.CreatedAt);
        Assert.Equal("contact_archived", Assert.Throws<DomainException>(() => CustomerContactWorkflow.Update(customer, contact.Id, Details(), Actor, Now.AddMinutes(2))).Code);
        var other = CustomerContactWorkflow.Add(customer, Details("Andere Person", true), Actor, Now.AddMinutes(2));
        CustomerContactWorkflow.Restore(customer, contact.Id, Actor, Now.AddMinutes(3));
        Assert.Equal(other.Id, Assert.Single(customer.Contacts!, x => x.IsPrimary).Id);
        var restored = customer.Contacts![0]; Assert.False(restored.Archived); Assert.Null(restored.ArchivedBy); Assert.Null(restored.ArchivedAt);
        Assert.Equal(contact.Id, restored.Id); Assert.Equal(contact.CreatedBy, restored.CreatedBy);
    }

    [Fact]
    public void Contacts_enforce_identity_primary_archive_and_timestamp_invariants()
    {
        var customer = Customer(); var first = CustomerContactWorkflow.Add(customer, Details(primary: true), Actor, Now);
        customer.Contacts!.Add(first); Assert.Equal("contact_duplicate_id", Assert.Throws<DomainException>(customer.Validate).Code);
        customer.Contacts![1] = first with { Id = Guid.NewGuid() }; Assert.Equal("contact_primary", Assert.Throws<DomainException>(customer.Validate).Code);
        customer.Contacts.RemoveAt(1); customer.Contacts[0] = first with { Archived = true };
        Assert.Equal("contact_archive", Assert.Throws<DomainException>(customer.Validate).Code);
        customer.Contacts[0] = first with { UpdatedAt = Now.AddMinutes(-1) };
        Assert.Equal("invalid_time", Assert.Throws<DomainException>(customer.Validate).Code);
        customer.Contacts[0] = first with { CreatedAt = Now.AddDays(-1) };
        Assert.Equal("invalid_time", Assert.Throws<DomainException>(customer.Validate).Code);
    }

    [Fact]
    public void Unknown_contact_archived_customer_invalid_actor_and_backward_time_are_rejected()
    {
        var customer = Customer(); var contact = CustomerContactWorkflow.Add(customer, Details(), Actor, Now);
        Assert.Equal("not_found", Assert.Throws<DomainException>(() => CustomerContactWorkflow.Archive(customer, Guid.NewGuid(), Actor, Now)).Code);
        Assert.Equal("required", Assert.Throws<DomainException>(() => CustomerContactWorkflow.Update(customer, contact.Id, Details(), Guid.Empty, Now)).Code);
        Assert.Equal("invalid_time", Assert.Throws<DomainException>(() => CustomerContactWorkflow.Archive(customer, contact.Id, Actor, Now.AddTicks(-1))).Code);
        customer.Archived = true; Assert.Equal("not_found", Assert.Throws<DomainException>(() => CustomerContactWorkflow.Add(customer, Details(), Actor, Now)).Code);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("email")]
    [InlineData("phone")]
    [InlineData("null")]
    public void Invalid_contact_fields_do_not_change_existing_primary(string field)
    {
        var customer = Customer(); var first = CustomerContactWorkflow.Add(customer, Details(primary: true), Actor, Now);
        var details = field switch { "name" => Details() with { Name = " " }, "email" => Details() with { Email = "not-an-email" },
            "phone" => Details() with { Phone = new string('1', 81) }, _ => Details() with { Function = null! } };
        Assert.Throws<DomainException>(() => CustomerContactWorkflow.Add(customer, details with { IsPrimary = true }, Actor, Now));
        Assert.Equal(first, Assert.Single(customer.Contacts!));
    }

    [Fact]
    public void Retained_contacts_are_bounded_including_archived_entries()
    {
        var customer = Customer();
        for (var i = 0; i < CustomerContactWorkflow.MaximumContacts; i++) CustomerContactWorkflow.Add(customer, Details($"Kontakt {i}"), Actor, Now);
        CustomerContactWorkflow.Archive(customer, customer.Contacts![0].Id, Actor, Now);
        Assert.Equal("contact_limit", Assert.Throws<DomainException>(() => CustomerContactWorkflow.Add(customer, Details(), Actor, Now)).Code);
        Assert.Equal(CustomerContactWorkflow.MaximumContacts, customer.Contacts.Count);
    }
}
