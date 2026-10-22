using System.Globalization;
using System.Text;

namespace FirstBlick.Core.Business;

public sealed record ActivityFilter(DateOnly From, DateOnly To, Guid? ProjectId, bool IncludeArchived);
public sealed record BusinessActivitySource(string Kind, Guid Id, long Revision, Guid ProjectId, string Title,
    string CurrentStatus, bool Archived, DateOnly CreatedOn, DateOnly? DueDate);
public sealed record ActivityCounts(int ProjectsCreated, int TasksCreated, int TasksDue);
public sealed record ActivityMonth(DateOnly From, DateOnly To, ActivityCounts Counts);
public sealed record ActivityPeriod(DateOnly From, DateOnly To, ActivityCounts Totals, IReadOnlyList<ActivityMonth> Months);
public sealed record ActivityReport(string Schema, Guid TenantId, string TimeZone, DateTimeOffset CapturedAt,
    ActivityFilter Filter, ActivityPeriod Current, ActivityPeriod Previous, IReadOnlyList<BusinessActivitySource> Sources, string Digest);

/// <summary>Creation dates are recorded events. Due dates and statuses describe the current retained records, never historical backlog.</summary>
public static class BusinessActivity
{
    public const string Schema = "firstblick.business-activity/1.0.0";
    public const int MaximumDays = 366;
    public const int MaximumSources = 5000;

    public static DateOnly PreviousFrom(ActivityFilter filter)
    {
        Guard.OptionalId(filter.ProjectId, "Project");
        var days = filter.To.DayNumber - filter.From.DayNumber + 1;
        Guard.Require(days is >= 1 and <= MaximumDays && filter.From.DayNumber >= days && filter.To.Year < 9999,
            "activity_range", "Choose an inclusive period of 1 to 366 days with a valid preceding comparison period.");
        return filter.From.AddDays(-days);
    }

    public static ActivityReport Create(Guid tenantId, string timeZone, DateTimeOffset capturedAt,
        ActivityFilter filter, IReadOnlyList<BusinessActivitySource> input)
    {
        Guard.Id(tenantId, "Business"); Guard.Utc(capturedAt, "Capture time"); Guard.Text(timeZone, "Time zone", 120);
        _ = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        var previousFrom = PreviousFrom(filter);
        Guard.Items(input, "Report sources", MaximumSources);
        Guard.Require(input.Select(x => (x.Kind, x.Id)).Distinct().Count() == input.Count,
            "activity_source", "Report sources must be unique.");
        foreach (var source in input)
        {
            Guard.Require(source.Kind is "projects" or "tasks" && source.Revision > 0, "activity_source", "Unsupported report source.");
            Guard.Id(source.Id, "Source"); Guard.Id(source.ProjectId, "Source project"); Guard.Text(source.Title, "Source title");
            Guard.Require(source.Kind != "projects" || source.ProjectId == source.Id && source.DueDate is null,
                "activity_source", "Project creation sources must identify their own project.");
            Guard.Require(source.Kind == "projects" ? Enum.TryParse<ProjectStatus>(source.CurrentStatus, out var projectStatus) && Enum.IsDefined(projectStatus)
                : Enum.TryParse<TaskStatus>(source.CurrentStatus, out var taskStatus) && Enum.IsDefined(taskStatus),
                "activity_source", "A current source status is required.");
            Guard.Require((!source.Archived || filter.IncludeArchived) && (!filter.ProjectId.HasValue || source.ProjectId == filter.ProjectId),
                "activity_source", "A report source does not match its filters.");
            Guard.Require(In(source.CreatedOn, previousFrom, filter.To) || source.Kind == "tasks" && source.DueDate.HasValue && In(source.DueDate.Value, previousFrom, filter.To),
                "activity_source", "A report source has no event in either period.");
        }
        var sources = input.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray();
        var current = Period(filter.From, filter.To, sources);
        var previous = Period(previousFrom, filter.From.AddDays(-1), sources);
        // Capture time is intentionally excluded: a later export of unchanged evidence has the same digest.
        var digest = ContentDigest.Of(new { schema = Schema, tenantId, timeZone, filter, current, previous, sources });
        return new(Schema, tenantId, timeZone, capturedAt, filter, current, previous, sources, digest);
    }

    private static ActivityPeriod Period(DateOnly from, DateOnly to, IReadOnlyList<BusinessActivitySource> sources)
    {
        var months = new List<ActivityMonth>();
        for (var start = from; start <= to;)
        {
            var monthEnd = new DateOnly(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month));
            var end = monthEnd < to ? monthEnd : to;
            months.Add(new(start, end, Count(start, end, sources)));
            start = end.AddDays(1);
        }
        return new(from, to, Count(from, to, sources), months);
    }

    private static ActivityCounts Count(DateOnly from, DateOnly to, IReadOnlyList<BusinessActivitySource> sources) => new(
        sources.Count(x => x.Kind == "projects" && In(x.CreatedOn, from, to)),
        sources.Count(x => x.Kind == "tasks" && In(x.CreatedOn, from, to)),
        sources.Count(x => x.Kind == "tasks" && x.DueDate.HasValue && In(x.DueDate.Value, from, to)));

    private static bool In(DateOnly date, DateOnly from, DateOnly to) => date >= from && date <= to;

    public static string SourceCsv(ActivityReport report)
    {
        var output = new StringBuilder("Bereich;Kennung;Revision;Projekt;Bezeichnung;Status aktuell;Archiviert;Angelegt am;Faellig am aktuell;Zeitraum von;Zeitraum bis;Vorzeitraum von;Vorzeitraum bis;Zeitzone;Betrieb;Berichtspruefsumme\r\n");
        foreach (var row in report.Sources)
        {
            string[] cells = [row.Kind, row.Id.ToString(), row.Revision.ToString(CultureInfo.InvariantCulture), row.ProjectId.ToString(), row.Title,
                row.CurrentStatus, row.Archived ? "true" : "false", Iso(row.CreatedOn), row.DueDate.HasValue ? Iso(row.DueDate.Value) : "",
                Iso(report.Current.From), Iso(report.Current.To), Iso(report.Previous.From), Iso(report.Previous.To), report.TimeZone,
                report.TenantId.ToString(), report.Digest];
            output.AppendJoin(';', cells.Select(CsvCell)).Append("\r\n");
        }
        return output.ToString();
    }

    public static string CsvCell(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && "=+-@＝＋－＠".Contains(trimmed[0]) || value.StartsWith('\t') || value.StartsWith('\r') || value.StartsWith('\n'))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string Iso(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
