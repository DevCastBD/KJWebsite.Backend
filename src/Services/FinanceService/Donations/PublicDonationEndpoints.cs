using FinanceService.Campaigns;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Payments;
using FinanceService.Receipts;
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

        app.MapPost(Route, async (HttpContext context, DonationRequest payload, FinanceDbContext db, IDonorProtector protector, IOptions<DonationOptions> options, PaymentGatewayRegistry gateways, IOptions<PaymentOptions> payments, ILogger<DonationRequest> logger, TimeProvider clock) =>
        {
            var (donation, invalid) = DonationValidator.Validate(payload, options.Value, gateways.KnownIds);
            if (donation is null)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
            }

            var now = clock.GetUtcNow();
            var today = DateOnly.FromDateTime(now.UtcDateTime);

            // A donor can only designate a campaign that exists and is open to gifts today.
            if (donation.DesignationType == Designations.Campaign && !await CampaignRules.AcceptsPublicGiftAsync(db, donation.DesignationId!, today))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: designation.id.");
            }

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
                CommunicationConsent = donation.CommunicationConsent,
                RecurringPlanId = planId,
                CreatedAt = now,
                ReceivedOn = today
            };
            entity.Events.Add(new DonationEventEntity { Status = DonationStatuses.Pending, At = now });
            db.Donations.Add(entity);
            await db.SaveChangesAsync();

            var total = donation.Fee.IsZero ? null : (donation.Amount + donation.Fee).ToDecimalString();
            // The donation is already saved: whatever happens at the gateway, the donor's intent is never lost.
            var checkout = await StartCheckoutAsync(context, db, gateways, payments.Value, logger, entity, donation, clock);
            var body = new CreatedDonation(entity.Id, reference, DonationStatuses.Pending, donation.Amount.ToDecimalString(), donation.Amount.Currency,
                donation.Fee.IsZero ? null : donation.Fee.ToDecimalString(), total,
                checkout?.Gateway, checkout?.Url, checkout is null ? "offline" : null);
            return Results.Created($"{Route}/{reference}/status", body);
        })
        .RequireIdempotency()
        .WithName("CreateDonation")
        .WithSummary("Start a donation")
        .WithDescription("Records the intent as `pending` and starts a hosted checkout: the response carries `gateway` and `checkoutUrl` to send the donor to. Pass `gateway` (bkash, nagad, sslcommerz) to choose one; otherwise the first enabled gateway that supports the currency is used. When no checkout can be started (no gateway switched on, or it is down) the donation is still recorded and the response has `fallback: offline` instead. A return from the gateway never confirms payment. Amounts are decimal strings with at most two decimals and must sit inside the currency's limits (see presets). With `coverFees` the response carries `fee` and `total`, and `amount` stays the designated gift. A `monthly` or `yearly` frequency also creates a recurring plan. Anonymous gifts are never attributed in any public response.")
        .WithTags("Donations")
        .AllowAnonymous();

        app.MapGet($"{Route}/{{reference}}/status", async (HttpContext context, string reference, FinanceDbContext db, IOptions<PaymentOptions> payments) =>
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
                    Reversed = db.Donations.Any(r => r.ReversesDonationId == d.Id),
                    Gateway = db.PaymentAttempts.Where(a => a.DonationId == d.Id && a.Status == PaymentAttemptStatuses.Created).OrderByDescending(a => a.Id).Select(a => a.Gateway).FirstOrDefault(),
                    HasReceipt = db.Receipts.Any(r => r.DonationId == d.Id && !db.ReceiptVoids.Any(v => v.ReceiptId == r.Id))
                })
                .FirstOrDefaultAsync();
            if (row is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Donation not found.");
            }

            // A reversed gift is no longer money we hold, so the donor's page shows it as cancelled.
            var status = row.Reversed ? DonationStatuses.Cancelled : row.Latest ?? DonationStatuses.Pending;
            return Results.Ok(new DonationStatusView(row.Reference, status, Format(row.Amount), row.Currency, row.IsAnonymous, row.Gateway,
                row.HasReceipt ? $"{payments.Value.PublicApiBaseUrl.TrimEnd('/')}{Route}/{row.Reference}/receipt.pdf" : null));
        })
        .WithName("GetDonationStatus")
        .WithSummary("The outcome of one donation, by its public reference")
        .WithDescription("Never cached. Returns the reference, status, designated amount, currency, the gateway used (when one was) and the anonymous flag, and nothing about the donor.")
        .WithTags("Donations")
        .AllowAnonymous();

        app.MapGet($"{Route}/{{reference}}/receipt.pdf", async (HttpContext context, string reference, FinanceDbContext db, ReceiptService receipts) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var receipt = DonationReference.LooksValid(reference)
                ? await (from r in db.Receipts.AsNoTracking()
                         join d in db.Donations.AsNoTracking() on r.DonationId equals d.Id
                         where d.Reference == reference && d.Kind != DonationKinds.Reversal && !db.ReceiptVoids.Any(v => v.ReceiptId == r.Id)
                         select r).FirstOrDefaultAsync()
                : null;
            if (receipt is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Receipt not found.");
            }

            context.Response.Headers.ContentDisposition = $"inline; filename=\"{receipt.Number}.pdf\"";
            return Results.File(ReceiptPdf.Render((await receipts.DocumentForAsync(receipt, context.RequestAborted))!), "application/pdf");
        })
        .WithName("GetDonationReceiptPdf")
        .WithSummary("The donor's receipt as a bilingual PDF")
        .WithDescription("Available once the donation is confirmed, and gone (404) if it is reversed. The unguessable reference is the access key, as for the status page. Never cached.")
        .WithTags("Donations")
        .AllowAnonymous();

        // Gateways send the donor back here (SSLCommerz with a POST, the wallets with a GET). The request is only a redirect to the site's status page:
        // nothing in it is trusted or read, and payment is confirmed solely by the signed webhook (BE-056).
        // The gateways disagree on the method (SSLCommerz POSTs, the wallets GET), so both are mapped to one handler under distinct operation ids.
        var returnHandler = async (HttpContext context, string reference, string? result, FinanceDbContext db, IOptions<PaymentOptions> payments) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var target = await StatusPageUrlAsync(db, payments.Value, reference, result == "cancel" ? "cancel" : "return");
            context.Response.Headers.Location = target;
            return Results.StatusCode(StatusCodes.Status303SeeOther);
        };

        foreach (var method in new[] { HttpMethods.Get, HttpMethods.Post })
        {
            app.MapMethods($"{Route}/{{reference}}/return", [method], returnHandler)
            .WithName($"DonationGatewayReturn{method[0]}{method[1..].ToLowerInvariant()}")
            .WithSummary("Where a payment gateway sends the donor back to")
            .WithDescription("Accepts `?result=success|fail|cancel` and answers `303` to the site's `/{lang}/donate/status?ref=&gateway=&result=return|cancel`. It never changes a donation: only the signed webhook confirms payment.")
            .WithTags("Donations")
            .AllowAnonymous();
        }
    }

    /// <summary>Where the donor lands after a gateway: the site's status page for their language and reference. Unknown or malformed references land on the plain lookup page.</summary>
    internal static async Task<string> StatusPageUrlAsync(FinanceDbContext db, PaymentOptions payments, string reference, string result)
    {
        var language = "en";
        string? gateway = null;
        var known = false;
        if (DonationReference.LooksValid(reference))
        {
            var row = await db.Donations.AsNoTracking()
                .Where(d => d.Reference == reference && d.Kind != DonationKinds.Reversal)
                .Select(d => new
                {
                    d.Language,
                    Gateway = db.PaymentAttempts.Where(a => a.DonationId == d.Id && a.Status == PaymentAttemptStatuses.Created).OrderByDescending(a => a.Id).Select(a => a.Gateway).FirstOrDefault()
                })
                .FirstOrDefaultAsync();
            if (row is not null)
            {
                known = true;
                language = row.Language;
                gateway = row.Gateway;
            }
        }

        var site = $"{payments.SiteBaseUrl.TrimEnd('/')}/{language}/donate/status";
        return known ? $"{site}?ref={Uri.EscapeDataString(reference)}{(gateway is null ? string.Empty : $"&gateway={Uri.EscapeDataString(gateway)}")}&result={result}" : site;
    }

    private static readonly Action<ILogger, string, string, string, Exception?> GatewayFailed = LoggerMessage.Define<string, string, string>(
        LogLevel.Warning, new EventId(1, nameof(GatewayFailed)), "Gateway {Gateway} could not start a checkout for {Reference}: {Code}");

    private sealed record StartedCheckout(string Gateway, string Url);

    // Tries each candidate gateway in turn; every try, good or bad, is recorded. Nothing a gateway does can fail the donation: no candidate means offline giving.
    private static async Task<StartedCheckout?> StartCheckoutAsync(
        HttpContext context, FinanceDbContext db, PaymentGatewayRegistry gateways, PaymentOptions payments, ILogger logger,
        DonationEntity entity, ValidDonation donation, TimeProvider clock)
    {
        var request = new CheckoutRequest(
            entity.Id, entity.Reference, donation.Amount + donation.Fee, donation.DonorName, donation.DonorEmail, donation.DonorPhone, donation.Language,
            ReturnUrlsFor(payments, entity.Reference, string.Empty), ClientIp(context));

        foreach (var gateway in gateways.Candidates(donation.Gateway, entity.Currency))
        {
            try
            {
                var session = await gateway.CreateCheckoutAsync(request with { Returns = ReturnUrlsFor(payments, entity.Reference, gateway.Id) }, context.RequestAborted);
                db.PaymentAttempts.Add(new PaymentAttemptEntity { DonationId = entity.Id, Gateway = gateway.Id, Status = PaymentAttemptStatuses.Created, ProviderPaymentId = session.ProviderPaymentId, At = clock.GetUtcNow() });
                await db.SaveChangesAsync();
                return new StartedCheckout(gateway.Id, session.RedirectUrl.ToString());
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var code = exception is GatewayException gatewayException ? gatewayException.Code : "GATEWAY_ERROR";
                // Only the stable code is logged and stored: a gateway's own message or request could carry credentials or donor details.
                GatewayFailed(logger, gateway.Id, entity.Reference, code, null);
                db.PaymentAttempts.Add(new PaymentAttemptEntity { DonationId = entity.Id, Gateway = gateway.Id, Status = PaymentAttemptStatuses.Failed, ErrorCode = code, At = clock.GetUtcNow() });
                await db.SaveChangesAsync();
            }
        }

        return null;
    }

    private static ReturnUrls ReturnUrlsFor(PaymentOptions payments, string reference, string gateway)
    {
        Uri Url(string result) => new($"{payments.PublicApiBaseUrl.TrimEnd('/')}{Route}/{reference}/return?result={result}");
        return new ReturnUrls(Url("success"), Url("fail"), Url("cancel"), new Uri($"{payments.PublicApiBaseUrl.TrimEnd('/')}{Route}/webhooks/{gateway}"));
    }

    private static string ClientIp(HttpContext context) => context.Connection.RemoteIpAddress switch
    {
        { IsIPv4MappedToIPv6: true } mapped => mapped.MapToIPv4().ToString(),
        { AddressFamily: System.Net.Sockets.AddressFamily.InterNetwork } v4 => v4.ToString(),
        _ => "127.0.0.1"
    };

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
