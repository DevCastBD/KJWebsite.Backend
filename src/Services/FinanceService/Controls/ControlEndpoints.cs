using System.Globalization;
using System.Security.Claims;
using FinanceService.Data.Entities;
using FinanceService.Ledger;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;

namespace FinanceService.Controls;

public sealed record ControlReasonRequest(string? Reason);

public sealed record ControlNoteRequest(string? Note);

public static class ControlEndpoints
{
    private const string Route = "/api/v1/admin/finance";
    private const int MaxLimit = 100;

    public static void MapControls(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{Route}/controls/requests", async (HttpContext context, ControlService controls, string? status, string? kind, int? limit, int? offset) =>
        {
            if ((status is not null && status is not ("pending" or "approved" or "rejected" or "withdrawn")) || (kind is not null && !ControlKinds.All.Contains(kind)))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: status (pending, approved, rejected, withdrawn) or kind.");
            }

            var take = Math.Clamp(limit ?? 50, 1, MaxLimit);
            var skip = Math.Max(0, offset ?? 0);
            var (items, total) = await controls.ListAsync(status, kind, take, skip, context.RequestAborted);
            return Results.Ok(new PagingEnvelope<ControlRequestView>(items, total, null, take, skip));
        })
        .WithName("ListControlRequests")
        .WithSummary("Requests waiting for, or past, a second person's decision")
        .WithDescription("Every sensitive action (manual adjustment, posting reversal, refund, period or fiscal-year reopen) is a request until a different person approves it. Filters: `status` (pending, approved, rejected, withdrawn), `kind`. Requires `finance.view`.")
        .WithTags("Controls")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/controls/requests/{{id}}", async (HttpContext context, string id, ControlService controls) =>
            await controls.GetAsync(id, context.RequestAborted) is { } view ? Results.Ok(view) : ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Request not found."))
        .WithName("GetControlRequest")
        .WithSummary("One request with its details and outcome")
        .WithTags("Controls")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/controls/requests/{{id}}/approve", async (HttpContext context, string id, ControlNoteRequest? payload, ControlService controls) =>
            Decision(context, await controls.DecideAsync(id, ControlDecisions.Approved, payload?.Note, Actor(context), context.RequestAborted)))
        .WithName("ApproveControlRequest")
        .WithSummary("Approve someone else's request and carry it out")
        .WithDescription("The action runs in the same commit as the approval, or not at all (for example `PERIOD_CLOSED` leaves the request open to be rejected). Approving your own request is refused (`403 SEGREGATION_OF_DUTIES`, and by the database). A request is decided once. Requires `finance.control.approve`.")
        .WithTags("Controls")
        .RequirePermission(Permissions.FinanceControlApprove);

        app.MapPost($"{Route}/controls/requests/{{id}}/reject", async (HttpContext context, string id, ControlNoteRequest? payload, ControlService controls) =>
            Decision(context, await controls.DecideAsync(id, ControlDecisions.Rejected, payload?.Note, Actor(context), context.RequestAborted)))
        .WithName("RejectControlRequest")
        .WithSummary("Reject someone else's request")
        .WithDescription("Nothing is posted. Rejecting your own request is refused: withdraw it instead. Requires `finance.control.approve`.")
        .WithTags("Controls")
        .RequirePermission(Permissions.FinanceControlApprove);

        app.MapPost($"{Route}/controls/requests/{{id}}/withdraw", async (HttpContext context, string id, ControlNoteRequest? payload, ControlService controls) =>
            Decision(context, await controls.DecideAsync(id, ControlDecisions.Withdrawn, payload?.Note, Actor(context), context.RequestAborted)))
        .WithName("WithdrawControlRequest")
        .WithSummary("Withdraw your own open request")
        .WithDescription("Only the person who made the request can withdraw it, and only while it is open. Requires `finance.view`.")
        .WithTags("Controls")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/controls/refunds", async (HttpContext context, ControlService controls) =>
        {
            var (refunds, unexpected) = await controls.RefundsAsync(context.RequestAborted);
            return Results.Ok(new { items = refunds, unexpectedNotices = unexpected });
        })
        .WithName("ListRefunds")
        .WithSummary("Refund requests reconciled against the gateways' own refund notices")
        .WithDescription("Each refund request with `status` and `reconciliation`: `awaiting_gateway` (approved, no signed refund notice yet), `reconciled` (the notice matches amount and currency), `amount_mismatch`, or `n/a` while not approved. `unexpectedNotices` lists refund notices for donations that finance never approved a refund for. Requires `finance.view`.")
        .WithTags("Controls")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/ledger/periods/{{period}}/reopen", async (HttpContext context, string period, ControlReasonRequest payload, ControlService controls) =>
            await Reopen(context, controls, "period", period, payload))
        .WithName("RequestPeriodReopen")
        .WithSummary("Ask to reopen a closed month")
        .WithDescription("`{reason}` (3 to 500 characters). Creates a request; a different person with `finance.control.approve` must approve it before the month takes postings again. Closing again afterwards works as before. Requires `finance.period.close`.")
        .WithTags("Controls")
        .RequirePermission(Permissions.FinancePeriodClose);

        app.MapPost($"{Route}/fiscal-years/{{label}}/reopen", async (HttpContext context, string label, ControlReasonRequest payload, ControlService controls) =>
            await Reopen(context, controls, "year", label, payload))
        .WithName("RequestFiscalYearReopen")
        .WithSummary("Ask to reopen a closed fiscal year")
        .WithDescription("`{reason}` (3 to 500 characters). Creates a request; a different person with `finance.control.approve` must approve it. Requires `finance.period.close`.")
        .WithTags("Controls")
        .RequirePermission(Permissions.FinancePeriodClose);
    }

    /// <summary>The answer to asking: 202 with the request, which is not done until someone else approves it.</summary>
    public static async Task<IResult> Accepted(HttpContext context, Result<ControlRequestEntity> asked, ControlService controls) =>
        asked.IsSuccess ? Results.Json(await controls.GetAsync(asked.Value!.Id, context.RequestAborted), statusCode: StatusCodes.Status202Accepted) : Failure(context, asked.Error!);

    private static async Task<IResult> Reopen(HttpContext context, ControlService controls, string scope, string key, ControlReasonRequest payload)
    {
        var reason = payload.Reason?.Trim();
        if (reason is not { Length: >= 3 and <= 500 })
        {
            return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: reason (3-500 characters).");
        }

        var asked = await controls.RequestReopenAsync(scope, key, reason, Actor(context), context.RequestAborted);
        return asked.IsSuccess ? Results.Json(await controls.GetAsync(asked.Value!.Id, context.RequestAborted), statusCode: StatusCodes.Status202Accepted) : Failure(context, asked.Error!);
    }

    private static IResult Decision(HttpContext context, Result<ControlRequestView> decided) =>
        decided.IsSuccess ? Results.Ok(decided.Value) : Failure(context, decided.Error!);

    public static string Actor(HttpContext context) => context.User.FindFirstValue(KjClaims.Subject) ?? string.Empty;

    public static IResult Failure(HttpContext context, ResultError error) =>
        ApiError.Create(context, error.Code switch
        {
            "NOT_FOUND" => StatusCodes.Status404NotFound,
            "SEGREGATION_OF_DUTIES" => StatusCodes.Status403Forbidden,
            "ALREADY_REVERSED" or "NOT_REVERSIBLE" or "PERIOD_CLOSED" or "ALREADY_CLOSED" or "NOT_POSTED" or "ALREADY_REQUESTED" or "ALREADY_DECIDED" or "NOT_CLOSED" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        }, error.Code, error.Message);
}
