using System.Security.Claims;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Ledger;
using FinanceService.Payments;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Finance;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Donations;

/// <summary>
/// The finance team's view. Donor details are readable here, anonymous gifts included, because receipts and audits need them; they are
/// still never returned by the public API. Corrections go through reversing entries, never edits.
/// </summary>
public static class AdminDonationEndpoints
{
    private const string Route = "/api/v1/admin/finance";
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    // A class with member initialisers, not a positional record: EF can compose later Where/Order clauses over this projection shape.
    private sealed class DonationView
    {
        public DonationEntity Donation { get; init; } = null!;
        public string Latest { get; init; } = string.Empty;
        public bool Reversed { get; init; }
        public string? Gateway { get; init; }
    }

    public static void MapAdminDonations(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{Route}/donations", async (FinanceDbContext db, IDonorProtector protector, string? from, string? to, string? status, string? method, string? designation, int? limit, int? offset) =>
        {
            var query = Views(db);
            if (DateOnly.TryParse(from, out var fromDate))
            {
                query = query.Where(v => v.Donation.ReceivedOn >= fromDate);
            }

            if (DateOnly.TryParse(to, out var toDate))
            {
                query = query.Where(v => v.Donation.ReceivedOn <= toDate);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = status == "refunded" ? query.Where(v => v.Reversed) : query.Where(v => !v.Reversed && v.Latest == status);
            }

            if (!string.IsNullOrWhiteSpace(method))
            {
                query = query.Where(v => v.Gateway == method);
            }

            if (!string.IsNullOrWhiteSpace(designation))
            {
                var parts = designation.Split(':', 2);
                var type = parts[0];
                var id = parts.Length > 1 ? parts[1] : null;
                query = query.Where(v => v.Donation.DesignationType == type && (id == null || v.Donation.DesignationId == id));
            }

            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var skip = Math.Max(0, offset ?? 0);
            var total = await query.CountAsync();
            var page = await query.OrderByDescending(v => v.Donation.ReceivedOn).ThenByDescending(v => v.Donation.Id).Skip(skip).Take(take).ToListAsync();
            return Results.Ok(new PagingEnvelope<FinanceDonationRow>([.. page.Select(v => Row(v, protector))], total, null, take, skip));
        })
        .WithName("ListFinanceDonations")
        .WithSummary("Donations and their reversing entries, newest first")
        .WithDescription("Filters: from/to (UTC dates, inclusive), method (the gateway), status (pending, confirmed, failed, cancelled, refunded), designation (`general`, `campaign:slug`, `project:slug`). Amounts are decimal strings; reversals are negative. Donor names are shown to finance staff even for anonymous gifts, flagged by `anonymous`.")
        .WithTags("Finance")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/donations/totals", async (FinanceDbContext db, string? designation) =>
        {
            var query = Views(db);
            if (!string.IsNullOrWhiteSpace(designation))
            {
                var parts = designation.Split(':', 2);
                var type = parts[0];
                var id = parts.Length > 1 ? parts[1] : null;
                query = query.Where(v => v.Donation.DesignationType == type && (id == null || v.Donation.DesignationId == id));
            }

            // Whole rows are summed here: SQLite, the test provider, cannot SUM a decimal, and a number computed in floating point is never acceptable.
            var rows = await query.Select(v => new { v.Donation.Currency, v.Donation.Amount, v.Latest }).ToListAsync();
            var totals = rows.GroupBy(r => r.Currency).OrderBy(g => g.Key, StringComparer.Ordinal).Select(group =>
            {
                var confirmed = Money.Sum(group.Where(r => r.Latest == DonationStatuses.Confirmed).Select(r => Money.Of(r.Amount, group.Key)), group.Key);
                var pending = Money.Sum(group.Where(r => r.Latest == DonationStatuses.Pending).Select(r => Money.Of(r.Amount, group.Key)), group.Key);
                return new CurrencyTotals(group.Key, confirmed.ToDecimalString(), pending.ToDecimalString(), group.Count());
            });
            return Results.Ok(new { items = totals.ToList() });
        })
        .WithName("GetFinanceDonationTotals")
        .WithSummary("Confirmed and pending totals per currency")
        .WithDescription("One entry per currency, never converted. Confirmed is net of reversals (reversing entries are negative); count includes every entry.")
        .WithTags("Finance")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/donations/{{id}}", async (HttpContext context, string id, FinanceDbContext db, IDonorProtector protector) =>
        {
            var view = await Views(db).FirstOrDefaultAsync(v => v.Donation.Id == id);
            if (view is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Donation not found.");
            }

            return Results.Ok(await DetailAsync(db, view, protector));
        })
        .WithName("GetFinanceDonation")
        .WithSummary("One donation with donor contact details, events and its reversal")
        .WithTags("Finance")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/donations/{{id}}/reversal", async (HttpContext context, string id, ReversalRequest payload, FinanceDbContext db, IDonorProtector protector, LedgerService ledger, TimeProvider clock) =>
        {
            var reason = payload.Reason?.Trim();
            if (reason is not { Length: >= 3 and <= 500 })
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: reason (3-500 characters).");
            }

            var view = await Views(db).FirstOrDefaultAsync(v => v.Donation.Id == id);
            if (view is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Donation not found.");
            }

            if (view.Reversed)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "ALREADY_REVERSED", "This donation has already been reversed.");
            }

            if (view.Donation.Kind == DonationKinds.Reversal || view.Latest != DonationStatuses.Confirmed)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "NOT_REVERSIBLE", "Only a confirmed donation can be reversed.");
            }

            var original = view.Donation;
            var now = clock.GetUtcNow();

            // The books are corrected in the same commit as the donation: a confirmed donation always has a posting, and its reversal gets one too.
            var postingId = await db.Postings.AsNoTracking().Where(p => p.SourceType == PostingSources.Donation && p.SourceId == original.Id).Select(p => (long?)p.Id).FirstOrDefaultAsync();
            if (postingId is null)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "NOT_POSTED", "This donation has no ledger posting to reverse.");
            }

            var stagedReversal = await ledger.StageReversalAsync(postingId.Value, reason, context.User.FindFirstValue(KjClaims.Subject), context.RequestAborted);
            if (!stagedReversal.IsSuccess)
            {
                return ApiError.Create(context, stagedReversal.Error!.Code == "NOT_FOUND" ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict, stagedReversal.Error.Code, stagedReversal.Error.Message);
            }

            var reversal = new DonationEntity
            {
                Id = PublicDonationEndpoints.NewId("don"),
                Reference = await PublicDonationEndpoints.NewReferenceAsync(db),
                Kind = DonationKinds.Reversal,
                Amount = -original.Amount,
                FeeAmount = -original.FeeAmount,
                Currency = original.Currency,
                DesignationType = original.DesignationType,
                DesignationId = original.DesignationId,
                IsAnonymous = original.IsAnonymous,
                DonorNameProtected = original.DonorNameProtected,
                DonorEmailProtected = original.DonorEmailProtected,
                DonorPhoneProtected = original.DonorPhoneProtected,
                Language = original.Language,
                RecurringPlanId = original.RecurringPlanId,
                ReversesDonationId = original.Id,
                ReversalReason = reason,
                CreatedBy = context.User.FindFirstValue(KjClaims.Subject),
                CreatedAt = now,
                ReceivedOn = DateOnly.FromDateTime(now.UtcDateTime)
            };
            reversal.Events.Add(new DonationEventEntity { Status = DonationStatuses.Confirmed, Note = $"Reverses {original.Reference}", At = now });
            db.Donations.Add(reversal);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // The unique index on the reversed donation: another request got there first.
                return ApiError.Create(context, StatusCodes.Status409Conflict, "ALREADY_REVERSED", "This donation has already been reversed.");
            }

            var created = new DonationView { Donation = reversal, Latest = DonationStatuses.Confirmed, Reversed = false };
            return Results.Created($"{Route}/donations/{reversal.Id}", await DetailAsync(db, created, protector));
        })
        .WithName("ReverseFinanceDonation")
        .WithSummary("Post a reversing entry for a confirmed donation")
        .WithDescription("The original is never changed: a new entry with negated amount and fee is appended, the donation's ledger posting is reversed in the same commit, and the original reads as `refunded`. A donation can be reversed once, and not when the month the reversal would land in is closed (`PERIOD_CLOSED`). Requires `finance.donation.reverse`.")
        .WithTags("Finance")
        .RequirePermission(Permissions.FinanceDonationReverse);

        app.MapGet($"{Route}/recurring-plans", async (FinanceDbContext db, IDonorProtector protector, int? limit, int? offset) =>
        {
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var skip = Math.Max(0, offset ?? 0);
            var total = await db.RecurringPlans.CountAsync();
            var plans = await db.RecurringPlans.AsNoTracking().OrderByDescending(p => p.StartsOn).ThenByDescending(p => p.Id).Skip(skip).Take(take).ToListAsync();
            var rows = plans.Select(p => new RecurringPlanRow(
                p.Id, p.Interval, PublicDonationEndpoints.Format(p.Amount), PublicDonationEndpoints.Format(p.FeeAmount), p.Currency,
                DesignationText(p.DesignationType, p.DesignationId), p.IsAnonymous, protector.Unprotect(p.DonorNameProtected), PublicDonationEndpoints.FormatDate(p.StartsOn)));
            return Results.Ok(new PagingEnvelope<RecurringPlanRow>([.. rows], total, null, take, skip));
        })
        .WithName("ListRecurringPlans")
        .WithSummary("Recurring donation plans, newest first")
        .WithTags("Finance")
        .RequirePermission(Permissions.FinanceView);
    }

    // The latest event and whether a reversal exists are worked out in SQL, so filters and paging see the same status the row shows.
    private static IQueryable<DonationView> Views(FinanceDbContext db) =>
        db.Donations.AsNoTracking().Select(d => new DonationView
        {
            Donation = d,
            Latest = db.DonationEvents.Where(e => e.DonationId == d.Id).OrderByDescending(e => e.Id).Select(e => e.Status).FirstOrDefault() ?? DonationStatuses.Pending,
            Reversed = db.Donations.Any(r => r.ReversesDonationId == d.Id),
            Gateway = db.PaymentAttempts.Where(a => a.DonationId == d.Id && a.Status == PaymentAttemptStatuses.Created).OrderByDescending(a => a.Id).Select(a => a.Gateway).FirstOrDefault()
        });

    private static FinanceDonationRow Row(DonationView view, IDonorProtector protector)
    {
        var d = view.Donation;
        return new FinanceDonationRow(
            d.Id, d.Reference, PublicDonationEndpoints.FormatDate(d.ReceivedOn), protector.Unprotect(d.DonorNameProtected), d.IsAnonymous,
            PublicDonationEndpoints.Format(d.Amount), PublicDonationEndpoints.Format(d.FeeAmount), d.Currency, view.Gateway, false,
            DesignationText(d.DesignationType, d.DesignationId), d.Kind, view.Reversed ? "refunded" : view.Latest);
    }

    private static async Task<FinanceDonationDetail> DetailAsync(FinanceDbContext db, DonationView view, IDonorProtector protector)
    {
        var d = view.Donation;
        var events = await db.DonationEvents.AsNoTracking().Where(e => e.DonationId == d.Id).OrderBy(e => e.Id).ToListAsync();
        var reversedBy = await db.Donations.AsNoTracking().Where(r => r.ReversesDonationId == d.Id).Select(r => r.Id).FirstOrDefaultAsync();
        var paid = await db.PaymentSettlements.AsNoTracking().FirstOrDefaultAsync(p => p.DonationId == d.Id);
        var settlement = paid is null ? null : new SettlementView(
            paid.Gateway, PublicDonationEndpoints.Format(paid.SettledAmount), paid.SettledCurrency, paid.Fee is { } f ? PublicDonationEndpoints.Format(f) : null,
            paid.ExchangeRate?.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture), paid.TransactionId);
        return new FinanceDonationDetail(
            Row(view, protector), protector.Unprotect(d.DonorEmailProtected), d.DonorPhoneProtected is null ? null : protector.Unprotect(d.DonorPhoneProtected),
            d.Dedication, d.RecurringPlanId, d.ReversesDonationId, reversedBy, d.ReversalReason,
            [.. events.Select(e => new FinanceDonationEvent(e.Status, e.Note, e.At))], settlement);
    }

    private static string DesignationText(string type, string? id) => type == Designations.General ? type : $"{type}:{id}";
}
