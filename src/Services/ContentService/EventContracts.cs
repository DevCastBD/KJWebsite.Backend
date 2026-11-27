namespace ContentService;

public sealed record EventSessionInput(int StartOffsetMinutes, int DurationMinutes, Dictionary<string, string>? Titles, string? Speaker, string? Room);

/// <summary>The language-independent schedule of an event. `Recurrence` is an RRULE subset; `Exceptions` are local start times (`yyyy-MM-ddTHH:mm`) removed from the series.</summary>
public sealed record EventScheduleRequest(
    DateTimeOffset Start,
    DateTimeOffset End,
    string TimeZoneId,
    string? OnlineUrl,
    double? Latitude,
    double? Longitude,
    int? Capacity,
    string? Recurrence,
    List<string>? Exceptions,
    string? SeriesKey,
    List<EventSessionInput>? Sessions,
    bool? RegistrationOpen = null,
    bool? RequiresApproval = null,
    long? PriceMinor = null,
    string? Currency = null);

public sealed record EventSessionView(int Position, int StartOffsetMinutes, int DurationMinutes, Dictionary<string, string> Titles, string Speaker, string Room);

public sealed record EventOccurrence(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string StartLocal, string EndLocal);

public sealed record EventScheduleView(
    string Slug,
    string TimeZoneId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string StartLocal,
    string EndLocal,
    string OnlineUrl,
    double? Latitude,
    double? Longitude,
    int? Capacity,
    string Recurrence,
    List<string> Exceptions,
    string SeriesKey,
    bool IsRecurring,
    List<EventSessionView> Sessions,
    List<EventOccurrence> Occurrences,
    bool RegistrationOpen = true,
    bool RequiresApproval = false,
    long? PriceMinor = null,
    string Currency = "");

/// <summary>One occurrence in the public cross-event listing.</summary>
public sealed record EventOccurrenceView(string Slug, string Title, string Summary, string ImageUrl, string TimeZoneId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string StartLocal, string EndLocal, string OnlineUrl, int? Capacity);
