using System.Security.Claims;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Idempotency;
using KJWebsite.BuildingBlocks.Security;

namespace FinanceService.Offline;

/// <summary>Back-office recording of offline gifts. The maker (record permission) and the checker (approve permission) are different people by rule, not by convention.</summary>
public static class OfflineEndpoints
{
    private const string Route = "/api/v1/admin/finance/donations";

    public static void MapOffline(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, async (HttpContext context, OfflineDonationRequest payload, OfflineDonationService service) =>
        {
            var recorded = await service.RecordAsync(payload, Actor(context), context.RequestAborted);
            return recorded.IsSuccess ? Results.Created($"{Route}/{recorded.Value!.Id}", recorded.Value) : Failure(context, recorded.Error!);
        })
        .RequireIdempotency()
        .WithName("RecordOfflineDonation")
        .WithSummary("Record a gift that arrived offline")
        .WithDescription("`{donorName, email?, phone?, amount, currency, method, date?, designation?, anonymous?, dedication?, referenceNo?, note?, valuation?}` with `method` one of bank, cash, cheque, bkash, nagad, in_kind. The donation is created `pending` and posts nothing until a different user approves it. `date` is when it arrived (not in the future). An in_kind gift needs `valuation: {basis, itemDescription, note?}` (basis market_price, appraisal, donor_receipt, cost or other; a note is required for other), and its amount is the valuation. Requires `finance.donation.record`.")
        .WithTags("Offline donations")
        .RequirePermission(Permissions.FinanceDonationRecord);

        app.MapPut($"{Route}/{{id}}/proofs", async (HttpContext context, string id, OfflineDonationService service) =>
        {
            var added = await service.AddProofAsync(id, context.Request.Body, context.Request.ContentType, context.Request.Headers["X-Filename"].ToString(), Actor(context), context.RequestAborted);
            return added.IsSuccess ? Results.Created($"{Route}/{id}/proofs/{added.Value!.Id}/file", added.Value) : Failure(context, added.Error!);
        })
        .WithName("AddOfflineDonationProof")
        .WithSummary("Attach a proof document")
        .WithDescription("Raw file body with its Content-Type (pdf, jpeg, png, webp, up to 5 MB, checked against the file's own bytes) and an optional URL-encoded `X-Filename`. Up to ten per donation, only while it awaits a decision. A proof is a financial record: nothing can delete it, and it is kept for the configured retention period. Requires `finance.donation.record`.")
        .WithTags("Offline donations")
        .RequirePermission(Permissions.FinanceDonationRecord);

        app.MapGet($"{Route}/{{id}}/offline", async (HttpContext context, string id, Data.FinanceDbContext db) =>
            await OfflineDonationService.ViewAsync(db, id, context.RequestAborted) is { } view ? Results.Ok(view) : ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Offline donation not found."))
        .WithName("GetOfflineDonation")
        .WithSummary("The offline record, its proofs and its decision")
        .WithTags("Offline donations")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/{{id}}/proofs/{{proofId}}/file", async (HttpContext context, string id, string proofId, OfflineDonationService service) =>
        {
            var opened = await service.OpenProofAsync(id, proofId, context.RequestAborted);
            if (opened is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Proof not found.");
            }

            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(opened.Value.Content, opened.Value.Proof.ContentType, opened.Value.Proof.FileName, enableRangeProcessing: false);
        })
        .WithName("DownloadOfflineDonationProof")
        .WithSummary("Download a proof document")
        .WithDescription("Served as an attachment, never cached. Requires `finance.view`.")
        .WithTags("Offline donations")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/{{id}}/approve", async (HttpContext context, string id, OfflineDonationService service) =>
        {
            var approved = await service.ApproveAsync(id, Actor(context), context.RequestAborted);
            return approved.IsSuccess ? Results.Ok(approved.Value) : Failure(context, approved.Error!);
        })
        .WithName("ApproveOfflineDonation")
        .WithSummary("Approve an offline donation: confirms it and posts it to the ledger")
        .WithDescription("Only a user other than the recorder may approve (403 SEPARATION_OF_DUTIES). A bank, cheque or wallet gift needs at least one proof (409 PROOF_REQUIRED). The gift posts to the ledger dated the day it arrived, or today if that month is closed. Approving again is harmless. Requires `finance.donation.approve`.")
        .WithTags("Offline donations")
        .RequirePermission(Permissions.FinanceDonationApprove);

        app.MapPost($"{Route}/{{id}}/reject", async (HttpContext context, string id, RejectRequest payload, OfflineDonationService service) =>
        {
            var rejected = await service.RejectAsync(id, payload.Reason, Actor(context), context.RequestAborted);
            return rejected.IsSuccess ? Results.Ok(rejected.Value) : Failure(context, rejected.Error!);
        })
        .WithName("RejectOfflineDonation")
        .WithSummary("Reject an offline donation: it is cancelled and posts nothing")
        .WithDescription("A reason of 3-500 characters is required. A mistaken record is rejected and recorded again; records are never edited. Requires `finance.donation.approve`.")
        .WithTags("Offline donations")
        .RequirePermission(Permissions.FinanceDonationApprove);
    }

    private static string Actor(HttpContext context) => context.User.FindFirstValue(KjClaims.Subject) ?? string.Empty;

    private static IResult Failure(HttpContext context, ResultError error) => ApiError.Create(context, error.Code switch
    {
        "NOT_FOUND" => StatusCodes.Status404NotFound,
        "SEPARATION_OF_DUTIES" => StatusCodes.Status403Forbidden,
        "ALREADY_DECIDED" or "PROOF_REQUIRED" or "TOO_MANY_PROOFS" or "PERIOD_CLOSED" => StatusCodes.Status409Conflict,
        "FILE_TOO_LARGE" => StatusCodes.Status413PayloadTooLarge,
        "UNSUPPORTED_TYPE" => StatusCodes.Status415UnsupportedMediaType,
        _ => StatusCodes.Status400BadRequest
    }, error.Code, error.Message);
}
