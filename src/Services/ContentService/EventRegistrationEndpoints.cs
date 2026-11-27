using ContentService.Data;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using static ContentService.ContentHttp;

namespace ContentService;

/// <summary>
/// Event registration. Anyone can register without an account; a signed-in member's id is recorded when a valid token is sent. The ticket
/// code is the credential for viewing, accepting or cancelling a registration, so it is long and random and responses are never cached.
/// </summary>
public static class EventRegistrationEndpoints
{
    private const string Public = "/api/v1/content";
    private const string Admin = "/api/v1/admin/content";
    private const string PublicTag = "Content — Event registration";
    private const string AdminTag = "Admin — Event registration";

    public static void MapEventRegistration(this WebApplication app)
    {
        app.MapPost($"{Public}/events/{{slug}}/registrations", async (HttpContext context, EventRegistrationStore store, string slug, RegisterRequest payload) =>
        {
            var language = LanguageResolver.Resolve(payload.Lang, context.Request.Headers.AcceptLanguage.ToString());
            var result = await store.RegisterAsync(slug, payload with { Lang = language }, context.User.FindFirst(KjClaims.Subject)?.Value);
            return Respond(context, result);
        })
        .WithName("RegisterForEvent").WithSummary("Register for an event, with or without an account")
        .WithDescription("Needs `name` and `email`; `phone` and `occurrence` (an occurrence start from the schedule, default the next one) are optional. The answer is a ticket whose `status` is `confirmed`, `waitlisted` (capacity reached), `pending_approval` (organiser must approve) or `pending_payment` (paid event: the seat is held until `deadlineAt`). Registering again with the same email for the same occurrence returns the existing ticket (200). The `ticketCode` is the QR payload and the credential for the ticket endpoints.")
        .WithTags(PublicTag).AllowAnonymous();

        app.MapGet($"{Public}/event-tickets/{{code}}", async (HttpContext context, EventRegistrationStore store, string code) =>
            Respond(context, await store.GetTicketAsync(code)))
        .WithName("GetEventTicket").WithSummary("Ticket status, waitlist position and deadline")
        .WithTags(PublicTag).AllowAnonymous();

        app.MapPost($"{Public}/event-tickets/{{code}}/accept", async (HttpContext context, EventRegistrationStore store, string code) =>
            Respond(context, await store.AcceptAsync(code)))
        .WithName("AcceptEventOffer").WithSummary("Accept a waitlist offer before its deadline")
        .WithDescription("A promoted waitlist entry holds its seat until `deadlineAt`. After that the seat is offered to the next person and this returns 409 OFFER_EXPIRED.")
        .WithTags(PublicTag).AllowAnonymous();

        app.MapPost($"{Public}/event-tickets/{{code}}/cancel", async (HttpContext context, EventRegistrationStore store, string code) =>
            Respond(context, await store.CancelAsync(code)))
        .WithName("CancelEventRegistration").WithSummary("Cancel a registration; a freed seat goes to the waitlist")
        .WithTags(PublicTag).AllowAnonymous();

        app.MapGet($"{Admin}/events/{{slug}}/registrations", async (HttpRequest request, EventRegistrationStore store, string slug, string? status, int? limit, int? offset) =>
        {
            if (!TryReadPaging(limit, offset, out var safeLimit, out var safeOffset))
            {
                return Invalid(request, "Invalid paging values.");
            }

            var list = await store.ListAsync(slug, string.IsNullOrWhiteSpace(status) ? null : status, safeLimit, safeOffset);
            request.HttpContext.Response.Headers.CacheControl = "no-store";
            return list is null ? NotFound(request, "Event not found.") : Results.Ok(list);
        })
        .WithName("ListEventRegistrations").WithSummary("Registrations of an event with a seat summary")
        .WithDescription("Includes the registrants' personal data, so it needs `events.manage`. `status` filters.")
        .WithTags(AdminTag).RequirePermission(Permissions.EventsManage);

        app.MapPost($"{Admin}/events/{{slug}}/registrations/{{id}}/approve", async (HttpContext context, EventRegistrationStore store, string slug, string id) =>
            Respond(context, await store.ApproveAsync(slug, id)))
        .WithName("ApproveEventRegistration").WithSummary("Approve a registration; it then takes a seat or the waitlist")
        .WithTags(AdminTag).RequirePermission(Permissions.EventsManage);

        app.MapPost($"{Admin}/events/{{slug}}/registrations/{{id}}/reject", async (HttpContext context, EventRegistrationStore store, string slug, string id, ReasonRequest payload) =>
            Respond(context, await store.RejectAsync(slug, id, payload.Reason)))
        .WithName("RejectEventRegistration").WithSummary("Reject a registration, with a reason")
        .WithTags(AdminTag).RequirePermission(Permissions.EventsManage);

        app.MapPost($"{Admin}/events/{{slug}}/registrations/{{id}}/payment", async (HttpContext context, EventRegistrationStore store, string slug, string id, PaymentRequest payload) =>
            Respond(context, await store.ConfirmPaymentAsync(slug, id, payload.Reference)))
        .WithName("ConfirmEventPayment").WithSummary("Record that a paid registration was paid, confirming it")
        .WithDescription("The only way a `pending_payment` registration becomes `confirmed`. Needs the payment reference; fails with 409 if the hold already expired.")
        .WithTags(AdminTag).RequirePermission(Permissions.EventsManage);

        app.MapPost($"{Admin}/events/{{slug}}/registrations/{{id}}/cancel", async (HttpContext context, EventRegistrationStore store, string slug, string id) =>
            Respond(context, await store.AdminCancelAsync(slug, id)))
        .WithName("AdminCancelEventRegistration").WithSummary("Cancel a registration on the registrant's behalf")
        .WithTags(AdminTag).RequirePermission(Permissions.EventsManage);
    }

    private static IResult Respond(HttpContext context, RegistrationResult result)
    {
        context.Response.Headers.CacheControl = "no-store";
        var request = context.Request;
        return result.Outcome switch
        {
            RegistrationOutcome.Created => Results.Json(result.Ticket, statusCode: StatusCodes.Status201Created),
            RegistrationOutcome.Ok => Results.Ok(result.Ticket),
            RegistrationOutcome.NotFound => NotFound(request, result.Error ?? "Not found."),
            RegistrationOutcome.Invalid => Invalid(request, result.Error ?? "Invalid request."),
            _ => ApiError.Create(context, StatusCodes.Status409Conflict, result.Code ?? "CONFLICT", result.Error ?? "Conflict.")
        };
    }
}

public sealed record ReasonRequest(string? Reason);

public sealed record PaymentRequest(string? Reference);
