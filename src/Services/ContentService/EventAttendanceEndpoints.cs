using ContentService.Data;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using static ContentService.ContentHttp;

namespace ContentService;

/// <summary>Check-in, no-shows, certificates and feedback for events.</summary>
public static class EventAttendanceEndpoints
{
    private const string Public = "/api/v1/content";
    private const string Admin = "/api/v1/admin/content";
    private const string AdminTag = "Admin — Event attendance";
    private const string PublicTag = "Content — Event attendance";

    public static void MapEventAttendance(this WebApplication app)
    {
        app.MapPost($"{Admin}/events/{{slug}}/check-ins", async (HttpContext context, EventAttendanceStore store, string slug, CheckInRequest payload) =>
            Respond(context, await store.CheckInAsync(slug, payload.Items), created: false))
        .WithName("CheckInEventAttendees").WithSummary("Check people in by ticket code or registration id (batch, idempotent)")
        .WithDescription("Built for the organiser app's offline queue: send what was scanned, late and in any order, up to 500 at once. Each item answers `checked_in`, `already_checked_in` (a repeat is a success; the earliest `checkedInAt` is kept), `not_found`, `wrong_event` or `not_eligible` (not confirmed, e.g. unpaid or cancelled). A late scan of someone already marked `no_show` puts them back to `confirmed`.")
        .WithTags(AdminTag).RequirePermission(Permissions.EventsManage);

        app.MapPost($"{Admin}/events/{{slug}}/attendance/close", async (HttpContext context, EventAttendanceStore store, string slug, AttendanceCloseRequest payload) =>
            Respond(context, await store.CloseAttendanceAsync(slug, payload.Occurrence), created: false))
        .WithName("CloseEventAttendance").WithSummary("Mark confirmed registrations that never checked in as no-shows")
        .WithDescription("Only after the occurrence ended. Cancelled registrations stay `cancelled`; no-shows are `no_show`.")
        .WithTags(AdminTag).RequirePermission(Permissions.EventsManage);

        app.MapPost($"{Admin}/events/{{slug}}/registrations/{{id}}/certificate", async (HttpContext context, EventAttendanceStore store, string slug, string id, IssueCertificateRequest payload) =>
            Respond(context, await store.IssueCertificateAsync(slug, id, payload.Name, context.User.FindFirst(KjClaims.Subject)?.Value ?? "unknown"), created: true))
        .WithName("IssueEventCertificate").WithSummary("Issue an attendance certificate, optionally with a corrected name")
        .WithDescription("Only for someone who checked in, and once: pass `name` to print a corrected spelling instead of the registered name.")
        .WithTags(AdminTag).RequirePermission(Permissions.EventsManage);

        app.MapGet($"{Admin}/events/{{slug}}/registrations/{{id}}/certificate", async (HttpContext context, EventAttendanceStore store, string slug, string id) =>
            await Pdf(context, await store.CertificatePdfAsync(null, slug, id)))
        .WithName("GetEventCertificateAdmin").WithSummary("Download an issued certificate (PDF)")
        .WithTags(AdminTag).RequirePermission(Permissions.EventsManage);

        app.MapGet($"{Public}/event-tickets/{{code}}/certificate", async (HttpContext context, EventAttendanceStore store, string code) =>
            await Pdf(context, await store.CertificatePdfAsync(code, null, null)))
        .WithName("GetEventCertificate").WithSummary("The ticket holder downloads their certificate (PDF)")
        .WithTags(PublicTag).AllowAnonymous();

        app.MapPut($"{Admin}/events/{{slug}}/feedback-form", async (HttpContext context, EventAttendanceStore store, string slug, FeedbackFormRequest payload) =>
            Respond(context, await store.PutFeedbackFormAsync(slug, payload), created: false))
        .WithName("PutEventFeedbackForm").WithSummary("Define or open/close the feedback form")
        .WithDescription("Questions are `rating` (1 to 5), `choice` (2 to 20 options) or `text`. Questions are locked once responses exist; `isOpen` can still change.")
        .WithTags(AdminTag).RequirePermission(Permissions.EventsManage);

        app.MapGet($"{Admin}/events/{{slug}}/feedback-form", async (HttpContext context, EventAttendanceStore store, string slug) =>
            Respond(context, await store.AdminFeedbackFormAsync(slug), created: false))
        .WithName("GetEventFeedbackForm").WithSummary("The feedback form definition")
        .WithTags(AdminTag).RequirePermission(Permissions.EventsManage);

        app.MapGet($"{Admin}/events/{{slug}}/feedback", async (HttpContext context, EventAttendanceStore store, string slug) =>
            Respond(context, await store.ResultsAsync(slug), created: false))
        .WithName("GetEventFeedbackResults").WithSummary("Aggregated feedback")
        .WithDescription("Averages, distributions, choice counts and free-text answers without any identity. Feedback can be candid, so only `events.manage` reads it.")
        .WithTags(AdminTag).RequirePermission(Permissions.EventsManage);

        app.MapGet($"{Public}/event-tickets/{{code}}/feedback-form", async (HttpContext context, EventAttendanceStore store, string code) =>
            Respond(context, await store.TicketFormAsync(code), created: false))
        .WithName("GetTicketFeedbackForm").WithSummary("The feedback form, for a ticket that was checked in")
        .WithTags(PublicTag).AllowAnonymous();

        app.MapPost($"{Public}/event-tickets/{{code}}/feedback", async (HttpContext context, EventAttendanceStore store, string code, FeedbackSubmission payload) =>
            Respond(context, await store.SubmitFeedbackAsync(code, payload), created: true))
        .WithName("SubmitEventFeedback").WithSummary("Submit feedback once per checked-in ticket")
        .WithTags(PublicTag).AllowAnonymous();
    }

    private static async Task<IResult> Pdf(HttpContext context, (byte[] Pdf, string FileName)? file)
    {
        await Task.CompletedTask;
        context.Response.Headers.CacheControl = "no-store";
        return file is null ? NotFound(context.Request, "Certificate not found.") : Results.File(file.Value.Pdf, "application/pdf", file.Value.FileName);
    }

    private static IResult Respond<T>(HttpContext context, AttendanceResult<T> result, bool created)
    {
        context.Response.Headers.CacheControl = "no-store";
        return result.Outcome switch
        {
            AttendanceOutcome.Ok => Results.Ok(result.Value),
            AttendanceOutcome.Created => created ? Results.Json(result.Value, statusCode: StatusCodes.Status201Created) : Results.Ok(result.Value),
            AttendanceOutcome.NotFound => NotFound(context.Request, result.Error ?? "Not found."),
            AttendanceOutcome.Invalid => Invalid(context.Request, result.Error ?? "Invalid request."),
            _ => ApiError.Create(context, StatusCodes.Status409Conflict, result.Code ?? "CONFLICT", result.Error ?? "Conflict.")
        };
    }
}
