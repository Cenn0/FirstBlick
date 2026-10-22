namespace FirstBlick.Core.Business;

public sealed record CustomerContactDetails(string Name, string Function, string Email, string Phone, string Notes, bool IsPrimary);

public sealed record CustomerContact(Guid Id, string Name, string Function, string Email, string Phone, string Notes,
    bool IsPrimary, bool Archived, Guid CreatedBy, DateTimeOffset CreatedAt, Guid UpdatedBy, DateTimeOffset UpdatedAt,
    Guid? ArchivedBy, DateTimeOffset? ArchivedAt);

/// <summary>A bounded customer-owned address book. Archiving retains the original identity and contact data.</summary>
public static class CustomerContactWorkflow
{
    public const int MaximumContacts = 100;

    public static void Validate(IReadOnlyList<CustomerContact>? contacts)
    {
        if (contacts is null) return;
        Guard.Items(contacts, nameof(Customer.Contacts), MaximumContacts);
        Guard.Require(contacts.Select(x => x.Id).Distinct().Count() == contacts.Count,
            "contact_duplicate_id", "Customer contact identities must be unique.");
        Guard.Require(contacts.Count(x => x.IsPrimary) <= 1,
            "contact_primary", "A customer can have at most one primary contact.");
        foreach (var contact in contacts)
        {
            Guard.Id(contact.Id, nameof(contact.Id));
            Details(new(contact.Name, contact.Function, contact.Email, contact.Phone, contact.Notes, contact.IsPrimary));
            Guard.Id(contact.CreatedBy, nameof(contact.CreatedBy)); Guard.Id(contact.UpdatedBy, nameof(contact.UpdatedBy));
            Guard.Utc(contact.CreatedAt, nameof(contact.CreatedAt)); Guard.Utc(contact.UpdatedAt, nameof(contact.UpdatedAt));
            Guard.Require(contact.UpdatedAt >= contact.CreatedAt, "invalid_time", "Contact changes cannot precede its creation.");
            Guard.Require(contact.Archived == (contact.ArchivedAt.HasValue && contact.ArchivedBy.HasValue)
                && (contact.Archived || (contact.ArchivedAt is null && contact.ArchivedBy is null))
                && (!contact.Archived || !contact.IsPrimary), "contact_archive", "An archived contact cannot be primary and requires its archive evidence.");
            if (contact.ArchivedAt is { } archived)
            {
                Guard.Utc(archived, nameof(contact.ArchivedAt)); Guard.Id(contact.ArchivedBy!.Value, nameof(contact.ArchivedBy));
                Guard.Require(archived == contact.UpdatedAt && contact.ArchivedBy == contact.UpdatedBy,
                    "contact_archive", "The archive evidence must match the last contact change.");
            }
        }
    }

    public static CustomerContact Add(Customer customer, CustomerContactDetails details, Guid actor, DateTimeOffset now)
    {
        BeforeChange(customer, actor, now); Details(details);
        Guard.Require((customer.Contacts?.Count ?? 0) < MaximumContacts, "contact_limit", "A customer can retain at most 100 contacts, including archived contacts.");
        var contact = new CustomerContact(Guid.NewGuid(), details.Name.Trim(), details.Function.Trim(), details.Email.Trim(),
            details.Phone.Trim(), details.Notes, details.IsPrimary, false, actor, now, actor, now, null, null);
        customer.Contacts ??= [];
        if (contact.IsPrimary) ClearPrimary(customer.Contacts, actor, now);
        customer.Contacts.Add(contact); Validate(customer.Contacts); return contact;
    }

    public static void Update(Customer customer, Guid id, CustomerContactDetails details, Guid actor, DateTimeOffset now)
    {
        BeforeChange(customer, actor, now); Details(details);
        var index = Index(customer, id); var old = customer.Contacts![index];
        Guard.Require(!old.Archived, "contact_archived", "Restore an archived contact before editing it.");
        if (details.IsPrimary) ClearPrimary(customer.Contacts, actor, now);
        customer.Contacts[index] = old with { Name = details.Name.Trim(), Function = details.Function.Trim(), Email = details.Email.Trim(),
            Phone = details.Phone.Trim(), Notes = details.Notes, IsPrimary = details.IsPrimary, UpdatedBy = actor, UpdatedAt = now };
        Validate(customer.Contacts);
    }

    public static void Archive(Customer customer, Guid id, Guid actor, DateTimeOffset now)
    {
        BeforeChange(customer, actor, now); var index = Index(customer, id); var old = customer.Contacts![index];
        Guard.Require(!old.Archived, "contact_archived", "This contact is already archived.");
        customer.Contacts[index] = old with { Archived = true, IsPrimary = false, ArchivedAt = now, ArchivedBy = actor, UpdatedAt = now, UpdatedBy = actor };
        Validate(customer.Contacts);
    }

    public static void Restore(Customer customer, Guid id, Guid actor, DateTimeOffset now)
    {
        BeforeChange(customer, actor, now); var index = Index(customer, id); var old = customer.Contacts![index];
        Guard.Require(old.Archived, "contact_not_archived", "Only an archived contact can be restored.");
        customer.Contacts[index] = old with { Archived = false, IsPrimary = false, ArchivedAt = null, ArchivedBy = null, UpdatedAt = now, UpdatedBy = actor };
        Validate(customer.Contacts);
    }

    private static void BeforeChange(Customer customer, Guid actor, DateTimeOffset now)
    {
        customer.Validate(); Guard.Id(actor, nameof(actor)); Guard.Utc(now, nameof(now));
        Guard.Require(!customer.Archived, "not_found", "The customer is archived.");
        Guard.Require(now >= customer.CreatedAt && (customer.Contacts?.All(x => now >= x.UpdatedAt) ?? true),
            "invalid_time", "Contact changes cannot predate retained customer or contact records.");
    }

    private static int Index(Customer customer, Guid id)
    {
        Guard.Id(id, nameof(id)); var index = customer.Contacts?.FindIndex(x => x.Id == id) ?? -1;
        Guard.Require(index >= 0, "not_found", "The contact does not belong to this customer."); return index;
    }

    private static void ClearPrimary(List<CustomerContact> contacts, Guid actor, DateTimeOffset now)
    {
        for (var i = 0; i < contacts.Count; i++)
            if (contacts[i].IsPrimary) contacts[i] = contacts[i] with { IsPrimary = false, UpdatedBy = actor, UpdatedAt = now };
    }

    private static void Details(CustomerContactDetails details)
    {
        Guard.Text(details.Name, nameof(details.Name)); Guard.Text(details.Function, nameof(details.Function), 240, false);
        Guard.Email(details.Email); Guard.Text(details.Phone, nameof(details.Phone), 80, false); Guard.Text(details.Notes, nameof(details.Notes), 2000, false);
        Guard.Require(details.Function is not null && details.Email is not null && details.Phone is not null && details.Notes is not null,
            "contact_fields", "Optional contact fields must be strings; use an empty string if unknown.");
    }
}
