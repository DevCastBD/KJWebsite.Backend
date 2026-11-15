using FinanceService.Data;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Idempotency;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Donors;

/// <summary>What the Sponsorship module may learn about a donor: enough to link a student, nothing about gifts, contact details or consent.</summary>
public sealed record DonorLinkTarget(string Id, string? AccountSubject, string Status, string AccountStatus);

public sealed record LinkNoticeRequest(string? LinkId);

public sealed record LinkNoticeResult(string Outcome);

/// <summary>
/// The two calls the Sponsorship module makes when staff link a student to a donor (#214). Donors, their consent and the email outbox stay in Finance;
/// the notice carries only the link id, so no child data is stored here.
/// </summary>
public static class LinkTargetEndpoints
{
    private const string Route = "/api/v1/admin/finance/donors";
    public const string Template = "donor.student_linked";

    public static void MapDonorLinkTargets(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{Route}/{{id}}/link-target", async (HttpContext context, string id, FinanceDbContext db) =>
        {
            var donor = await db.Donors.AsNoTracking().Where(d => d.Id == id)
                .Select(d => new DonorLinkTarget(d.Id, d.AccountSubject, d.Status, d.AccountStatus))
                .FirstOrDefaultAsync(context.RequestAborted);
            return donor is null
                ? ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Donor not found.")
                : Results.Ok(donor);
        })
        .WithName("GetDonorLinkTarget")
        .WithSummary("The donor facts the Sponsorship module needs to link a student")
        .WithDescription("Returns the donor id, account subject, status and account status, or 404. Used by the Sponsorship module; requires `sponsorship.link.manage`.")
        .WithTags("Donors")
        .RequirePermission(Permissions.SponsorshipLinkManage);

        app.MapPost($"{Route}/{{id}}/link-notices", async (HttpContext context, string id, LinkNoticeRequest payload, FinanceDbContext db, DonorMessenger messenger) =>
        {
            // Without the key a repeated call would queue a second email, so unlike most POSTs this one refuses to run without it.
            if (!context.Request.Headers.ContainsKey(IdempotencyExtensions.HeaderName))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: Idempotency-Key header is required.");
            }

            if (string.IsNullOrWhiteSpace(payload.LinkId) || payload.LinkId.Length > 40)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: linkId.");
            }

            var donor = await db.Donors.FirstOrDefaultAsync(d => d.Id == id, context.RequestAborted);
            if (donor is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Donor not found.");
            }

            // Goes through the one consent gate: queued only when the donor granted `updates`, otherwise a suppression row records it.
            var outcome = await messenger.StageAsync(donor, Template, new { linkId = payload.LinkId }, cancellationToken: context.RequestAborted);
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Ok(new LinkNoticeResult(outcome == SendOutcome.Queued ? "queued" : "suppressed"));
        })
        .WithName("QueueDonorLinkNotice")
        .WithSummary("Queue the 'student linked' notice to a donor, through the consent gate")
        .WithDescription("Body: the link id only. Queued when the donor granted `updates` consent, otherwise suppressed and recorded. Idempotent by `Idempotency-Key`. Requires `sponsorship.link.manage`.")
        .WithTags("Donors")
        .RequireIdempotency()
        .RequirePermission(Permissions.SponsorshipLinkManage);
    }
}
