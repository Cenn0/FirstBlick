namespace FirstBlick.Core.Business;

public sealed record MaintenanceCheckTemplate(string Title, string Instructions = "");
public enum MaintenanceCheckState { Pending, Done, NotApplicable }
public enum MaintenanceVisitStatus { Planned, Completed }

public sealed class MaintenanceCheck
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Instructions { get; set; } = "";
    public MaintenanceCheckState State { get; set; }
    public string Note { get; set; } = "";
    public Guid? RecordedBy { get; set; }
    public DateTimeOffset? RecordedAt { get; set; }
}

public sealed record MaintenanceTerms(Guid CustomerId, Guid BuildingId, string Title, string Scope,
    DateOnly StartDate, int IntervalMonths);

/// <summary>A single contract occurrence, with an immutable work scope and checklist, belongs to exactly one project.</summary>
public sealed class MaintenanceVisit : TenantEntity
{
    public Guid ContractId { get; set; }
    public long ContractRevision { get; set; }
    public Guid ProjectId { get; set; }
    public DateOnly DueDate { get; set; }
    public MaintenanceTerms? Terms { get; set; }
    public string ScopeDigest { get; set; } = "";
    public List<MaintenanceCheck> Checklist { get; set; } = [];
    public MaintenanceVisitStatus Status { get; set; }
    public string CompletionNotes { get; set; } = "";
    public Guid? CompletedBy { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateOnly? NextDueDate { get; set; }

    public string CalculateScopeDigest() => ContentDigest.Of(new
    {
        ContractId, ContractRevision, ProjectId, DueDate, Terms,
        checklist = Checklist.Select(x => new { x.Id, x.Title, x.Instructions }).ToArray()
    });

    public override void Validate()
    {
        ValidateCommon(); Guard.Id(ContractId, nameof(ContractId)); Guard.Id(ProjectId, nameof(ProjectId));
        Guard.Require(ContractRevision > 0 && DueDate != default && Terms is not null, "maintenance_evidence", "The visit requires its contract revision, due date and frozen scope.");
        Guard.Id(Terms.CustomerId, "CustomerId"); Guard.Id(Terms.BuildingId, "BuildingId");
        Guard.Text(Terms.Title, "Title"); Guard.Text(Terms.Scope, "Scope", 20_000);
        Guard.Require(Terms.StartDate != default && Terms.IntervalMonths is >= 1 and <= 120 && DueDate >= Terms.StartDate,
            "maintenance_evidence", "The recurrence snapshot is invalid.");
        Guard.Items(Checklist, nameof(Checklist), 50, 1);
        Guard.Require(Checklist.Select(x => x.Id).Distinct().Count() == Checklist.Count, "duplicate_check", "Checklist identifiers must be unique.");
        foreach (var item in Checklist)
        {
            Guard.Id(item.Id, "CheckId"); Guard.Text(item.Title, "Check title"); Guard.Text(item.Instructions, "Check instructions", 4000, false);
            Guard.Require(Enum.IsDefined(item.State), "invalid_status", "The checklist state is invalid.");
            Guard.Text(item.Note, "Check note", 4000, item.State == MaintenanceCheckState.NotApplicable);
            if (item.State == MaintenanceCheckState.Pending)
                Guard.Require(item.RecordedBy is null && item.RecordedAt is null, "maintenance_evidence", "Pending checks cannot claim a completion.");
            else
            {
                Guard.Require(item.RecordedBy.HasValue && item.RecordedAt.HasValue, "maintenance_evidence", "A completed check requires an actor and time.");
                Guard.Id(item.RecordedBy.Value, "RecordedBy"); Guard.Utc(item.RecordedAt.Value, "RecordedAt");
            }
        }
        Guard.Require(ScopeDigest == CalculateScopeDigest(), "maintenance_evidence", "The frozen visit scope or checklist has changed.");
        Guard.Require(Enum.IsDefined(Status), "invalid_status", "The maintenance visit status is invalid.");
        Guard.Text(CompletionNotes, nameof(CompletionNotes), 8000, Status == MaintenanceVisitStatus.Completed);
        if (Status == MaintenanceVisitStatus.Completed)
        {
            Guard.Require(Checklist.All(x => x.State != MaintenanceCheckState.Pending) && CompletedBy.HasValue && CompletedAt.HasValue
                && NextDueDate.HasValue && NextDueDate > DueDate, "maintenance_incomplete", "Completion requires all checks, an actor and the next recurrence date.");
            Guard.Id(CompletedBy.Value, nameof(CompletedBy)); Guard.Utc(CompletedAt.Value, nameof(CompletedAt));
        }
        else Guard.Require(CompletedBy is null && CompletedAt is null && NextDueDate is null && CompletionNotes == "", "maintenance_evidence", "A planned visit cannot claim completion.");
    }

    public override IEnumerable<EntityReference> References() =>
        [BusinessRegistry.Reference<MaintenanceContract>(ContractId), BusinessRegistry.Reference<Project>(ProjectId)];
}

public static class MaintenanceWorkflow
{
    public static void ValidateTemplate(IReadOnlyList<MaintenanceCheckTemplate> checklist, bool required)
    {
        Guard.Items(checklist, "Checklist template", 50, required ? 1 : 0);
        foreach (var item in checklist) { Guard.Text(item.Title, "Check title"); Guard.Text(item.Instructions, "Check instructions", 4000, false); }
    }

    public static (Project Project, MaintenanceVisit Visit) Plan(MaintenanceContract contract, DateOnly expectedDueDate, string projectNumber)
    {
        contract.Validate();
        Guard.Require(contract.Active && !contract.Archived && contract.NextDueDate == expectedDueDate, "stale_occurrence", "The current maintenance occurrence has changed or is inactive.");
        Guard.Require(!contract.EndDate.HasValue || expectedDueDate <= contract.EndDate, "contract_expired", "The maintenance contract has ended.");
        ValidateTemplate(contract.ChecklistTemplate, true);
        var project = new Project { TenantId = contract.TenantId, CustomerId = contract.CustomerId, BuildingId = contract.BuildingId,
            Name = contract.Title.Length <= 220 ? $"{contract.Title} · {expectedDueDate:dd.MM.yyyy}" : $"{contract.Title[..220]} · {expectedDueDate:dd.MM.yyyy}",
            Number = projectNumber, Type = ProjectType.Maintenance, Status = ProjectStatus.Planning,
            Description = contract.Scope, PlannedStart = expectedDueDate, PlannedEnd = expectedDueDate };
        var visit = new MaintenanceVisit { TenantId = contract.TenantId, ContractId = contract.Id, ContractRevision = contract.Revision,
            ProjectId = project.Id, DueDate = expectedDueDate,
            Terms = new(contract.CustomerId, contract.BuildingId, contract.Title, contract.Scope, contract.StartDate, contract.IntervalMonths),
            Checklist = contract.ChecklistTemplate.Select(x => new MaintenanceCheck { Title = x.Title, Instructions = x.Instructions }).ToList() };
        visit.ScopeDigest = visit.CalculateScopeDigest(); project.Validate(); visit.Validate(); return (project, visit);
    }

    public static void Record(MaintenanceVisit visit, Guid checkId, MaintenanceCheckState state, string note, Guid actor, DateTimeOffset now)
    {
        visit.Validate(); Guard.Id(actor, nameof(actor)); Guard.Utc(now, nameof(now));
        Guard.Require(visit.Status == MaintenanceVisitStatus.Planned, "maintenance_sealed", "Completed maintenance evidence is immutable.");
        Guard.Require(Enum.IsDefined(state), "invalid_status", "The checklist state is invalid.");
        Guard.Text(note, "Check note", 4000, state == MaintenanceCheckState.NotApplicable);
        var check = visit.Checklist.SingleOrDefault(x => x.Id == checkId) ?? throw new DomainException("not_found", "This checklist item is not part of the selected visit.");
        check.State = state; check.Note = note;
        check.RecordedBy = state == MaintenanceCheckState.Pending ? null : actor;
        check.RecordedAt = state == MaintenanceCheckState.Pending ? null : now;
        visit.Validate();
    }

    public static void Complete(MaintenanceVisit visit, MaintenanceContract contract, Project project, string notes, Guid actor, DateTimeOffset now)
    {
        visit.Validate(); contract.Validate(); project.Validate(); Guard.Id(actor, nameof(actor)); Guard.Utc(now, nameof(now)); Guard.Text(notes, nameof(notes), 8000);
        Guard.Require(visit.Status == MaintenanceVisitStatus.Planned, "maintenance_sealed", "This occurrence has already been completed.");
        Guard.Require(visit.Checklist.All(x => x.State != MaintenanceCheckState.Pending), "maintenance_incomplete", "Record a result for every checklist item before completing the visit.");
        Guard.Require(visit.TenantId == contract.TenantId && visit.TenantId == project.TenantId && visit.ContractId == contract.Id
            && visit.ProjectId == project.Id && project.Type == ProjectType.Maintenance && project.Status != ProjectStatus.Cancelled
            && project.CustomerId == visit.Terms!.CustomerId && project.BuildingId == visit.Terms.BuildingId
            && contract.CustomerId == visit.Terms.CustomerId && contract.BuildingId == visit.Terms.BuildingId
            && contract.StartDate == visit.Terms.StartDate && contract.IntervalMonths == visit.Terms.IntervalMonths,
            "maintenance_project_mismatch", "The visit must retain its original contract, customer, building and recurrence.");
        var next = OperationsRules.CompleteMaintenance(contract, visit.DueDate);
        project.Status = ProjectStatus.Completed; visit.Status = MaintenanceVisitStatus.Completed;
        visit.CompletedBy = actor; visit.CompletedAt = now; visit.CompletionNotes = notes; visit.NextDueDate = next;
        visit.Validate();
    }
}
