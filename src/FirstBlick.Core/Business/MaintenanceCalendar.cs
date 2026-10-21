using System.Globalization;
using System.Text;

namespace FirstBlick.Core.Business;

/// <summary>
/// RFC 5545 snapshot of one persisted maintenance occurrence. The contractual due
/// date is an all-day reminder, not an appointment or an invitation.
/// </summary>
public static class MaintenanceCalendar
{
    public const string ContentType = "text/calendar; charset=utf-8";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string Uid(MaintenanceVisit visit) => $"maintenance-{visit.TenantId:N}-{visit.Id:N}@firstblick.invalid";
    public static string FileName(MaintenanceVisit visit) => $"wartung-{visit.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}-{visit.Id:N}.ics";

    public static byte[] Export(MaintenanceVisit visit)
    {
        visit.Validate();
        Guard.Require(!visit.Archived, "not_found", "The maintenance occurrence is archived.");
        Guard.Require(visit.DueDate < DateOnly.MaxValue, "calendar_date", "The exclusive calendar end date is outside the supported date range.");
        // RFC 5545 INTEGER is signed 32-bit; entity revisions begin at one.
        Guard.Require(visit.Revision <= (long)int.MaxValue + 1, "calendar_revision", "The calendar sequence exceeds the supported range.");
        var summary = visit.Status == MaintenanceVisitStatus.Completed ? "Wartung abgeschlossen: " : "Wartung fällig: ";
        var lines = new[]
        {
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//FirstBlick//Maintenance calendar 1.0//DE", "CALSCALE:GREGORIAN",
            "BEGIN:VEVENT", "UID:" + Uid(visit),
            "DTSTAMP:" + Timestamp(visit.UpdatedAt), "CREATED:" + Timestamp(visit.CreatedAt), "LAST-MODIFIED:" + Timestamp(visit.UpdatedAt),
            "SEQUENCE:" + (visit.Revision - 1).ToString(CultureInfo.InvariantCulture),
            "DTSTART;VALUE=DATE:" + visit.DueDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            "DTEND;VALUE=DATE:" + visit.DueDate.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            "SUMMARY:" + EscapeText(summary + visit.Terms!.Title),
            "STATUS:CONFIRMED", "TRANSP:TRANSPARENT", "CLASS:PRIVATE",
            "X-FIRSTBLICK-MAINTENANCE-STATUS:" + visit.Status,
            "END:VEVENT", "END:VCALENDAR"
        };
        var output = new StringBuilder();
        foreach (var line in lines) AppendFolded(output, line);
        return Utf8.GetBytes(output.ToString());
    }

    private static string Timestamp(DateTimeOffset value) => value.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string EscapeText(string value)
    {
        Guard.Require(value.All(c => c >= ' ' && c != '\u007f' || c is '\t' or '\r' or '\n'),
            "calendar_text", "Calendar titles cannot contain unsupported control characters.");
        try { _ = Utf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw new DomainException("calendar_text", "Calendar titles must contain valid Unicode text."); }
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal);
    }

    private static void AppendFolded(StringBuilder output, string line)
    {
        var octets = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (octets + rune.Utf8SequenceLength > 75)
            {
                output.Append("\r\n "); octets = 1;
            }
            output.Append(rune.ToString()); octets += rune.Utf8SequenceLength;
        }
        output.Append("\r\n");
    }
}
