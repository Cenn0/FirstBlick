using FirstBlick.Core.Business;
using TaskStatus = FirstBlick.Core.Business.TaskStatus;

namespace FirstBlick.Core.Tests.Business;

public sealed class TaskDependencyTests
{
    private static TaskItem Task(Guid tenant, Guid project, string title) => new() { TenantId = tenant, ProjectId = project, Title = title };
    private static (TaskItem First, TaskItem Second, TaskItem Third) Chain()
    {
        var tenant = Guid.NewGuid(); var project = Guid.NewGuid();
        var first = Task(tenant, project, "Material prüfen"); var second = Task(tenant, project, "Dach schließen"); var third = Task(tenant, project, "Abnehmen");
        second.PredecessorTaskIds = [first.Id]; third.PredecessorTaskIds = [second.Id]; return (first, second, third);
    }
    [Fact]
    public void Valid_chain_can_progress_without_changing_dates_or_assignments()
    {
        var (a, b, c) = Chain(); b.DueDate = new(2026, 10, 30); var due = b.DueDate;
        TaskDependencies.ValidateGraph([a, b, c]); a.Status = TaskStatus.Done; b.Status = TaskStatus.InProgress;
        TaskDependencies.ValidateGraph([a, b, c]); b.Status = TaskStatus.Done; c.Status = TaskStatus.Done;
        TaskDependencies.ValidateGraph([a, b, c]); Assert.Equal(due, b.DueDate); Assert.Null(b.AssignedEmployeeId);
    }
    [Theory]
    [InlineData(TaskStatus.InProgress)]
    [InlineData(TaskStatus.Done)]
    public void Started_and_completed_tasks_require_done_predecessors(TaskStatus status)
    {
        var (a, b, c) = Chain(); b.Status = status;
        Assert.Equal("task_dependency_blocked", Assert.Throws<DomainException>(() => TaskDependencies.ValidateGraph([a, b, c])).Code);
        a.Status = TaskStatus.Cancelled;
        Assert.Equal("task_dependency_blocked", Assert.Throws<DomainException>(() => TaskDependencies.ValidateGraph([a, b, c])).Code);
    }
    [Fact]
    public void Reopening_a_predecessor_of_a_completed_task_invalidates_the_graph()
    {
        var (a, b, c) = Chain(); a.Status = b.Status = c.Status = TaskStatus.Done; TaskDependencies.ValidateGraph([a, b, c]);
        a.Status = TaskStatus.Open;
        Assert.Equal("task_dependency_blocked", Assert.Throws<DomainException>(() => TaskDependencies.ValidateGraph([a, b, c])).Code);
    }
    [Fact]
    public void Indirect_cycles_are_detected_independent_of_input_order()
    {
        var (a, b, c) = Chain(); a.PredecessorTaskIds = [c.Id];
        Assert.Equal("task_dependency_cycle", Assert.Throws<DomainException>(() => TaskDependencies.ValidateGraph([c, a, b])).Code);
    }
    [Fact]
    public void Missing_archived_and_cross_project_nodes_are_rejected()
    {
        var (a, b, c) = Chain();
        Assert.Equal("task_dependency_reference", Assert.Throws<DomainException>(() => TaskDependencies.ValidateGraph([b, c])).Code);
        a.Archived = true;
        Assert.Equal("task_dependency_reference", Assert.Throws<DomainException>(() => TaskDependencies.ValidateGraph([a, b, c])).Code);
        a.Archived = false; a.ProjectId = Guid.NewGuid();
        Assert.Equal("task_dependency_project", Assert.Throws<DomainException>(() => TaskDependencies.ValidateGraph([a, b, c])).Code);
        a.ProjectId = b.ProjectId; a.TenantId = Guid.NewGuid();
        Assert.Equal("task_dependency_project", Assert.Throws<DomainException>(() => TaskDependencies.ValidateGraph([a, b, c])).Code);
    }
    [Fact]
    public void Self_empty_and_duplicate_predecessors_are_rejected()
    {
        var (a, _, _) = Chain();
        foreach (var ids in new List<Guid>[] { [a.Id], [Guid.Empty], [Guid.Empty, Guid.Empty] })
        {
            a.PredecessorTaskIds = ids; Assert.Equal("task_dependency_invalid", Assert.Throws<DomainException>(() => TaskDependencies.ValidateShape(a)).Code);
        }
        var id = Guid.NewGuid(); a.PredecessorTaskIds = [id, id]; Assert.Throws<DomainException>(() => TaskDependencies.ValidateShape(a));
    }
    [Fact]
    public void Bounds_are_enforced_without_recursive_traversal()
    {
        var (a, _, _) = Chain(); a.PredecessorTaskIds = Enumerable.Range(0, 17).Select(_ => Guid.NewGuid()).ToList();
        Assert.Equal("task_dependency_limit", Assert.Throws<DomainException>(() => TaskDependencies.ValidateShape(a)).Code);
        var chain = Enumerable.Range(0, 1000).Select(index => Task(a.TenantId, a.ProjectId, $"Aufgabe {index}")).ToList();
        for (var i = 1; i < chain.Count; i++) chain[i].PredecessorTaskIds = [chain[i - 1].Id];
        TaskDependencies.ValidateGraph(chain);
        chain.Add(Task(a.TenantId, a.ProjectId, "Zu viel"));
        Assert.Equal("task_graph_limit", Assert.Throws<DomainException>(() => TaskDependencies.ValidateGraph(chain)).Code);
    }
    [Fact]
    public void Older_generic_edits_preserve_edges_and_cannot_replace_or_clear_them()
    {
        var (_, before, _) = Chain(); var next = Task(before.TenantId, before.ProjectId, "Umbenannt");
        TaskDependencies.PreserveGenericEdit(before, next); Assert.Equal(before.PredecessorTaskIds, next.PredecessorTaskIds);
        Assert.NotSame(before.PredecessorTaskIds, next.PredecessorTaskIds);
        next.PredecessorTaskIds = [];
        Assert.Equal("task_dependency_command_required", Assert.Throws<DomainException>(() => TaskDependencies.PreserveGenericEdit(before, next)).Code);
        before.PredecessorTaskIds = null; TaskDependencies.PreserveGenericEdit(before, next); Assert.Null(next.PredecessorTaskIds);
    }
    [Theory]
    [InlineData(TaskStatus.InProgress)]
    [InlineData(TaskStatus.Done)]
    [InlineData(TaskStatus.Cancelled)]
    public void Only_open_tasks_can_change_their_predecessor_list(TaskStatus status)
    {
        var (a, b, _) = Chain(); b.Status = status;
        Assert.Equal("task_dependency_locked", Assert.Throws<DomainException>(() => TaskDependencies.Replace(b, [a.Id])).Code);
    }
    [Fact]
    public void Task_references_include_predecessors_for_tenant_qualified_foreign_keys_and_archive_protection()
    {
        var (a, b, _) = Chain();
        Assert.Contains(new EntityReference("tasks", a.Id), b.References());
        Assert.Contains(new EntityReference("projects", b.ProjectId), b.References());
    }
}
