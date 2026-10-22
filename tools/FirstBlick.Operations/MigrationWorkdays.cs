using FirstBlick.Core.Business;

namespace FirstBlick.Operations;

internal static partial class MigrationDrill
{
    private static void SeedMaintenanceWorkdays(Customer customer, Building building, DateTimeOffset at, List<TenantEntity> entities)
    {
        var contract = new MaintenanceContract
        {
            TenantId = customer.TenantId, CustomerId = customer.Id, BuildingId = building.Id,
            CreatedAt = at, UpdatedAt = at, Title = "Historical workday maintenance", Scope = "Retained roof inspection scope",
            StartDate = new(2027, 1, 31), NextDueDate = new(2027, 1, 31), EndDate = new(2027, 2, 28), IntervalMonths = 1,
            ChecklistTemplate = [new("Retained drain check", "Original work instruction")],
            Workdays = new([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], [new(2027, 2, 1)])
        };
        var (project, visit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "Historical-workday-project");
        project.CreatedAt = project.UpdatedAt = visit.CreatedAt = visit.UpdatedAt = at;
        entities.Add(contract); entities.Add(project); entities.Add(visit);
        // Future scheduling changes do not rewrite the already retained execution plan.
        contract.Workdays = new([DayOfWeek.Friday], []);
        contract.Revision++; contract.UpdatedAt = at.AddSeconds(1);
    }

    private static void VerifyMaintenanceWorkdays(IEnumerable<TenantEntity> source)
    {
        var entities = source.ToArray();
        var visit = entities.OfType<MaintenanceVisit>().Single(x => x.ExecutionPlan is not null); visit.Validate();
        var plan = visit.ExecutionPlan!;
        var contract = entities.OfType<MaintenanceContract>().Single(x => x.TenantId == visit.TenantId && x.Id == visit.ContractId);
        var project = entities.OfType<Project>().Single(x => x.TenantId == visit.TenantId && x.Id == visit.ProjectId);
        Require.That(visit.DueDate == new DateOnly(2027, 1, 31) && plan.ScheduledDate == new DateOnly(2027, 2, 2)
            && project.PlannedStart == plan.ScheduledDate && project.PlannedEnd == project.PlannedStart,
            "Migration changed the retained maintenance due date or separate execution day.");
        Require.That(plan.ContractEndDate == new DateOnly(2027, 2, 28)
            && plan.Workdays.ExcludedDates.SequenceEqual(new[] { new DateOnly(2027, 2, 1) })
            && plan.Workdays.Weekdays.Contains(DayOfWeek.Tuesday)
            && contract.Workdays!.Weekdays.SequenceEqual(new[] { DayOfWeek.Friday })
            && contract.Revision > visit.ContractRevision,
            "Migration replaced the original workday snapshot with the later contract configuration.");
        Require.That(MaintenanceWorkdays.Preview(contract, visit).ScheduledDate == plan.ScheduledDate,
            "A migrated existing occurrence no longer uses its retained execution day.");
    }

    private static async Task RejectMaintenanceWorkdaySources(MigrationBundle bundle, IEnumerable<TenantEntity> source,
        Func<string, MigrationBundle, Func<MigrationIssue, bool>?, Task> reject)
    {
        var entities = source.ToArray();
        Task Reject(string name, MigrationBundle altered, Func<MigrationIssue, bool>? expected = null) =>
            reject("maintenance-workdays-" + name, altered, expected);
        await Reject("wrong-execution-day", Change<MaintenanceVisit>(bundle, visit =>
        {
            visit.ExecutionPlan = visit.ExecutionPlan! with { ScheduledDate = visit.ExecutionPlan!.ScheduledDate.AddDays(1) };
            visit.ScopeDigest = visit.CalculateScopeDigest();
        }, visit => visit.ExecutionPlan is not null),
            issue => issue.Message == "The execution day does not match the retained workday calculation.");
        await Reject("changed-scope-digest", Change<MaintenanceVisit>(bundle, visit => visit.ScopeDigest = new string('0', 64)),
            issue => issue.Message == "The frozen visit scope or checklist has changed.");
        var unrelatedProject = entities.OfType<Project>().First(x => x.Type != ProjectType.Maintenance);
        await Reject("wrong-project", Change<MaintenanceVisit>(bundle, visit =>
        { visit.ProjectId = unrelatedProject.Id; visit.ScopeDigest = visit.CalculateScopeDigest(); }),
            issue => issue.Message == "A maintenance visit must retain its original contract and matching maintenance project.");
        await Reject("wrong-contract-revision", Change<MaintenanceVisit>(bundle, visit =>
        { visit.ContractRevision = long.MaxValue; visit.ScopeDigest = visit.CalculateScopeDigest(); }),
            issue => issue.Message == "A maintenance visit must retain existing source identities and compatible contract history.");
    }
}
