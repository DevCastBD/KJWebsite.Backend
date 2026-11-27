using ContentService.Data;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using static ContentService.ContentHttp;

namespace ContentService;

/// <summary>Channel preferences, the in-app inbox and web-push consent for the signed-in member; and the staff `notify` call that honours them.</summary>
public static class NotificationChannelEndpoints
{
    private const string Member = "/api/v1/notifications";
    private const string Admin = "/api/v1/admin/notifications";
    private const string Tag = "Notification channels";

    public static void MapNotificationChannels(this WebApplication app)
    {
        app.MapGet($"{Member}/preferences", async (HttpContext context, NotificationChannelStore store) =>
            Subject(context) is { } subject ? Results.Ok(await store.GetPreferencesAsync(subject)) : Unauthorized(context))
        .WithName("GetNotificationPreferences").WithSummary("My channels: email, SMS, in-app, push")
        .WithDescription("Email and in-app default on; SMS and push default off and stay off until turned on here.")
        .WithTags(Tag).RequirePermission(Permissions.MemberProfileEdit);

        app.MapPut($"{Member}/preferences", async (HttpContext context, NotificationChannelStore store, NotificationPreferenceWrite payload) =>
        {
            if (Subject(context) is not { } subject)
            {
                return Unauthorized(context);
            }

            var (view, error) = await store.SetPreferencesAsync(subject, payload);
            return view is null ? Invalid(context.Request, error!) : Results.Ok(view);
        })
        .WithName("SetNotificationPreferences").WithSummary("Change my channels, Bangladeshi mobile number and language")
        .WithDescription("`phone` accepts 01XXXXXXXXX, 8801XXXXXXXXX or +8801XXXXXXXXX and is stored as +8801XXXXXXXXX; an empty string removes it. SMS cannot be turned on without a number.")
        .WithTags(Tag).RequirePermission(Permissions.MemberProfileEdit);

        app.MapGet($"{Member}/inbox", async (HttpContext context, NotificationChannelStore store, bool? unreadOnly, int? limit, int? offset) =>
        {
            if (Subject(context) is not { } subject)
            {
                return Unauthorized(context);
            }

            return TryReadPaging(limit, offset, out var safeLimit, out var safeOffset)
                ? Results.Ok(await store.InboxAsync(subject, unreadOnly == true, safeLimit, safeOffset))
                : Invalid(context.Request, "limit must be 1-100 and offset 0 or more.");
        })
        .WithName("GetInAppInbox").WithSummary("My in-app notifications, newest first, with the unread count")
        .WithTags(Tag).RequirePermission(Permissions.MemberProfileEdit);

        app.MapPost($"{Member}/inbox/{{id:long}}/read", async (HttpContext context, NotificationChannelStore store, long id) =>
            Subject(context) is { } subject ? await store.MarkReadAsync(subject, id) ? Results.NoContent() : NotFound(context.Request, "Notification not found.") : Unauthorized(context))
        .WithName("MarkInAppRead").WithSummary("Mark one notification read (on every device)")
        .WithTags(Tag).RequirePermission(Permissions.MemberProfileEdit);

        app.MapPost($"{Member}/inbox/read-all", async (HttpContext context, NotificationChannelStore store) =>
            Subject(context) is { } subject ? Results.Ok(new { marked = await store.MarkAllReadAsync(subject) }) : Unauthorized(context))
        .WithName("MarkAllInAppRead").WithSummary("Mark everything read")
        .WithTags(Tag).RequirePermission(Permissions.MemberProfileEdit);

        app.MapPost($"{Member}/push-subscriptions", async (HttpContext context, NotificationChannelStore store, PushSubscriptionWrite payload) =>
        {
            if (Subject(context) is not { } subject)
            {
                return Unauthorized(context);
            }

            var error = await store.AddPushAsync(subject, payload);
            return error is null ? Results.NoContent() : Invalid(context.Request, error);
        })
        .WithName("AddPushSubscription").WithSummary("Register this browser for web push (explicit consent only)")
        .WithDescription("Send only after the member has agreed in the UI, with `consent: true`; never on first visit. Stores the subscription and turns the push preference on. Delivery needs a VAPID key pair and push library, which are not configured yet.")
        .WithTags(Tag).RequirePermission(Permissions.MemberProfileEdit);

        app.MapDelete($"{Member}/push-subscriptions", async (HttpContext context, NotificationChannelStore store, string endpoint) =>
            Subject(context) is { } subject ? await store.RemovePushAsync(subject, endpoint) ? Results.NoContent() : NotFound(context.Request, "Subscription not found.") : Unauthorized(context))
        .WithName("RemovePushSubscription").WithSummary("Unregister a browser")
        .WithTags(Tag).RequirePermission(Permissions.MemberProfileEdit);

        app.MapPost($"{Admin}/templates/{{key}}/notify", async (HttpContext context, NotificationChannelStore store, string key, NotifyRequest payload) =>
        {
            var (outcomes, code, error) = await store.NotifyAsync(key, payload);
            if (outcomes is not null)
            {
                return Results.Ok(new { channels = outcomes });
            }

            return ApiError.Create(context, code == "NOT_FOUND" ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest, code ?? "VALIDATION_ERROR", error ?? "Invalid request.");
        })
        .WithName("NotifyOverChannels").WithSummary("Send one catalogue message over the channels the person allows")
        .WithDescription("Returns each channel as `queued` or `skipped` with a reason. SMS needs all of: `allowSms: true` on this call (an explicit decision per send), a template on the SMS allow-list, the person's opt-in and a valid Bangladeshi number, a message within the segment limit, and the per-number and daily limits. A skipped channel never blocks the others.")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);

        app.MapGet($"{Admin}/sms", async (HttpContext context, NotificationChannelStore store, string? status, int? limit, int? offset) =>
            TryReadPaging(limit, offset, out var safeLimit, out var safeOffset) ? Results.Ok(await store.SmsAsync(status, safeLimit, safeOffset)) : Invalid(context.Request, "limit must be 1-100 and offset 0 or more."))
        .WithName("ListSmsMessages").WithSummary("Queued and sent SMS with encoding and segment count (cost)")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);
    }

    private static string? Subject(HttpContext context) => context.User.FindFirst(KjClaims.Subject)?.Value is { Length: > 0 } s ? s : null;

    private static IResult Unauthorized(HttpContext context) => ApiError.Create(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Sign in first.");
}
