using System.Globalization;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Donors;

/// <summary>What a donor may see of their own record: nothing staff keep for themselves (notes, status, keys, account state).</summary>
public sealed record DonorMeView(string Name, string Type, string Organisation, string Email, string Phone, string Address, string Country, string Language, string Since);

/// <summary>One of the donor's own gifts, from a valid receipt. `Designation` is a label for display; the student's name joins it with #214.</summary>
public sealed record DonorMeGift(string Id, string Reference, string Date, string Amount, string Currency, string Designation, string DesignationType, string? DesignationId, string Status, bool InKind, string ReceiptNumber);

public sealed record DonorMeTotal(string Currency, string Cash, string InKind);

public sealed record DonorMeDonations(IReadOnlyList<DonorMeGift> Items, int Total, int Limit, int Offset, IReadOnlyList<DonorMeTotal> Totals);

/// <summary>
/// The signed-in donor's own data, read-only (#212). No route takes a donor id: the donor is found from the token's subject, so one
/// donor cannot name another, and an id sent in the query or body is never read. Only the Donor role may call it. A donor whose record
/// is inactive or whose sign-in is disabled is refused on every request, so it holds before the access token expires (D16).
/// </summary>
public static class DonorSelfEndpoints
{
    public const string Route = "/api/v1/donors/me";
    private const string Tag = "Donor portal";
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;
    private static readonly string[] Writes = ["POST", "PUT", "PATCH", "DELETE"];

    /// <summary>
    /// Personal data must never sit in a shared cache. The headers are set when the response starts, so they also cover a refusal
    /// written by the authorization step, which runs before any endpoint code.
    /// </summary>
    public static IApplicationBuilder UseDonorSelfHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments(Route))
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "private, no-store";
                context.Response.Headers.Vary = "Authorization";
                return Task.CompletedTask;
            });
        }

        await next();
    });

    public static void MapDonorSelf(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (HttpContext context, FinanceDbContext db, IDonorProtector protector) =>
            await WithDonor(context, db, donor =>
            {
                var view = new DonorMeView(
                    protector.Unprotect(donor.NameProtected),
                    donor.Type,
                    donor.Organisation ?? string.Empty,
                    protector.Unprotect(donor.EmailProtected),
                    donor.PhoneProtected is null ? string.Empty : protector.Unprotect(donor.PhoneProtected),
                    donor.AddressProtected is null ? string.Empty : protector.Unprotect(donor.AddressProtected),
                    donor.Country ?? string.Empty,
                    donor.Language,
                    donor.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return Task.FromResult(Results.Ok(view));
            }))
        .WithName("GetMyDonorProfile")
        .WithSummary("The donor's own profile")
        .WithDescription("Donor-visible fields only: never notes, staff status, account state or keys. The donor is found from the token; no id is accepted. 403 for any role but Donor, for an inactive donor and for a disabled account.")
        .WithTags(Tag)
        .RequireDonorSelf();

        app.MapGet($"{Route}/donations", async (HttpContext context, FinanceDbContext db, int? limit, int? offset, int? year) =>
        {
            var invalid = new List<string>();
            if (limit is < 1 or > MaxLimit)
            {
                invalid.Add("limit");
            }

            if (offset is < 0)
            {
                invalid.Add("offset");
            }

            if (year is < 1970 or > 9999)
            {
                invalid.Add("year");
            }

            if (invalid.Count > 0)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
            }

            return await WithDonor(context, db, async donor =>
            {
                var rows = await (from r in db.Receipts.AsNoTracking()
                                  join d in db.Donations.AsNoTracking() on r.DonationId equals d.Id
                                  where r.DonorKey == donor.DonorKey && !r.IsAnonymous && !db.ReceiptVoids.Any(v => v.ReceiptId == r.Id)
                                  select new { r.ReceivedOn, r.Number, r.Amount, r.Currency, r.IsInKind, r.DesignationType, r.DesignationId, DonationId = d.Id, d.Reference })
                    .ToListAsync(context.RequestAborted);
                if (year is { } wanted)
                {
                    rows = [.. rows.Where(r => r.ReceivedOn.Year == wanted)];
                }

                // Newest first; the receipt number breaks ties so the order never changes between pages.
                var ordered = rows.OrderByDescending(r => r.ReceivedOn).ThenByDescending(r => r.Number, StringComparer.Ordinal).ToList();
                var take = limit ?? DefaultLimit;
                var skip = offset ?? 0;

                var campaignIds = ordered.Skip(skip).Take(take).Where(r => r.DesignationType == Designations.Campaign && r.DesignationId is not null).Select(r => r.DesignationId!).Distinct().ToList();
                var campaigns = await db.Campaigns.AsNoTracking().Where(c => campaignIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, context.RequestAborted);

                string Label(string type, string? id) => type switch
                {
                    Designations.Campaign when id is not null && campaigns.TryGetValue(id, out var campaign) =>
                        donor.Language == "bn" && !string.IsNullOrWhiteSpace(campaign.TitleBn) ? campaign.TitleBn! : campaign.Title,
                    Designations.General => "General",
                    _ => id ?? type
                };

                var page = ordered.Skip(skip).Take(take).Select(r => new DonorMeGift(
                    r.DonationId, r.Reference, r.ReceivedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Amount.ToString("0.00", CultureInfo.InvariantCulture), r.Currency,
                    Label(r.DesignationType, r.DesignationId), r.DesignationType, r.DesignationId, "confirmed", r.IsInKind, r.Number)).ToList();

                // Totals cover everything the filter matched, not just this page, and never add one currency to another.
                var totals = DonorSegments.Totals(ordered.Select(r => new GiftFact(r.ReceivedOn, r.Currency, r.Amount, r.IsInKind, false)))
                    .Select(t => new DonorMeTotal(t.Currency, t.Cash.ToDecimalString(), t.InKind.ToDecimalString())).ToList();
                return Results.Ok(new DonorMeDonations(page, ordered.Count, take, skip, totals));
            });
        })
        .WithName("ListMyDonations")
        .WithSummary("The donor's own gifts")
        .WithDescription("From valid receipts, newest first; reversed and anonymous gifts are absent. `limit` 1 to 100 (default 20), `offset`, optional calendar `year`. `totals` are per currency over the whole filtered set.")
        .WithTags(Tag)
        .RequireDonorSelf();

        // A mapped path with no handler for a method answers through the framework's own endpoint, which the deny-by-default policy would
        // refuse with 401 or 403. These say what is true: the self-service is read-only. They read nothing and change nothing.
        app.MapMethods(Route, Writes, (HttpContext context) => ReadOnly(context))
            .ExcludeFromDescription().AllowAnonymous();
        app.MapMethods($"{Route}/donations", Writes, (HttpContext context) => ReadOnly(context))
            .ExcludeFromDescription().AllowAnonymous();
    }

    private static IResult ReadOnly(HttpContext context)
    {
        context.Response.Headers.Allow = "GET";
        return ApiError.Create(context, StatusCodes.Status405MethodNotAllowed, "METHOD_NOT_ALLOWED", "Donor self-service is read-only.");
    }

    /// <summary>Finds the donor from the token's subject and refuses an inactive donor or a disabled sign-in before any data is read.</summary>
    private static async Task<IResult> WithDonor(HttpContext context, FinanceDbContext db, Func<DonorEntity, Task<IResult>> action)
    {
        var subject = context.User.FindFirst(KjClaims.Subject)?.Value;
        var donor = string.IsNullOrEmpty(subject) ? null : await db.Donors.AsNoTracking().FirstOrDefaultAsync(d => d.AccountSubject == subject, context.RequestAborted);
        if (donor is null)
        {
            return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_A_DONOR", "The signed-in account has no donor record.");
        }

        if (donor.Status == DonorStatuses.Inactive)
        {
            return ApiError.Create(context, StatusCodes.Status403Forbidden, "DONOR_INACTIVE", "This donor record is inactive.");
        }

        return donor.AccountStatus == AccountStatuses.Disabled
            ? ApiError.Create(context, StatusCodes.Status403Forbidden, "ACCOUNT_DISABLED", "This sign-in has been disabled.")
            : await action(donor);
    }
}
