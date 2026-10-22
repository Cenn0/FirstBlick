using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using TaskStatus = FirstBlick.Core.Business.TaskStatus;

namespace FirstBlick.Core.Tests.Business;

public sealed class ProjectTemplateTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static (ProjectTemplate Template, Project Project) Sources()
    {
        var tenant = Guid.NewGuid();
        return (new ProjectTemplate { TenantId = tenant, Name = "Dachsanierung", Description = "Abstimmen, ausführen, prüfen",
            ProjectType = ProjectType.Renovation, CreatedAt = Recorded, UpdatedAt = Recorded,
            Tasks = [new("Material prüfen", "Bestellung kontrollieren", -2), new("Dacharbeiten", "Nach Auftrag", 0), new("Endkontrolle", "Fotos dokumentieren", 3)] },
            new Project { TenantId = tenant, CustomerId = Guid.NewGuid(), BuildingId = Guid.NewGuid(), Name = "Dach A",
                Type = ProjectType.Renovation, Status = ProjectStatus.Planning, PlannedStart = new(2028, 2, 28),
                CreatedAt = Recorded, UpdatedAt = Recorded });
    }

    [Fact]
    public void Preview_uses_calendar_days_in_order_across_leap_day_without_assigning_staff()
    {
        var (template, project) = Sources(); var preview = ProjectTemplateWorkflow.Preview(template, project, null);
        Assert.Equal(ProjectTemplateAnchorKind.ProjectStart, preview.AnchorKind); Assert.Equal(project.PlannedStart, preview.AnchorDate);
        Assert.Equal(new[] { new DateOnly(2028, 2, 26), new(2028, 2, 28), new(2028, 3, 2) }, preview.Tasks.Select(x => x.DueDate));
        Assert.Equal(new[] { 1, 2, 3 }, preview.Tasks.Select(x => x.Position));
        var (application, tasks) = ProjectTemplateWorkflow.Apply(preview, Guid.NewGuid(), Recorded.AddDays(1));
        Assert.Equal(template.Id, application.TemplateId); Assert.Equal(project.Id, application.ProjectId);
        Assert.All(tasks, task => { Assert.Null(task.AssignedEmployeeId); Assert.Equal(TaskStatus.Open, task.Status); ProjectTemplateWorkflow.EnsureTaskOrigin(task, application); });
        Assert.Equal(preview.Tasks.Select(x => x.DueDate), tasks.Select(x => x.DueDate!.Value));
        Assert.Equal(3, tasks.Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public void Explicit_anchor_is_independent_from_project_start_and_changes_the_preview_digest()
    {
        var (template, project) = Sources(); var normal = ProjectTemplateWorkflow.Preview(template, project, null);
        var explicitSame = ProjectTemplateWorkflow.Preview(template, project, project.PlannedStart);
        Assert.NotEqual(normal.Digest, explicitSame.Digest); Assert.Equal(ProjectTemplateAnchorKind.Explicit, explicitSame.AnchorKind);
        project.PlannedStart = null;
        Assert.Equal("project_template_anchor", Assert.Throws<DomainException>(() => ProjectTemplateWorkflow.Preview(template, project, null)).Code);
        var selected = ProjectTemplateWorkflow.Preview(template, project, new(2028, 12, 31));
        Assert.Equal(new DateOnly(2029, 1, 3), selected.Tasks[2].DueDate);
    }

    [Fact]
    public void Preview_copies_all_sources_and_application_retains_the_original_after_edits()
    {
        var (template, project) = Sources(); var preview = ProjectTemplateWorkflow.Preview(template, project, null);
        var (application, tasks) = ProjectTemplateWorkflow.Apply(preview, Guid.NewGuid(), Recorded.AddDays(1)); var digest = application.Digest;
        template.Name = "Andere Vorlage"; template.Tasks[0] = new("Anderer Schritt", "", 20); template.Revision++;
        project.Name = "Umbenannt"; project.PlannedStart = new(2030, 1, 1); project.Revision++;
        tasks[0].Title = "Für diese Baustelle angepasst"; tasks[0].DueDate = new(2028, 3, 1); tasks[0].Status = TaskStatus.Done;
        tasks[0].Revision++; tasks[0].UpdatedAt = Recorded.AddDays(2);
        preview.Validate(); application.Validate(); ProjectTemplateWorkflow.EnsureTaskOrigin(tasks[0], application);
        Assert.Equal(digest, application.Digest); Assert.Equal("Dachsanierung", application.Preview!.Template.Name);
        Assert.Equal("Material prüfen", application.Preview.Tasks[0].Title); Assert.Equal("Dach A", application.Preview.Project.Name);
    }

    [Theory]
    [InlineData(ProjectStatus.Completed)]
    [InlineData(ProjectStatus.Cancelled)]
    public void Finished_projects_cannot_receive_templates(ProjectStatus status)
    {
        var (template, project) = Sources(); project.Status = status;
        Assert.Equal("project_template_inactive", Assert.Throws<DomainException>(() => ProjectTemplateWorkflow.Preview(template, project, null)).Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Archived_sources_cannot_generate_work(bool archiveTemplate)
    {
        var (template, project) = Sources(); if (archiveTemplate) template.Archived = true; else project.Archived = true;
        Assert.Equal("project_template_inactive", Assert.Throws<DomainException>(() => ProjectTemplateWorkflow.Preview(template, project, null)).Code);
    }

    [Fact]
    public void Cross_tenant_and_project_type_mismatch_are_rejected()
    {
        var (template, project) = Sources(); template.TenantId = Guid.NewGuid();
        Assert.Equal("project_template_tenant", Assert.Throws<DomainException>(() => ProjectTemplateWorkflow.Preview(template, project, null)).Code);
        template.TenantId = project.TenantId; template.ProjectType = ProjectType.Repair;
        Assert.Equal("project_template_type", Assert.Throws<DomainException>(() => ProjectTemplateWorkflow.Preview(template, project, null)).Code);
    }

    [Theory]
    [InlineData(3651)]
    [InlineData(-3651)]
    public void Excessive_offsets_are_rejected(int offset)
    {
        var (template, _) = Sources(); template.Tasks = [new("Prüfen", "", offset)];
        Assert.Equal("project_template_offset", Assert.Throws<DomainException>(template.Validate).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Task_count_is_bounded_before_any_application(int count)
    {
        var (template, _) = Sources(); template.Tasks = Enumerable.Range(0, count).Select(x => new ProjectTemplateTask($"Schritt {x}", "", x)).ToList();
        Assert.Equal("invalid_collection", Assert.Throws<DomainException>(template.Validate).Code);
    }

    [Theory]
    [InlineData(1, 1, 2, -2)]
    [InlineData(9999, 12, 30, 2)]
    public void Date_overflow_is_a_domain_error_not_a_partial_plan(int year, int month, int day, int offset)
    {
        var (template, project) = Sources(); template.Tasks = [new("Gültiger Schritt", "", 0), new("Überlauf", "", offset)];
        Assert.Equal("project_template_date", Assert.Throws<DomainException>(() => ProjectTemplateWorkflow.Preview(template, project, new(year, month, day))).Code);
    }

    [Fact]
    public void Reordered_tasks_or_changed_sources_are_detected_even_when_a_digest_is_supplied()
    {
        var (template, project) = Sources(); var preview = ProjectTemplateWorkflow.Preview(template, project, null);
        var wrongDate = preview with { Tasks = [preview.Tasks[0] with { DueDate = preview.Tasks[0].DueDate.AddDays(1) }, .. preview.Tasks.Skip(1)] };
        wrongDate = wrongDate with { Digest = ContentDigest.Of(wrongDate with { Digest = "" }) };
        Assert.Equal("project_template_evidence", Assert.Throws<DomainException>(wrongDate.Validate).Code);
        preview.Template.Name = "Ungeprüft geändert";
        Assert.Equal("project_template_evidence", Assert.Throws<DomainException>(preview.Validate).Code);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("application")]
    [InlineData("position")]
    [InlineData("digest")]
    public void Task_provenance_cannot_be_reparented_or_swapped(string change)
    {
        var (template, project) = Sources(); var (application, tasks) = ProjectTemplateWorkflow.Apply(ProjectTemplateWorkflow.Preview(template, project, null), Guid.NewGuid(), Recorded.AddDays(1));
        var task = tasks[0];
        if (change == "project") task.ProjectId = Guid.NewGuid();
        else task.TemplateOrigin = change switch
        {
            "application" => task.TemplateOrigin! with { ApplicationId = Guid.NewGuid() },
            "position" => task.TemplateOrigin! with { Position = 2 },
            _ => task.TemplateOrigin! with { ApplicationDigest = new string('0', 64) }
        };
        Assert.Equal("project_template_origin", Assert.Throws<DomainException>(() => ProjectTemplateWorkflow.EnsureTaskOrigin(task, application)).Code);
    }

    [Theory]
    [InlineData("ids")]
    [InlineData("actor")]
    [InlineData("archived")]
    [InlineData("revision")]
    public void Application_original_cannot_be_modified(string change)
    {
        var (template, project) = Sources(); var (application, _) = ProjectTemplateWorkflow.Apply(ProjectTemplateWorkflow.Preview(template, project, null), Guid.NewGuid(), Recorded.AddDays(1));
        if (change == "ids") application.Tasks[0] = application.Tasks[0] with { TaskId = application.Tasks[1].TaskId };
        if (change == "actor") application.AppliedBy = Guid.NewGuid();
        if (change == "archived") application.Archived = true;
        if (change == "revision") application.Revision++;
        Assert.Equal("project_template_evidence", Assert.Throws<DomainException>(application.Validate).Code);
    }

    [Theory]
    [InlineData(BusinessRole.ManagingDirector, true, true)]
    [InlineData(BusinessRole.Office, true, true)]
    [InlineData(BusinessRole.SiteManager, true, true)]
    [InlineData(BusinessRole.Accounting, true, false)]
    [InlineData(BusinessRole.Worker, false, false)]
    [InlineData(BusinessRole.Trainer, false, false)]
    [InlineData(BusinessRole.Partner, false, false)]
    public void Template_access_is_explicit_and_application_records_have_no_generic_write(BusinessRole role, bool read, bool write)
    {
        Assert.Equal(read, AccessRules.CanRead(role, "project-templates")); Assert.Equal(write, AccessRules.CanWrite(role, "project-templates"));
        Assert.Equal(read, AccessRules.CanRead(role, "project-template-applications")); Assert.False(AccessRules.CanWrite(role, "project-template-applications"));
        Assert.Equal("core", AccessRules.ModuleFor("project-templates"));
    }
}
