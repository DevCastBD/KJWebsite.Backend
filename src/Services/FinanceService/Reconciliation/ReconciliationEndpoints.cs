using System.Globalization;
using System.Security.Claims;
using FinanceService.Data;
using FinanceService.Data.Entities;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Reconciliation;

public sealed record ProviderNoticeRow(string Id, string Gateway, string Kind, string DonationId, string? Reference, string? TransactionId, string? Amount, string? Currency, DateTimeOffset At);

public sealed record WebhookRejectionRow(string Id, string Gateway, string ReasonCode, string SourceIp, string? Reference, DateTimeOffset At);

public static class ReconciliationEndpoints
{
    private const string Route = "/api/v1/admin/finance";
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    public static void MapReconciliation(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{Route}/webhook-rejections", async (FinanceDbContext db, string? gateway, string? reason, int? limit, int? offset) =>
        {
            var query = db.WebhookRejections.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(gateway))
            {
                query = query.Where(r => r.Gateway == gateway);
            }

            if (!string.IsNullOrWhiteSpace(reason))
            {
                query = query.Where(r => r.ReasonCode == reason);
            }

            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var skip = Math.Max(0, offset ?? 0);
            var total = await query.CountAsync();
            var page = await query.OrderByDescending(r => r.Id).Skip(skip).Take(take).ToListAsync();
            return Results.Ok(new PagingEnvelope<WebhookRejectionRow>([.. page.Select(r => new WebhookRejectionRow(r.Id.ToString(CultureInfo.InvariantCulture), r.Gateway, r.ReasonCode, r.SourceIp, r.Reference, r.At))], total, null, take, skip));
        })
        .WithName("ListWebhookRejections")
        .WithSummary("Refused gateway calls, newest first")
        .WithDescription("The alert list: unsigned or badly signed webhooks, calls about unknown payments, and paid amounts that did not match the donation. Reason codes: BAD_SIGNATURE, UNKNOWN_GATEWAY, GATEWAY_DISABLED, UNKNOWN_PAYMENT, REFERENCE_MISMATCH, AMOUNT_MISMATCH.")
        .WithTags("Reconciliation")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/provider-notices", async (FinanceDbContext db, string? gateway, string? kind, int? limit, int? offset) =>
        {
            var query = from n in db.ProviderNotices.AsNoTracking()
                        join d in db.Donations.AsNoTracking() on n.DonationId equals d.Id
                        select new { Notice = n, d.Reference };
            if (!string.IsNullOrWhiteSpace(gateway))
            {
                query = query.Where(x => x.Notice.Gateway == gateway);
            }

            if (!string.IsNullOrWhiteSpace(kind))
            {
                query = query.Where(x => x.Notice.Kind == kind);
            }

            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var skip = Math.Max(0, offset ?? 0);
            var total = await query.CountAsync();
            var page = await query.OrderByDescending(x => x.Notice.Id).Skip(skip).Take(take).ToListAsync();
            return Results.Ok(new PagingEnvelope<ProviderNoticeRow>([.. page.Select(x => new ProviderNoticeRow(
                x.Notice.Id.ToString(CultureInfo.InvariantCulture), x.Notice.Gateway, x.Notice.Kind, x.Notice.DonationId, x.Reference, x.Notice.TransactionId,
                x.Notice.Amount?.ToString("0.00", CultureInfo.InvariantCulture), x.Notice.Currency, x.Notice.At))], total, null, take, skip));
        })
        .WithName("ListProviderNotices")
        .WithSummary("Refunds and disputes the gateways reported, newest first")
        .WithDescription("Signed refund and dispute messages from Stripe and PayPal. They change nothing by themselves: finance reviews each and, where money went back, reverses the donation with the reversal endpoint (the full refund and dispute workflow is BE-068). Filters: gateway, kind (refund, dispute).")
        .WithTags("Reconciliation")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/reconciliation/pending/run", async (HttpContext context, PendingResolver resolver) =>
            Results.Ok(await resolver.RunAsync(context.RequestAborted)))
        .WithName("ResolvePendingPayments")
        .WithSummary("Ask the gateways about checkouts still pending")
        .WithDescription("Asks each gateway about every donation still pending after `ReconcileAfterMinutes` and applies the answer exactly as a webhook would. A gateway that does not answer leaves the donation pending. The same job also runs on a timer. Requires `finance.reconcile`.")
        .WithTags("Reconciliation")
        .RequirePermission(Permissions.FinanceReconcile);

        app.MapPost($"{Route}/payouts", async (HttpContext context, PayoutImport payload, PayoutService payouts) =>
        {
            var imported = await payouts.ImportAsync(payload, context.User.FindFirstValue(KjClaims.Subject), context.RequestAborted);
            if (!imported.IsSuccess)
            {
                var error = imported.Error!;
                return ApiError.Create(context, error.Code switch
                {
                    "ALREADY_IMPORTED" or "PERIOD_CLOSED" => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status400BadRequest
                }, error.Code, error.Message);
            }

            return Results.Created($"{Route}/payouts/{imported.Value!.Id}", imported.Value);
        })
        .WithName("ImportGatewayPayout")
        .WithSummary("Import a gateway settlement and match it to donations")
        .WithDescription("`{gateway, payoutRef, paidOn?, lines:[{reference, gross, fee, net, currency}]}`, amounts as decimal strings with `gross = fee + net`. Each line is matched (donation confirmed, not reversed, same gateway, not already settled, gross equal to the charge), unmatched, amount_mismatch or duplicate. Matched lines post the settlement to the ledger; the whole batch is written together or not at all. A payout can be imported once. Requires `finance.reconcile`.")
        .WithTags("Reconciliation")
        .RequirePermission(Permissions.FinanceReconcile);

        app.MapGet($"{Route}/payouts", async (FinanceDbContext db, int? limit, int? offset) =>
        {
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var skip = Math.Max(0, offset ?? 0);
            var total = await db.Payouts.CountAsync();
            var page = await db.Payouts.AsNoTracking().Include(p => p.Lines).OrderByDescending(p => p.Id).Skip(skip).Take(take).ToListAsync();
            return Results.Ok(new PagingEnvelope<PayoutView>([.. page.Select(PayoutService.ToView)], total, null, take, skip));
        })
        .WithName("ListGatewayPayouts")
        .WithSummary("Imported payouts with their line matches")
        .WithTags("Reconciliation")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/payouts/{{id:long}}", async (HttpContext context, long id, PayoutService payouts) =>
            await payouts.ViewAsync(id) is { } view ? Results.Ok(view) : ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Payout not found."))
        .WithName("GetGatewayPayout")
        .WithSummary("One payout with its lines")
        .WithTags("Reconciliation")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/reconciliation/unpaid", async (PayoutService payouts, string? gateway, int? limit, int? offset) =>
        {
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var skip = Math.Max(0, offset ?? 0);
            var (rows, total) = await payouts.UnpaidAsync(string.IsNullOrWhiteSpace(gateway) ? null : gateway, skip, take);
            return Results.Ok(new PagingEnvelope<UnpaidDonationRow>(rows, total, null, take, skip));
        })
        .WithName("ListUnpaidGatewayDonations")
        .WithSummary("Confirmed gateway donations not yet settled by an imported payout")
        .WithTags("Reconciliation")
        .RequirePermission(Permissions.FinanceView);
    }
}
