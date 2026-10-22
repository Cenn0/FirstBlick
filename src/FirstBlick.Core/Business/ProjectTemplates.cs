namespace FirstBlick.Core.Business;

public sealed record ProjectTemplateTask(string Title, string Description, int OffsetDays);

/// <summary>A reusable ordered work checklist. Offsets are calendar days, not workdays or scheduling assignments.</summary>
public sealed class ProjectTemplate : TenantEntity
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public ProjectType ProjectType { get; set; }
    public List<ProjectTemplateTask> Tasks { get; set; } = [];

    public override void Validate()
    {
        ValidateCommon(); Guard.Text(Name, nameof(Name)); Guard.Text(Description, nameof(Description), 8000, false);
        Guard.Require(Description is not null && Enum.IsDefined(ProjectType), "project_template_invalid", "Choose a supported project type and description.");
        Guard.Items(Tasks, nameof(Tasks), 50, 1);
        foreach (var task in Tasks)
        {
            Guard.Text(task.Title, "Task title"); Guard.Text(task.Description, "Task description", 4000, false);
            Guard.Require(task.Description is not null && task.OffsetDays is >= -3650 and <= 3650,
                "project_template_offset", "Task offsets must be whole calendar days between -3650 and 3650.");
        }
    }
}

public enum ProjectTemplateAnchorKind { ProjectStart, Explicit }
public sealed record ProjectTemplatePlannedTask(int Position, string Title, string Description, int OffsetDays, DateOnly DueDate);
public sealed record ProjectTemplatePreview(Guid TenantId, ProjectTemplate Template, string TemplateDigest, Project Project,
    string ProjectDigest, DateOnly AnchorDate, ProjectTemplateAnchorKind AnchorKind, IReadOnlyList<ProjectTemplatePlannedTask> Tasks, string Digest)
{
    public void Validate()
    {
        Guard.Require(Template is not null && Project is not null, "project_template_evidence", "The preview requires both original source snapshots.");
        ProjectTemplateWorkflow.EnsureSources(Template, Project);
        Guard.Require(TenantId == Template.TenantId && TemplateDigest == ContentDigest.Of(Template) && ProjectDigest == ContentDigest.Of(Project),
            "project_template_evidence", "The preview source snapshots do not match their digests.");
        Guard.Require(Enum.IsDefined(AnchorKind) && AnchorDate != default
            && (AnchorKind != ProjectTemplateAnchorKind.ProjectStart || AnchorDate == Project.PlannedStart),
            "project_template_anchor", "The anchor must retain its explicit date or the reviewed project start.");
        var expected = ProjectTemplateWorkflow.PlanTasks(Template, AnchorDate);
        Guard.Items(Tasks, nameof(Tasks), 50, 1);
        Guard.Require(Tasks.SequenceEqual(expected) && Digest is { Length: 64 } && Digest == ContentDigest.Of(this with { Digest = "" }),
            "project_template_evidence", "The reviewed task dates or preview digest have changed.");
    }
}

public sealed record ProjectTemplateCreatedTask(Guid TaskId, int Position);
public sealed record ProjectTemplateTaskOrigin(Guid ApplicationId, Guid TemplateId, long TemplateRevision, int Position, string ApplicationDigest);

/// <summary>Retained application evidence survives subsequent edits to its template, project and tasks.</summary>
public sealed class ProjectTemplateApplication : TenantEntity
{
    public Guid TemplateId { get; set; }
    public Guid ProjectId { get; set; }
    public ProjectTemplatePreview? Preview { get; set; }
    public Guid AppliedBy { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
    public List<ProjectTemplateCreatedTask> Tasks { get; set; } = [];
    public string Digest { get; set; } = "";

    public override void Validate()
    {
        ValidateCommon(); Guard.Id(TemplateId, nameof(TemplateId)); Guard.Id(ProjectId, nameof(ProjectId)); Guard.Id(AppliedBy, nameof(AppliedBy));
        Guard.Utc(AppliedAt, nameof(AppliedAt));
        Guard.Require(!Archived && Revision == 1 && CreatedAt == AppliedAt && Preview is not null,
            "project_template_evidence", "A template application is an immutable original with its reviewed preview.");
        Preview.Validate();
        Guard.Require(Preview.TenantId == TenantId && Preview.Template.Id == TemplateId && Preview.Project.Id == ProjectId
            && AppliedAt >= Preview.Template.UpdatedAt && AppliedAt >= Preview.Project.UpdatedAt,
            "project_template_evidence", "The application must retain its sources, business and recording chronology.");
        Guard.Items(Tasks, nameof(Tasks), 50, 1);
        Guard.Require(Tasks.Count == Preview.Tasks.Count && Tasks.Select(x => x.TaskId).Distinct().Count() == Tasks.Count
            && Tasks.Select(x => x.Position).SequenceEqual(Enumerable.Range(1, Tasks.Count)),
            "project_template_evidence", "The application must identify every created task exactly once in template order.");
        foreach (var task in Tasks) Guard.Id(task.TaskId, "Created task");
        Guard.Require(Digest is { Length: 64 } && Digest == CalculateDigest(), "project_template_evidence", "The original application evidence has changed.");
    }

    public string CalculateDigest() => ContentDigest.Of(new
    { format = "project-template-application-v1", TenantId, Id, TemplateId, ProjectId, Preview, AppliedBy, AppliedAt, Tasks });

    public override IEnumerable<EntityReference> References() =>
        [BusinessRegistry.Reference<ProjectTemplate>(TemplateId), BusinessRegistry.Reference<Project>(ProjectId)];
}

public static class ProjectTemplateWorkflow
{
    public static void EnsureSources(ProjectTemplate template, Project project)
    {
        template.Validate(); project.Validate();
        Guard.Require(template.TenantId == project.TenantId, "project_template_tenant", "The template and project must belong to the same business.");
        Guard.Require(!template.Archived && !project.Archived && project.Status is not ProjectStatus.Completed and not ProjectStatus.Cancelled,
            "project_template_inactive", "Apply an active template to an open project.");
        Guard.Require(template.ProjectType == project.Type, "project_template_type", "The template must match the selected project type.");
    }

    public static IReadOnlyList<ProjectTemplatePlannedTask> PlanTasks(ProjectTemplate template, DateOnly anchor)
    {
        template.Validate(); Guard.Require(anchor != default, "project_template_anchor", "Select an explicit anchor date or set the project start.");
        return template.Tasks.Select((task, index) =>
        {
            var day = (long)anchor.DayNumber + task.OffsetDays;
            Guard.Require(day >= DateOnly.MinValue.DayNumber && day <= DateOnly.MaxValue.DayNumber,
                "project_template_date", "A task offset exceeds the supported calendar range.");
            return new ProjectTemplatePlannedTask(index + 1, task.Title, task.Description, task.OffsetDays, DateOnly.FromDayNumber((int)day));
        }).ToArray();
    }

    public static ProjectTemplatePreview Preview(ProjectTemplate template, Project project, DateOnly? explicitAnchor)
    {
        EnsureSources(template, project);
        var anchor = explicitAnchor ?? project.PlannedStart;
        Guard.Require(anchor.HasValue && anchor != default(DateOnly), "project_template_anchor", "Set the planned project start or choose an explicit anchor date.");
        // Copy the source graph: later editing of the loaded entities must not change reviewed evidence.
        var frozenTemplate = new ProjectTemplate { Id = template.Id, TenantId = template.TenantId, Revision = template.Revision,
            CreatedAt = template.CreatedAt, UpdatedAt = template.UpdatedAt, Archived = template.Archived, Name = template.Name,
            Description = template.Description, ProjectType = template.ProjectType, Tasks = template.Tasks.Select(x => x with { }).ToList() };
        var frozenProject = new Project { Id = project.Id, TenantId = project.TenantId, Revision = project.Revision,
            CreatedAt = project.CreatedAt, UpdatedAt = project.UpdatedAt, Archived = project.Archived, CustomerId = project.CustomerId,
            BuildingId = project.BuildingId, Name = project.Name, Number = project.Number, Type = project.Type, Status = project.Status,
            Description = project.Description, PlannedStart = project.PlannedStart, PlannedEnd = project.PlannedEnd };
        var preview = new ProjectTemplatePreview(template.TenantId, frozenTemplate, ContentDigest.Of(frozenTemplate), frozenProject,
            ContentDigest.Of(frozenProject), anchor.Value, explicitAnchor.HasValue ? ProjectTemplateAnchorKind.Explicit : ProjectTemplateAnchorKind.ProjectStart,
            PlanTasks(frozenTemplate, anchor.Value), "");
        preview = preview with { Digest = ContentDigest.Of(preview) }; preview.Validate(); return preview;
    }

    public static (ProjectTemplateApplication Application, IReadOnlyList<TaskItem> Tasks) Apply(ProjectTemplatePreview preview, Guid actor, DateTimeOffset now)
    {
        preview.Validate(); Guard.Id(actor, nameof(actor)); Guard.Utc(now, nameof(now));
        // PostgreSQL stores the immutable CreatedAt column at microsecond precision;
        // later task edits restore that column. Capture the same precision at origin
        // so an ordinary edit cannot change the task's recorded creation instant.
        now = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        var tasks = preview.Tasks.Select(x => new TaskItem { TenantId = preview.TenantId, ProjectId = preview.Project.Id,
            Title = x.Title, Description = x.Description, DueDate = x.DueDate, Status = TaskStatus.Open, AssignedEmployeeId = null,
            CreatedAt = now, UpdatedAt = now }).ToArray();
        var application = new ProjectTemplateApplication { TenantId = preview.TenantId, TemplateId = preview.Template.Id, ProjectId = preview.Project.Id,
            Preview = preview, AppliedBy = actor, AppliedAt = now, CreatedAt = now, UpdatedAt = now,
            Tasks = tasks.Select((task, index) => new ProjectTemplateCreatedTask(task.Id, index + 1)).ToList() };
        application.Digest = application.CalculateDigest(); application.Validate();
        foreach (var (task, index) in tasks.Select((task, index) => (task, index)))
        {
            task.TemplateOrigin = new(application.Id, application.TemplateId, preview.Template.Revision, index + 1, application.Digest);
            task.Validate();
        }
        return (application, tasks);
    }

    public static void EnsureTaskOrigin(TaskItem task, ProjectTemplateApplication application)
    {
        application.Validate(); task.Validate();
        var origin = task.TemplateOrigin;
        Guard.Require(origin is not null && task.TenantId == application.TenantId && task.ProjectId == application.ProjectId
            && origin.ApplicationId == application.Id && origin.TemplateId == application.TemplateId
            && origin.TemplateRevision == application.Preview!.Template.Revision && origin.ApplicationDigest == application.Digest
            && application.Tasks.Any(x => x.TaskId == task.Id && x.Position == origin.Position)
            && task.CreatedAt == application.AppliedAt,
            "project_template_origin", "The task must retain its original application, project, position and digest.");
    }
}
