using FirstBlick.Core.Business;

namespace FirstBlick.Core.Integrations;

public sealed record PayrollTimeSegment(Guid TimeEntryId, Guid EmployeeId, Guid ProjectId, DateOnly LocalDate,
    DateTimeOffset Start, DateTimeOffset End, long GrossTicks, long BreakTicks, long NetTicks, decimal NetHours, string SourceDigest);

/// <summary>Exports recorded approved work time only. It does not calculate pay, statutory allowances, tax or working-time compliance.</summary>
public static class PayrollTimeExport
{
    public const string Schema = "firstblick.payroll-time/1.0.0";
    public static IReadOnlyList<PayrollTimeSegment> Split(TimeEntry entry, ExportWindow window)
    {
        entry.Validate(); window.Validate();
        Guard.Require(entry.Approved, "time_not_approved", "Only approved time can be exported for payroll processing.");
        var start = entry.Start > window.Start ? entry.Start : window.Start;
        var end = entry.End < window.EndExclusive ? entry.End : window.EndExclusive;
        if (end <= start) return [];
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, window.Zone).DateTime);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end.AddTicks(-1), window.Zone).DateTime);
        var rows = new List<PayrollTimeSegment>(); var digest = ContentDigest.Of(entry);
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var segmentStart = Later(start, window.Boundary(day)); var segmentEnd = Earlier(end, window.Boundary(day.AddDays(1)));
            if (segmentEnd <= segmentStart) continue;
            var gross = (segmentEnd - segmentStart).Ticks;
            var breaks = entry.Breaks.Sum(pause => Math.Max(0, (Earlier(pause.End, segmentEnd) - Later(pause.Start, segmentStart)).Ticks));
            var net = gross - breaks;
            Guard.Require(net >= 0, "invalid_time", "Break intersections exceed the exported time interval.");
            if (net > 0) rows.Add(new(entry.Id, entry.EmployeeId, entry.ProjectId, day, segmentStart, segmentEnd, gross, breaks, net,
                decimal.Round((decimal)net / TimeSpan.TicksPerHour, 6, MidpointRounding.AwayFromZero), digest));
        }
        return rows.AsReadOnly();
    }

    public static ExportBundle Build(ExportContext context, IReadOnlyCollection<TimeEntry> entries, IReadOnlyCollection<Employee> employees, IReadOnlyCollection<Project> projects)
    {
        context.Validate(); ExportContract.ValidateSources(entries, context.TenantId); ExportContract.ValidateSources(employees, context.TenantId); ExportContract.ValidateSources(projects, context.TenantId);
        Guard.Require((long)entries.Count + employees.Count + projects.Count <= ExportContract.MaximumSources, "export_budget", "Too many time-export source records.");
        var people = employees.ToDictionary(x => x.Id); var sites = projects.ToDictionary(x => x.Id);
        IEnumerable<IReadOnlyList<CsvCell>> Rows()
        {
            foreach (var entry in entries.Where(x => x.Approved && !x.Archived).OrderBy(x => x.Start).ThenBy(x => x.Id))
            {
                Guard.Require(people.TryGetValue(entry.EmployeeId, out var person) && sites.TryGetValue(entry.ProjectId, out _), "export_reference", "A time entry's employee or project evidence is unavailable.");
                var project = sites[entry.ProjectId];
                foreach (var part in Split(entry, context.Window))
                    yield return ExportContract.Row(context, CsvCell.Id(entry.Id), CsvCell.Integer(entry.Revision), CsvCell.Id(person.Id), CsvCell.Id(person.UserId), CsvCell.Text(person.Name),
                        CsvCell.Id(project.Id), CsvCell.Text(project.Name), CsvCell.Text(context.Window.TimeZone), CsvCell.Date(part.LocalDate), CsvCell.Instant(entry.Start), CsvCell.Instant(entry.End),
                        CsvCell.Instant(part.Start), CsvCell.Instant(part.End), CsvCell.Integer(part.GrossTicks), CsvCell.Integer(part.BreakTicks), CsvCell.Integer(part.NetTicks), CsvCell.Number(part.NetHours),
                        CsvCell.Text(entry.Activity), CsvCell.Code(part.SourceDigest), CsvCell.Code(ContentDigest.Of(person)), CsvCell.Flag(person.Active));
            }
        }
        var table = ExportContract.Table(context, "approved_time.csv", Schema,
            ["time_entry_id", "time_entry_revision", "employee_id", "user_id", "employee_name_current", "project_id", "project_name_current", "time_zone", "local_date",
                "record_start_utc", "record_end_utc", "segment_start_utc", "segment_end_utc", "gross_ticks", "break_ticks", "net_ticks", "net_hours",
                "activity", "source_digest", "employee_profile_digest", "employee_active_current"], Rows());
        return ExportContract.Bundle(Schema, context,
            "Approved UTC intervals intersecting the inclusive local date window, split at each local midnight. Breaks are clipped exactly; 10,000,000 ticks = one second. Hours are rounded once per segment to six decimals. No wages, tax or allowances are inferred.", [table]);
    }
    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
