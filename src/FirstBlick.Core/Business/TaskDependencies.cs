namespace FirstBlick.Core.Business;

/// <summary>Finish-to-start dependencies. Dates are descriptive and are never rescheduled implicitly.</summary>
public static class TaskDependencies
{
    public const int MaximumPredecessors = 16;
    public const int MaximumTasks = 1000;

    public static void ValidateShape(TaskItem task)
    {
        var predecessors = task.PredecessorTaskIds;
        Guard.Require(predecessors is null || predecessors.Count <= MaximumPredecessors,
            "task_dependency_limit", "Eine Aufgabe kann höchstens 16 unmittelbare Vorgänger haben.");
        Guard.Require(predecessors is null || (predecessors.All(x => x != Guid.Empty && x != task.Id)
            && predecessors.Distinct().Count() == predecessors.Count),
            "task_dependency_invalid", "Vorgänger müssen eindeutige andere Aufgaben sein.");
    }

    public static void PreserveGenericEdit(TaskItem previous, TaskItem next)
    {
        // Older generic editors do not know this field. An omitted value retains the current edges.
        if (next.PredecessorTaskIds is null) next.PredecessorTaskIds = previous.PredecessorTaskIds?.ToList();
        else Guard.Require(next.PredecessorTaskIds.SequenceEqual(previous.PredecessorTaskIds ?? []),
            "task_dependency_command_required", "Aufgabenabhängigkeiten werden im eigenen Arbeitsbereich geändert.");
        if (previous.PredecessorTaskIds is null && next.PredecessorTaskIds?.Count == 0) next.PredecessorTaskIds = null;
    }

    public static void Replace(TaskItem task, IReadOnlyList<Guid> predecessors)
    {
        Guard.Require(!task.Archived && task.Status == TaskStatus.Open,
            "task_dependency_locked", "Abhängigkeiten können nur bei einer offenen Aufgabe geändert werden.");
        Guard.Require(predecessors is not null, "task_dependency_invalid", "Die Vorgängerliste ist erforderlich.");
        task.PredecessorTaskIds = predecessors.Order().ToList();
        ValidateShape(task);
    }

    public static void ValidateGraph(IReadOnlyList<TaskItem> tasks)
    {
        Guard.Require(tasks.Count <= MaximumTasks, "task_graph_limit", "Die Abhängigkeitsprüfung unterstützt höchstens 1.000 Aufgaben je Projekt.");
        Guard.Require(tasks.Select(x => x.Id).Distinct().Count() == tasks.Count,
            "task_dependency_invalid", "Der Aufgabengraph enthält doppelte Kennungen.");
        if (tasks.Count == 0) return;
        var tenant = tasks[0].TenantId; var project = tasks[0].ProjectId;
        Guard.Require(tasks.All(x => x.TenantId == tenant && x.ProjectId == project),
            "task_dependency_project", "Alle Aufgaben einer Abhängigkeit müssen zu demselben Betrieb und Projekt gehören.");
        var byId = tasks.ToDictionary(x => x.Id);
        var remaining = new Dictionary<Guid, int>();
        var following = tasks.ToDictionary(x => x.Id, _ => new List<Guid>());
        foreach (var task in tasks)
        {
            ValidateShape(task);
            remaining[task.Id] = task.PredecessorTaskIds?.Count ?? 0;
            foreach (var id in task.PredecessorTaskIds ?? [])
            {
                Guard.Require(byId.TryGetValue(id, out var predecessor) && !predecessor.Archived,
                    "task_dependency_reference", "Ein Vorgänger fehlt, ist archiviert oder gehört zu einem anderen Projekt.");
                following[id].Add(task.Id);
                Guard.Require(task.Status is not TaskStatus.InProgress and not TaskStatus.Done || predecessor.Status == TaskStatus.Done,
                    "task_dependency_blocked", "Eine gestartete oder erledigte Aufgabe benötigt vollständig erledigte Vorgänger. Rücknahmen dürfen diese Reihenfolge nicht aufheben.");
            }
        }
        var ready = new Queue<Guid>(remaining.Where(x => x.Value == 0).Select(x => x.Key)); var visited = 0;
        while (ready.TryDequeue(out var id))
        {
            visited++;
            foreach (var next in following[id]) if (--remaining[next] == 0) ready.Enqueue(next);
        }
        Guard.Require(visited == tasks.Count, "task_dependency_cycle", "Aufgabenabhängigkeiten dürfen keinen Kreis bilden.");
    }
}
