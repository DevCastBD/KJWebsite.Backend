using ContentService.Data;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using static ContentService.ContentHttp;

namespace ContentService;

/// <summary>iCalendar (.ics) feeds for events.</summary>
public static class EventCalendarEndpoints
{
    private const string Public = "/api/v1/content";
    private const string Tag = "Content — Event calendar";
    private const string IcsType = "text/calendar; charset=utf-8";

    public static void MapEventCalendar(this WebApplication app)
    {
        app.MapGet($"{Public}/event-feed.ics", async (HttpContext context, EventCalendarStore store, string? lang) =>
            PublicIcs(context, await store.PublicFeedAsync(Language(context, lang), null)))
        .WithName("GetEventsCalendarFeed").WithSummary("Subscribe to all published events (iCalendar)")
        .WithDescription("Published events as occurrences from 30 days ago onwards, with UTC times so every calendar shows the right hour in its own zone and across DST. `lang` (en or bn) picks the title language.")
        .WithTags(Tag).AllowAnonymous();

        app.MapGet($"{Public}/events/{{slug}}/calendar.ics", async (HttpContext context, EventCalendarStore store, string slug, string? lang) =>
            PublicIcs(context, await store.PublicFeedAsync(Language(context, lang), slug)))
        .WithName("GetEventCalendar").WithSummary("One published event, all its occurrences (iCalendar)")
        .WithTags(Tag).AllowAnonymous();

        app.MapGet($"{Public}/event-tickets/{{code}}/calendar.ics", async (HttpContext context, EventCalendarStore store, string code, string? lang) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var (ics, error) = await store.InviteAsync(code, Language(context, lang));
            return error switch
            {
                null => Results.File(ics!, IcsType, "invite.ics"),
                "NOT_CONFIRMED" => ApiError.Create(context, StatusCodes.Status409Conflict, "NOT_CONFIRMED", "A calendar invite is only available once the registration is confirmed."),
                _ => NotFound(context.Request, "Ticket not found.")
            };
        })
        .WithName("GetEventInvite").WithSummary("Calendar invite for a confirmed registration (iCalendar)")
        .WithDescription("The registrant's own occurrence, for adding to a calendar. Waitlisted, unpaid or cancelled tickets get 409.")
        .WithTags(Tag).AllowAnonymous();

        app.MapPost($"{Public}/calendar-feed/token", async (HttpContext context, EventCalendarStore store) =>
        {
            var subject = context.User.FindFirst(KjClaims.Subject)?.Value;
            if (string.IsNullOrEmpty(subject))
            {
                return ApiError.Create(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Sign in first.");
            }

            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(await store.CreateTokenAsync(subject, $"{Public}/calendar-feed"), statusCode: StatusCodes.Status201Created);
        })
        .WithName("CreateCalendarFeedToken").WithSummary("Create (or rotate) my calendar subscription URL")
        .WithDescription("Returns the secret `token` and `path` once; only a hash is stored. The URL is a credential: anyone holding it sees your registered events, and calendar apps resend it on every refresh. Creating a new one revokes the previous; `DELETE` revokes it.")
        .WithTags(Tag).RequirePermission(Permissions.MemberProfileEdit);

        app.MapDelete($"{Public}/calendar-feed/token", async (HttpContext context, EventCalendarStore store) =>
        {
            var subject = context.User.FindFirst(KjClaims.Subject)?.Value;
            return !string.IsNullOrEmpty(subject) && await store.RevokeTokenAsync(subject) ? Results.NoContent() : NotFound(context.Request, "You have no active calendar feed.");
        })
        .WithName("RevokeCalendarFeedToken").WithSummary("Revoke my calendar subscription URL")
        .WithTags(Tag).RequirePermission(Permissions.MemberProfileEdit);

        app.MapGet($"{Public}/calendar-feed/{{token}}.ics", async (HttpContext context, EventCalendarStore store, string token, string? lang) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var ics = await store.MemberFeedAsync(token, Language(context, lang));
            return ics is null ? NotFound(context.Request, "Feed not found.") : Results.File(ics, IcsType);
        })
        .WithName("GetMemberCalendarFeed").WithSummary("A member's subscription feed (iCalendar), authorised by the token in the URL")
        .WithDescription("Confirmed registrations of the member who made the token. Unknown and revoked tokens are both 404.")
        .WithTags(Tag).AllowAnonymous();
    }

    private static string Language(HttpContext context, string? lang) => LanguageResolver.Resolve(lang, context.Request.Headers.AcceptLanguage.ToString());

    private static IResult PublicIcs(HttpContext context, byte[]? ics)
    {
        if (ics is null)
        {
            return NotFound(context.Request, "Event not found.");
        }

        context.Response.Headers.CacheControl = "public, max-age=900";
        context.Response.Headers.Vary = "Accept-Language";
        return Results.File(ics, IcsType);
    }
}
