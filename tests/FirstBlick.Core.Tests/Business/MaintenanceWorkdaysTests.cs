using System.Text.Json;
using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class MaintenanceWorkdaysTests
{
    private static MaintenanceWorkdayPolicy Weekdays(params DateOnly[] excluded) => new(
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], excluded);
    private static MaintenanceContract Contract(DateOnly? dueDate = null) => new()
    {
        TenantId = Guid.NewGuid(), CustomerId = Guid.NewGuid(), BuildingId = Guid.NewGuid(), Title = "Wartung",
        Scope = "Dach und Abläufe prüfen", StartDate = dueDate ?? new(2027, 1, 31), NextDueDate = dueDate ?? new(2027, 1, 31),
        IntervalMonths = 1, ChecklistTemplate = [new("Abläufe prüfen")]
    };

    [Fact]
    public void Legacy_occurrence_keeps_original_date_scope_digest_and_absent_fields()
    {
        var contract = Contract(); var (project, visit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA");
        var oldDigest = ContentDigest.Of(new
        {
            visit.ContractId, visit.ContractRevision, visit.ProjectId, visit.DueDate, visit.Terms,
            checklist = visit.Checklist.Select(x => new { x.Id, x.Title, x.Instructions }).ToArray()
        });
        Assert.Equal(oldDigest, visit.ScopeDigest); Assert.Null(visit.ExecutionPlan); Assert.Equal(visit.DueDate, project.PlannedStart);
        var json = JsonSerializer.Serialize(visit); Assert.DoesNotContain("ExecutionPlan", json);
        var retained = JsonSerializer.Deserialize<MaintenanceVisit>(json)!; retained.Validate(); Assert.Equal(oldDigest, retained.ScopeDigest);
        Assert.DoesNotContain("Workdays", JsonSerializer.Serialize(contract));
    }

    [Fact]
    public void Weekend_and_explicit_exclusion_shift_execution_but_not_the_recurrence_anchor()
    {
        var contract = Contract(); contract.Workdays = Weekdays(new DateOnly(2027, 2, 1));
        var (project, visit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA");
        Assert.Equal(new DateOnly(2027, 1, 31), visit.DueDate); Assert.Equal(new DateOnly(2027, 2, 2), visit.ExecutionPlan!.ScheduledDate);
        Assert.Equal(visit.ExecutionPlan.ScheduledDate, project.PlannedStart); Assert.Equal(project.PlannedStart, project.PlannedEnd);
        var actor = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        MaintenanceWorkflow.Record(visit, visit.Checklist[0].Id, MaintenanceCheckState.Done, "Geprüft", actor, now);
        MaintenanceWorkflow.Complete(visit, contract, project, "Abgeschlossen", actor, now);
        Assert.Equal(new DateOnly(2027, 2, 28), contract.NextDueDate); Assert.Equal(1, contract.CompletedOccurrences);
    }

    [Theory]
    [InlineData(2026, 3, 29, 2026, 3, 30)]
    [InlineData(2026, 10, 25, 2026, 10, 26)]
    [InlineData(2028, 2, 29, 2028, 2, 29)]
    public void Date_only_planning_crosses_dst_and_leap_day_without_utc_conversion(int y, int m, int d, int ey, int em, int ed)
    {
        Assert.Equal(new DateOnly(ey, em, ed), MaintenanceWorkdays.ScheduledDate(new(y, m, d), null, Weekdays()));
    }

    [Fact]
    public void Contract_end_is_an_inclusive_hard_boundary()
    {
        var due = new DateOnly(2027, 1, 31);
        Assert.Equal(new DateOnly(2027, 2, 1), MaintenanceWorkdays.ScheduledDate(due, new(2027, 2, 1), Weekdays()));
        Assert.Equal("maintenance_workday_window", Assert.Throws<DomainException>(() => MaintenanceWorkdays.ScheduledDate(due, due, Weekdays())).Code);
    }

    [Fact]
    public void Search_stops_after_366_days_and_before_the_date_type_overflows()
    {
        var due = new DateOnly(2027, 1, 31);
        var excluded = Enumerable.Range(0, 367).Select(due.AddDays).Where(x => x.DayOfWeek == DayOfWeek.Monday).ToArray();
        Assert.Equal("maintenance_workday_window", Assert.Throws<DomainException>(() =>
            MaintenanceWorkdays.ScheduledDate(due, null, new([DayOfWeek.Monday], excluded))).Code);
        var wrongDay = (DayOfWeek)(((int)DateOnly.MaxValue.DayOfWeek + 1) % 7);
        Assert.Equal("maintenance_workday_window", Assert.Throws<DomainException>(() =>
            MaintenanceWorkdays.ScheduledDate(DateOnly.MaxValue, null, new([wrongDay], []))).Code);
        Assert.Equal(DateOnly.MaxValue, MaintenanceWorkdays.ScheduledDate(DateOnly.MaxValue, null, new([DateOnly.MaxValue.DayOfWeek], [])));
    }

    [Fact]
    public void Invalid_empty_duplicate_or_unbounded_policy_is_rejected()
    {
        var day = new DateOnly(2027, 1, 1);
        MaintenanceWorkdayPolicy[] invalid = [new([], []), new([DayOfWeek.Monday, DayOfWeek.Monday], []),
            new([(DayOfWeek)8], []), new([DayOfWeek.Monday], [default]), new([DayOfWeek.Monday], [day, day]),
            new([DayOfWeek.Monday], Enumerable.Range(0, 367).Select(day.AddDays).ToArray()), new(null!, []), new([DayOfWeek.Monday], null!)];
        foreach (var policy in invalid) Assert.Equal("maintenance_workday_policy", Assert.Throws<DomainException>(() => MaintenanceWorkdays.Validate(policy)).Code);
    }

    [Fact]
    public void Existing_occurrence_retains_original_policy_after_contract_configuration_changes()
    {
        var contract = Contract(); var excluded = new List<DateOnly> { new(2027, 2, 1) };
        contract.Workdays = new([DayOfWeek.Monday, DayOfWeek.Tuesday], excluded);
        var (_, visit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA");
        excluded.Clear(); contract.Workdays = new([DayOfWeek.Friday], []);
        var preview = MaintenanceWorkdays.Preview(contract, visit);
        Assert.Equal(new DateOnly(2027, 2, 2), preview.ScheduledDate); Assert.Equal(visit.Id, preview.ExistingVisitId);
        Assert.Single(visit.ExecutionPlan!.Workdays.ExcludedDates); visit.Validate();
        Assert.Equal(new DateOnly(2027, 2, 5), MaintenanceWorkdays.Preview(contract).ScheduledDate);
    }

    [Fact]
    public void Preview_digest_changes_with_source_revision_and_when_an_occurrence_already_exists()
    {
        var contract = Contract(); contract.Workdays = Weekdays();
        var first = MaintenanceWorkdays.Preview(contract);
        Assert.Equal(first.Digest, MaintenanceWorkdays.Preview(contract).Digest);
        contract.Revision++;
        Assert.NotEqual(first.Digest, MaintenanceWorkdays.Preview(contract).Digest);
        var (_, visit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA");
        Assert.NotEqual(MaintenanceWorkdays.Preview(contract).Digest, MaintenanceWorkdays.Preview(contract, visit).Digest);
    }

    [Fact]
    public void Retained_execution_date_policy_and_digest_cannot_be_changed()
    {
        var contract = Contract(); contract.Workdays = Weekdays();
        var (_, visit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA"); var original = visit.ExecutionPlan!;
        visit.ExecutionPlan = original with { ScheduledDate = original.ScheduledDate.AddDays(1) };
        Assert.Equal("maintenance_workday_evidence", Assert.Throws<DomainException>(visit.Validate).Code);
        visit.ExecutionPlan = original with { Workdays = new([DayOfWeek.Monday], []) };
        Assert.Equal("maintenance_evidence", Assert.Throws<DomainException>(visit.Validate).Code);
        visit.ExecutionPlan = null;
        Assert.Equal("maintenance_evidence", Assert.Throws<DomainException>(visit.Validate).Code);
    }
}
