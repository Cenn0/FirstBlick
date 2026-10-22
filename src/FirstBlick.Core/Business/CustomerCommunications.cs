namespace FirstBlick.Core.Business;

public enum CustomerCommunicationChannel { Telephone, Email, Meeting, Note }
public enum CustomerCommunicationDirection { Incoming, Outgoing, Internal }

public sealed record CustomerCommunicationRequest(CustomerCommunicationChannel Channel, CustomerCommunicationDirection Direction,
    DateTimeOffset OccurredAt, string Subject, string Text, Guid? ContactId = null, Guid? ProjectId = null);
public sealed record CustomerCommunicationContactSnapshot(Guid Id, string Name, string Function, string Email, string Phone);
public sealed record CustomerCommunicationProjectSnapshot(Guid Id, string Name, string Number);

/// <summary>A manually recorded conversation. Original content is retained; corrections are separate records.</summary>
public sealed class CustomerCommunication : TenantEntity
{
    public Guid CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public CustomerCommunicationChannel Channel { get; set; }
    public CustomerCommunicationDirection Direction { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Subject { get; set; } = "";
    public string Text { get; set; } = "";
    public CustomerCommunicationContactSnapshot? Contact { get; set; }
    public CustomerCommunicationProjectSnapshot? Project { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid RecordedBy { get; set; }
    public string Digest { get; set; } = "";

    public override void Validate()
    {
        ValidateCommon(); Guard.Id(CustomerId, nameof(CustomerId)); Guard.Text(CustomerName, nameof(CustomerName));
        Guard.Require(Revision == 1 && !Archived, "communication_immutable", "Original communication records cannot be overwritten or archived.");
        CustomerCommunicationWorkflow.ValidateDetails(new(Channel, Direction, OccurredAt, Subject, Text, Contact?.Id, Project?.Id));
        Guard.Utc(RecordedAt, nameof(RecordedAt)); Guard.Id(RecordedBy, nameof(RecordedBy));
        Guard.Require(RecordedAt == CreatedAt && OccurredAt <= RecordedAt, "communication_time", "A conversation cannot occur after its recording.");
        if (Contact is { } contact)
        {
            Guard.Id(contact.Id, nameof(Contact)); Guard.Text(contact.Name, nameof(contact.Name));
            Guard.Text(contact.Function, nameof(contact.Function), 240, false); Guard.Email(contact.Email);
            Guard.Text(contact.Phone, nameof(contact.Phone), 80, false);
            Guard.Require(contact.Function is not null && contact.Email is not null && contact.Phone is not null,
                "communication_contact", "Optional contact snapshot fields must be strings.");
        }
        if (Project is { } project)
        {
            Guard.Id(project.Id, nameof(Project)); Guard.Text(project.Name, nameof(project.Name));
            Guard.Text(project.Number, nameof(project.Number), 80, false);
            Guard.Require(project.Number is not null, "communication_project", "The project number snapshot must be a string.");
        }
        Guard.Require(Digest == CustomerCommunicationWorkflow.Digest(this), "communication_digest", "The retained conversation no longer matches its digest.");
    }

    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Customer>(CustomerId);
        if (Project is { } project) yield return BusinessRegistry.Reference<Project>(project.Id);
    }
}

/// <summary>Voiding preserves the original bytes and records who corrected them, when and why.</summary>
public sealed class CustomerCommunicationVoid : TenantEntity
{
    public Guid CustomerId { get; set; }
    public Guid CommunicationId { get; set; }
    public string SourceDigest { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset VoidedAt { get; set; }
    public Guid VoidedBy { get; set; }
    public string Digest { get; set; } = "";

    public override void Validate()
    {
        ValidateCommon(); Guard.Id(CustomerId, nameof(CustomerId)); Guard.Id(CommunicationId, nameof(CommunicationId));
        Guard.Require(Revision == 1 && !Archived, "communication_immutable", "A conversation correction is retained unchanged.");
        Guard.Text(Reason, nameof(Reason), 2000); Guard.Utc(VoidedAt, nameof(VoidedAt)); Guard.Id(VoidedBy, nameof(VoidedBy));
        Guard.Require(VoidedAt == CreatedAt, "communication_time", "The correction must retain its original recording time.");
        Guard.Require(SourceDigest is { Length: 64 } && SourceDigest.All(char.IsAsciiHexDigit),
            "communication_digest", "The original conversation digest is required.");
        Guard.Require(Digest == CustomerCommunicationWorkflow.VoidDigest(this), "communication_digest", "The correction no longer matches its digest.");
    }

    public override IEnumerable<EntityReference> References() =>
        [BusinessRegistry.Reference<Customer>(CustomerId), BusinessRegistry.Reference<CustomerCommunication>(CommunicationId)];
}

public static class CustomerCommunicationWorkflow
{
    public static CustomerCommunication Record(Customer customer, CustomerCommunicationRequest request, Project? project,
        Guid actor, DateTimeOffset now)
    {
        customer.Validate(); ValidateDetails(request); Guard.Id(actor, nameof(actor)); Guard.Utc(now, nameof(now));
        Guard.Require(!customer.Archived, "not_found", "The customer is archived.");
        Guard.Require(now >= customer.UpdatedAt && request.OccurredAt <= now,
            "communication_time", "The conversation must have occurred, and its recording cannot predate the customer state.");
        CustomerCommunicationContactSnapshot? contactSnapshot = null;
        if (request.ContactId is { } contactId)
        {
            var contact = customer.Contacts?.SingleOrDefault(x => x.Id == contactId);
            Guard.Require(contact is not null && !contact.Archived, "communication_contact", "Select an active contact belonging to this customer.");
            Guard.Require(contact.CreatedAt <= now, "communication_time", "The selected contact must exist before recording.");
            contactSnapshot = new(contact.Id, contact.Name, contact.Function, contact.Email, contact.Phone);
        }
        Guard.Require(request.ProjectId.HasValue == (project is not null), "communication_project", "Resolve the explicitly selected project.");
        if (project is not null)
        {
            project.Validate();
            Guard.Require(!project.Archived && project.Id == request.ProjectId && project.TenantId == customer.TenantId && project.CustomerId == customer.Id,
                "communication_project", "The selected project must belong to this customer and business.");
            Guard.Require(project.CreatedAt <= now, "communication_time", "The selected project must exist before recording.");
        }
        var entry = new CustomerCommunication
        {
            TenantId = customer.TenantId, CreatedAt = now, UpdatedAt = now, CustomerId = customer.Id, CustomerName = customer.Name,
            Channel = request.Channel, Direction = request.Direction, OccurredAt = request.OccurredAt,
            Subject = request.Subject.Trim(), Text = request.Text, Contact = contactSnapshot,
            Project = project is null ? null : new(project.Id, project.Name, project.Number ?? ""), RecordedAt = now, RecordedBy = actor
        };
        entry.Digest = Digest(entry); entry.Validate(); return entry;
    }

    public static CustomerCommunicationVoid Void(CustomerCommunication entry, string sourceDigest, string reason, Guid actor, DateTimeOffset now)
    {
        entry.Validate(); Guard.Id(actor, nameof(actor)); Guard.Utc(now, nameof(now)); Guard.Text(reason, nameof(reason), 2000);
        Guard.Require(sourceDigest == entry.Digest, "communication_digest", "Reload and confirm the original conversation before correcting it.");
        Guard.Require(now >= entry.RecordedAt, "communication_time", "A correction cannot predate the original conversation record.");
        var voiding = new CustomerCommunicationVoid
        {
            TenantId = entry.TenantId, CreatedAt = now, UpdatedAt = now, CustomerId = entry.CustomerId, CommunicationId = entry.Id,
            SourceDigest = entry.Digest, Reason = reason.Trim(), VoidedAt = now, VoidedBy = actor
        };
        voiding.Digest = VoidDigest(voiding); voiding.Validate(); return voiding;
    }

    public static void EnsureVoidSource(CustomerCommunication entry, CustomerCommunicationVoid voiding)
    {
        entry.Validate(); voiding.Validate();
        Guard.Require(voiding.TenantId == entry.TenantId && voiding.CustomerId == entry.CustomerId
            && voiding.CommunicationId == entry.Id && voiding.SourceDigest == entry.Digest && voiding.VoidedAt >= entry.RecordedAt,
            "communication_source", "The correction must preserve its exact original conversation and customer.");
    }

    public static void ValidateDetails(CustomerCommunicationRequest request)
    {
        Guard.Require(request is not null, "required", "Communication details are required.");
        Guard.Require(Enum.IsDefined(request.Channel) && Enum.IsDefined(request.Direction), "communication_channel", "Select a supported channel and direction.");
        Guard.Require(request.Channel != CustomerCommunicationChannel.Note || request.Direction == CustomerCommunicationDirection.Internal,
            "communication_direction", "A note is internal; it does not assert that a message was sent or received.");
        Guard.Utc(request.OccurredAt, nameof(request.OccurredAt));
        Guard.Require(request.OccurredAt.Ticks % TimeSpan.TicksPerSecond == 0, "communication_time", "Communication timestamps use whole seconds.");
        Guard.Text(request.Subject, nameof(request.Subject)); Guard.Text(request.Text, nameof(request.Text), 20_000);
        Guard.OptionalId(request.ContactId, nameof(request.ContactId)); Guard.OptionalId(request.ProjectId, nameof(request.ProjectId));
    }

    public static string Digest(CustomerCommunication entry) => ContentDigest.Of(new
    {
        entry.Id, entry.TenantId, entry.CustomerId, entry.CustomerName, entry.Channel, entry.Direction,
        entry.OccurredAt, entry.Subject, entry.Text, entry.Contact, entry.Project, entry.RecordedAt, entry.RecordedBy
    });

    public static string VoidDigest(CustomerCommunicationVoid entry) => ContentDigest.Of(new
    {
        entry.Id, entry.TenantId, entry.CustomerId, entry.CommunicationId, entry.SourceDigest, entry.Reason, entry.VoidedAt, entry.VoidedBy
    });
}
