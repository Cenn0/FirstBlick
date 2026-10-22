using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class CustomerCommunicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Actor = Guid.NewGuid();

    [Fact]
    public void Recording_freezes_the_selected_customer_contact_and_project_without_changing_them()
    {
        var customer = Customer(); var project = Project(customer); var contact = customer.Contacts![0];
        var entry = CustomerCommunicationWorkflow.Record(customer, Details() with { Subject = "  Abstimmung  ", ContactId = contact.Id, ProjectId = project.Id }, project, Actor, Now);
        Assert.Equal("Abstimmung", entry.Subject); Assert.Equal(Actor, entry.RecordedBy); Assert.Equal(Now, entry.RecordedAt);
        Assert.Equal("Zustand bei Gesprächserfassung", entry.CustomerName); Assert.Equal(contact.Name, entry.Contact!.Name);
        Assert.Equal(project.Name, entry.Project!.Name); Assert.Equal(project.Number, entry.Project.Number);
        var digest = entry.Digest;
        customer.Name = "Spätere Firmenbezeichnung"; project.Name = "Späterer Projekttitel";
        CustomerContactWorkflow.Update(customer, contact.Id, new("Neue Person", "", "neu@example.test", "", "", false), Actor, Now.AddDays(1));
        CustomerContactWorkflow.Archive(customer, contact.Id, Actor, Now.AddDays(2));
        entry.Validate(); Assert.Equal(digest, entry.Digest); Assert.Equal(contact.Name, entry.Contact.Name);
        Assert.Equal("Originaler Projekttitel", entry.Project.Name); Assert.Equal("Zustand bei Gesprächserfassung", entry.CustomerName);
        Assert.Equal(1, customer.Revision); Assert.Equal(1, project.Revision);
    }

    [Fact]
    public void A_historical_conversation_may_predate_master_data_creation()
    {
        var customer = Customer();
        var entry = CustomerCommunicationWorkflow.Record(customer, Details() with { OccurredAt = Now.AddYears(-1) }, null, Actor, Now);
        Assert.True(entry.OccurredAt < customer.CreatedAt); entry.Validate();
        Assert.Null(entry.Contact); Assert.Null(entry.Project);
        Assert.Equal(BusinessRegistry.Reference<Customer>(customer.Id), Assert.Single(entry.References()));
    }

    [Theory]
    [InlineData(CustomerCommunicationChannel.Telephone, CustomerCommunicationDirection.Incoming)]
    [InlineData(CustomerCommunicationChannel.Email, CustomerCommunicationDirection.Outgoing)]
    [InlineData(CustomerCommunicationChannel.Meeting, CustomerCommunicationDirection.Internal)]
    [InlineData(CustomerCommunicationChannel.Note, CustomerCommunicationDirection.Internal)]
    public void Supported_channel_and_direction_pairs_are_recorded(CustomerCommunicationChannel channel, CustomerCommunicationDirection direction)
    {
        var entry = CustomerCommunicationWorkflow.Record(Customer(), Details() with { Channel = channel, Direction = direction }, null, Actor, Now);
        Assert.Equal(channel, entry.Channel); Assert.Equal(direction, entry.Direction);
    }

    [Theory]
    [InlineData(CustomerCommunicationChannel.Note, CustomerCommunicationDirection.Outgoing, "communication_direction")]
    [InlineData(CustomerCommunicationChannel.Note, CustomerCommunicationDirection.Incoming, "communication_direction")]
    [InlineData((CustomerCommunicationChannel)99, CustomerCommunicationDirection.Internal, "communication_channel")]
    [InlineData(CustomerCommunicationChannel.Telephone, (CustomerCommunicationDirection)99, "communication_channel")]
    public void Invalid_channel_semantics_are_rejected(CustomerCommunicationChannel channel, CustomerCommunicationDirection direction, string code)
        => Error(code, () => CustomerCommunicationWorkflow.Record(Customer(), Details() with { Channel = channel, Direction = direction }, null, Actor, Now));

    [Fact]
    public void Invalid_or_future_times_cannot_be_recorded()
    {
        Error("invalid_utc", () => Record(Details() with { OccurredAt = Now.ToOffset(TimeSpan.FromHours(2)) }));
        Error("communication_time", () => Record(Details() with { OccurredAt = Now.AddSeconds(1) }));
        Error("communication_time", () => Record(Details() with { OccurredAt = Now.AddSeconds(-1).AddTicks(1) }));
        var customer = Customer(); customer.UpdatedAt = Now.AddSeconds(1);
        Error("communication_time", () => CustomerCommunicationWorkflow.Record(customer, Details(), null, Actor, Now));
    }

    [Fact]
    public void Empty_oversized_or_null_content_is_rejected()
    {
        Error("required", () => Record(Details() with { Subject = " " }));
        Error("required", () => Record(Details() with { Text = null! }));
        Error("text_too_long", () => Record(Details() with { Subject = new string('x', 241) }));
        Error("text_too_long", () => Record(Details() with { Text = new string('x', 20_001) }));
        Error("invalid_text", () => Record(Details() with { Text = "Text\0" }));
        Error("required", () => Record(Details() with { ContactId = Guid.Empty }));
        Error("required", () => Record(Details() with { ProjectId = Guid.Empty }));
    }

    [Fact]
    public void Unknown_archived_and_foreign_contacts_are_not_substituted()
    {
        var customer = Customer(); var contact = customer.Contacts![0];
        Error("communication_contact", () => CustomerCommunicationWorkflow.Record(customer, Details() with { ContactId = Guid.NewGuid() }, null, Actor, Now));
        CustomerContactWorkflow.Archive(customer, contact.Id, Actor, Now.AddSeconds(-1));
        Error("communication_contact", () => CustomerCommunicationWorkflow.Record(customer, Details() with { ContactId = contact.Id }, null, Actor, Now));
    }

    [Fact]
    public void Optional_project_must_be_resolved_in_the_same_customer_and_tenant()
    {
        var customer = Customer(); var project = Project(customer); var request = Details() with { ProjectId = project.Id };
        Error("communication_project", () => CustomerCommunicationWorkflow.Record(customer, request, null, Actor, Now));
        Error("communication_project", () => CustomerCommunicationWorkflow.Record(customer, Details(), project, Actor, Now));
        project.CustomerId = Guid.NewGuid();
        Error("communication_project", () => CustomerCommunicationWorkflow.Record(customer, request, project, Actor, Now));
        project.CustomerId = customer.Id; project.TenantId = Guid.NewGuid();
        Error("communication_project", () => CustomerCommunicationWorkflow.Record(customer, request, project, Actor, Now));
        project.TenantId = customer.TenantId; project.Archived = true;
        Error("communication_project", () => CustomerCommunicationWorkflow.Record(customer, request, project, Actor, Now));
    }

    [Fact]
    public void Digest_detects_changes_to_original_content_identity_and_snapshots()
    {
        var entry = Record(); entry.Text = "Nachträglich geändert";
        Error("communication_digest", entry.Validate);
        entry = Record(); entry.RecordedBy = Guid.NewGuid(); Error("communication_digest", entry.Validate);
        entry = Record(); entry.CustomerId = Guid.NewGuid(); Error("communication_digest", entry.Validate);
        entry = Record(); entry.Archived = true; Error("communication_immutable", entry.Validate);
        entry = Record(); entry.Revision = 2; Error("communication_immutable", entry.Validate);
    }

    [Fact]
    public void Voiding_is_separate_immutable_evidence_and_retains_the_original_digest()
    {
        var entry = Record(); var original = ContentDigest.Of(entry);
        var voiding = CustomerCommunicationWorkflow.Void(entry, entry.Digest, "  Falscher Gesprächsverlauf  ", Actor, Now.AddMinutes(1));
        CustomerCommunicationWorkflow.EnsureVoidSource(entry, voiding);
        Assert.Equal(original, ContentDigest.Of(entry)); Assert.Equal("Falscher Gesprächsverlauf", voiding.Reason);
        Assert.Equal(entry.Digest, voiding.SourceDigest); Assert.Equal(entry.Id, voiding.CommunicationId);
        Assert.Equal(Actor, voiding.VoidedBy); Assert.Equal(Now.AddMinutes(1), voiding.VoidedAt);
        Assert.Contains(BusinessRegistry.Reference<CustomerCommunication>(entry.Id), voiding.References());
        Assert.Contains(BusinessRegistry.Reference<Customer>(entry.CustomerId), voiding.References());
    }

    [Fact]
    public void Voiding_requires_exact_source_reason_actor_and_ordered_time()
    {
        var entry = Record();
        Error("communication_digest", () => CustomerCommunicationWorkflow.Void(entry, new string('0', 64), "Korrektur", Actor, Now));
        Error("required", () => CustomerCommunicationWorkflow.Void(entry, entry.Digest, " ", Actor, Now));
        Error("text_too_long", () => CustomerCommunicationWorkflow.Void(entry, entry.Digest, new string('x', 2001), Actor, Now));
        Error("required", () => CustomerCommunicationWorkflow.Void(entry, entry.Digest, "Korrektur", Guid.Empty, Now));
        Error("communication_time", () => CustomerCommunicationWorkflow.Void(entry, entry.Digest, "Korrektur", Actor, Now.AddSeconds(-1)));
        var voiding = CustomerCommunicationWorkflow.Void(entry, entry.Digest, "Korrektur", Actor, Now);
        voiding.Reason = "Manipuliert"; Error("communication_digest", voiding.Validate);
    }

    [Fact]
    public void A_recomputed_void_digest_does_not_allow_substituting_its_source()
    {
        var entry = Record(); var voiding = CustomerCommunicationWorkflow.Void(entry, entry.Digest, "Korrektur", Actor, Now);
        voiding.CustomerId = Guid.NewGuid(); voiding.Digest = CustomerCommunicationWorkflow.VoidDigest(voiding);
        Error("communication_source", () => CustomerCommunicationWorkflow.EnsureVoidSource(entry, voiding));
    }

    private static CustomerCommunication Record(CustomerCommunicationRequest? details = null)
        => CustomerCommunicationWorkflow.Record(Customer(), details ?? Details(), null, Actor, Now);
    private static CustomerCommunicationRequest Details() => new(CustomerCommunicationChannel.Telephone,
        CustomerCommunicationDirection.Incoming, Now.AddDays(-1), "Abstimmung Dachreparatur", "Zeile 1\nZeile 2");
    private static Customer Customer()
    {
        var customer = new Customer { TenantId = Guid.NewGuid(), Name = "Zustand bei Gesprächserfassung", CreatedAt = Now.AddDays(-30), UpdatedAt = Now.AddDays(-30) };
        CustomerContactWorkflow.Add(customer, new("Verwaltung", "Objektleitung", "kontakt@example.test", "0421 123", "", true), Actor, Now.AddDays(-5));
        customer.UpdatedAt = Now.AddDays(-5); return customer;
    }
    private static Project Project(Customer customer) => new()
    {
        TenantId = customer.TenantId, CustomerId = customer.Id, BuildingId = Guid.NewGuid(), Name = "Originaler Projekttitel", Number = "P-2026-1",
        CreatedAt = Now.AddDays(-20), UpdatedAt = Now.AddDays(-20)
    };
    private static void Error(string code, Action action) => Assert.Equal(code, Assert.Throws<DomainException>(action).Code);
}
