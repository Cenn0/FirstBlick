using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using TaskStatus = FirstBlick.Core.Business.TaskStatus;

namespace FirstBlick.Operations;

internal static partial class MigrationDrill
{
    private const string DependencyPreparationTitle = "Historical dependency preparation";
    private const string DependencyExecutionTitle = "Historical dependency execution";
    private const string DependencyAcceptanceTitle = "Historical dependency acceptance";

    private static void SeedTaskDependencyHistory(Project project, DateTimeOffset at, List<TenantEntity> entities)
    {
        var preparation = new TaskItem { TenantId = project.TenantId, ProjectId = project.Id,
            Title = DependencyPreparationTitle, Status = TaskStatus.Done, PredecessorTaskIds = [],
            Revision = 2, CreatedAt = at, UpdatedAt = at.AddSeconds(1) };
        var execution = new TaskItem { TenantId = project.TenantId, ProjectId = project.Id,
            Title = DependencyExecutionTitle, Status = TaskStatus.Done, PredecessorTaskIds = [preparation.Id],
            Revision = 3, CreatedAt = at, UpdatedAt = at.AddSeconds(2) };
        var acceptance = new TaskItem { TenantId = project.TenantId, ProjectId = project.Id,
            Title = DependencyAcceptanceTitle, Status = TaskStatus.Open, PredecessorTaskIds = [execution.Id],
            CreatedAt = at, UpdatedAt = at };
        entities.AddRange([preparation, execution, acceptance]);
    }

    private static void VerifyTaskDependencyHistory(IEnumerable<TenantEntity> source)
    {
        var tasks = source.OfType<TaskItem>().Where(task => task.Title is DependencyPreparationTitle
            or DependencyExecutionTitle or DependencyAcceptanceTitle).ToArray();
        Require.That(tasks.Length == 3 && tasks.Select(task => task.Title).Distinct().Count() == 3,
            "Migration lost the three retained task-dependency identities.");
        var preparation = tasks.Single(task => task.Title == DependencyPreparationTitle);
        var execution = tasks.Single(task => task.Title == DependencyExecutionTitle);
        var acceptance = tasks.Single(task => task.Title == DependencyAcceptanceTitle);
        Require.That(tasks.All(task => !task.Archived && task.TemplateOrigin is null)
            && preparation.Status == TaskStatus.Done && preparation.Revision == 2
            && execution.Status == TaskStatus.Done && execution.Revision == 3
            && acceptance.Status == TaskStatus.Open && acceptance.Revision == 1,
            "Migration changed retained task status, revision or independent provenance.");
        Require.That(preparation.PredecessorTaskIds is { Count: 0 }
            && execution.PredecessorTaskIds is { Count: 1 } && execution.PredecessorTaskIds[0] == preparation.Id
            && acceptance.PredecessorTaskIds is { Count: 1 } && acceptance.PredecessorTaskIds[0] == execution.Id,
            "Migration changed the retained preparation-to-execution-to-acceptance dependency chain.");
        TaskDependencies.ValidateGraph(tasks);
    }

    private static async Task RejectTaskDependencySources(MigrationBundle bundle, IEnumerable<TenantEntity> source,
        Func<string, MigrationBundle, Func<MigrationIssue, bool>?, Task> reject)
    {
        var entities = source.ToArray();
        var preparation = entities.OfType<TaskItem>().Single(task => task.Title == DependencyPreparationTitle);
        var execution = entities.OfType<TaskItem>().Single(task => task.Title == DependencyExecutionTitle);
        var acceptance = entities.OfType<TaskItem>().Single(task => task.Title == DependencyAcceptanceTitle);
        Task Reject(string name, MigrationBundle altered, string code) => reject("task-dependency-" + name, altered,
            issue => issue.Code == code && issue.Location.StartsWith("relations/tasks/", StringComparison.Ordinal));

        // All three statuses must be open here so the cycle itself, rather than a
        // completed successor's unmet predecessor, is the reason for rejection.
        var cycle = Change<TaskItem>(bundle, task =>
        { task.Status = TaskStatus.Open; task.PredecessorTaskIds = [acceptance.Id]; }, task => task.Id == preparation.Id);
        cycle = Change<TaskItem>(cycle, task => task.Status = TaskStatus.Open, task => task.Id == execution.Id);
        await Reject("cycle", cycle, "task_dependency_cycle");

        // Supply a real, valid second project only in this negative bundle. An
        // ordinary missing-reference error must not mask the same-project rule.
        var foreignProject = AppJson.Read<Project>(AppJson.Serialize(entities.OfType<Project>().Single(project => project.Id == preparation.ProjectId)));
        foreignProject.Id = Guid.NewGuid(); foreignProject.Name = "Synthetic foreign dependency project"; foreignProject.Number = "";
        var foreignData = MigrationJson.Element(foreignProject);
        var foreignEnvelope = new MigrationEntity("task-dependency-project-" + foreignProject.Id.ToString("N"), "projects",
            foreignData, MigrationJson.Hash(foreignData), MigrationEvidence.NotApplicable, MigrationEvidence.NotApplicable, null);
        var foreign = Controls(bundle with { Entities = [.. bundle.Entities, foreignEnvelope] });
        foreign = Change<TaskItem>(foreign, task => task.ProjectId = foreignProject.Id, task => task.Id == acceptance.Id);
        await Reject("foreign-project", foreign, "task_dependency_reference");

        await Reject("reopened-predecessor", Change<TaskItem>(bundle, task => task.Status = TaskStatus.Open,
            task => task.Id == preparation.Id), "task_dependency_blocked");
        await Reject("archived-predecessor", Change<TaskItem>(bundle, task => task.Archived = true,
            task => task.Id == preparation.Id), "task_dependency_reference");
    }
}
