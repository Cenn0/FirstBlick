namespace FirstBlick.Core.Business;

/// <summary>Explicit business weekdays and excluded dates; no public-holiday or legal-calendar inference.</summary>
public sealed record MaintenanceWorkdayPolicy(IReadOnlyList<DayOfWeek> Weekdays, IReadOnlyList<DateOnly> ExcludedDates);

/// <summary>The workday calculation retained by one occurrence. Contractual recurrence remains separate.</summary>
public sealed record MaintenanceExecutionPlan(DateOnly DueDate, DateOnly ScheduledDate, DateOnly? ContractEndDate,
    MaintenanceWorkdayPolicy Workdays);

public sealed record MaintenancePlanningPreview(Guid TenantId, Guid ContractId, long ContractRevision, DateOnly DueDate,
    DateOnly ScheduledDate, DateOnly? ContractEndDate, MaintenanceWorkdayPolicy? Workdays, Guid? ExistingVisitId,
    string Digest);

public static class MaintenanceWorkdays
{
    public const int MaximumShiftDays = 366;
    public const int MaximumExcludedDates = 366;

    public static void Validate(MaintenanceWorkdayPolicy? policy)
    {
        if (policy is null) return;
        Guard.Require(policy.Weekdays is { Count: >= 1 and <= 7 }
            && policy.Weekdays.All(Enum.IsDefined) && policy.Weekdays.Distinct().Count() == policy.Weekdays.Count,
            "maintenance_workday_policy", "Select one to seven distinct valid workdays.");
        Guard.Require(policy.ExcludedDates is { Count: <= MaximumExcludedDates }
            && policy.ExcludedDates.All(x => x != default) && policy.ExcludedDates.Distinct().Count() == policy.ExcludedDates.Count,
            "maintenance_workday_policy", "Excluded dates must be valid, distinct and limited to 366 dates.");
    }

    public static MaintenanceWorkdayPolicy? Copy(MaintenanceWorkdayPolicy? policy)
    {
        Validate(policy);
        return policy is null ? null : new(policy.Weekdays.OrderBy(x => (int)x).ToArray(), policy.ExcludedDates.OrderBy(x => x).ToArray());
    }

    public static DateOnly ScheduledDate(DateOnly dueDate, DateOnly? endDate, MaintenanceWorkdayPolicy? policy)
    {
        Validate(policy);
        Guard.Require(dueDate != default && (!endDate.HasValue || endDate >= dueDate),
            "maintenance_workday_window", "No execution day is available within the contract dates.");
        if (policy is null) return dueDate;
        var weekdays = policy.Weekdays.ToHashSet(); var excluded = policy.ExcludedDates.ToHashSet();
        var lastDay = Math.Min(DateOnly.MaxValue.DayNumber, dueDate.DayNumber + MaximumShiftDays);
        if (endDate.HasValue) lastDay = Math.Min(lastDay, endDate.Value.DayNumber);
        for (var day = dueDate.DayNumber; day <= lastDay; day++)
        {
            var date = DateOnly.FromDayNumber(day);
            if (weekdays.Contains(date.DayOfWeek) && !excluded.Contains(date)) return date;
        }
        throw new DomainException("maintenance_workday_window", "No allowed execution day exists within the contract and the 366-day search limit.");
    }

    public static MaintenanceExecutionPlan? Plan(MaintenanceContract contract)
    {
        var policy = Copy(contract.Workdays);
        return policy is null ? null : new(contract.NextDueDate,
            ScheduledDate(contract.NextDueDate, contract.EndDate, policy), contract.EndDate, policy);
    }

    public static void Validate(MaintenanceExecutionPlan? plan, DateOnly dueDate)
    {
        if (plan is null) return;
        Guard.Require(plan.Workdays is not null && plan.DueDate == dueDate,
            "maintenance_workday_evidence", "The retained workday calculation must belong to this occurrence.");
        Guard.Require(plan.ScheduledDate == ScheduledDate(dueDate, plan.ContractEndDate, plan.Workdays),
            "maintenance_workday_evidence", "The execution day does not match the retained workday calculation.");
    }

    public static MaintenancePlanningPreview Preview(MaintenanceContract contract, MaintenanceVisit? existing = null)
    {
        contract.Validate();
        Guard.Require(contract.Active && !contract.Archived, "stale_occurrence", "The contract is inactive.");
        Guard.Require(!contract.EndDate.HasValue || contract.NextDueDate <= contract.EndDate,
            "contract_expired", "The maintenance contract has ended.");
        if (existing is not null)
        {
            existing.Validate();
            Guard.Require(existing.TenantId == contract.TenantId && existing.ContractId == contract.Id
                && existing.DueDate == contract.NextDueDate && !existing.Archived,
                "maintenance_evidence", "The existing occurrence does not match the contract.");
        }
        var plan = existing is null ? Plan(contract) : existing.ExecutionPlan;
        var view = new MaintenancePlanningPreview(contract.TenantId, contract.Id, contract.Revision, contract.NextDueDate,
            plan?.ScheduledDate ?? contract.NextDueDate, plan?.ContractEndDate ?? (existing is null ? contract.EndDate : null),
            Copy(plan?.Workdays), existing?.Id, "");
        return view with { Digest = ContentDigest.Of(view) };
    }
}
