using System.Security.Cryptography;
using System.Text;
using ContentService.Data;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.Extensions.Options;
using static ContentService.ContentHttp;

namespace ContentService;

/// <summary>Newsletter subscription (double opt-in), preferences, RFC 8058 unsubscribe, bounce/complaint intake and the back-office list and segments.</summary>
public static class NewsletterEndpoints
{
    private const string Public = "/api/v1/newsletter";
    private const string Admin = "/api/v1/admin/newsletter";
    private const string PublicTag = "Newsletter";
    private const string AdminTag = "Newsletter — back office";

    public static void MapNewsletter(this WebApplication app)
    {
        app.MapPost($"{Public}/subscribe", async (HttpContext context, NewsletterStore store, NewsletterSubscribeRequest payload) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var (valid, error) = await store.SubscribeAsync(payload);
            return valid ? Results.Accepted() : Invalid(context.Request, error!);
        })
        .WithName("SubscribeNewsletter").WithSummary("Ask to subscribe (double opt-in)")
        .WithDescription("Always answers 202 for a valid address, whether or not it is already known, so the endpoint cannot be used to find out who is subscribed. Nothing is sent to the address except a confirmation email until the owner confirms it. `website` is a honeypot: leave it empty. `lang` is bn (default) or en.")
        .WithTags(PublicTag).AllowAnonymous();

        app.MapPost($"{Public}/confirm", async (HttpContext context, NewsletterStore store, NewsletterTokenRequest payload) =>
            await store.ConfirmAsync(payload.Token) ? Results.NoContent() : NotFound(context.Request, "This confirmation link is not valid."))
        .WithName("ConfirmNewsletter").WithSummary("Confirm a subscription with the token from the email")
        .WithDescription("POST, not GET, so a mail scanner that opens links cannot confirm on the owner's behalf. Idempotent.")
        .WithTags(PublicTag).AllowAnonymous();

        app.MapPost($"{Public}/unsubscribe/{{token}}", async (HttpContext context, NewsletterStore store, string token) =>
            await store.UnsubscribeAsync(token) ? Results.NoContent() : NotFound(context.Request, "This unsubscribe link is not valid."))
        .WithName("UnsubscribeNewsletter").WithSummary("One-click unsubscribe (RFC 8058)")
        .WithDescription("The URL sent in `List-Unsubscribe`, called with `List-Unsubscribe=One-Click` by mail clients. No login, no confirmation step, no body is read. Idempotent.")
        .WithTags(PublicTag).AllowAnonymous().DisableAntiforgery();

        app.MapGet($"{Public}/preferences/{{token}}", async (HttpContext context, NewsletterStore store, string token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var view = await store.GetPreferencesAsync(token);
            return view is null ? NotFound(context.Request, "This link is not valid.") : Results.Ok(view);
        })
        .WithName("GetNewsletterPreferences").WithSummary("Preference centre: current language and topics (address masked)")
        .WithTags(PublicTag).AllowAnonymous();

        app.MapPut($"{Public}/preferences/{{token}}", async (HttpContext context, NewsletterStore store, string token, NewsletterPreferencesRequest payload) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var (view, error) = await store.UpdatePreferencesAsync(token, payload);
            return error is not null ? Invalid(context.Request, error) : view is null ? NotFound(context.Request, "This link is not valid.") : Results.Ok(view);
        })
        .WithName("UpdateNewsletterPreferences").WithSummary("Preference centre: change language and topics")
        .WithTags(PublicTag).AllowAnonymous();

        app.MapPost($"{Public}/provider-events", async (HttpContext context, NewsletterStore store, IOptions<NewsletterOptions> options, NewsletterProviderEvent[] payload) =>
        {
            var secret = options.Value.WebhookSecret;
            if (string.IsNullOrEmpty(secret))
            {
                return NotFound(context.Request, "Not found.");
            }

            var sent = context.Request.Headers["X-Webhook-Secret"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(sent)))
            {
                return ApiError.Create(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid webhook secret.");
            }

            var (applied, error) = await store.ApplyProviderEventsAsync(payload);
            return error is null ? Results.Ok(new { applied }) : Invalid(context.Request, error);
        })
        .WithName("NewsletterProviderEvents").WithSummary("Bounce and complaint events from the email provider")
        .WithDescription("Authenticated by the `X-Webhook-Secret` header. `bounce` (permanent) and `complaint` suppress the address at once; `soft-bounce` suppresses on the third. 404 while `Newsletter:WebhookSecret` is not configured.")
        .WithTags(PublicTag).AllowAnonymous();

        app.MapGet($"{Admin}/subscribers", async (HttpContext context, NewsletterStore store, string? status, string? q, int? limit, int? offset) =>
        {
            if (!TryReadPaging(limit, offset, out var safeLimit, out var safeOffset))
            {
                return Invalid(context.Request, "limit must be 1-100 and offset 0 or more.");
            }

            var (rows, total) = await store.ListAsync(status, q, safeLimit, safeOffset);
            return Results.Ok(new PagingEnvelope<NewsletterSubscriberView>(rows.ToArray(), total, null, safeLimit, safeOffset));
        })
        .WithName("ListNewsletterSubscribers").WithSummary("Subscribers, filterable by status and address")
        .WithTags(AdminTag).RequirePermission(Permissions.CommsManage);

        app.MapGet($"{Admin}/segments", async (NewsletterStore store) => Results.Ok(await store.ListSegmentsAsync()))
        .WithName("ListNewsletterSegments").WithSummary("Saved segments")
        .WithTags(AdminTag).RequirePermission(Permissions.CommsManage);

        app.MapPost($"{Admin}/segments", async (HttpContext context, NewsletterStore store, NewsletterSegmentRequest payload) =>
        {
            var (segment, error) = await store.CreateSegmentAsync(payload);
            return segment is null ? Invalid(context.Request, error!) : Results.Created($"{Admin}/segments/{segment.Id}", segment);
        })
        .WithName("CreateNewsletterSegment").WithSummary("Save a segment (languages, sources, topics, event attendees)")
        .WithTags(AdminTag).RequirePermission(Permissions.CommsManage);

        app.MapGet($"{Admin}/segments/{{id}}/subscribers", async (HttpContext context, NewsletterStore store, string id, int? limit, int? offset) =>
        {
            if (!TryReadPaging(limit, offset, out var safeLimit, out var safeOffset))
            {
                return Invalid(context.Request, "limit must be 1-100 and offset 0 or more.");
            }

            var result = await store.ResolveSegmentAsync(id, safeLimit, safeOffset);
            return result is null ? NotFound(context.Request, "Segment not found.") : Results.Ok(new PagingEnvelope<NewsletterSubscriberView>(result.Value.Rows.ToArray(), result.Value.Total, null, safeLimit, safeOffset));
        })
        .WithName("ResolveNewsletterSegment").WithSummary("Who a segment currently contains")
        .WithDescription("Confirmed subscribers only, in a fixed order: the same data always resolves to the same list, and an unconfirmed, unsubscribed or suppressed address never appears.")
        .WithTags(AdminTag).RequirePermission(Permissions.CommsManage);

        app.MapDelete($"{Admin}/segments/{{id}}", async (HttpContext context, NewsletterStore store, string id) =>
            await store.DeleteSegmentAsync(id) ? Results.NoContent() : NotFound(context.Request, "Segment not found."))
        .WithName("DeleteNewsletterSegment").WithSummary("Delete a saved segment")
        .WithTags(AdminTag).RequirePermission(Permissions.CommsManage);
    }
}

public sealed record NewsletterTokenRequest(string? Token);
