using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;

namespace FirstBlick.Core.Tests.Integrations;

public sealed class PayrollExportTests
{
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static DateTimeOffset At(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);
    private static TimeEntry Entry(DateTimeOffset start, DateTimeOffset end) => new()
    {
        TenantId = Tenant, EmployeeId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Start = start, End = end, Activity = "Recorded work", Approved = true
    };

    [Fact]
    public void Overnight_work_and_a_break_crossing_local_midnight_are_partitioned_without_loss()
    {
        // UTC 21:00-02:00 is 23:00-04:00 in Berlin; the break is 23:45-00:15 locally.
        var entry = Entry(At(10, 19, 21), At(10, 20, 2)); entry.Breaks.Add(new(At(10, 19, 21, 45), At(10, 19, 22, 15)));
        var parts = PayrollTimeExport.Split(entry, new(new(2026, 10, 19), new(2026, 10, 20), "Europe/Berlin"));
        Assert.Equal(2, parts.Count); Assert.Equal(new DateOnly(2026, 10, 19), parts[0].LocalDate); Assert.Equal(new DateOnly(2026, 10, 20), parts[1].LocalDate);
        Assert.Equal(0.75m, parts[0].NetHours); Assert.Equal(3.75m, parts[1].NetHours);
        Assert.Equal(TimeSpan.FromMinutes(15).Ticks, parts[0].BreakTicks); Assert.Equal(TimeSpan.FromMinutes(15).Ticks, parts[1].BreakTicks);
        Assert.Equal(entry.NetHours(), parts.Sum(x => x.NetHours)); Assert.Equal(TimeSpan.FromHours(4.5).Ticks, parts.Sum(x => x.NetTicks));
        Assert.Equal(parts[0].End, parts[1].Start); Assert.All(parts, x => Assert.Equal(ContentDigest.Of(entry), x.SourceDigest));
        var clipped = Assert.Single(PayrollTimeExport.Split(entry, new(new(2026, 10, 20), new(2026, 10, 20), "Europe/Berlin")));
        Assert.Equal(At(10, 19, 22), clipped.Start); Assert.Equal(3.75m, clipped.NetHours);
    }

    [Fact]
    public void Spring_clock_change_counts_three_elapsed_hours_in_four_displayed_wall_hours()
    {
        // Berlin 00:00-04:00 jumps over local 02:00-03:00 on 2026-03-29.
        var entry = Entry(At(3, 28, 23), At(3, 29, 2));
        var part = Assert.Single(PayrollTimeExport.Split(entry, new(new(2026, 3, 29), new(2026, 3, 29), "Europe/Berlin")));
        Assert.Equal(3m, part.NetHours); Assert.Equal(TimeSpan.FromHours(3).Ticks, part.GrossTicks);
        Assert.Equal(TimeSpan.FromHours(23), new ExportWindow(new(2026, 3, 29), new(2026, 3, 29), "Europe/Berlin").EndExclusive - part.Start);
    }

    [Fact]
    public void Autumn_clock_change_counts_both_occurrences_and_clips_break_in_utc()
    {
        // Berlin 00:00-04:00 includes the repeated hour on 2026-10-25.
        var entry = Entry(At(10, 24, 22), At(10, 25, 3)); entry.Breaks.Add(new(At(10, 25, 0, 30), At(10, 25, 1, 30)));
        var window = new ExportWindow(new(2026, 10, 25), new(2026, 10, 25), "Europe/Berlin");
        var part = Assert.Single(PayrollTimeExport.Split(entry, window));
        Assert.Equal(5 * TimeSpan.TicksPerHour, part.GrossTicks); Assert.Equal(TimeSpan.TicksPerHour, part.BreakTicks); Assert.Equal(4m, part.NetHours);
        Assert.Equal(TimeSpan.FromHours(25), window.EndExclusive - window.Start);
    }

    [Fact]
    public void Half_open_endings_and_full_break_segments_do_not_create_phantom_work()
    {
        var entry = Entry(At(10, 19, 23), At(10, 20, 1)); entry.Breaks.Add(new(At(10, 19, 23), At(10, 20, 0)));
        Assert.Empty(PayrollTimeExport.Split(entry, new(new(2026, 10, 19), new(2026, 10, 19), "UTC")));
        Assert.Equal(1m, Assert.Single(PayrollTimeExport.Split(entry, new(new(2026, 10, 20), new(2026, 10, 20), "UTC"))).NetHours);
        Assert.Empty(PayrollTimeExport.Split(entry, new(new(2026, 10, 21), new(2026, 10, 21), "UTC")));
        var ending = Entry(At(10, 19, 22), At(10, 20, 0));
        Assert.Empty(PayrollTimeExport.Split(ending, new(new(2026, 10, 20), new(2026, 10, 20), "UTC")));
    }

    [Fact]
    public void Invalid_or_ambiguous_local_midnights_fail_instead_of_guessing_payroll_boundaries()
    {
        var skippedDay = new ExportWindow(new(2011, 12, 30), new(2011, 12, 30), "Pacific/Apia");
        Assert.Equal("unsupported_time_boundary", Assert.Throws<DomainException>(() => skippedDay.Start).Code);
        Assert.Throws<DomainException>(() => new ExportWindow(new(2026, 1, 1), new(2027, 1, 2), "UTC").Validate());
        Assert.Throws<DomainException>(() => new ExportWindow(new(2026, 1, 1), new(2026, 1, 1), "Invalid/Zone").Validate());
    }

    [Fact]
    public void Export_keeps_approved_historic_time_for_inactive_employees_and_omits_unapproved_or_archived_entries()
    {
        var person = new Employee { TenantId = Tenant, Name = "=Employee text", Active = false };
        var project = new Project { TenantId = Tenant, Name = "Site", CustomerId = Guid.NewGuid(), BuildingId = Guid.NewGuid() };
        var approved = Entry(At(10, 20, 8), At(10, 20, 9)); approved.EmployeeId = person.Id; approved.ProjectId = project.Id;
        var draft = Entry(At(10, 20, 9), At(10, 20, 10)); draft.EmployeeId = person.Id; draft.ProjectId = project.Id; draft.Approved = false;
        var archived = Entry(At(10, 20, 10), At(10, 20, 11)); archived.EmployeeId = person.Id; archived.ProjectId = project.Id; archived.Archived = true;
        var context = new ExportContext(Guid.NewGuid(), Tenant, Guid.NewGuid(), At(10, 21, 8), new(new(2026, 10, 20), new(2026, 10, 20), "UTC"));
        var file = Assert.Single(PayrollTimeExport.Build(context, [approved, draft, archived], [person], [project]).Files);
        var records = CsvV1.Parse(file.Content); var row = records[1].Fields; var names = records[0].Fields.ToList();
        Assert.Equal(2, records.Count); Assert.Equal("text:=Employee text", row[names.IndexOf("employee_name_current")]);
        Assert.Equal("false", row[names.IndexOf("employee_active_current")]); Assert.Equal("1", row[names.IndexOf("net_hours")]);
        Assert.Equal("time_not_approved", Assert.Throws<DomainException>(() => PayrollTimeExport.Split(draft, context.Window)).Code);
        Assert.Equal("export_reference", Assert.Throws<DomainException>(() => PayrollTimeExport.Build(context, [approved], [], [project])).Code);
        person.TenantId = Guid.NewGuid();
        Assert.Equal("export_tenant", Assert.Throws<DomainException>(() => PayrollTimeExport.Build(context, [approved], [person], [project])).Code);
    }
}
