using System.Globalization;
using System.Text.Json;
using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

/// <summary>
/// The schedule of `event` items: UTC instants plus an explicit IANA zone, ordered sessions and a recurrence rule. A recurring series is one
/// row; occurrences are expanded on read in the event's own zone, so editing the series (or the rule) never touches per-occurrence rows.
/// </summary>
public sealed class EventStore(ContentDbContext db, TimeProvider time, RevisionStore revisions)
{
    private const string LocalFormat = "yyyy-MM-ddTHH:mm:ss";
    private const string ExceptionFormat = "yyyy-MM-ddTHH:mm";
    private const int MaxSessions = 200;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<EventScheduleView?> AdminAsync(string slug) => await LoadAsync(slug, publishedOnly: false);

    public async Task<EventScheduleView?> PublicAsync(string slug) => await LoadAsync(slug, publishedOnly: true);

    public async Task<string?> ETagAsync(string slug)
    {
        var updatedAt = await db.Items.AsNoTracking().Where(i => i.Type == ContentTypes.Event && i.Slug == slug && i.DeletedAt == null)
            .Select(i => (DateTimeOffset?)i.UpdatedAt).FirstOrDefaultAsync();
        return updatedAt is null ? null : ContentETag.For(updatedAt.Value);
    }

    public async Task<UpsertResult<EventScheduleView>> PutAsync(string slug, EventScheduleRequest request, IReadOnlyCollection<string>? ifMatch)
    {
        var item = await db.Items.Include(i => i.Event).FirstOrDefaultAsync(i => i.Type == ContentTypes.Event && i.Slug == slug && i.DeletedAt == null);
        if (item is null)
        {
            return new UpsertResult<EventScheduleView>(UpsertOutcome.NotFound);
        }

        if (ifMatch is not null && !ContentETag.Satisfies(ifMatch, item.UpdatedAt))
        {
            return new UpsertResult<EventScheduleView>(UpsertOutcome.Conflict);
        }

        var (zone, error) = Validate(request);
        if (error is not null || zone is null)
        {
            return new UpsertResult<EventScheduleView>(UpsertOutcome.Invalid, Error: error);
        }

        var detail = item.Event;
        if (detail is null)
        {
            detail = new EventEntity { ContentItemId = item.Id };
            db.Set<EventEntity>().Add(detail);
            item.Event = detail;
        }

        detail.StartUtc = request.Start.ToUniversalTime();
        detail.EndUtc = request.End.ToUniversalTime();
        detail.TimeZoneId = request.TimeZoneId.Trim();
        detail.OnlineUrl = request.OnlineUrl?.Trim() ?? string.Empty;
        detail.Latitude = request.Latitude;
        detail.Longitude = request.Longitude;
        detail.Capacity = request.Capacity;
        detail.RecurrenceRule = request.Recurrence?.Trim() ?? string.Empty;
        detail.ExceptionsJson = JsonSerializer.Serialize((request.Exceptions ?? []).Select(NormalizeException).Distinct().Order().ToList(), Json);
        detail.SeriesKey = request.SeriesKey?.Trim() ?? string.Empty;
        detail.RegistrationOpen = request.RegistrationOpen ?? detail.RegistrationOpen;
        detail.RequiresApproval = request.RequiresApproval ?? detail.RequiresApproval;
        detail.PriceMinor = request.PriceMinor is > 0 ? request.PriceMinor : null;
        detail.Currency = detail.PriceMinor is null ? string.Empty : request.Currency!.Trim().ToUpperInvariant();

        var existing = await db.Set<EventSessionEntity>().Where(s => s.ContentItemId == item.Id).ToListAsync();
        db.Set<EventSessionEntity>().RemoveRange(existing);
        var position = 0;
        foreach (var session in (request.Sessions ?? []).OrderBy(s => s.StartOffsetMinutes))
        {
            db.Set<EventSessionEntity>().Add(new EventSessionEntity
            {
                Id = $"evs_{Guid.NewGuid():N}",
                ContentItemId = item.Id,
                Position = position++,
                StartOffsetMinutes = session.StartOffsetMinutes,
                DurationMinutes = session.DurationMinutes,
                TitlesJson = JsonSerializer.Serialize(session.Titles ?? [], Json),
                Speaker = session.Speaker?.Trim() ?? string.Empty,
                Room = session.Room?.Trim() ?? string.Empty
            });
        }

        // The schedule is part of the item: bump the version so the ETag and the concurrency check cover it.
        item.UpdatedAt = new DateTimeOffset(time.GetUtcNow().UtcTicks / 10 * 10, TimeSpan.Zero);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return new UpsertResult<EventScheduleView>(UpsertOutcome.Conflict);
        }

        await revisions.RecordAsync(item.Id, "schedule");
        return new UpsertResult<EventScheduleView>(UpsertOutcome.Updated, await LoadAsync(slug, publishedOnly: false), ETag: ContentETag.For(item.UpdatedAt));
    }

    /// <summary>Occurrences of published events between `from` and `to` (UTC), soonest first, with the title in `lang`. Events without a translation in `lang` are left out.</summary>
    public async Task<List<EventOccurrenceView>> OccurrencesAsync(string lang, DateTimeOffset from, DateTimeOffset to)
    {
        // DateTimeOffset cannot be compared inside an EF predicate on SQLite, so the window is applied in memory.
        var items = await db.Items.AsNoTracking().AsSplitQuery().Include(i => i.Event).Include(i => i.Translations)
            .Where(i => i.Type == ContentTypes.Event && i.DeletedAt == null && i.Status == StatusRules.Published && i.Event != null)
            .ToListAsync();

        var rows = new List<EventOccurrenceView>();
        foreach (var item in items)
        {
            var translation = item.Translations.FirstOrDefault(t => t.Lang == lang);
            if (translation is null || item.Event is null || !TryZone(item.Event.TimeZoneId, out var zone))
            {
                continue;
            }

            foreach (var occurrence in Expand(item.Event, zone).Where(o => o.EndUtc >= from && o.StartUtc <= to))
            {
                rows.Add(new EventOccurrenceView(item.Slug, translation.Title, translation.Summary, item.ImageUrl, item.Event.TimeZoneId,
                    occurrence.StartUtc, occurrence.EndUtc, occurrence.StartLocal, occurrence.EndLocal, item.Event.OnlineUrl, item.Event.Capacity));
            }
        }

        return [.. rows.OrderBy(r => r.StartUtc).ThenBy(r => r.Slug, StringComparer.Ordinal)];
    }

    private async Task<EventScheduleView?> LoadAsync(string slug, bool publishedOnly)
    {
        var item = await db.Items.AsNoTracking().Include(i => i.Event)
            .FirstOrDefaultAsync(i => i.Type == ContentTypes.Event && i.Slug == slug && i.DeletedAt == null && (!publishedOnly || i.Status == StatusRules.Published));
        if (item?.Event is null || !TryZone(item.Event.TimeZoneId, out var zone))
        {
            return null;
        }

        var sessions = (await db.Set<EventSessionEntity>().AsNoTracking().Where(s => s.ContentItemId == item.Id).ToListAsync())
            .OrderBy(s => s.Position)
            .Select(s => new EventSessionView(s.Position, s.StartOffsetMinutes, s.DurationMinutes,
                JsonSerializer.Deserialize<Dictionary<string, string>>(s.TitlesJson, Json) ?? [], s.Speaker, s.Room))
            .ToList();
        return ToView(item.Slug, item.Event, zone, sessions);
    }

    private static EventScheduleView ToView(string slug, EventEntity e, TimeZoneInfo zone, List<EventSessionView> sessions) => new(
        slug, e.TimeZoneId, e.StartUtc, e.EndUtc, Local(e.StartUtc, zone), Local(e.EndUtc, zone), e.OnlineUrl, e.Latitude, e.Longitude,
        e.Capacity, e.RecurrenceRule, JsonSerializer.Deserialize<List<string>>(e.ExceptionsJson, Json) ?? [], e.SeriesKey,
        e.RecurrenceRule.Length > 0, sessions, [.. Expand(e, zone)], e.RegistrationOpen, e.RequiresApproval, e.PriceMinor, e.Currency);

    /// <summary>The occurrences of an event: the single start, or the rule expanded in the event's zone minus its exceptions.</summary>
    public static IEnumerable<EventOccurrence> Expand(EventEntity e, TimeZoneInfo zone)
    {
        var duration = e.EndUtc - e.StartUtc;
        if (e.RecurrenceRule.Length == 0 || RecurrenceRule.Parse(e.RecurrenceRule).Rule is not { } rule)
        {
            yield return Occurrence(e.StartUtc, duration, zone);
            yield break;
        }

        var skipped = (JsonSerializer.Deserialize<List<string>>(e.ExceptionsJson, Json) ?? []).ToHashSet();
        var firstLocal = TimeZoneInfo.ConvertTime(e.StartUtc, zone).DateTime;
        foreach (var local in rule.Expand(firstLocal))
        {
            if (skipped.Contains(local.ToString(ExceptionFormat, CultureInfo.InvariantCulture)))
            {
                continue;
            }

            yield return Occurrence(ToUtc(local, zone), duration, zone);
        }
    }

    private static EventOccurrence Occurrence(DateTimeOffset start, TimeSpan duration, TimeZoneInfo zone) =>
        new(start, start + duration, Local(start, zone), Local(start + duration, zone));

    /// <summary>A wall-clock time in the zone as a UTC instant. A time skipped by a DST jump moves forward by the gap, as calendar clients do.</summary>
    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    private static string Local(DateTimeOffset utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(utc, zone).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    private static (TimeZoneInfo? Zone, string? Error) Validate(EventScheduleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TimeZoneId) || !TryZone(request.TimeZoneId.Trim(), out var zone))
        {
            return (null, "TimeZoneId must be a valid IANA time zone such as 'Asia/Dhaka'.");
        }

        if (request.End <= request.Start)
        {
            return (null, "The event must end after it starts.");
        }

        if (request.Capacity is < 0)
        {
            return (null, "Capacity cannot be negative.");
        }

        if (request.PriceMinor is < 0 || request.PriceMinor is > 0 && (request.Currency is null || request.Currency.Trim().Length != 3 || !request.Currency.Trim().All(char.IsAsciiLetter)))
        {
            return (null, "A paid event needs a positive PriceMinor and a 3-letter Currency.");
        }

        if (request.Latitude is { } lat && (lat is < -90 or > 90) || request.Longitude is { } lon && (lon is < -180 or > 180) || (request.Latitude is null) != (request.Longitude is null))
        {
            return (null, "Latitude (-90 to 90) and longitude (-180 to 180) must be given together.");
        }

        if (!string.IsNullOrWhiteSpace(request.OnlineUrl) && (!Uri.TryCreate(request.OnlineUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            return (null, "OnlineUrl must be an absolute https URL.");
        }

        if (!string.IsNullOrWhiteSpace(request.Recurrence) && RecurrenceRule.Parse(request.Recurrence).Error is { } ruleError)
        {
            return (null, ruleError);
        }

        if (string.IsNullOrWhiteSpace(request.Recurrence) && request.Exceptions is { Count: > 0 })
        {
            return (null, "Exceptions only apply to a recurring event.");
        }

        foreach (var exception in request.Exceptions ?? [])
        {
            if (!DateTime.TryParseExact(exception, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                return (null, $"Exception '{exception}' must be a local start time like 2026-12-05T10:00.");
            }
        }

        var sessions = request.Sessions ?? [];
        if (sessions.Count > MaxSessions)
        {
            return (null, $"At most {MaxSessions} sessions.");
        }

        var minutes = (request.End - request.Start).TotalMinutes;
        foreach (var session in sessions)
        {
            if (session.StartOffsetMinutes < 0 || session.DurationMinutes < 1 || session.StartOffsetMinutes + session.DurationMinutes > minutes)
            {
                return (null, "Each session must start at or after the event start, last at least a minute and end by the event end.");
            }

            if (session.Titles is null || !session.Titles.Any(t => t.Key is "en" or "bn" && !string.IsNullOrWhiteSpace(t.Value)) || session.Titles.Keys.Any(k => k is not ("en" or "bn")))
            {
                return (null, "Each session needs a title in en and/or bn.");
            }
        }

        return (zone, null);
    }

    private static string NormalizeException(string value) =>
        DateTime.ParseExact(value, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None).ToString(ExceptionFormat, CultureInfo.InvariantCulture);

    private static bool TryZone(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }
}
