using System.Globalization;
using System.Security.Claims;
using System.Text;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Ledger;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Finance;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Donors;

public sealed record DonorTotalView(string Currency, string Cash, string InKind);

/// <summary>A donor as the back office lists them. `Total` and `Currency` give the main currency (the largest total) for the frontend; `Totals` has every currency.</summary>
public sealed record DonorRow(string Id, string Name, string Email, string Total, string Currency, int Count, string LastGiftOn, IReadOnlyList<string> Segments, IReadOnlyList<DonorTotalView> Totals, string Type, string Status, string AccountStatus);

public sealed record ConsentState(bool Granted, string? Source, DateTimeOffset? At);

public sealed record DonorGiftView(string Id, string Reference, string Date, string DonorId, string DonorName, string Amount, string Currency, string? Method, bool Offline, string Designation, string Status, string Receipt);

public sealed record ConsentEventView(string Purpose, bool Granted, string Source, string? RecordedBy, DateTimeOffset At);

/// <summary>The profile fields beyond what the list shows. `MustChangePassword` is read from Identity just now and is null when Identity could not answer or the donor has no account.</summary>
public sealed record DonorProfile(string Type, string? Organisation, string? Address, string? Country, string? Notes, string Language, string Status, string AccountStatus, bool AccountShared, bool? MustChangePassword, DateTimeOffset CreatedAt);

public sealed record DonorDetail(DonorRow Donor, string? Phone, string FirstGiftOn, IReadOnlyDictionary<string, ConsentState> Consent, IReadOnlyList<ConsentEventView> ConsentHistory, IReadOnlyList<DonorGiftView> Gifts, DonorProfile Profile);

public sealed record ConsentRequest(string? Purpose, bool Granted, string? Source);

public sealed record DonorMessageRequest(string? Template, string? Segment, string? AsOf);

public sealed record DonorMessageResult(string Template, string Purpose, int Queued, int Suppressed);

public sealed record DonorReconciliationLine(string DonorId, string Currency, string Receipts, string Ledger, string Difference);

public sealed record DonorReconciliation(bool Reconciled, int Donors, IReadOnlyList<DonorReconciliationLine> Mismatches);

public static class DonorEndpoints
{
    private const string Route = "/api/v1/admin/finance/donors";
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    public static void MapDonors(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (HttpContext context, DonorData data, string? segment, string? asOf, string? q, string? status, string? accountStatus, bool? hasAccount, int? limit, int? offset) =>
        {
            if (!TryScope(context, segment, asOf, data.Today, out var date, out var error))
            {
                return error!;
            }

            var donors = (await data.LoadAsync(date, context.RequestAborted)).Where(d => string.IsNullOrEmpty(segment) || d.Segments.Contains(segment));
            if ((status is not null && !DonorStatuses.All.Contains(status)) || (accountStatus is not null && !AccountStatuses.All.Contains(accountStatus)))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: status (active or inactive) or accountStatus (none, active or disabled).");
            }

            donors = donors.Where(d => (status is null || d.Entity.Status == status)
                && (accountStatus is null || d.Entity.AccountStatus == accountStatus)
                && (hasAccount is null || (d.Entity.AccountSubject is not null) == hasAccount));
            if (!string.IsNullOrWhiteSpace(q))
            {
                donors = donors.Where(d => d.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || d.Email.Contains(q, StringComparison.OrdinalIgnoreCase));
            }

            var all = donors.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Entity.Id, StringComparer.Ordinal).ToList();
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var skip = Math.Max(0, offset ?? 0);
            return Results.Ok(new PagingEnvelope<DonorRow>([.. all.Skip(skip).Take(take).Select(d => d.Row())], all.Count, null, take, skip));
        })
        .WithName("ListDonors")
        .WithSummary("Donors with lifetime giving and segments")
        .WithDescription("Only non-anonymous gifts with an email build a donor: an anonymous gift leaves no attributable record. Giving is derived from valid receipts, so it always matches the books. Filters: segment (recurring, major, new, lapsed), asOf (yyyy-MM-dd; segments are re-run as of that date), q (name or email).")
        .WithTags("Donors")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/{{id}}", async (HttpContext context, string id, DonorData data, IAuthDonorClient auth, string? asOf) =>
        {
            if (!TryScope(context, null, asOf, data.Today, out var date, out var error))
            {
                return error!;
            }

            var donor = (await data.LoadAsync(date, context.RequestAborted)).FirstOrDefault(d => d.Entity.Id == id);
            if (donor is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Donor not found.");
            }

            var live = donor.Entity.AccountSubject is null ? null : await auth.GetAsync(donor.Entity.AccountSubject, DonorProfileEndpoints.Bearer(context), context.RequestAborted);
            context.Response.Headers.ETag = DonorProfileEndpoints.ETagOf(donor.Entity);
            return Results.Ok(await data.DetailAsync(donor, live, context.RequestAborted));
        })
        .WithName("GetDonor")
        .WithSummary("One donor with contact, consent and its history, and every gift")
        .WithTags("Donors")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/{{id}}/donations", async (HttpContext context, string id, DonorData data) =>
        {
            var donor = (await data.LoadAsync(data.Today, context.RequestAborted)).FirstOrDefault(d => d.Entity.Id == id);
            return donor is null ? ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Donor not found.") : Results.Ok(new { items = donor.GiftViews() });
        })
        .WithName("ListDonorDonations")
        .WithSummary("A donor's confirmed, unreversed gifts, newest first")
        .WithTags("Donors")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/{{id}}/consent", async (HttpContext context, string id, ConsentRequest payload, FinanceDbContext db, DonorMessenger messenger, TimeProvider clock) =>
        {
            if (payload.Purpose is null || !ConsentPurposes.All.Contains(payload.Purpose) || (payload.Source is not null && !ConsentSources.All.Contains(payload.Source) || payload.Source == ConsentSources.DonationForm))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: purpose (thanks, updates, appeals) or source (admin, donor_request).");
            }

            if (!await db.Donors.AnyAsync(d => d.Id == id))
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Donor not found.");
            }

            // Recorded as given: a grant or a withdrawal is a new row, and the latest one wins.
            db.DonorConsents.Add(new DonorConsentEntity
            {
                DonorId = id,
                Purpose = payload.Purpose,
                Granted = payload.Granted,
                Source = payload.Source ?? ConsentSources.Admin,
                RecordedBy = context.User.FindFirstValue(KjClaims.Subject),
                At = clock.GetUtcNow()
            });
            await db.SaveChangesAsync();
            return Results.Ok(await ConsentOf(db, id, messenger));
        })
        .WithName("ChangeDonorConsent")
        .WithSummary("Record a donor granting or withdrawing consent for one purpose")
        .WithDescription("Purposes: thanks, updates, appeals. Source is `admin` (default) or `donor_request` (the donor asked us to); the donation form's own consent is recorded automatically and cannot be set here. Append-only; the latest entry per purpose is the donor's current consent. Requires `finance.donor.manage`.")
        .WithTags("Donors")
        .RequirePermission(Permissions.FinanceDonorManage);

        app.MapPost($"{Route}/messages", async (HttpContext context, DonorMessageRequest payload, DonorData data, FinanceDbContext db, DonorMessenger messenger) =>
        {
            if (payload.Template is not ("donor.appeal" or "donor.update"))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: template (donor.appeal or donor.update).");
            }

            if (!TryScope(context, payload.Segment, payload.AsOf, data.Today, out var date, out var error))
            {
                return error!;
            }

            int queued = 0, suppressed = 0;
            foreach (var donor in (await data.LoadAsync(date, context.RequestAborted)).Where(d => string.IsNullOrEmpty(payload.Segment) || d.Segments.Contains(payload.Segment)))
            {
                var outcome = await messenger.StageAsync(donor.Entity, payload.Template, new { segment = payload.Segment, asOf = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, cancellationToken: context.RequestAborted);
                if (outcome == SendOutcome.Queued)
                {
                    queued++;
                }
                else
                {
                    suppressed++;
                }
            }

            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Ok(new DonorMessageResult(payload.Template, DonorMessenger.PurposeOf(payload.Template), queued, suppressed));
        })
        .WithName("QueueDonorMessages")
        .WithSummary("Queue an appeal or update to a segment, through the consent gate")
        .WithDescription("Template `donor.appeal` needs each donor's `appeals` consent and `donor.update` their `updates` consent; donors without it are suppressed (and the suppression is recorded), never written to. Requires `finance.donor.manage`.")
        .WithTags("Donors")
        .RequirePermission(Permissions.FinanceDonorManage);

        app.MapGet($"{Route}/export", async (HttpContext context, DonorData data, FinanceDbContext db, DonorMessenger messenger, TimeProvider clock, string? segment, string? asOf, string? consent) =>
        {
            if (!TryScope(context, segment, asOf, data.Today, out var date, out var error))
            {
                return error!;
            }

            if (consent is not null && !ConsentPurposes.All.Contains(consent))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: consent (thanks, updates, appeals).");
            }

            var rows = new List<DonorView>();
            foreach (var donor in (await data.LoadAsync(date, context.RequestAborted)).Where(d => string.IsNullOrEmpty(segment) || d.Segments.Contains(segment)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (consent is null || await messenger.IsGrantedAsync(donor.Entity.Id, consent, cancellationToken: context.RequestAborted))
                {
                    rows.Add(donor);
                }
            }

            var csv = new StringBuilder("id,name,email,gifts,totals,first_gift_on,last_gift_on,segments,consent_thanks,consent_updates,consent_appeals\r\n");
            foreach (var donor in rows)
            {
                var state = await ConsentOf(db, donor.Entity.Id, messenger);
                csv.Append(string.Join(',', new[]
                {
                    Cell(donor.Entity.Id), Cell(donor.Name), Cell(donor.Email), Cell(donor.Gifts.Count.ToString(CultureInfo.InvariantCulture)),
                    Cell(string.Join("; ", donor.Totals.Select(t => $"{t.Currency} {t.Cash.ToDecimalString()} money + {t.InKind.ToDecimalString()} in-kind"))),
                    Cell(donor.Gifts.Min(g => g.Fact.ReceivedOn).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), Cell(donor.Gifts.Max(g => g.Fact.ReceivedOn).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    Cell(string.Join("; ", donor.Segments)),
                    Cell(state[ConsentPurposes.Thanks].Granted ? "yes" : "no"), Cell(state[ConsentPurposes.Updates].Granted ? "yes" : "no"), Cell(state[ConsentPurposes.Appeals].Granted ? "yes" : "no")
                })).Append("\r\n");
            }

            // Taking personal data out is itself recorded.
            db.DonorExports.Add(new DonorExportEntity
            {
                Segment = string.IsNullOrEmpty(segment) ? "all" : segment,
                AsOf = date,
                ConsentFilter = consent,
                Rows = rows.Count,
                ExportedBy = context.User.FindFirstValue(KjClaims.Subject) ?? string.Empty,
                At = clock.GetUtcNow()
            });
            await db.SaveChangesAsync(context.RequestAborted);

            context.Response.Headers.CacheControl = "private, no-store";
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"donors-{(string.IsNullOrEmpty(segment) ? "all" : segment)}-{date:yyyy-MM-dd}.csv");
        })
        .WithName("ExportDonors")
        .WithSummary("Export a segment as CSV")
        .WithDescription("The same donors and the same rules as the list, for the same `asOf` date, so an export can be reproduced. `consent=appeals` (or thanks, updates) keeps only donors who granted that purpose. Cells that could run as spreadsheet formulas are neutralised. Every export is logged with who, what and how many. Requires `finance.donor.export`.")
        .WithTags("Donors")
        .RequirePermission(Permissions.FinanceDonorExport);

        app.MapGet($"{Route}/reconciliation", async (DonorData data, CancellationToken cancellationToken) => Results.Ok(await data.ReconcileAsync(cancellationToken)))
        .WithName("ReconcileDonors")
        .WithSummary("Compare each donor's giving with the ledger")
        .WithDescription("For every donor and currency: the sum of valid receipts against the net donation income the ledger holds for the same donations (reversals netted). `reconciled` is true when every difference is zero. Anonymous gifts are in neither side.")
        .WithTags("Donors")
        .RequirePermission(Permissions.FinanceView);
    }

    internal static async Task<IReadOnlyDictionary<string, ConsentState>> ConsentOf(FinanceDbContext db, string donorId, DonorMessenger messenger)
    {
        _ = messenger;
        var rows = (await db.DonorConsents.AsNoTracking().Where(c => c.DonorId == donorId).ToListAsync()).OrderBy(c => c.Id).ToList();
        return ConsentPurposes.All.ToDictionary(p => p, p => rows.LastOrDefault(c => c.Purpose == p) is { } last ? new ConsentState(last.Granted, last.Source, last.At) : new ConsentState(false, null, null));
    }

    private static bool TryScope(HttpContext context, string? segment, string? asOf, DateOnly today, out DateOnly date, out IResult? error)
    {
        date = today;
        error = null;
        if (!string.IsNullOrWhiteSpace(asOf) && !DateOnly.TryParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            error = ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: asOf (yyyy-MM-dd).");
            return false;
        }

        if (!string.IsNullOrEmpty(segment) && !Segments.All.Contains(segment))
        {
            error = ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: segment (recurring, major, new, lapsed).");
            return false;
        }

        return true;
    }

    // Quoted, and anything a spreadsheet would run as a formula is made inert with a leading apostrophe.
    private static string Cell(string value)
    {
        var text = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
