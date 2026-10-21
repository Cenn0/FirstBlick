using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class DiaryWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Submission_freezes_content_and_labels_and_rejects_later_source_edits()
    {
        var (entry, project, building, employee) = Draft();
        DiaryWorkflow.Submit(entry, project, building, employee, "Dachbetrieb", employee.UserId!.Value, Now);
        var original = entry.Submission;
        project.Name = "Changed project"; employee.Name = "Changed person"; building.Address = "Changed address";
        entry.Validate(); Assert.Equal("Sanierung", original!.Content.ProjectName); Assert.Equal("Alex", original.Content.EmployeeName);
        entry.Work = "Rewritten work";
        Assert.Equal("diary_evidence_changed", Assert.Throws<DomainException>(entry.Validate).Code);
    }

    [Fact]
    public void Return_requires_a_reason_and_allows_correction_then_a_new_submission()
    {
        var (entry, project, building, employee) = Draft(); var reviewer = Guid.NewGuid();
        DiaryWorkflow.Submit(entry, project, building, employee, "Dachbetrieb", employee.UserId!.Value, Now);
        var oldDigest = entry.Submission!.Digest;
        Assert.Equal("required", Assert.Throws<DomainException>(() => DiaryWorkflow.Review(entry, false, oldDigest, reviewer, "Bauleitung", " ", Now)).Code);
        DiaryWorkflow.Review(entry, false, oldDigest, reviewer, "Bauleitung", "Bitte Unterbrechung erläutern", Now);
        entry.Obstructions = "Starker Regen, 45 Minuten Unterbrechung"; entry.Validate();
        DiaryWorkflow.Submit(entry, project, building, employee, "Dachbetrieb", employee.UserId.Value, Now.AddHours(1));
        Assert.Null(entry.Review); Assert.NotEqual(oldDigest, entry.Submission!.Digest); Assert.Equal(DiaryStatus.Submitted, entry.Status);
        Assert.Equal("stale_content", Assert.Throws<DomainException>(() => DiaryWorkflow.Review(entry, true, oldDigest, reviewer, "Bauleitung", "", Now.AddHours(2))).Code);
    }

    [Fact]
    public void Neither_author_nor_person_submitting_on_behalf_can_review()
    {
        var (entry, project, building, employee) = Draft(); var office = Guid.NewGuid();
        DiaryWorkflow.Submit(entry, project, building, employee, "Dachbetrieb", office, Now);
        foreach (var actor in new[] { office, employee.UserId!.Value })
            Assert.Equal("self_review", Assert.Throws<DomainException>(() => DiaryWorkflow.Review(entry, true, entry.Submission!.Digest, actor, "Person", "", Now)).Code);
    }

    [Fact]
    public void Approval_has_a_separate_digest_and_rejects_repeated_transitions()
    {
        var (entry, project, building, employee) = Draft();
        DiaryWorkflow.Submit(entry, project, building, employee, "Dachbetrieb", employee.UserId!.Value, Now);
        Assert.Throws<DomainException>(() => DiaryWorkflow.ReportDigest(entry));
        DiaryWorkflow.Review(entry, true, entry.Submission!.Digest, Guid.NewGuid(), "Bauleitung", "Tagesstand bestätigt", Now.AddMinutes(30));
        var digest = DiaryWorkflow.ReportDigest(entry);
        Assert.Equal(64, digest.Length); Assert.NotEqual(entry.Submission.Digest, digest);
        Assert.Throws<DomainException>(() => DiaryWorkflow.Submit(entry, project, building, employee, "Dachbetrieb", employee.UserId.Value, Now.AddHours(1)));
        Assert.Throws<DomainException>(() => DiaryWorkflow.Review(entry, false, entry.Submission.Digest, Guid.NewGuid(), "Bauleitung", "Anderer Stand", Now.AddHours(1)));
        entry.Review = entry.Review! with { ReviewedAt = Now.AddMinutes(-1) };
        Assert.Equal("diary_evidence_changed", Assert.Throws<DomainException>(entry.Validate).Code);
    }

    [Fact]
    public void Submission_rejects_foreign_or_inactive_people_and_tampered_evidence()
    {
        var (entry, project, building, employee) = Draft(); employee.Active = false;
        Assert.Equal("invalid_reference", Assert.Throws<DomainException>(() => DiaryWorkflow.Submit(entry, project, building, employee, "Dachbetrieb", Guid.NewGuid(), Now)).Code);
        employee.Active = true; building.TenantId = Guid.NewGuid();
        Assert.Equal("invalid_reference", Assert.Throws<DomainException>(() => DiaryWorkflow.Submit(entry, project, building, employee, "Dachbetrieb", Guid.NewGuid(), Now)).Code);
        building.TenantId = entry.TenantId;
        DiaryWorkflow.Submit(entry, project, building, employee, "Dachbetrieb", Guid.NewGuid(), Now);
        entry.Submission = entry.Submission! with { Content = entry.Submission.Content with { EmployeeName = "Forged" } };
        Assert.Equal("diary_evidence_changed", Assert.Throws<DomainException>(entry.Validate).Code);
    }

    private static (DiaryEntry Entry, Project Project, Building Building, Employee Employee) Draft()
    {
        var tenant = Guid.NewGuid(); var customer = Guid.NewGuid();
        var building = new Building { TenantId = tenant, CustomerId = customer, Name = "Haus", Address = "Musterstraße 1" };
        var project = new Project { TenantId = tenant, CustomerId = customer, BuildingId = building.Id, Name = "Sanierung", Number = "P-12" };
        var employee = new Employee { TenantId = tenant, Name = "Alex", UserId = Guid.NewGuid() };
        var entry = new DiaryEntry { TenantId = tenant, ProjectId = project.Id, EmployeeId = employee.Id, Date = new(2026, 10, 20), Work = "Dachfläche abgedichtet", Weather = "Trocken" };
        return (entry, project, building, employee);
    }
}
