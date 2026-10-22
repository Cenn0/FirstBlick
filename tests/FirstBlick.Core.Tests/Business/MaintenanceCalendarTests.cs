using System.Globalization;
using System.Text;
using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class MaintenanceCalendarTests
{
    [Theory]
    [InlineData("2027-01-31", "20270201")]
    [InlineData("2028-02-29", "20280301")]
    [InlineData("2026-03-29", "20260330")]
    [InlineData("2026-12-31", "20270101")]
    public void Export_preserves_contractual_dates_across_month_end_leap_day_and_dst(string dueDate, string exclusiveEnd)
    {
        var visit = Visit(DateOnly.Parse(dueDate, CultureInfo.InvariantCulture));
        var calendar = Unfold(MaintenanceCalendar.Export(visit));
        Assert.Contains($"DTSTART;VALUE=DATE:{dueDate.Replace("-", "", StringComparison.Ordinal)}\r\n", calendar);
        Assert.Contains($"DTEND;VALUE=DATE:{exclusiveEnd}\r\n", calendar);
        Assert.DoesNotContain("TZID", calendar); Assert.DoesNotContain("RRULE", calendar); Assert.DoesNotContain("METHOD", calendar);
        Assert.Contains("TRANSP:TRANSPARENT\r\n", calendar); Assert.Contains("CLASS:PRIVATE\r\n", calendar);
    }

    [Fact]
    public void Snapshot_is_deterministic_and_uid_is_stable_across_revisions_but_distinct_across_tenants()
    {
        var visit = Visit(); var first = MaintenanceCalendar.Export(visit); var uid = MaintenanceCalendar.Uid(visit);
        Assert.Equal(first, MaintenanceCalendar.Export(visit));
        Assert.Contains("SEQUENCE:0\r\n", Unfold(first));
        Assert.Contains("DTSTAMP:20261021T090102Z\r\n", Unfold(first));
        Assert.Contains("LAST-MODIFIED:20261021T090102Z\r\n", Unfold(first));
        visit.Revision = 4; visit.UpdatedAt = visit.UpdatedAt.AddSeconds(2);
        var revised = Unfold(MaintenanceCalendar.Export(visit));
        Assert.Equal(uid, MaintenanceCalendar.Uid(visit)); Assert.Contains("SEQUENCE:3\r\n", revised);
        Assert.Contains("LAST-MODIFIED:20261021T090104Z\r\n", revised);
        visit.TenantId = Guid.NewGuid(); Assert.NotEqual(uid, MaintenanceCalendar.Uid(visit));
    }

    [Fact]
    public void Text_is_escaped_without_injecting_properties_and_no_checklist_or_person_details_leave_the_record()
    {
        var visit = Visit();
        visit.Terms = visit.Terms! with { Title = "Dach; Nord, Süd\\West\r\nATTENDEE:mailto:person@example.test" };
        visit.ScopeDigest = visit.CalculateScopeDigest();
        var text = Unfold(MaintenanceCalendar.Export(visit));
        Assert.Contains("SUMMARY:Wartung fällig: Dach\\; Nord\\, Süd\\\\West\\nATTENDEE:mailto:person@example.test\r\n", text);
        Assert.DoesNotContain("\r\nATTENDEE:", text);
        Assert.DoesNotContain("Private instructions", text); Assert.DoesNotContain("Private scope", text);
        Assert.DoesNotContain(visit.Terms.CustomerId.ToString(), text); Assert.DoesNotContain(visit.Checklist[0].Id.ToString(), text);
    }

    [Fact]
    public void Folding_counts_utf8_octets_and_never_splits_a_unicode_character()
    {
        var visit = Visit(); visit.Terms = visit.Terms! with { Title = string.Concat(Enumerable.Repeat("Dach 🏠 äöü ", 16)) };
        visit.ScopeDigest = visit.CalculateScopeDigest();
        var bytes = MaintenanceCalendar.Export(visit); var strictUtf8 = new UTF8Encoding(false, true);
        var text = strictUtf8.GetString(bytes);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.Contains("\r\n ", text);
        foreach (var line in text.Split("\r\n", StringSplitOptions.None))
        {
            Assert.InRange(strictUtf8.GetByteCount(line), 0, 75);
            Assert.Equal(line, strictUtf8.GetString(strictUtf8.GetBytes(line)));
        }
        Assert.Contains("SUMMARY:Wartung fällig: " + visit.Terms.Title + "\r\n", Unfold(bytes));
        Assert.DoesNotContain('\n', text.Replace("\r\n", "", StringComparison.Ordinal));
        Assert.DoesNotContain('\r', text.Replace("\r\n", "", StringComparison.Ordinal));
    }

    [Fact]
    public void Completed_occurrence_uses_legal_event_status_and_preserves_the_original_due_date()
    {
        var contract = Contract(new(2027, 1, 31)); var (project, visit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA-1");
        MaintenanceWorkflow.Record(visit, visit.Checklist[0].Id, MaintenanceCheckState.Done, "Private check notes", Guid.NewGuid(), DateTimeOffset.UtcNow);
        MaintenanceWorkflow.Complete(visit, contract, project, "Private completion notes", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var text = Unfold(MaintenanceCalendar.Export(visit));
        Assert.Contains("STATUS:CONFIRMED\r\n", text); Assert.DoesNotContain("\r\nSTATUS:COMPLETED", text);
        Assert.Contains("X-FIRSTBLICK-MAINTENANCE-STATUS:Completed\r\n", text);
        Assert.Contains("SUMMARY:Wartung abgeschlossen: Dachwartung\r\n", text);
        Assert.Contains("DTSTART;VALUE=DATE:20270131\r\n", text);
        Assert.DoesNotContain("Private completion notes", text); Assert.DoesNotContain("Private check notes", text);
        Assert.DoesNotContain(visit.CompletedBy!.Value.ToString(), text);
    }

    [Fact]
    public void Unsupported_dates_sequences_archived_or_corrupt_records_fail_closed()
    {
        var visit = Visit(DateOnly.MaxValue);
        Assert.Equal("calendar_date", Assert.Throws<DomainException>(() => MaintenanceCalendar.Export(visit)).Code);
        visit = Visit(); visit.Revision = (long)int.MaxValue + 2;
        Assert.Equal("calendar_revision", Assert.Throws<DomainException>(() => MaintenanceCalendar.Export(visit)).Code);
        visit.Revision--; Assert.Contains("SEQUENCE:2147483647\r\n", Unfold(MaintenanceCalendar.Export(visit)));
        visit.Archived = true; Assert.Equal("not_found", Assert.Throws<DomainException>(() => MaintenanceCalendar.Export(visit)).Code);
        visit = Visit(); visit.Terms = visit.Terms! with { Title = "Changed source" };
        Assert.Equal("maintenance_evidence", Assert.Throws<DomainException>(() => MaintenanceCalendar.Export(visit)).Code);
    }

    [Theory]
    [InlineData("Title\u0007")]
    [InlineData("Title\u007f")]
    public void Unsupported_control_characters_do_not_create_invalid_calendar_files(string title)
    {
        var visit = Visit(); visit.Terms = visit.Terms! with { Title = title }; visit.ScopeDigest = visit.CalculateScopeDigest();
        Assert.Equal("calendar_text", Assert.Throws<DomainException>(() => MaintenanceCalendar.Export(visit)).Code);
    }

    private static string Unfold(byte[] bytes) => Encoding.UTF8.GetString(bytes).Replace("\r\n ", "", StringComparison.Ordinal);
    private static MaintenanceVisit Visit(DateOnly? date = null)
    {
        var contract = Contract(date ?? new(2027, 1, 31)); var (_, visit) = MaintenanceWorkflow.Plan(contract, contract.NextDueDate, "WA-1");
        visit.CreatedAt = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero); visit.UpdatedAt = new(2026, 10, 21, 9, 1, 2, TimeSpan.Zero);
        return visit;
    }
    private static MaintenanceContract Contract(DateOnly date) => new()
    {
        TenantId = Guid.NewGuid(), CustomerId = Guid.NewGuid(), BuildingId = Guid.NewGuid(), Title = "Dachwartung", Scope = "Private scope",
        StartDate = date, NextDueDate = date, IntervalMonths = 1, ChecklistTemplate = [new("Private check", "Private instructions")]
    };
}
