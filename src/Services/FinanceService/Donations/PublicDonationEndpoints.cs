using FinanceService.Data;
using FinanceService.Data.Entities;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Donations;

/// <summary>The donor-facing API: the amount presets, the donation form and the status page's lookup. None of it returns donor details.</summary>
public static class PublicDonationEndpoints
{
    private const string Route = "/api/v1/donations";

    public static void MapPublicDonations(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{Route}/presets", (HttpContext context, IOptions<DonationOptions> options) =>
        {
            var o = options.Value;
            context.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Ok(new DonationPresets(
                o.DefaultCurrency,
                [.. o.Currencies.Select(c => new CurrencyPresets(c.Key, [.. c.Value.Presets.Select(Format)], Format(c.Value.Min), Format(c.Value.Max)))],
                ["once", "monthly", "yearly"]));
        })
        .WithName("GetDonationPresets")
        .WithSummary("Suggested amounts and limits per currency")
        .WithDescription("Amounts are decimal strings. Currency rules come from configuration, so they can change without a frontend release.")
        .WithTags("Donations")
        .AllowAnonymous();

        app.MapPost(Route, async (HttpContext context, DonationRequest payload, FinanceDbContext db, IDonorProtector protector, IOptions<DonationOptions> options, TimeProvider clock) =>
        {
            var (donation, invalid) = DonationValidator.Validate(payload, options.Value);
            if (donation is null)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
            }

            var now = clock.GetUtcNow();
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var name = protector.Protect(donation.DonorName);
            var email = protector.Protect(donation.DonorEmail);
            var phone = donation.DonorPhone is null ? null : protector.Protect(donation.DonorPhone);

            string? planId = null;
            if (donation.RecurringInterval is not null)
            {
                planId = NewId("plan");
                db.RecurringPlans.Add(new RecurringPlanEntity
                {
                    Id = planId,
                    Interval = donation.RecurringInterval,
                    Amount = donation.Amount.Amount,
                    FeeAmount = donation.Fee.Amount,
                    Currency = donation.Amount.Currency,
                    DesignationType = donation.DesignationType,
                    DesignationId = donation.DesignationId,
                    IsAnonymous = donation.Anonymous,
                    Dedication = donation.Dedication,
                    DonorNameProtected = name,
                    DonorEmailProtected = email,
                    DonorPhoneProtected = phone,
                    Language = donation.Language,
                    StartsOn = today,
                    CreatedAt = now
                });
            }

            var reference = await NewReferenceAsync(db);
            var entity = new DonationEntity
            {
                Id = NewId("don"),
                Reference = reference,
                Kind = planId is null ? DonationKinds.OneTime : DonationKinds.Recurring,
                Amount = donation.Amount.Amount,
                FeeAmount = donation.Fee.Amount,
                Currency = donation.Amount.Currency,
                DesignationType = donation.DesignationType,
                DesignationId = donation.DesignationId,
                IsAnonymous = donation.Anonymous,
                Dedication = donation.Dedication,
                DonorNameProtected = name,
                DonorEmailProtected = email,
                DonorPhoneProtected = phone,
                Language = donation.Language,
                RecurringPlanId = planId,
                CreatedAt = now,
                ReceivedOn = today
            };
            entity.Events.Add(new DonationEventEntity { Status = DonationStatuses.Pending, At = now });
            db.Donations.Add(entity);
            await db.SaveChangesAsync();

            var total = donation.Fee.IsZero ? null : (donation.Amount + donation.Fee).ToDecimalString();
            var body = new CreatedDonation(entity.Id, reference, DonationStatuses.Pending, donation.Amount.ToDecimalString(), donation.Amount.Currency,
                donation.Fee.IsZero ? null : donation.Fee.ToDecimalString(), total);
            return Results.Created($"{Route}/{reference}/status", body);
        })
        .RequireIdempotency()
        .WithName("CreateDonation")
        .WithSummary("Start a donation")
        .WithDescription("Records the intent as `pending`; payment is attached later by the gateway adapters. Amounts are decimal strings with at most two decimals and must sit inside the currency's limits (see presets). With `coverFees` the response carries `fee` and `total`, and `amount` stays the designated gift. A `monthly` or `yearly` frequency also creates a recurring plan. Anonymous gifts are never attributed in any public response.")
        .WithTags("Donations")
        .AllowAnonymous();

        app.MapGet($"{Route}/{{reference}}/status", async (HttpContext context, string reference, FinanceDbContext db) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!DonationReference.LooksValid(reference))
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Donation not found.");
            }

            var row = await db.Donations.AsNoTracking()
                .Where(d => d.Reference == reference && d.Kind != DonationKinds.Reversal)
                .Select(d => new
                {
                    d.Reference,
                    d.Amount,
                    d.Currency,
                    d.IsAnonymous,
                    Latest = db.DonationEvents.Where(e => e.DonationId == d.Id).OrderByDescending(e => e.Id).Select(e => e.Status).FirstOrDefault(),
                    Reversed = db.Donations.Any(r => r.ReversesDonationId == d.Id)
                })
                .FirstOrDefaultAsync();
            if (row is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Donation not found.");
            }

            // A reversed gift is no longer money we hold, so the donor's page shows it as cancelled.
            var status = row.Reversed ? DonationStatuses.Cancelled : row.Latest ?? DonationStatuses.Pending;
            return Results.Ok(new DonationStatusView(row.Reference, status, Format(row.Amount), row.Currency, row.IsAnonymous));
        })
        .WithName("GetDonationStatus")
        .WithSummary("The outcome of one donation, by its public reference")
        .WithDescription("Never cached. Returns the reference, status, designated amount, currency and the anonymous flag, and nothing about the donor.")
        .WithTags("Donations")
        .AllowAnonymous();
    }

    internal static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    internal static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    internal static string Format(decimal amount) => amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    internal static async Task<string> NewReferenceAsync(FinanceDbContext db)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = DonationReference.New();
            if (!await db.Donations.AnyAsync(d => d.Reference == candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not allocate a unique donation reference.");
    }
}
