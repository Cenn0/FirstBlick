using System.Globalization;
using System.Text.RegularExpressions;

namespace FirstBlick.Core.Business;

public enum TrainingRecordStatus { Draft, Submitted, Reviewed }
public sealed class TrainingRecord : TenantEntity
{
    public Guid ApprenticeEmployeeId { get; set; }
    public Guid MentorEmployeeId { get; set; }
    public DateOnly WeekStart { get; set; }
    public string Activities { get; set; } = "";
    public string Achievements { get; set; } = "";
    public string Difficulties { get; set; } = "";
    public string LearningGoals { get; set; } = "";
    public string NextWeekGoals { get; set; } = "";
    public int WorkloadRating { get; set; } = 3;
    public int ComplexityRating { get; set; } = 3;
    public string MentorFeedback { get; set; } = "";
    public TrainingRecordStatus Status { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(ApprenticeEmployeeId, nameof(ApprenticeEmployeeId)); Guard.Id(MentorEmployeeId, nameof(MentorEmployeeId));
        Guard.Require(ApprenticeEmployeeId != MentorEmployeeId, "self_review", "An apprentice cannot review their own training record.");
        Guard.Require(WeekStart != default && WeekStart.DayOfWeek == DayOfWeek.Monday, "invalid_week", "A weekly training record starts on a Monday.");
        Guard.Require(Enum.IsDefined(Status), "invalid_status", "Training status is invalid.");
        Guard.Text(Activities, nameof(Activities), 20_000, Status != TrainingRecordStatus.Draft); Guard.Text(Achievements, nameof(Achievements), 8000, false);
        Guard.Text(Difficulties, nameof(Difficulties), 8000, false); Guard.Text(LearningGoals, nameof(LearningGoals), 8000, false);
        Guard.Text(NextWeekGoals, nameof(NextWeekGoals), 8000, false); Guard.Text(MentorFeedback, nameof(MentorFeedback), 8000, Status == TrainingRecordStatus.Reviewed);
        Guard.Require(WorkloadRating is >= 1 and <= 5 && ComplexityRating is >= 1 and <= 5, "invalid_rating", "Workload and complexity ratings must be from 1 to 5.");
        Guard.Require((Status == TrainingRecordStatus.Reviewed) == ReviewedAt.HasValue, "invalid_review", "Only a reviewed record has a review timestamp.");
        if (ReviewedAt is { } at) Guard.Utc(at, nameof(ReviewedAt));
    }
    public override IEnumerable<EntityReference> References() => [BusinessRegistry.Reference<Employee>(ApprenticeEmployeeId), BusinessRegistry.Reference<Employee>(MentorEmployeeId)];
}

public static class TrainingRules
{
    public static void EnsurePeople(TrainingRecord record, Employee apprentice, Employee mentor)
    {
        record.Validate(); apprentice.Validate(); mentor.Validate();
        Guard.Require(record.TenantId == apprentice.TenantId && record.TenantId == mentor.TenantId && record.ApprenticeEmployeeId == apprentice.Id && record.MentorEmployeeId == mentor.Id,
            "reference_mismatch", "Training record participants do not match the selected employees.");
        Guard.Require(apprentice.IsApprentice && apprentice.Active && mentor.Active && apprentice.MentorEmployeeId == mentor.Id,
            "invalid_training_participants", "The active apprentice and assigned active mentor are required.");
    }
}

public enum FormFieldKind { ShortText, LongText, Number, Date, YesNo, Choice }
public sealed record FormField
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public FormFieldKind Kind { get; set; }
    public bool Required { get; set; }
    public int MaximumLength { get; set; } = 2000;
    public decimal? Minimum { get; set; }
    public decimal? Maximum { get; set; }
    public List<string> Choices { get; set; } = [];
    public void Validate()
    {
        Guard.Text(Key, nameof(Key), 64); Guard.Text(Label, nameof(Label), 240);
        Guard.Require(Regex.IsMatch(Key, "^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), "invalid_field_key", "Field keys use a lower-case letter followed by letters, digits or underscores.");
        Guard.Require(Enum.IsDefined(Kind) && MaximumLength is >= 1 and <= 20_000, "invalid_field", "Field kind or length is invalid.");
        Guard.Require(!Minimum.HasValue || !Maximum.HasValue || Minimum <= Maximum, "invalid_range", "Field minimum exceeds its maximum.");
        Guard.Items(Choices, nameof(Choices), 100, Kind == FormFieldKind.Choice ? 1 : 0); foreach (var choice in Choices) Guard.Text(choice, "Choice", 240);
        Guard.Require(Choices.Distinct(StringComparer.Ordinal).Count() == Choices.Count, "duplicate_choice", "Choice values must be unique.");
        Guard.Require(Kind == FormFieldKind.Choice || Choices.Count == 0, "invalid_field", "Only choice fields can define choices.");
        Guard.Require(Kind == FormFieldKind.Number || (!Minimum.HasValue && !Maximum.HasValue), "invalid_field", "Only number fields can define numeric limits.");
    }
}

public sealed class FormDefinition : TenantEntity
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Version { get; set; } = 1;
    public List<FormField> Fields { get; set; } = [];
    public bool Published { get; set; }
    public string PublishedDigest { get; set; } = "";
    public override void Validate()
    {
        ValidateCommon(); Guard.Text(Name, nameof(Name)); Guard.Text(Description, nameof(Description), 4000, false);
        Guard.Require(Version > 0, "invalid_version", "Form version must be positive."); Guard.Items(Fields, nameof(Fields), 100, 1);
        foreach (var field in Fields) field.Validate();
        Guard.Require(Fields.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() == Fields.Count, "duplicate_field", "Form field keys must be unique.");
        if (Published) Guard.Require(PublishedDigest == FormRules.DefinitionDigest(this), "form_immutable", "Published form definitions are immutable; create a new version.");
    }
}

public sealed class FormSubmission : TenantEntity
{
    public Guid FormDefinitionId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SubmittedBy { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public int DefinitionVersion { get; set; }
    public string DefinitionDigest { get; set; } = "";
    public List<FormField> Fields { get; set; } = [];
    public Dictionary<string, string> Values { get; set; } = [];
    public string SubmittedDigest { get; set; } = "";
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(FormDefinitionId, nameof(FormDefinitionId)); Guard.Id(ProjectId, nameof(ProjectId)); Guard.Id(SubmittedBy, nameof(SubmittedBy));
        Guard.Utc(SubmittedAt, nameof(SubmittedAt)); Guard.Require(DefinitionVersion > 0, "invalid_version", "Form version must be positive.");
        Guard.Text(DefinitionDigest, nameof(DefinitionDigest), 64); Guard.Items(Fields, nameof(Fields), 100, 1); foreach (var field in Fields) field.Validate();
        FormRules.ValidateValues(Fields, Values);
        if (!string.IsNullOrEmpty(SubmittedDigest)) Guard.Require(SubmittedDigest == FormRules.SubmissionDigest(this), "submission_immutable", "Submitted form records are immutable.");
    }
    public override IEnumerable<EntityReference> References() => [BusinessRegistry.Reference<FormDefinition>(FormDefinitionId), BusinessRegistry.Reference<Project>(ProjectId)];
}

public static class FormRules
{
    public static string DefinitionDigest(FormDefinition definition) => ContentDigest.Of(new { definition.Name, definition.Description, definition.Version, definition.Fields });
    public static string SubmissionDigest(FormSubmission submission) => ContentDigest.Of(new
    {
        submission.TenantId, submission.Id, submission.FormDefinitionId, submission.ProjectId, submission.SubmittedBy, submission.SubmittedAt,
        submission.DefinitionVersion, submission.DefinitionDigest, submission.Fields,
        Values = submission.Values.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray()
    });

    public static void Publish(FormDefinition definition)
    {
        definition.Validate(); Guard.Require(!definition.Published, "form_immutable", "This definition was already published.");
        definition.PublishedDigest = DefinitionDigest(definition); definition.Published = true;
    }

    public static FormSubmission Submit(FormDefinition definition, Guid projectId, Guid actorId, Dictionary<string, string> values, DateTimeOffset now)
    {
        definition.Validate(); Guard.Require(definition.Published, "form_not_published", "Only a published form can be submitted.");
        ValidateValues(definition.Fields, values);
        var submission = new FormSubmission
        {
            TenantId = definition.TenantId, FormDefinitionId = definition.Id, ProjectId = projectId, SubmittedBy = actorId, SubmittedAt = now,
            DefinitionVersion = definition.Version, DefinitionDigest = definition.PublishedDigest,
            Fields = definition.Fields.Select(x => x with { Choices = x.Choices.ToList() }).ToList(),
            Values = new(values, StringComparer.Ordinal)
        };
        submission.Validate(); submission.SubmittedDigest = SubmissionDigest(submission); return submission;
    }

    public static void ValidateValues(IReadOnlyList<FormField> fields, IReadOnlyDictionary<string, string> values)
    {
        Guard.Require(values is not null && values.Count <= 100, "invalid_values", "Form values exceed their budget.");
        var keys = fields.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        Guard.Require(values!.Keys.All(keys.Contains), "unknown_field", "The submitted form contains an unknown field.");
        Guard.Require(values.Sum(x => x.Key.Length + (x.Value?.Length ?? 0)) <= 100_000, "form_budget", "Form values exceed the 100,000-character storage budget.");
        foreach (var field in fields)
        {
            values.TryGetValue(field.Key, out var value); Guard.Text(value, field.Label, field.MaximumLength, field.Required);
            if (string.IsNullOrWhiteSpace(value)) continue;
            switch (field.Kind)
            {
                case FormFieldKind.Number:
                    Guard.Require(decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number), "invalid_number", $"{field.Label} requires a decimal number using a dot.");
                    Guard.Require((!field.Minimum.HasValue || number >= field.Minimum) && (!field.Maximum.HasValue || number <= field.Maximum), "out_of_range", $"{field.Label} is outside the configured range."); break;
                case FormFieldKind.Date:
                    Guard.Require(DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "invalid_date", $"{field.Label} requires an ISO date."); break;
                case FormFieldKind.YesNo:
                    Guard.Require(value is "true" or "false", "invalid_boolean", $"{field.Label} requires true or false."); break;
                case FormFieldKind.Choice:
                    Guard.Require(field.Choices.Contains(value, StringComparer.Ordinal), "invalid_choice", $"{field.Label} is not an available choice."); break;
            }
        }
    }
}
