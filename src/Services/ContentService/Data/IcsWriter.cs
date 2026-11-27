using System.Globalization;
using System.Text;

namespace ContentService.Data;

/// <summary>
/// A small RFC 5545 writer: CRLF line endings, text escaping and folding at 75 octets without splitting a UTF-8 sequence (Bengali titles
/// are multi-byte). Times are written in UTC (`...Z`), so a calendar client shows the right wall-clock time in any zone and across DST
/// without needing a VTIMEZONE; the event's own zone has already been applied when the instants were computed.
/// </summary>
public sealed class IcsWriter
{
    private readonly StringBuilder _out = new();

    public IcsWriter(string calendarName, string productId)
    {
        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line($"PRODID:{productId}");
        Line("CALSCALE:GREGORIAN");
        Line("METHOD:PUBLISH");
        Property("X-WR-CALNAME", Escape(calendarName));
        Line("REFRESH-INTERVAL;VALUE=DURATION:PT6H");
        Line("X-PUBLISHED-TTL:PT6H");
    }

    public void Event(string uid, DateTimeOffset stamp, DateTimeOffset modified, DateTimeOffset start, DateTimeOffset end, string summary, string? description, string? location, string? url, int sequence)
    {
        Line("BEGIN:VEVENT");
        Property("UID", uid);
        Line($"DTSTAMP:{Utc(stamp)}");
        Line($"LAST-MODIFIED:{Utc(modified)}");
        Line($"SEQUENCE:{sequence}");
        Line($"DTSTART:{Utc(start)}");
        Line($"DTEND:{Utc(end)}");
        Property("SUMMARY", Escape(summary));
        if (!string.IsNullOrWhiteSpace(description))
        {
            Property("DESCRIPTION", Escape(description));
        }

        if (!string.IsNullOrWhiteSpace(location))
        {
            Property("LOCATION", Escape(location));
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            Property("URL", url);
        }

        Line("STATUS:CONFIRMED");
        Line("TRANSP:OPAQUE");
        Line("END:VEVENT");
    }

    public byte[] ToBytes()
    {
        Line("END:VCALENDAR");
        return new UTF8Encoding(false).GetBytes(_out.ToString());
    }

    public static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Escapes TEXT per RFC 5545 §3.3.11: backslash, semicolon, comma and newlines.</summary>
    public static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal).Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\n", StringComparison.Ordinal);

    private void Property(string name, string value) => Line($"{name}:{value}");

    /// <summary>Appends a content line, folded so no physical line exceeds 75 octets (continuations begin with one space).</summary>
    private void Line(string line)
    {
        var octets = 0;
        var limit = 75;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (octets + size > limit)
            {
                _out.Append("\r\n ");
                octets = 1;
                limit = 75;
            }

            _out.Append(rune.ToString());
            octets += size;
        }

        _out.Append("\r\n");
    }
}
