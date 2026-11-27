using ContentService.Data;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using static ContentService.ContentHttp;

namespace ContentService;

/// <summary>
/// Event schedule endpoints. The text of an event (title, summary, body, venue name and address in the translation attributes) uses the
/// generic `events` content API; this adds the part that is not text: instants, zone, capacity, sessions and recurrence.
/// </summary>
public static class EventEndpoints
{
    private const string Public = "/api/v1/content";
    private const string Admin = "/api/v1/admin/content";

    public static void MapEvents(this WebApplication app)
    {
        app.MapGet($"{Admin}/events/{{slug}}/schedule", async (HttpRequest request, EventStore store, string slug) =>
        {
            var view = await store.AdminAsync(slug);
            if (view is null)
            {
                return NotFound(request, "The event has no schedule yet, or does not exist.");
            }

            SetETag(request, await store.ETagAsync(slug));
            request.HttpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(view);
        })
        .WithName("GetAdminEventSchedule").WithSummary("Get an event's schedule, sessions and occurrences")
        .WithDescription("The `ETag` is the item's version: send it back as `If-Match` on PUT.")
        .WithTags("Admin — Content").RequirePermission(Permissions.ContentView);

        app.MapPut($"{Admin}/events/{{slug}}/schedule", async (HttpRequest request, EventStore store, string slug, EventScheduleRequest payload) =>
        {
            var result = await store.PutAsync(slug, payload, request.Headers.IfMatch.ToArray()!);
            if (result.Outcome is UpsertOutcome.Updated or UpsertOutcome.Created)
            {
                SetETag(request, result.ETag);
                return Results.Ok(result.Value);
            }

            return Failure(request, result);
        })
        .WithName("PutEventSchedule").WithSummary("Set an event's schedule")
        .WithDescription("Replaces start/end (stored in UTC), the IANA `timeZoneId`, venue coordinates, online URL, capacity, the recurrence rule (RRULE subset: FREQ DAILY/WEEKLY/MONTHLY, INTERVAL, COUNT or UNTIL, BYDAY) with its exceptions, and the ordered sessions. A recurring series is this one record; occurrences are expanded on read in the event's zone.")
        .WithTags("Admin — Content").EvictsContentCache().RequirePermission(Permissions.ContentUpdate);

        app.MapGet($"{Public}/events/{{slug}}/schedule", async (HttpRequest request, EventStore store, string slug) =>
        {
            var view = await store.PublicAsync(slug);
            return view is null ? NotFound(request, "Event not found.") : Cached(request, view);
        })
        .WithName("GetEventSchedule").WithSummary("Schedule, sessions and occurrences of a published event")
        .WithTags("Content — Event").CachePublicContent().AllowAnonymous();

        app.MapGet($"{Public}/event-occurrences", async (HttpRequest request, EventStore store, string? lang, DateTimeOffset? from, DateTimeOffset? to, int? limit, int? offset) =>
        {
            var language = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
            if (!TryReadPaging(limit, offset, out var safeLimit, out var safeOffset))
            {
                return Invalid(request, "Invalid paging values.");
            }

            var start = from ?? DateTimeOffset.UtcNow;
            var end = to ?? start.AddDays(90);
            if (end < start || end - start > TimeSpan.FromDays(400))
            {
                return Invalid(request, "`to` must not be before `from`, and the window is at most 400 days.");
            }

            return Cached(request, Page(await store.OccurrencesAsync(language, start, end), safeLimit, safeOffset, language));
        })
        .WithName("ListEventOccurrences").WithSummary("Upcoming event occurrences, soonest first")
        .WithDescription("Published events expanded into occurrences between `from` (default now) and `to` (default 90 days later). Times are UTC instants with the local wall-clock time in the event's own zone.")
        .WithTags("Content — Event").CachePublicContent().AllowAnonymous();
    }
}
