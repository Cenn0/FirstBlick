using System.Data;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FirstBlick.Infrastructure;

/// <summary>Called only while holding the tenant transaction lock. Project, frozen checklist, audit and outbox commit together.</summary>
public sealed class MaintenancePlanner(FirstBlickDbContext db, TenantScope scope, EntityStore store, ILogger<MaintenancePlanner> logger)
{
    public async Task<MaintenancePlanningPreview> Preview(MaintenanceContract contract, CancellationToken ct = default)
    {
        Guard.Require(contract.TenantId == scope.TenantId, "not_found", "The contract belongs to another business.");
        return MaintenanceWorkdays.Preview(contract, await Existing(contract, contract.NextDueDate, ct));
    }

    public async Task<MaintenanceVisit> EnsurePlanned(MaintenanceContract contract, DateOnly expectedDueDate, CancellationToken ct = default)
    {
        Guard.Require(contract.TenantId == scope.TenantId, "not_found", "The contract belongs to another business.");
        contract.Validate();
        Guard.Require(contract.Active && !contract.Archived && contract.NextDueDate == expectedDueDate, "stale_occurrence", "The contract is inactive or its current occurrence has changed.");
        Guard.Require(!contract.EndDate.HasValue || expectedDueDate <= contract.EndDate, "contract_expired", "The maintenance contract has ended.");
        var existing = await Existing(contract, expectedDueDate, ct);
        if (existing is not null) return existing;
        var building = await store.Get<Building>(contract.BuildingId, ct); var customer = await store.Get<Customer>(contract.CustomerId, ct);
        Guard.Require(!building.Archived && !customer.Archived && building.CustomerId == contract.CustomerId,
            "maintenance_project_mismatch", "The contract requires an available matching customer and building.");
        var (project, planned) = MaintenanceWorkflow.Plan(contract, expectedDueDate, await store.NextNumber("WA", expectedDueDate, ct));
        await store.Save(project, "maintenance.project_planned", true, ct);
        await store.Save(planned, "maintenance.visit_planned", true, ct);
        return planned;
    }

    private async Task<MaintenanceVisit?> Existing(MaintenanceContract contract, DateOnly dueDate, CancellationToken ct)
    {
        var key = $"contract:{contract.Id:N}:due:{dueDate:yyyy-MM-dd}";
        var row = await db.Entities.AsNoTracking().SingleOrDefaultAsync(x => x.Kind == "maintenance-visits" && x.UniqueKey == key, ct);
        if (row is null) return null;
        var visit = AppJson.Read<MaintenanceVisit>(row.Data); visit.Validate();
        Guard.Require(!row.Archived && !visit.Archived && row.Id == visit.Id && row.Revision == visit.Revision
            && visit.TenantId == scope.TenantId && visit.ContractId == contract.Id && visit.DueDate == dueDate,
            "maintenance_evidence", "The existing occurrence metadata does not match its stored evidence.");
        return visit;
    }

    /// <summary>Opt-in scheduling creates only the current occurrence per contract. No invoice, calendar invitation or provider message is sent.</summary>
    public async Task<int> ScheduleDue(DateTimeOffset utcNow, CancellationToken ct = default)
    {
        Guard.Utc(utcNow, nameof(utcNow));
        var count = 0; var offset = 0;
        while (true)
        {
            var batch = await db.Entities.IgnoreQueryFilters().AsNoTracking().Where(x => x.Kind == "maintenance-contracts" && !x.Archived)
                .OrderBy(x => x.TenantId).ThenBy(x => x.Id).Skip(offset).Take(100).ToListAsync(ct);
            if (batch.Count == 0) break;
            offset += batch.Count;
            foreach (var candidate in batch)
            {
                try
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({candidate.TenantId.ToString()}, 0))", ct);
                    if (await RecoveryBarrier.IsHeld(db, candidate.TenantId, ct)) continue;
                    scope.TenantId = candidate.TenantId; scope.UserId = Guid.Empty; scope.Role = BusinessRole.ManagingDirector;
                    var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidate.TenantId, ct);
                    if (tenant is null || !tenant.Modules.Contains("maintenance", StringComparer.Ordinal) || !tenant.Modules.Contains("core", StringComparer.Ordinal)) continue;
                    var current = await db.Entities.AsNoTracking().SingleOrDefaultAsync(x => x.Kind == "maintenance-contracts" && x.Id == candidate.Id && !x.Archived, ct);
                    if (current is null) continue;
                    var contract = AppJson.Read<MaintenanceContract>(current.Data); contract.Validate();
                    if (contract.Id != current.Id || contract.Revision != current.Revision || contract.TenantId != scope.TenantId || contract.Archived
                        || !contract.AutomaticPlanning || !contract.Active || contract.EndDate.HasValue && contract.NextDueDate > contract.EndDate) continue;
                    var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone)).DateTime);
                    if (contract.NextDueDate.DayNumber - today.DayNumber > contract.PlanningLeadDays) continue;
                    var key = $"contract:{contract.Id:N}:due:{contract.NextDueDate:yyyy-MM-dd}";
                    if (await db.Entities.AnyAsync(x => x.Kind == "maintenance-visits" && x.UniqueKey == key, ct)) continue;
                    await EnsurePlanned(contract, contract.NextDueDate, ct);
                    await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); count++;
                }
                catch (Exception ex) when (ex is DomainException or JsonException or TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    // A malformed or unavailable source must not starve other contracts. No source content is written to logs.
                    logger.LogWarning("Maintenance planning skipped an invalid contract {ContractId}", candidate.Id);
                }
                finally { db.ChangeTracker.Clear(); }
            }
        }
        return count;
    }
}
