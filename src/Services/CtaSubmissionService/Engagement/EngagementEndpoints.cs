using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;

namespace CtaSubmissionService.Engagement;

/// <summary>The unified engagement inbox: list and work enquiries, routing rules, response targets and the SLA report.</summary>
public static class EngagementEndpoints
{
    private const string Admin = "/api/v1/admin/cta";
    private const string Tag = "Admin — Engagement inbox";

    public static void MapEngagement(this WebApplication app)
    {
        app.MapGet($"{Admin}/inbox", async (HttpContext context, EngagementStore store, string? status, string? category, string? assignee, string? slaState, int? limit, int? offset) =>
        {
            var safeLimit = limit ?? 50;
            var safeOffset = offset ?? 0;
            if (safeLimit is < 1 or > 100 || safeOffset < 0)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "limit must be 1-100 and offset 0 or more.");
            }

            return Results.Ok(await store.ListAsync(status, category, assignee, slaState, Subject(context), safeLimit, safeOffset));
        })
        .WithName("ListEngagementInbox").WithSummary("All enquiries in one list: urgent first, then by response deadline")
        .WithDescription("`status`: new, in_progress, responded, closed, or open (not yet answered or closed). `category`: membership, volunteering, partnership, donation, media, general. `assignee`: a user id, `me` or `none`. `slaState`: on_track, at_risk (a quarter or less of the target left), breached, met, met_late, closed_unanswered, none. Media enquiries are never below high priority.")
        .WithTags(Tag).RequirePermission(Permissions.CtaView);

        app.MapGet($"{Admin}/inbox/{{id}}", async (HttpContext context, EngagementStore store, string id) =>
            await store.GetAsync(id) is { } detail ? Results.Ok(detail) : ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Enquiry not found."))
        .WithName("GetEngagementItem").WithSummary("One enquiry with the submitted values and its history (personal data)")
        .WithTags(Tag).RequirePermission(Permissions.CtaView);

        app.MapPost($"{Admin}/inbox/{{id}}/assign", async (HttpContext context, EngagementStore store, string id, EngagementAssignRequest payload) =>
            Respond(context, await store.AssignAsync(id, payload.Assignee, Subject(context) ?? "unknown")))
        .WithName("AssignEngagementItem").WithSummary("Give an enquiry an owner")
        .WithTags(Tag).RequirePermission(Permissions.CtaManage);

        app.MapPost($"{Admin}/inbox/{{id}}/respond", async (HttpContext context, EngagementStore store, string id, EngagementNoteRequest payload) =>
            Respond(context, await store.RespondAsync(id, payload.Note, Subject(context) ?? "unknown")))
        .WithName("RespondEngagementItem").WithSummary("Record that the enquiry was answered; the first time stops its SLA clock")
        .WithTags(Tag).RequirePermission(Permissions.CtaManage);

        app.MapPost($"{Admin}/inbox/{{id}}/close", async (HttpContext context, EngagementStore store, string id, EngagementNoteRequest payload) =>
            Respond(context, await store.CloseAsync(id, payload.Note, Subject(context) ?? "unknown")))
        .WithName("CloseEngagementItem").WithSummary("Close an enquiry")
        .WithTags(Tag).RequirePermission(Permissions.CtaManage);

        app.MapGet($"{Admin}/routing-rules", async (EngagementStore store) => Results.Ok(await store.RulesAsync()))
        .WithName("ListEngagementRules").WithSummary("Routing rules in the order they are applied")
        .WithTags(Tag).RequirePermission(Permissions.CtaView);

        app.MapPut($"{Admin}/routing-rules", async (HttpContext context, EngagementStore store, EngagementRulesRequest payload) =>
            Respond(context, await store.SetRulesAsync(payload.Rules ?? [])))
        .WithName("SetEngagementRules").WithSummary("Replace the routing rules (order matters: the first match wins)")
        .WithDescription("Each rule matches on `matchCategory`, `matchLanguage` and/or `matchKeyword` (text in the submitted values) and sets `assignTo`, `priority` and/or `setCategory`. Applies to enquiries received from now on. Configurable here, not in code, so the organisation's structure can change.")
        .WithTags(Tag).RequirePermission(Permissions.CtaManage);

        app.MapGet($"{Admin}/sla-targets", async (EngagementStore store) => Results.Ok(await store.TargetsAsync()))
        .WithName("GetEngagementSlaTargets").WithSummary("First-response targets in minutes, per category")
        .WithDescription("Empty until the core members agree targets. A category without a target has no clock.")
        .WithTags(Tag).RequirePermission(Permissions.CtaView);

        app.MapPut($"{Admin}/sla-targets", async (HttpContext context, EngagementStore store, EngagementSlaRequest payload) =>
            Respond(context, await store.SetTargetsAsync(payload.Targets ?? [])))
        .WithName("SetEngagementSlaTargets").WithSummary("Set or remove response targets (`{\"media\": 240, \"general\": null}`)")
        .WithDescription("Takes effect on open enquiries at once. Only the categories named are changed.")
        .WithTags(Tag).RequirePermission(Permissions.CtaManage);

        app.MapGet($"{Admin}/sla-report", async (HttpContext context, EngagementStore store, DateTimeOffset? from, DateTimeOffset? to) =>
        {
            var end = to ?? DateTimeOffset.UtcNow;
            var start = from ?? end.AddDays(-30);
            return end <= start || end - start > TimeSpan.FromDays(366)
                ? ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "`to` must be after `from`, within one year (default: the last 30 days).")
                : Results.Ok(await store.ReportAsync(start, end));
        })
        .WithName("GetEngagementSlaReport").WithSummary("Response times and SLA breaches per category")
        .WithDescription("For enquiries submitted in the window: received, responded, closed unanswered, open, open and breached, open and at risk, answered late, average and median first-response minutes, and the target.")
        .WithTags(Tag).RequirePermission(Permissions.CtaView);
    }

    private static string? Subject(HttpContext context) => context.User.FindFirst(KjClaims.Subject)?.Value;

    private static IResult Respond<T>(HttpContext context, EngagementResult<T> result)
    {
        if (result.Value is not null)
        {
            return Results.Ok(result.Value);
        }

        var status = result.Code switch
        {
            "NOT_FOUND" => StatusCodes.Status404NotFound,
            "CONFLICT" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return ApiError.Create(context, status, result.Code ?? "VALIDATION_ERROR", result.Error ?? "Invalid request.");
    }
}
