namespace FirstBlick.Core.Business;

public sealed class Customer : TenantEntity
{
    public string Name { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Address { get; set; } = "";
    public string Notes { get; set; } = "";
    // Absent on retained customer snapshots; contacts are managed only by their own commands.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<CustomerContact>? Contacts { get; set; }
    public override void Validate()
    {
        ValidateCommon(); Guard.Text(Name, nameof(Name)); Guard.Text(ContactName, nameof(ContactName), required: false);
        Guard.Email(Email); Guard.Text(Phone, nameof(Phone), 80, false); Guard.Text(Address, nameof(Address), 2000, false);
        Guard.Text(Notes, nameof(Notes), 20_000, false);
        CustomerContactWorkflow.Validate(Contacts);
        Guard.Require(Contacts is null || Contacts.All(x => x.CreatedAt >= CreatedAt),
            "invalid_time", "Customer contacts cannot predate their customer.");
    }
}

public sealed class Building : TenantEntity
{
    public Guid CustomerId { get; set; }
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string RoofDescription { get; set; } = "";
    public DateOnly? ConstructionDate { get; set; }
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(CustomerId, nameof(CustomerId)); Guard.Text(Name, nameof(Name));
        Guard.Text(Address, nameof(Address), 2000); Guard.Text(RoofDescription, nameof(RoofDescription), 20_000, false);
    }
    public override IEnumerable<EntityReference> References() => [BusinessRegistry.Reference<Customer>(CustomerId)];
}

public enum ProjectStatus { Inquiry, Planning, Active, Completed, Cancelled }
public enum ProjectType { Renovation, Repair, Maintenance }
public sealed class Project : TenantEntity
{
    public Guid CustomerId { get; set; }
    public Guid BuildingId { get; set; }
    public string Name { get; set; } = "";
    public string Number { get; set; } = "";
    public ProjectType Type { get; set; }
    public ProjectStatus Status { get; set; }
    public string Description { get; set; } = "";
    public DateOnly? PlannedStart { get; set; }
    public DateOnly? PlannedEnd { get; set; }
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(CustomerId, nameof(CustomerId)); Guard.Id(BuildingId, nameof(BuildingId));
        Guard.Text(Name, nameof(Name)); Guard.Text(Number, nameof(Number), 80, false); Guard.Text(Description, nameof(Description), 20_000, false);
        Guard.Require(Enum.IsDefined(Type) && Enum.IsDefined(Status), "invalid_status", "Project type or status is invalid.");
        Guard.Require(!PlannedStart.HasValue || !PlannedEnd.HasValue || PlannedEnd >= PlannedStart, "invalid_window", "Planned end must not precede planned start.");
    }
    public override IEnumerable<EntityReference> References() => [BusinessRegistry.Reference<Customer>(CustomerId), BusinessRegistry.Reference<Building>(BuildingId)];
}

public enum TaskStatus { Open, InProgress, Done, Cancelled }
public sealed class TaskItem : TenantEntity
{
    public Guid ProjectId { get; set; }
    public Guid? AssignedEmployeeId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateOnly? DueDate { get; set; }
    public TaskStatus Status { get; set; }
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(ProjectId, nameof(ProjectId)); Guard.OptionalId(AssignedEmployeeId, nameof(AssignedEmployeeId));
        Guard.Text(Title, nameof(Title)); Guard.Text(Description, nameof(Description), 20_000, false);
        Guard.Require(Enum.IsDefined(Status), "invalid_status", "Task status is invalid.");
    }
    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Project>(ProjectId);
        if (AssignedEmployeeId is { } id) yield return BusinessRegistry.Reference<Employee>(id);
    }
}

public sealed record GaebCatalogOrigin(string SourceSha256, string PositionKey, string ItemDigest,
    string ExchangePhase, string PriceEvidence);

public sealed class CatalogItem : TenantEntity
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GaebCatalogOrigin? GaebOrigin { get; set; }
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "m²";
    public decimal UnitMaterialCost { get; set; }
    public decimal UnitLaborHours { get; set; }
    public decimal HourlyRate { get; set; }
    public decimal MarkupPercent { get; set; }
    public decimal VatPercent { get; set; }
    public override void Validate()
    {
        ValidateCommon(); ToLine().Validate();
        if (GaebOrigin is { } origin)
        {
            Guard.Require(origin.SourceSha256.Length == 64
                && origin.SourceSha256.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f')
                && origin.ItemDigest.Length == 64
                && origin.ItemDigest.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f'),
                "gaeb_origin", "GAEB origins require stable lowercase SHA-256 values.");
            Guard.Text(origin.PositionKey, "GAEB position", 240);
            Guard.Require(origin.ExchangePhase is "X83" or "X84", "gaeb_origin", "Unsupported GAEB phase.");
            Guard.Text(origin.PriceEvidence, "GAEB price basis", 2000);
        }
    }
    public QuoteLine ToLine(decimal quantity = 1m) => new()
    {
        CatalogItemId = Id, Description = Name, Unit = Unit, Quantity = quantity, UnitMaterialCost = UnitMaterialCost,
        UnitLaborHours = UnitLaborHours, HourlyRate = HourlyRate, MarkupPercent = MarkupPercent, VatPercent = VatPercent
    };
}
