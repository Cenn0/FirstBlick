namespace FirstBlick.Core.Business;

public enum DiaryStatus { Draft, Submitted, Approved, Returned }

public sealed record DiaryReportContent(Guid TenantId, Guid DiaryId, Guid ProjectId, Guid EmployeeId,
    Guid? EmployeeUserId, DateOnly Date, string BusinessName, string ProjectName, string ProjectNumber,
    string BuildingAddress, string EmployeeName, string Work, string Weather, string Obstructions, string SafetyNotes);
public sealed record DiarySubmission(DiaryReportContent Content, Guid SubmittedBy, DateTimeOffset SubmittedAt, string Digest);
public sealed record DiaryReview(bool Approved, Guid ReviewedBy, string ReviewerName, DateTimeOffset ReviewedAt, string Notes);

/// <summary>Submitted contents and party labels are frozen; review never edits the author's report.</summary>
public static class DiaryWorkflow
{
    public static void Submit(DiaryEntry entry, Project project, Building building, Employee employee,
        string businessName, Guid actor, DateTimeOffset now)
    {
        entry.Validate(); project.Validate(); building.Validate(); employee.Validate(); Guard.Id(actor, nameof(actor)); Guard.Utc(now, nameof(now));
        Guard.Require(!entry.Archived && entry.Status is DiaryStatus.Draft or DiaryStatus.Returned,
            "invalid_transition", "Only a draft or returned diary report can be submitted.");
        Guard.Require(project.TenantId == entry.TenantId && project.Id == entry.ProjectId && !project.Archived
            && building.TenantId == entry.TenantId && building.Id == project.BuildingId && !building.Archived
            && employee.TenantId == entry.TenantId && employee.Id == entry.EmployeeId && employee.Active && !employee.Archived,
            "invalid_reference", "The report requires its own active employee, project and building.");
        Guard.Text(businessName, nameof(businessName));
        var content = new DiaryReportContent(entry.TenantId, entry.Id, entry.ProjectId, entry.EmployeeId, employee.UserId,
            entry.Date, businessName, project.Name, project.Number, building.Address, employee.Name,
            entry.Work, entry.Weather, entry.Obstructions, entry.SafetyNotes);
        entry.Submission = new(content, actor, now, Digest(content, actor, now));
        entry.Review = null; entry.Status = DiaryStatus.Submitted; entry.Validate();
    }

    public static void Review(DiaryEntry entry, bool approve, string expectedDigest, Guid actor, string actorName, string notes, DateTimeOffset now)
    {
        entry.Validate(); Guard.Id(actor, nameof(actor)); Guard.Utc(now, nameof(now)); Guard.Text(actorName, nameof(actorName));
        Guard.Text(notes, nameof(notes), 4000, !approve);
        Guard.Require(!entry.Archived && entry.Status == DiaryStatus.Submitted && entry.Submission is not null,
            "invalid_transition", "Only a submitted diary report can be reviewed.");
        Guard.Require(entry.Submission!.Digest == expectedDigest, "stale_content", "Review the exact submitted report digest.");
        Guard.Require(actor != entry.Submission.SubmittedBy && actor != entry.Submission.Content.EmployeeUserId,
            "self_review", "A different person must review the submitted report.");
        Guard.Require(now >= entry.Submission.SubmittedAt, "invalid_time", "Review cannot precede submission.");
        entry.Review = new(approve, actor, actorName, now, notes);
        entry.Status = approve ? DiaryStatus.Approved : DiaryStatus.Returned; entry.Validate();
    }

    public static string ReportDigest(DiaryEntry entry)
    {
        entry.Validate();
        Guard.Require(entry.Status == DiaryStatus.Approved, "invalid_transition", "Approve the report before producing its final PDF.");
        return ContentDigest.Of(new { schema = "firstblick.diary-report/1", entry.Submission, entry.Review });
    }

    public static void ValidateState(DiaryEntry entry)
    {
        Guard.Require(Enum.IsDefined(entry.Status), "invalid_status", "The diary report status is invalid.");
        if (entry.Status == DiaryStatus.Draft)
        {
            Guard.Require(entry.Submission is null && entry.Review is null, "diary_evidence_changed", "Draft reports cannot invent submission or review evidence.");
            return;
        }
        Guard.Require(entry.Submission?.Content is not null, "diary_evidence_changed", "A submitted report requires frozen content.");
        var submission = entry.Submission!; var content = submission.Content;
        Guard.Id(submission.SubmittedBy, "SubmittedBy"); Guard.Utc(submission.SubmittedAt, "SubmittedAt");
        Guard.OptionalId(content.EmployeeUserId, "EmployeeUserId");
        Guard.Text(content.BusinessName, "BusinessName"); Guard.Text(content.ProjectName, "ProjectName");
        Guard.Text(content.ProjectNumber, "ProjectNumber", 80, false); Guard.Text(content.EmployeeName, "EmployeeName");
        Guard.Text(content.BuildingAddress, "BuildingAddress", 2000); Guard.Text(content.Work, "Work", 20_000);
        Guard.Text(content.Weather, "Weather", 2000, false); Guard.Text(content.Obstructions, "Obstructions", 8000, false);
        Guard.Text(content.SafetyNotes, "SafetyNotes", 8000, false);
        Guard.Require(content.Date != default && content.TenantId == entry.TenantId && content.DiaryId == entry.Id
            && content.ProjectId == entry.ProjectId && content.EmployeeId == entry.EmployeeId
            && submission.Digest == Digest(content, submission.SubmittedBy, submission.SubmittedAt),
            "diary_evidence_changed", "The report's identity or frozen submission evidence changed.");
        if (entry.Status != DiaryStatus.Returned)
            Guard.Require(content.Date == entry.Date && content.Work == entry.Work && content.Weather == entry.Weather
                && content.Obstructions == entry.Obstructions && content.SafetyNotes == entry.SafetyNotes,
                "diary_evidence_changed", "Submitted diary contents cannot be changed by editing their source fields.");
        if (entry.Status == DiaryStatus.Submitted)
        {
            Guard.Require(entry.Review is null, "diary_evidence_changed", "Submitted reports do not contain a review yet.");
            return;
        }
        Guard.Require(entry.Review is not null, "diary_evidence_changed", "A reviewed report requires review evidence.");
        var review = entry.Review!; Guard.Id(review.ReviewedBy, "ReviewedBy"); Guard.Utc(review.ReviewedAt, "ReviewedAt");
        Guard.Text(review.ReviewerName, "ReviewerName"); Guard.Text(review.Notes, "ReviewNotes", 4000, entry.Status == DiaryStatus.Returned);
        Guard.Require(review.Approved == (entry.Status == DiaryStatus.Approved)
            && review.ReviewedBy != submission.SubmittedBy && review.ReviewedBy != content.EmployeeUserId
            && review.ReviewedAt >= submission.SubmittedAt, "diary_evidence_changed", "The independent review evidence is invalid.");
    }

    private static string Digest(DiaryReportContent content, Guid submittedBy, DateTimeOffset submittedAt) =>
        ContentDigest.Of(new { schema = "firstblick.diary-submission/1", content, submittedBy, submittedAt });
}
