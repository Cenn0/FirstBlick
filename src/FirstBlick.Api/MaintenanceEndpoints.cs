using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure;
using FirstBlick.Infrastructure.Persistence;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record MaintenanceConfigurationRequest(bool Active, bool AutomaticPlanning, int PlanningLeadDays, List<MaintenanceCheckTemplate> ChecklistTemplate,
    MaintenanceWorkdayPolicy? Workdays = null, bool UpdateWorkdays = false);
public sealed record MaintenancePlanRequest(DateOnly ExpectedDueDate, string? ReviewedPreviewDigest = null);
public sealed record MaintenanceCheckRequest(Guid CheckId, MaintenanceCheckState State, string Note);
public sealed record MaintenanceVisitCompletionRequest(string Notes);

public static class MaintenanceEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/maintenance-contracts/{id:guid}/configure", (Guid id, MaintenanceConfigurationRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("maintenance-contracts", "maintenance.configure", body, async ct =>
            {
                var contract = await c.Current<MaintenanceContract>(id, ct);
                MaintenanceWorkflow.ValidateTemplate(body.ChecklistTemplate, true);
                contract.Active = body.Active; contract.AutomaticPlanning = body.AutomaticPlanning;
                contract.PlanningLeadDays = body.PlanningLeadDays; contract.ChecklistTemplate = body.ChecklistTemplate.ToList();
                Guard.Require(body.UpdateWorkdays || body.Workdays is null, "maintenance_workdays_command_required", "Workday changes require explicit confirmation.");
                if (body.UpdateWorkdays) contract.Workdays = MaintenanceWorkdays.Copy(body.Workdays);
                await GraphRules.Validate(contract, c.Store, c.Db, c.Scope, ct);
                await c.Store.Save(contract, "maintenance.configure", false, ct); return contract;
            })).Produces<MaintenanceContract>();
        api.MapGet("/maintenance-contracts/{id:guid}/planning-preview", async (Guid id, long revision,
            MaintenancePlanner planner, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
            {
                await c.Commands.Authorize("maintenance-contracts", false, ct);
                var contract = await c.Current<MaintenanceContract>(id, ct, false);
                Guard.Require(revision > 0 && contract.Revision == revision, "revision_conflict", "The contract changed. Open its current revision before previewing.");
                c.Http.Response.Headers.CacheControl = "private, no-store";
                c.Http.Response.Headers.ETag = $"\"{contract.Revision}\"";
                return Results.Ok(await planner.Preview(contract, ct));
            }).Produces<MaintenancePlanningPreview>();
        api.MapPost("/maintenance-contracts/{id:guid}/schedule", (Guid id, MaintenancePlanRequest body,
            MaintenancePlanner planner, [AsParameters] BusinessCommandServices c) =>
            c.Commands.Execute(c.Http, "maintenance-contracts", "maintenance.schedule", body, async ct =>
            {
                var contract = await c.Current<MaintenanceContract>(id, ct);
                var preview = await planner.Preview(contract, ct);
                if (contract.Workdays is not null || preview.Workdays is not null || body.ReviewedPreviewDigest is not null)
                    Guard.Require(body.ReviewedPreviewDigest == preview.Digest,
                        "maintenance_workday_preview", "Review the current execution-day preview before planning this occurrence.");
                return await planner.EnsurePlanned(contract, body.ExpectedDueDate, ct);
            }, ct => c.Commands.Authorize("projects", true, ct))).Produces<MaintenanceVisit>();

        api.MapGet("/maintenance-visits", async (Guid? contractId, int? page, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await c.Commands.Authorize("maintenance-visits", false, ct);
            var query = c.Db.Entities.AsNoTracking().Where(x => x.Kind == "maintenance-visits" && !x.Archived);
            if (contractId.HasValue)
            {
                // Field workers do not receive the contract itself; their current project assignments still constrain these results.
                var links = c.Db.Links.Where(x => x.SourceKind == "maintenance-visits" && x.TargetKind == "maintenance-contracts" && x.TargetId == contractId.Value);
                query = query.Where(x => links.Any(l => l.SourceId == x.Id));
            }
            var visible = await RecordPolicy.VisibleIds("maintenance-visits", c.Scope, c.Store, ct);
            if (visible is not null) query = query.Where(x => visible.Contains(x.Id));
            var currentPage = Math.Clamp(page ?? 1, 1, 100_000); const int pageSize = 25;
            var total = await query.CountAsync(ct);
            var rows = await query.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id).Skip((currentPage - 1) * pageSize).Take(pageSize).Select(x => x.Data).ToListAsync(ct);
            var items = rows.Select(AppJson.Read<MaintenanceVisit>).Select(v => new { v.Id, v.Revision, v.ContractId, v.ProjectId,
                v.DueDate, scheduledDate = v.ExecutionPlan?.ScheduledDate ?? v.DueDate, title = v.Terms!.Title, v.Status, completedChecks = v.Checklist.Count(x => x.State != MaintenanceCheckState.Pending),
                totalChecks = v.Checklist.Count, v.CompletedAt, v.NextDueDate });
            return Results.Ok(new { items, total, page = currentPage, pageSize });
        });
        api.MapGet("/maintenance-visits/{id:guid}", async (Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await c.Commands.Authorize("maintenance-visits", false, ct);
            var visit = await c.Current<MaintenanceVisit>(id, ct, false); visit.Validate();
            c.Http.Response.Headers.ETag = $"\"{visit.Revision}\""; return Results.Ok(visit);
        }).Produces<MaintenanceVisit>();
        api.MapGet("/maintenance-visits/{id:guid}/calendar", async (Guid id, long revision, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await c.Commands.Authorize("maintenance-visits", false, ct);
            var visit = await c.Current<MaintenanceVisit>(id, ct, false);
            Guard.Require(revision > 0 && visit.Revision == revision, "revision_conflict", "The maintenance occurrence changed. Open its current revision before exporting.");
            var bytes = MaintenanceCalendar.Export(visit);
            c.Http.Response.Headers.CacheControl = "private, no-store";
            c.Http.Response.Headers.ETag = $"\"{visit.Revision}\"";
            c.Http.Response.Headers["X-Content-SHA256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return Results.File(bytes, MaintenanceCalendar.ContentType, MaintenanceCalendar.FileName(visit));
        }).Produces(StatusCodes.Status200OK, contentType: "text/calendar");
        api.MapPost("/maintenance-visits/{id:guid}/check", (Guid id, MaintenanceCheckRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Commands.Execute(c.Http, "maintenance-visits", "maintenance.check", body, async ct =>
            {
                var visit = await c.Current<MaintenanceVisit>(id, ct);
                MaintenanceWorkflow.Record(visit, body.CheckId, body.State, body.Note, c.Scope.UserId, DateTimeOffset.UtcNow);
                await c.Store.Save(visit, "maintenance.check", false, ct); return visit;
            }, ct => RecordPolicy.EnsureVisible("maintenance-visits", id, c.Scope, c.Store, ct))).Produces<MaintenanceVisit>();
        api.MapPost("/maintenance-visits/{id:guid}/complete", (Guid id, MaintenanceVisitCompletionRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Commands.Execute(c.Http, "maintenance-visits", "maintenance.visit_complete", body, async ct =>
            {
                var visit = await c.Current<MaintenanceVisit>(id, ct);
                var contract = await c.Current<MaintenanceContract>(visit.ContractId, ct, false);
                var project = await c.Current<Project>(visit.ProjectId, ct, false);
                var key = "maintenance-completion:" + project.Id.ToString("N");
                Guard.Require(!await c.Db.Receipts.AnyAsync(x => x.ActorId == Guid.Empty && x.Key == key, ct), "maintenance_project_reused", "This project has already completed an occurrence.");
                MaintenanceWorkflow.Complete(visit, contract, project, body.Notes, c.Scope.UserId, DateTimeOffset.UtcNow);
                var evidence = new { contractId = contract.Id, projectId = project.Id, visitId = visit.Id, dueDate = visit.DueDate,
                    completedBy = visit.CompletedBy, completedAt = visit.CompletedAt, scopeDigest = visit.ScopeDigest, nextDueDate = visit.NextDueDate };
                c.Db.Receipts.Add(new CommandReceipt { TenantId = c.Scope.TenantId, ActorId = Guid.Empty, Key = key,
                    RequestDigest = ContentDigest.Of(evidence), Response = AppJson.Serialize(evidence), CreatedAt = visit.CompletedAt!.Value });
                await c.Store.Save(project, "maintenance.project_complete", false, ct);
                await c.Store.Save(contract, "maintenance.complete", false, ct);
                await c.Store.Save(visit, "maintenance.visit_complete", false, ct); return visit;
            }, async ct =>
            {
                await c.Commands.Authorize("maintenance-contracts", true, ct);
                await c.Commands.Authorize("projects", true, ct);
            })).Produces<MaintenanceVisit>();
    }
}
