using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class BusinessActivityTests
{
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid project = Guid.NewGuid();
    private static readonly DateTimeOffset Captured = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly ActivityFilter Filter = new(new(2026, 10, 1), new(2026, 10, 31), null, false);

    [Fact]
    public void Creation_and_current_due_dates_are_distinct_and_previous_period_has_equal_calendar_days()
    {
        BusinessActivitySource[] sources = [Project(new(2026, 10, 1)), Task(new(2026, 9, 15), new(2026, 10, 12), "Done"), Task(new(2026, 10, 31), null)];
        var report = Build(sources);
        Assert.Equal(new ActivityCounts(1, 1, 1), report.Current.Totals);
        Assert.Equal(new ActivityCounts(0, 1, 0), report.Previous.Totals);
        Assert.Equal(new DateOnly(2026, 8, 31), report.Previous.From);
        Assert.Equal(2, report.Previous.Months.Count);
        Assert.Equal(31, report.Current.To.DayNumber - report.Current.From.DayNumber + 1);
        Assert.Equal(31, report.Previous.To.DayNumber - report.Previous.From.DayNumber + 1);
    }

    [Fact]
    public void Leap_day_and_partial_months_are_covered_once_without_percentage_division_by_zero()
    {
        var filter = new ActivityFilter(new(2024, 3, 1), new(2024, 3, 31), null, false);
        var report = BusinessActivity.Create(tenant, "Europe/Berlin", Captured, filter, [Task(new(2024, 2, 29), new(2024, 3, 31))]);
        Assert.Equal(new DateOnly(2024, 1, 30), report.Previous.From);
        Assert.Equal(new DateOnly(2024, 2, 29), report.Previous.Months[1].To);
        Assert.Equal(1, report.Previous.Totals.TasksCreated);
        Assert.Equal(1, report.Current.Totals.TasksDue);
        Assert.Equal(0, report.Current.Totals.ProjectsCreated);
    }

    [Fact]
    public void Digest_is_order_and_capture_time_independent_but_binds_revision_filter_and_current_values()
    {
        var first = Project(new(2026, 10, 1)); var second = Task(new(2026, 9, 1), new(2026, 10, 2));
        var report = Build([first, second]);
        var reordered = BusinessActivity.Create(tenant, "Europe/Berlin", Captured.AddHours(1), Filter, [second, first]);
        Assert.Equal(report.Digest, reordered.Digest);
        Assert.NotEqual(report.Digest, Build([first, second with { Revision = 2 }]).Digest);
        Assert.NotEqual(report.Digest, Build([first, second with { DueDate = new(2026, 10, 3) }]).Digest);
        Assert.NotEqual(report.Digest, BusinessActivity.Create(tenant, "UTC", Captured, Filter, [first, second]).Digest);
        Assert.NotEqual(report.Digest, BusinessActivity.Create(tenant, "Europe/Berlin", Captured, Filter with { IncludeArchived = true }, [first, second]).Digest);
    }

    [Fact]
    public void Filters_duplicate_and_foreign_sources_and_source_budget_are_enforced()
    {
        var source = Project(new(2026, 10, 1));
        Assert.Throws<DomainException>(() => Build([source, source]));
        Assert.Throws<DomainException>(() => Build([source with { Archived = true }]));
        Assert.Throws<DomainException>(() => Build([source with { CreatedOn = new(2020, 1, 1) }]));
        Assert.Throws<DomainException>(() => Build([source with { Kind = "invoices" }]));
        Assert.Throws<DomainException>(() => BusinessActivity.Create(tenant, "UTC", Captured, Filter with { ProjectId = Guid.NewGuid() }, [source]));
        Assert.Throws<DomainException>(() => Build(Enumerable.Range(0, 5001).Select(_ => Task(new(2026, 10, 1), null)).ToArray()));
    }

    [Theory]
    [InlineData("2026-10-10", "2026-10-09")]
    [InlineData("2025-01-01", "2026-01-02")]
    [InlineData("0001-01-01", "0001-01-01")]
    [InlineData("9999-01-01", "9999-01-02")]
    public void Unsupported_periods_are_rejected(string from, string to)
        => Assert.Throws<DomainException>(() => BusinessActivity.PreviousFrom(new(DateOnly.Parse(from), DateOnly.Parse(to), null, false)));

    [Theory]
    [InlineData("=2+2")]
    [InlineData("  +SUM(A1)")]
    [InlineData("\t@HYPERLINK")]
    [InlineData("\r=1")]
    [InlineData("＠formula")]
    [InlineData("=1\";=2")]
    public void Csv_text_cells_quote_separators_and_neutralize_formula_prefixes(string text)
    {
        var escaped = BusinessActivity.CsvCell(text);
        Assert.StartsWith("\"'", escaped);
        Assert.EndsWith("\"", escaped);
        Assert.Contains(text.Replace("\"", "\"\""), escaped);
    }

    [Fact]
    public void Csv_preserves_source_identity_filters_and_evidence_while_json_remains_lossless()
    {
        var source = Project(new(2026, 10, 1)) with { Title = "=1\";=2\nDach" };
        var report = Build([source]); var csv = BusinessActivity.SourceCsv(report);
        Assert.Contains("\"'=1\"\";=2\nDach\"", csv);
        Assert.Contains(report.Digest, csv); Assert.Contains(tenant.ToString(), csv); Assert.Contains(project.ToString(), csv);
        Assert.Equal(source.Title, report.Sources.Single().Title);
        Assert.Contains("2026-08-31", csv);
    }

    private ActivityReport Build(IReadOnlyList<BusinessActivitySource> sources) => BusinessActivity.Create(tenant, "Europe/Berlin", Captured, Filter, sources);
    private BusinessActivitySource Project(DateOnly date) => new("projects", project, 1, project, "Dachsanierung", "Active", false, date, null);
    private BusinessActivitySource Task(DateOnly date, DateOnly? due, string status = "Open") => new("tasks", Guid.NewGuid(), 1, project, "Aufmaß", status, false, date, due);
}
