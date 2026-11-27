namespace ContentService.Data.Entities;

/// <summary>
/// The language-independent part of an `event` content item (plan §3.6). Instants are stored in UTC; the IANA zone the organisers
/// meant is stored beside them so calendar output can show the right wall-clock time. Translated text (venue name, address, notes)
/// lives in the translation attributes.
/// </summary>
public sealed class EventEntity
{
    public string ContentItemId { get; set; } = string.Empty;
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }

    /// <summary>IANA zone id, e.g. `Asia/Dhaka`. Recurrence is expanded in this zone so a weekly 10:00 stays 10:00 across DST.</summary>
    public string TimeZoneId { get; set; } = string.Empty;

    public string OnlineUrl { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>Seats per occurrence. Null means unlimited.</summary>
    public int? Capacity { get; set; }

    /// <summary>An RFC 5545 RRULE subset (FREQ, INTERVAL, COUNT, UNTIL, BYDAY). Empty for a single event; the series is this one row.</summary>
    public string RecurrenceRule { get; set; } = string.Empty;

    /// <summary>JSON array of ISO local start date-times removed from the series.</summary>
    public string ExceptionsJson { get; set; } = "[]";

    /// <summary>Groups related events (e.g. one programme's editions). Free text chosen by editors.</summary>
    public string SeriesKey { get; set; } = string.Empty;
}

/// <summary>One agenda row of an event, in position order. The title is per language.</summary>
public sealed class EventSessionEntity
{
    public string Id { get; set; } = string.Empty;
    public string ContentItemId { get; set; } = string.Empty;
    public int Position { get; set; }

    /// <summary>Offset from the occurrence start, so every occurrence of a series shares one agenda.</summary>
    public int StartOffsetMinutes { get; set; }
    public int DurationMinutes { get; set; }
    public string TitlesJson { get; set; } = "{}";
    public string Speaker { get; set; } = string.Empty;
    public string Room { get; set; } = string.Empty;
}
