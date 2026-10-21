using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Documents;
using FirstBlick.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record DiaryReviewRequest(bool Approve, string Digest, string Notes = "");

public static class DiaryReportEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/diary-entries/{id:guid}/submit", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("diary-entries", "diary.submit", body, async ct =>
            {
                var entry = await c.Current<DiaryEntry>(id, ct);
                await RecordPolicy.EnsureWrite(entry, c.Scope, c.Store, c.Db, ct);
                var project = await c.Store.Get<Project>(entry.ProjectId, ct);
                var employee = await c.Store.Get<Employee>(entry.EmployeeId, ct);
                var building = await c.Store.Get<Building>(project.BuildingId, ct);
                var name = await c.Db.Tenants.Where(t => t.Id == c.Scope.TenantId).Select(t => t.Name).SingleAsync(ct);
                DiaryWorkflow.Submit(entry, project, building, employee, name, c.Scope.UserId, DateTimeOffset.UtcNow);
                await c.Store.Save(entry, "diary.submit", false, ct); return entry;
            })).Produces<DiaryEntry>().WithTags("diary-entries");

        api.MapPost("/diary-entries/{id:guid}/review", (Guid id, DiaryReviewRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("diary-entries", "diary.review", body, async ct =>
            {
                Guard.Require(c.Scope.Role is BusinessRole.ManagingDirector or BusinessRole.SiteManager,
                    "forbidden", "Diary review requires site management or a managing director.");
                var entry = await c.Current<DiaryEntry>(id, ct);
                var reviewer = await c.Db.Users.Where(u => u.Id == c.Scope.UserId).Select(u => u.DisplayName).SingleAsync(ct);
                // Also check today's personal account mapping, since it may have changed after submission.
                Guard.Require((await c.Store.Get<Employee>(entry.EmployeeId, ct)).UserId != c.Scope.UserId,
                    "self_review", "An employee cannot approve their own report.");
                DiaryWorkflow.Review(entry, body.Approve, body.Digest, c.Scope.UserId, reviewer, body.Notes, DateTimeOffset.UtcNow);
                await c.Store.Save(entry, body.Approve ? "diary.approve" : "diary.return", false, ct); return entry;
            })).Produces<DiaryEntry>().WithTags("diary-entries");

        api.MapPost("/diary-entries/{id:guid}/pdf", (Guid id, EmptyBusinessCommand body, IObjectStore objects, [AsParameters] BusinessCommandServices c) =>
            c.Execute("diary-entries", "diary.pdf", body, async ct =>
            {
                var entry = await c.Current<DiaryEntry>(id, ct);
                var digest = DiaryWorkflow.ReportDigest(entry);
                return await DocumentEndpoints.SaveGenerated(c.Db, c.Scope, objects, BusinessPdf.Diary(entry),
                    $"Tagesrapport-{entry.Date:yyyy-MM-dd}-{id:N}.pdf", entry.ProjectId, "diary-entries", id, digest, ct);
            })).WithTags("diary-entries");
    }
}
