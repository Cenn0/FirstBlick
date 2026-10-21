using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class PlanningWeekTests
{
    [Fact]
    public void Business_week_handles_daylight_saving_gap_and_repeated_hour_without_guessing()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var spring = PlanningWeek.Boundaries(new DateOnly(2026, 3, 23), berlin);
        Assert.Equal(7, spring.Count);
        Assert.Equal(new DateOnly(2026, 3, 29), spring[6].Date);
        Assert.Equal(TimeSpan.FromHours(23), spring[6].EndUtc - spring[6].StartUtc);
        Assert.Equal(spring[5].EndUtc, spring[6].StartUtc);

        var autumn = PlanningWeek.Boundaries(new DateOnly(2026, 10, 19), berlin);
        Assert.Equal(TimeSpan.FromHours(25), autumn[6].EndUtc - autumn[6].StartUtc);
        Assert.Equal(7, autumn.Count);
        Assert.Equal(new DateOnly(2026, 10, 25), autumn[6].Date);
        Assert.Equal(169, autumn.Sum(day => (day.EndUtc - day.StartUtc).TotalHours));
        Assert.Equal(167, spring.Sum(day => (day.EndUtc - day.StartUtc).TotalHours));
    }

    [Fact]
    public void Day_membership_uses_half_open_intervals_and_maps_cross_midnight_assignments_twice()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var days = PlanningWeek.Boundaries(new DateOnly(2026, 10, 19), berlin);
        var first = days[0];
        Assert.Empty(PlanningWeek.DaysCovered(first.StartUtc.AddHours(-2), first.StartUtc, days));
        Assert.Equal([first.Date], PlanningWeek.DaysCovered(first.StartUtc, first.EndUtc, days));
        Assert.Equal([days[0].Date, days[1].Date], PlanningWeek.DaysCovered(
            first.EndUtc.AddHours(-1), first.EndUtc.AddHours(2), days));
        Assert.Equal([days[6].Date], PlanningWeek.DaysCovered(
            days[6].StartUtc, days[6].EndUtc, days));
        Assert.Empty(PlanningWeek.DaysCovered(days[6].EndUtc, days[6].EndUtc.AddHours(1), days));
    }

    [Fact]
    public void Week_start_cannot_be_a_non_monday_or_year_outside_supported_bounds()
    {
        var zone = TimeZoneInfo.Utc;
        Assert.Equal("invalid_planning_week", Assert.Throws<DomainException>(() =>
            PlanningWeek.Boundaries(new DateOnly(2026, 10, 20), zone)).Code);
        Assert.Equal("invalid_planning_week", Assert.Throws<DomainException>(() =>
            PlanningWeek.Boundaries(new DateOnly(1900, 1, 1), zone)).Code);
    }

    [Fact]
    public void Non_utc_and_invalid_windows_are_not_rendered()
    {
        var monday = PlanningWeek.Boundaries(new DateOnly(2026, 10, 19), TimeZoneInfo.Utc);
        Assert.Equal("invalid_utc", Assert.Throws<DomainException>(() =>
            PlanningWeek.DaysCovered(new DateTimeOffset(2026, 10, 19, 1, 0, 0, TimeSpan.FromHours(1)),
                new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.FromHours(1)), monday)).Code);
        Assert.Equal("invalid_window", Assert.Throws<DomainException>(() =>
            PlanningWeek.DaysCovered(monday[0].StartUtc, monday[0].StartUtc, monday)).Code);
    }
}
