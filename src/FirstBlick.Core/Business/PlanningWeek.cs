namespace FirstBlick.Core.Business;

/// <summary>
/// Local business-calendar boundaries are converted individually into UTC instants.
/// This preserves 23- and 25-hour dates at daylight-saving transitions.
/// </summary>
public static class PlanningWeek
{
    public sealed record Day(DateOnly Date, DateTimeOffset StartUtc, DateTimeOffset EndUtc);
    public const int MaximumAssignments = 1000;

    public static IReadOnlyList<Day> Boundaries(DateOnly monday, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        Guard.Require(monday.Year is >= 2000 and <= 2099
            && monday.DayOfWeek == DayOfWeek.Monday, "invalid_planning_week",
            "Week start must be a Monday between 2000 and 2099.");
        var days = new List<Day>(7);
        for (var index = 0; index < 7; index++)
        {
            var day = monday.AddDays(index);
            DateTimeOffset AtMidnight(DateOnly localDay)
            {
                var local = localDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
                Guard.Require(!zone.IsInvalidTime(local) && !zone.IsAmbiguousTime(local),
                    "invalid_planning_timezone", "Business-local midnight must unambiguously exist.");
                return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
            }
            var start = AtMidnight(day);
            var end = AtMidnight(day.AddDays(1));
            Guard.Require(end > start, "invalid_planning_timezone", "Business-day boundaries must increase.");
            days.Add(new(day, start, end));
        }
        return days.AsReadOnly();
    }

    /// <summary>Half-open overlaps allow adjacent assignments without duplicating boundary instants.</summary>
    public static bool Overlaps(DateTimeOffset assignmentStart, DateTimeOffset assignmentEnd, Day day)
        => assignmentStart < day.EndUtc && assignmentEnd > day.StartUtc;

    public static IReadOnlyList<DateOnly> DaysCovered(DateTimeOffset start, DateTimeOffset end, IReadOnlyList<Day> days)
    {
        Guard.Window(start, end);
        return days.Where(day => Overlaps(start, end, day)).Select(day => day.Date).ToArray();
    }
}
