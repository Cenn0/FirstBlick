using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class MaintenanceWorkflowTests
{
    private static MaintenanceContract Contract() => new()
    {
        TenantId = Guid.NewGuid(), CustomerId = Guid.NewGuid(), BuildingId = Guid.NewGuid(), Title = "Dachwartung",
        Scope = "Dachflächen und Abläufe prüfen", StartDate = new(2027, 1, 31), NextDueDate = new(2027, 1, 31), IntervalMonths = 1,
        ChecklistTemplate = [new("Abläufe prüfen", "Laub entfernen und Durchfluss kontrollieren"), new("Anschlüsse prüfen")]
    };

    [Fact]
    public void Planning_freezes_contract_scope_and_independent_checklist_for_one_work_project()
    {
        var contract = Contract(); var (project, visit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA-2027-000001");
        contract.Scope = "New future work"; contract.ChecklistTemplate[0] = new("Different future check");
        Assert.Equal(ProjectType.Maintenance, project.Type); Assert.Equal(ProjectStatus.Planning, project.Status);
        Assert.Equal(contract.CustomerId, project.CustomerId); Assert.Equal(contract.BuildingId, project.BuildingId);
        Assert.Equal(project.Id, visit.ProjectId); Assert.Equal(new DateOnly(2027, 1, 31), project.PlannedStart);
        Assert.Equal("Dachflächen und Abläufe prüfen", visit.Terms!.Scope); Assert.Equal("Abläufe prüfen", visit.Checklist[0].Title);
        Assert.Equal(2, visit.Checklist.Count); Assert.All(visit.Checklist, x => Assert.Equal(MaintenanceCheckState.Pending, x.State)); visit.Validate();
    }

    [Theory]
    [InlineData(false, false, "stale_occurrence")]
    [InlineData(true, true, "stale_occurrence")]
    public void Inactive_or_archived_contracts_are_not_planned(bool active, bool archived, string code)
    {
        var contract = Contract(); contract.Active = active; contract.Archived = archived;
        Assert.Equal(code, Assert.Throws<DomainException>(() => MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA")).Code);
    }

    [Fact]
    public void Explicit_checklist_and_current_due_date_are_required()
    {
        var contract = Contract();
        Assert.Equal("stale_occurrence", Assert.Throws<DomainException>(() => MaintenanceWorkflow.Plan(contract, contract.NextDueDate.AddDays(1), "WA")).Code);
        contract.ChecklistTemplate.Clear();
        Assert.Equal("invalid_collection", Assert.Throws<DomainException>(() => MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA")).Code);
        contract.AutomaticPlanning = true;
        Assert.Equal("invalid_collection", Assert.Throws<DomainException>(contract.Validate).Code);
    }

    [Fact]
    public void Checklist_results_preserve_scope_and_reopening_removes_claimed_actor_and_time()
    {
        var contract = Contract(); var (_, visit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA"); var digest = visit.ScopeDigest;
        var actor = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        MaintenanceWorkflow.Record(visit, visit.Checklist[0].Id, MaintenanceCheckState.Done, "Laub entfernt", actor, now);
        Assert.Equal(digest, visit.ScopeDigest); Assert.Equal(actor, visit.Checklist[0].RecordedBy); Assert.Equal(now, visit.Checklist[0].RecordedAt);
        MaintenanceWorkflow.Record(visit, visit.Checklist[0].Id, MaintenanceCheckState.Pending, "Noch einmal prüfen", actor, now);
        Assert.Null(visit.Checklist[0].RecordedAt); Assert.Null(visit.Checklist[0].RecordedBy); visit.Validate();
    }

    [Fact]
    public void Not_applicable_requires_a_reason_and_foreign_item_is_rejected()
    {
        var contract = Contract(); var (_, visit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA");
        Assert.Equal("required", Assert.Throws<DomainException>(() => MaintenanceWorkflow.Record(visit, visit.Checklist[0].Id,
            MaintenanceCheckState.NotApplicable, "", Guid.NewGuid(), DateTimeOffset.UtcNow)).Code);
        Assert.Equal("not_found", Assert.Throws<DomainException>(() => MaintenanceWorkflow.Record(visit, Guid.NewGuid(),
            MaintenanceCheckState.Done, "", Guid.NewGuid(), DateTimeOffset.UtcNow)).Code);
    }

    [Fact]
    public void Pending_check_prevents_advancement_without_mutating_any_entity()
    {
        var contract = Contract(); var (project, visit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA");
        Assert.Equal("maintenance_incomplete", Assert.Throws<DomainException>(() => MaintenanceWorkflow.Complete(visit, contract, project,
            "Arbeiten abgeschlossen", Guid.NewGuid(), DateTimeOffset.UtcNow)).Code);
        Assert.Equal(0, contract.CompletedOccurrences); Assert.Equal(ProjectStatus.Planning, project.Status); Assert.Equal(MaintenanceVisitStatus.Planned, visit.Status);
    }

    [Fact]
    public void Completion_advances_from_original_month_end_and_seals_every_check()
    {
        var contract = Contract(); var actor = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var (project, visit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA");
        foreach (var item in visit.Checklist) MaintenanceWorkflow.Record(visit, item.Id, MaintenanceCheckState.Done, "Geprüft", actor, now);
        MaintenanceWorkflow.Complete(visit, contract, project, "Dach kontrolliert", actor, now);
        Assert.Equal(new DateOnly(2027, 2, 28), contract.NextDueDate); Assert.Equal(contract.NextDueDate, visit.NextDueDate);
        Assert.Equal(1, contract.CompletedOccurrences); Assert.Equal(ProjectStatus.Completed, project.Status);
        Assert.Equal("maintenance_sealed", Assert.Throws<DomainException>(() => MaintenanceWorkflow.Record(visit, visit.Checklist[0].Id,
            MaintenanceCheckState.Pending, "", actor, now)).Code);
        Assert.Equal("maintenance_sealed", Assert.Throws<DomainException>(() => MaintenanceWorkflow.Complete(visit, contract, project, "Again", actor, now)).Code);
        var (nextProject, nextVisit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA-2");
        foreach (var item in nextVisit.Checklist) MaintenanceWorkflow.Record(nextVisit, item.Id, MaintenanceCheckState.NotApplicable, "Nicht vorhanden", actor, now);
        MaintenanceWorkflow.Complete(nextVisit, contract, nextProject, "Zweite Prüfung", actor, now);
        Assert.Equal(new DateOnly(2027, 3, 31), contract.NextDueDate);
    }

    [Fact]
    public void End_date_stops_auto_recurrence_and_keeps_the_last_next_date_as_evidence()
    {
        var contract = Contract(); contract.EndDate = contract.NextDueDate;
        var (project, visit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA");
        foreach (var item in visit.Checklist) MaintenanceWorkflow.Record(visit, item.Id, MaintenanceCheckState.Done, "", Guid.NewGuid(), DateTimeOffset.UtcNow);
        MaintenanceWorkflow.Complete(visit, contract, project, "Letzter Termin", Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.False(contract.Active); Assert.Equal(new DateOnly(2027, 2, 28), visit.NextDueDate);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("checklist")]
    [InlineData("project")]
    public void Tampering_with_frozen_visit_evidence_is_rejected(string field)
    {
        var contract = Contract(); var (_, visit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA");
        if (field == "scope") visit.Terms = visit.Terms! with { Scope = "Changed scope" };
        if (field == "checklist") visit.Checklist[0].Title = "Changed checklist";
        if (field == "project") visit.ProjectId = Guid.NewGuid();
        Assert.Equal("maintenance_evidence", Assert.Throws<DomainException>(visit.Validate).Code);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("building")]
    [InlineData("type")]
    [InlineData("cancelled")]
    [InlineData("interval")]
    public void Completion_rejects_changed_project_or_contract_context(string field)
    {
        var contract = Contract(); var (project, visit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA");
        foreach (var item in visit.Checklist) MaintenanceWorkflow.Record(visit, item.Id, MaintenanceCheckState.Done, "", Guid.NewGuid(), DateTimeOffset.UtcNow);
        if (field == "tenant") project.TenantId = Guid.NewGuid();
        if (field == "building") project.BuildingId = Guid.NewGuid();
        if (field == "type") project.Type = ProjectType.Repair;
        if (field == "cancelled") project.Status = ProjectStatus.Cancelled;
        if (field == "interval") contract.IntervalMonths = 2;
        Assert.Equal("maintenance_project_mismatch", Assert.Throws<DomainException>(() => MaintenanceWorkflow.Complete(visit, contract, project,
            "Checked", Guid.NewGuid(), DateTimeOffset.UtcNow)).Code);
    }
}
