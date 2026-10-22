using FirstBlick.Core.Business;
using TaskStatus = FirstBlick.Core.Business.TaskStatus;

namespace FirstBlick.Operations;

internal static partial class MigrationDrill
{
    private static void SeedProjectTemplateHistory(Project project, Guid actor, DateTimeOffset at, List<TenantEntity> entities)
    {
        var template = new ProjectTemplate { TenantId = project.TenantId, Name = "Historical reviewed renovation template",
            Description = "Retained calendar-day task plan", ProjectType = project.Type, CreatedAt = at, UpdatedAt = at,
            Tasks = [new("Prepare historical site", "Explicitly two calendar days before the anchor", -2),
                new("Check historical roof", "Synthetic leap-day task", 1),
                new("Review historical work", "Retained unedited task", 3)] };
        var preview = ProjectTemplateWorkflow.Preview(template, project, new DateOnly(2028, 2, 28));
        var (application, tasks) = ProjectTemplateWorkflow.Apply(preview, actor, at);
        entities.Add(template); entities.Add(application); entities.AddRange(tasks);
        project.Revision++; project.UpdatedAt = at;
        // Task work and future template edits do not rewrite the original adoption.
        tasks[0].Title = "Current completed preparation"; tasks[0].Status = TaskStatus.Done;
        tasks[0].Revision++; tasks[0].UpdatedAt = at.AddSeconds(1);
        tasks[1].Archived = true; tasks[1].Revision++; tasks[1].UpdatedAt = at.AddSeconds(1);
        template.Name = "Current renamed archived template"; template.Tasks[0] = template.Tasks[0] with { OffsetDays = -3 };
        template.Archived = true; template.Revision++; template.UpdatedAt = at.AddSeconds(2);
    }

    private static void VerifyProjectTemplateHistory(IEnumerable<TenantEntity> source)
    {
        var entities = source.ToArray();
        var application = entities.OfType<ProjectTemplateApplication>().Single(); application.Validate();
        var preview = application.Preview!;
        var template = entities.OfType<ProjectTemplate>().Single();
        Require.That(template.Archived && template.Revision > preview.Template.Revision
            && template.Name != preview.Template.Name
            && template.Tasks[0].OffsetDays != preview.Template.Tasks[0].OffsetDays,
            "Migration replaced historical template contents with its edited current version.");
        var tasks = entities.OfType<TaskItem>().Where(x => x.TemplateOrigin?.ApplicationId == application.Id)
            .OrderBy(x => x.TemplateOrigin!.Position).ToArray();
        Require.That(tasks.Length == 3 && tasks[0].Status == TaskStatus.Done && tasks[1].Archived && tasks[2].Revision == 1
            && tasks[0].Title != preview.Tasks[0].Title,
            "Migration lost later task work, archived identity or original application contents.");
        Require.That(preview.Tasks[0].DueDate == new DateOnly(2028, 2, 26)
            && preview.Tasks[1].DueDate == new DateOnly(2028, 2, 29),
            "Migration changed retained relative calendar dates.");
        foreach (var task in tasks) ProjectTemplateWorkflow.EnsureTaskOrigin(task, application);
    }

    private static async Task RejectProjectTemplateSources(MigrationBundle bundle, IEnumerable<TenantEntity> source,
        Func<string, MigrationBundle, Func<MigrationIssue, bool>?, Task> reject)
    {
        var entities = source.ToArray();
        Task Reject(string name, MigrationBundle altered, Func<MigrationIssue, bool>? expected = null) =>
            reject("project-template-" + name, altered, expected);
        await Reject("changed-original", Change<ProjectTemplateApplication>(bundle, application => application.Preview!.Template.Name = "Changed retained name"));
        await Reject("unknown-actor", Change<ProjectTemplateApplication>(bundle, application =>
        { application.AppliedBy = Guid.NewGuid(); application.Digest = application.CalculateDigest(); }),
            issue => issue.Message == "A historical actor/user must retain a supplied tenant membership, including inactive members.");
        await Reject("after-export", Change<ProjectTemplateApplication>(bundle, application =>
        {
            application.AppliedAt = bundle.ExportedAt.AddTicks(10); application.CreatedAt = application.UpdatedAt = application.AppliedAt;
            application.Digest = application.CalculateDigest();
        }), issue => issue.Location.StartsWith("project-template-applications/", StringComparison.Ordinal)
            && issue.Message == "Entity envelope timestamps must fit PostgreSQL microseconds and cannot postdate the export; their historical JSON values are never truncated by the importer.");
        await Reject("missing-task", Change<ProjectTemplateApplication>(bundle, application =>
        { application.Tasks[0] = application.Tasks[0] with { TaskId = Guid.NewGuid() }; application.Digest = application.CalculateDigest(); }));
        await Reject("missing-origin", Change<TaskItem>(bundle, task => task.TemplateOrigin = null, task => task.TemplateOrigin is not null));
        await Reject("wrong-origin-digest", Change<TaskItem>(bundle, task =>
        { task.TemplateOrigin = task.TemplateOrigin! with { ApplicationDigest = new string('0', 64) }; }, task => task.TemplateOrigin is not null));
        await Reject("swapped-task-position", Change<TaskItem>(bundle, task =>
        { task.TemplateOrigin = task.TemplateOrigin! with { Position = 2 }; }, task => task.TemplateOrigin?.Position == 1));
        await Reject("changed-task-creation", Change<TaskItem>(bundle, task => task.CreatedAt = task.CreatedAt.AddTicks(-10),
            task => task.TemplateOrigin is not null));
        await Reject("source-revision-regression", Change<ProjectTemplate>(bundle, template => template.Revision = 1),
            issue => issue.Message == "An unchanged template revision must equal its retained application snapshot.");
        await Reject("project-revision-regression", Change<Project>(bundle, project =>
            project.Revision = entities.OfType<ProjectTemplateApplication>().Single().Preview!.Project.Revision),
            issue => issue.Message == "A project template application must retain existing source identities and compatible revision history.");
        await Reject("duplicate-application", Duplicate<ProjectTemplateApplication>(bundle, application => application.Digest = application.CalculateDigest()),
            issue => issue.Message == "The imported record violates its shared database uniqueness contract.");
        await Reject("missing-historical-customer", Change<ProjectTemplateApplication>(bundle, application =>
        {
            var preview = application.Preview!; preview.Project.CustomerId = Guid.NewGuid();
            preview = preview with { ProjectDigest = ContentDigest.Of(preview.Project), Digest = "" };
            application.Preview = preview with { Digest = ContentDigest.Of(preview) }; application.Digest = application.CalculateDigest();
        }), issue => issue.Message.StartsWith("Missing tenant-qualified customers reference", StringComparison.Ordinal));
        await Reject("missing-historical-building", Change<ProjectTemplateApplication>(bundle, application =>
        {
            var preview = application.Preview!; preview.Project.BuildingId = Guid.NewGuid();
            preview = preview with { ProjectDigest = ContentDigest.Of(preview.Project), Digest = "" };
            application.Preview = preview with { Digest = ContentDigest.Of(preview) }; application.Digest = application.CalculateDigest();
        }), issue => issue.Message.StartsWith("Missing tenant-qualified buildings reference", StringComparison.Ordinal));
        await Reject("unedited-task-rewritten", Change<TaskItem>(bundle, task => task.Title = "Undocumented first-revision rewrite",
            task => task.TemplateOrigin is not null && task.Revision == 1),
            issue => issue.Message == "An unedited template task must retain the original planned contents and open unassigned state.");
        await Reject("unedited-task-completed", Change<TaskItem>(bundle, task => task.Status = TaskStatus.Done,
            task => task.TemplateOrigin is not null && task.Revision == 1),
            issue => issue.Message == "An unedited template task must retain the original planned contents and open unassigned state.");
    }
}
