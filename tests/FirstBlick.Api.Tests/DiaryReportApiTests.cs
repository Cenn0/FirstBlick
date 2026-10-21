using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PdfSharp.Pdf.IO;
using static FirstBlick.Api.Tests.PostgresApiFixture;
using static FirstBlick.Api.Tests.ExchangeApiHarness;

namespace FirstBlick.Api.Tests;

public sealed class DiaryReportApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Worker_submit_independent_review_and_download_bind_the_exact_frozen_report_and_party_labels()
    {
        var (worker, manager, project, employee) = await Setup();
        await using var harness = new ExchangeApiHarness(fixture);
        using var author = await harness.Login(worker); using var reviewer = await harness.Login(manager);
        var draft = await Ok<DiaryEntry>(Send(author, HttpMethod.Post, "/api/v1/diary-entries", new
        { projectId = project.Id, employeeId = employee.Id, date = "2026-10-20", work = string.Join('\n', Enumerable.Range(1, 120).Select(n => $"Arbeitsschritt {n}: Dachfläche abgedichtet und Anschlüsse geprüft.")), weather = "Trocken" }));
        Assert.Equal("invalid_transition", await Error(Send(author, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/pdf", new { }, draft.Revision)));
        var key = Guid.NewGuid().ToString("N");
        var submitted = await Ok<DiaryEntry>(Send(author, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/submit", new { }, draft.Revision, key));
        var replay = await Ok<DiaryEntry>(Send(author, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/submit", new { }, draft.Revision, key));
        Assert.Equal(submitted.Revision, replay.Revision);
        Assert.Equal("diary_immutable", await Error(Send(author, HttpMethod.Put, $"/api/v1/diary-entries/{draft.Id}", submitted, submitted.Revision)));
        Assert.Equal("diary_immutable", await Error(Send(reviewer, HttpMethod.Delete, $"/api/v1/diary-entries/{draft.Id}", revision: submitted.Revision)));
        Assert.Equal("forbidden", await Error(Send(author, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/review", new { approve = true, digest = submitted.Submission!.Digest }, submitted.Revision)));
        var reviewKey = Guid.NewGuid().ToString("N");
        var reviewBody = new { approve = true, digest = submitted.Submission!.Digest, notes = "Tagesstand geprüft" };
        var approved = await Ok<DiaryEntry>(Send(reviewer, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/review", reviewBody, submitted.Revision, reviewKey));
        employee.Name = "Changed employee"; project.Name = "Changed project";
        await Ok<Employee>(Send(reviewer, HttpMethod.Put, $"/api/v1/employees/{employee.Id}", employee, employee.Revision));
        await Ok<Project>(Send(reviewer, HttpMethod.Put, $"/api/v1/projects/{project.Id}", project, project.Revision));
        Assert.Equal("Mitarbeiter Original", approved.Submission!.Content.EmployeeName); Assert.Equal("Projekt Original", approved.Submission.Content.ProjectName);
        var pdfKey = Guid.NewGuid().ToString("N");
        var document = await Ok<JsonElement>(Send(author, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/pdf", new { }, approved.Revision, pdfKey));
        var replayedDocument = await Ok<JsonElement>(Send(author, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/pdf", new { }, approved.Revision, pdfKey));
        Assert.Equal(document.GetProperty("id").GetGuid(), replayedDocument.GetProperty("id").GetGuid()); Assert.Equal(1, harness.Store.PutCount);
        var bytes = await Download(author, document);
        using var pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        Assert.True(pdf.PageCount >= 3);
        Assert.Equal("Frozen source SHA-256: " + DiaryWorkflow.ReportDigest(approved), pdf.Info.Subject);
        Assert.Equal(DiaryWorkflow.ReportDigest(approved), document.GetProperty("sourceDigest").GetString());
        var foreignTenant = await fixture.CreateTenant(); var foreignUser = await fixture.CreateUser(foreignTenant.Id);
        using var foreign = await harness.Login(foreignUser);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/v1/documents/{document.GetProperty("id").GetGuid()}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/v1/diary-entries/{draft.Id}")).StatusCode);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(1, await db.Audit.IgnoreQueryFilters().CountAsync(a => a.TenantId == worker.TenantId && a.EntityId == draft.Id && a.Action == "diary.submit"));
            Assert.Equal(1, await db.Audit.IgnoreQueryFilters().CountAsync(a => a.TenantId == worker.TenantId && a.EntityId == draft.Id && a.Action == "diary.approve"));
            var membership = await db.Memberships.IgnoreQueryFilters().SingleAsync(m => m.TenantId == manager.TenantId && m.UserId == manager.UserId);
            membership.Role = BusinessRole.Office; await db.SaveChangesAsync();
        });
        Assert.Equal("forbidden", await Error(Send(reviewer, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/review", reviewBody, submitted.Revision, reviewKey)));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            await db.Entities.IgnoreQueryFilters().Where(e => e.TenantId == worker.TenantId && e.Kind == "schedule-assignments")
                .ExecuteUpdateAsync(update => update.SetProperty(e => e.Archived, true));
        });
        Assert.Equal("not_found", await Error(Send(author, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/pdf", new { }, approved.Revision, pdfKey)));
        Assert.Equal("not_found", await Error(Send(author, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/pdf", new { }, approved.Revision)));
        Assert.Equal(1, harness.Store.PutCount);
    }

    [PostgresApiFact]
    public async Task Returned_report_can_be_corrected_and_resubmitted_but_generic_edits_cannot_forge_review_or_change_its_identity()
    {
        var (worker, manager, project, employee) = await Setup();
        using var author = await fixture.Login(worker); using var reviewer = await fixture.Login(manager);
        var draft = await fixture.Save(new DiaryEntry { TenantId = worker.TenantId, ProjectId = project.Id, EmployeeId = employee.Id, Date = new(2026, 10, 20), Work = "Abdichtung" }, worker.UserId);
        var submitted = await Ok<DiaryEntry>(Send(author, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/submit", new { }, draft.Revision));
        Assert.Equal("required", await Error(Send(reviewer, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/review", new { approve = false, digest = submitted.Submission!.Digest, notes = "" }, submitted.Revision)));
        var returned = await Ok<DiaryEntry>(Send(reviewer, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/review", new { approve = false, digest = submitted.Submission!.Digest, notes = "Wetter ergänzen" }, submitted.Revision));
        var originalReview = returned.Review;
        returned.Review = null;
        Assert.Equal("command_required", await Error(Send(author, HttpMethod.Put, $"/api/v1/diary-entries/{draft.Id}", returned, returned.Revision)));
        returned.Review = originalReview; returned.Weather = "Regen";
        var corrected = await Ok<DiaryEntry>(Send(author, HttpMethod.Put, $"/api/v1/diary-entries/{draft.Id}", returned, returned.Revision));
        var resubmitted = await Ok<DiaryEntry>(Send(author, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/submit", new { }, corrected.Revision));
        Assert.NotEqual(submitted.Submission.Digest, resubmitted.Submission!.Digest);
        Assert.Equal("stale_content", await Error(Send(reviewer, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/review", new { approve = true, digest = submitted.Submission.Digest }, resubmitted.Revision)));
        var body = new { approve = true, digest = resubmitted.Submission.Digest };
        var responses = await Task.WhenAll(Send(reviewer, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/review", body, resubmitted.Revision),
            Send(reviewer, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/review", body, resubmitted.Revision));
        Assert.Single(responses, r => r.IsSuccessStatusCode); Assert.Single(responses, r => !r.IsSuccessStatusCode);
        foreach (var response in responses) response.Dispose();
    }

    [PostgresApiFact]
    public async Task Office_cannot_review_and_a_director_cannot_review_own_report_or_invent_approved_records()
    {
        var (worker, manager, project, employee) = await Setup();
        var officeUser = await fixture.CreateUser(worker.TenantId, BusinessRole.Office);
        using var office = await fixture.Login(officeUser); using var director = await fixture.Login(manager);
        var draft = await fixture.Save(new DiaryEntry { TenantId = worker.TenantId, ProjectId = project.Id, EmployeeId = employee.Id, Date = new(2026, 10, 20), Work = "Abdichtung" }, manager.UserId);
        var submitted = await Ok<DiaryEntry>(Send(director, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/submit", new { }, draft.Revision));
        var body = new { approve = true, digest = submitted.Submission!.Digest };
        Assert.Equal("forbidden", await Error(Send(office, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/review", body, submitted.Revision)));
        Assert.Equal("self_review", await Error(Send(director, HttpMethod.Post, $"/api/v1/diary-entries/{draft.Id}/review", body, submitted.Revision)));
        submitted.Status = DiaryStatus.Approved;
        Assert.Equal("command_required", await Error(Send(director, HttpMethod.Post, "/api/v1/diary-entries", submitted)));
    }

    private async Task<(ApiTestUser Worker, ApiTestUser Manager, Project Project, Employee Employee)> Setup()
    {
        var tenant = await fixture.CreateTenant("Dachbetrieb Original");
        var worker = await fixture.CreateUser(tenant.Id, BusinessRole.Worker); var manager = await fixture.CreateUser(tenant.Id);
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Kunde" }, manager.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id, Name = "Haus", Address = "Musterstraße 1" }, manager.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id, BuildingId = building.Id, Name = "Projekt Original" }, manager.UserId);
        var employee = await fixture.Save(new Employee { TenantId = tenant.Id, UserId = worker.UserId, Name = "Mitarbeiter Original" }, manager.UserId);
        await fixture.Save(new ScheduleAssignment { TenantId = tenant.Id, ProjectId = project.Id, EmployeeId = employee.Id, Title = "Dacharbeiten", Start = new(2026, 10, 20, 6, 0, 0, TimeSpan.Zero), End = new(2026, 10, 20, 14, 0, 0, TimeSpan.Zero) }, manager.UserId);
        return (worker, manager, project, employee);
    }
}
