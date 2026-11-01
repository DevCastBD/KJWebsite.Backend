using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using FinanceService.Ledger;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Finance;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Campaigns;

public sealed record FundraisingCampaignRequest(string? Title, string? TitleBn, string? Slug, string? Goal, string? Currency, string? Status, string? StartsOn, string? EndsOn);

public sealed record FundraisingCampaignRow(string Id, string Slug, string Title, string? TitleBn, string Goal, string Currency, string Raised, string InKind, int Gifts, IReadOnlyList<OtherCurrencyAmount> OtherCurrencies, string Status, string? StartsOn, string? EndsOn);

public sealed record CampaignReconciliationLine(string Slug, string Currency, string Receipts, string Ledger, string Difference, string? Cached);

public sealed record CampaignReconciliation(bool Reconciled, IReadOnlyList<CampaignReconciliationLine> Lines);

/// <summary>Whether a campaign slug may be named as a donation's designation.</summary>
public static class CampaignRules
{
    /// <summary>A public donor can give only to a campaign that is active, has started and has not ended.</summary>
    public static async Task<bool> AcceptsPublicGiftAsync(FinanceDbContext db, string slug, DateOnly today)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == slug);
        return campaign?.Accepting(today) == true;
    }

    /// <summary>Back-office entry for a gift that already arrived: the campaign must exist and not be a draft (it may have ended since).</summary>
    public static async Task<bool> AcceptsRecordedGiftAsync(FinanceDbContext db, string slug) =>
        await db.Campaigns.AsNoTracking().AnyAsync(c => c.Slug == slug && c.Status != CampaignStatuses.Draft);
}

public static partial class CampaignEndpoints
{
    private const string AdminRoute = "/api/v1/admin/finance/campaigns";
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    public static void MapCampaigns(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/campaigns/{slug}/progress", async (HttpContext context, string slug, CampaignProgressCache cache) =>
        {
            if (!SlugPattern().IsMatch(slug) || slug.Length > 100)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Campaign not found.");
            }

            (CampaignProgressView? View, TimeSpan Remaining) result;
            try
            {
                result = await cache.GetAsync(slug, context.RequestAborted);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // No trustworthy figure: fail fast so the frontend keeps its own snapshot rather than show a wrong number.
                context.Response.Headers.RetryAfter = "5";
                return ApiError.Create(context, StatusCodes.Status503ServiceUnavailable, "TEMPORARILY_UNAVAILABLE", "The campaign total is not available right now.");
            }

            if (result.View is not { } view)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Campaign not found.");
            }

            var etag = CampaignProgressCache.ETagOf(view);
            context.Response.Headers.ETag = etag;
            context.Response.Headers.CacheControl = $"public, max-age={Math.Max(1, (int)result.Remaining.TotalSeconds)}";
            if (context.Request.Headers.IfNoneMatch.Contains(etag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Ok(view);
        })
        .WithName("GetCampaignProgress")
        .WithSummary("A campaign's public progress")
        .WithDescription("`raised` is a decimal string in the campaign's own `currency` and counts only confirmed, un-reversed gifts in that currency; gifts in other currencies are listed in `otherCurrencies` and are never converted into it. The figure is computed at most once per `cacheSeconds` however many requests arrive, carries an ETag and Cache-Control, and answers 503 rather than a stale-beyond-limit number. 404 for an unknown or draft campaign.")
        .WithTags("Campaigns")
        .AllowAnonymous();

        app.MapGet(AdminRoute, async (FinanceDbContext db, string? status, int? limit, int? offset) =>
        {
            var query = db.Campaigns.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(c => c.Status == status);
            }

            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var skip = Math.Max(0, offset ?? 0);
            var total = await query.CountAsync();
            var page = await query.OrderBy(c => c.Slug).Skip(skip).Take(take).ToListAsync();
            var totals = await CampaignMath.TotalsAsync(db, page, default);
            return Results.Ok(new PagingEnvelope<FundraisingCampaignRow>([.. page.Select(c => Row(c, totals[c.Slug]))], total, null, take, skip));
        })
        .WithName("ListFundraisingCampaigns")
        .WithSummary("Campaigns with what each has raised")
        .WithDescription("Figures are computed live from receipts, not from the public cache.")
        .WithTags("Campaigns")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost(AdminRoute, async (HttpContext context, FundraisingCampaignRequest payload, FinanceDbContext db, TimeProvider clock) =>
        {
            var parsed = Parse(payload, creating: true);
            if (parsed.Invalid.Count > 0)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", parsed.Invalid)}.");
            }

            var slug = parsed.Slug ?? await UniqueSlugAsync(db, parsed.Title!);
            if (await db.Campaigns.AnyAsync(c => c.Slug == slug))
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "SLUG_EXISTS", $"A campaign with the slug '{slug}' already exists.");
            }

            var campaign = new CampaignEntity
            {
                Id = PublicDonationEndpoints.NewId("cmp"),
                Slug = slug,
                Title = parsed.Title!,
                TitleBn = parsed.TitleBn,
                GoalAmount = parsed.Goal!.Value.Amount,
                Currency = parsed.Goal.Value.Currency,
                Status = parsed.Status ?? CampaignStatuses.Draft,
                StartsOn = parsed.StartsOn,
                EndsOn = parsed.EndsOn,
                CreatedBy = context.User.FindFirstValue(KjClaims.Subject),
                CreatedAt = clock.GetUtcNow()
            };
            db.Campaigns.Add(campaign);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "SLUG_EXISTS", $"A campaign with the slug '{slug}' already exists.");
            }

            return Results.Created($"{AdminRoute}/{campaign.Id}", Row(campaign, (await CampaignMath.TotalsAsync(db, [campaign], default))[campaign.Slug]));
        })
        .WithName("CreateFundraisingCampaign")
        .WithSummary("Create a campaign")
        .WithDescription("`{title, titleBn?, slug?, goal, currency, status?, startsOn?, endsOn?}`; `goal` is a positive decimal string. The slug (default: made from the title) is the public identifier and never changes. New campaigns are drafts unless `status` says otherwise. Requires `finance.campaign.manage`.")
        .WithTags("Campaigns")
        .RequirePermission(Permissions.FinanceCampaignManage);

        app.MapPut($"{AdminRoute}/{{id}}", async (HttpContext context, string id, FundraisingCampaignRequest payload, FinanceDbContext db, CampaignProgressCache cache) =>
        {
            var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == id);
            if (campaign is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Campaign not found.");
            }

            var parsed = Parse(payload, creating: false);
            if (parsed.Invalid.Count > 0)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", parsed.Invalid)}.");
            }

            if (parsed.Slug is not null && parsed.Slug != campaign.Slug)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "SLUG_LOCKED", "A campaign's slug is its public identifier and cannot change.");
            }

            if (parsed.Goal!.Value.Currency != campaign.Currency && await db.Receipts.AnyAsync(r => r.DesignationType == Designations.Campaign && r.DesignationId == campaign.Slug))
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "CURRENCY_LOCKED", "The currency cannot change once gifts have been designated to the campaign.");
            }

            campaign.Title = parsed.Title!;
            campaign.TitleBn = parsed.TitleBn;
            campaign.GoalAmount = parsed.Goal.Value.Amount;
            campaign.Currency = parsed.Goal.Value.Currency;
            campaign.Status = parsed.Status ?? campaign.Status;
            campaign.StartsOn = parsed.StartsOn;
            campaign.EndsOn = parsed.EndsOn;
            await db.SaveChangesAsync();
            cache.Invalidate(campaign.Slug);
            return Results.Ok(Row(campaign, (await CampaignMath.TotalsAsync(db, [campaign], default))[campaign.Slug]));
        })
        .WithName("UpdateFundraisingCampaign")
        .WithSummary("Edit a campaign's title, goal, dates or status")
        .WithDescription("Same body as create. The slug cannot change (409 SLUG_LOCKED) and neither can the currency once gifts are designated to the campaign (409 CURRENCY_LOCKED). The public figure is refreshed at once. Requires `finance.campaign.manage`.")
        .WithTags("Campaigns")
        .RequirePermission(Permissions.FinanceCampaignManage);

        app.MapGet($"{AdminRoute}/reconciliation", async (FinanceDbContext db, CampaignProgressCache cache) =>
        {
            var campaigns = await db.Campaigns.AsNoTracking().OrderBy(c => c.Slug).ToListAsync();
            var totals = await CampaignMath.TotalsAsync(db, campaigns, default);

            // Net donation income the ledger holds for the donations designated to each campaign (reversals netted), against what the receipts say.
            var designated = (await db.Donations.AsNoTracking().Where(d => d.DesignationType == Designations.Campaign && d.Kind != DonationKinds.Reversal).Select(d => new { d.Id, Slug = d.DesignationId! }).ToListAsync())
                .ToDictionary(d => d.Id, d => d.Slug);
            var postings = await db.Postings.AsNoTracking().Where(p => p.SourceType == PostingSources.Donation || p.ReversesPostingId != null).Select(p => new { p.Id, p.SourceType, p.SourceId, p.ReversesPostingId }).ToListAsync();
            var donationOfPosting = postings.Where(p => p.SourceType == PostingSources.Donation && p.SourceId != null).ToDictionary(p => p.Id, p => p.SourceId!);
            foreach (var reversal in postings.Where(p => p.ReversesPostingId is not null))
            {
                if (donationOfPosting.TryGetValue(reversal.ReversesPostingId!.Value, out var donationId))
                {
                    donationOfPosting[reversal.Id] = donationId;
                }
            }

            var inKind = (await db.Receipts.AsNoTracking().Where(r => r.IsInKind).Select(r => r.DonationId).ToListAsync()).ToHashSet();
            var income = LedgerAccounts.DonationsDesignated;
            var entries = await (from e in db.LedgerEntries.AsNoTracking() join a in db.Accounts.AsNoTracking() on e.AccountId equals a.Id where a.Code == income select new { e.PostingId, e.Currency, e.Debit, e.Credit }).ToListAsync();
            var ledger = new Dictionary<(string Slug, string Currency), decimal>();
            foreach (var entry in entries)
            {
                if (donationOfPosting.TryGetValue(entry.PostingId, out var donationId) && designated.TryGetValue(donationId, out var slug) && !inKind.Contains(donationId))
                {
                    ledger[(slug, entry.Currency)] = ledger.GetValueOrDefault((slug, entry.Currency)) + entry.Credit - entry.Debit;
                }
            }

            var lines = new List<CampaignReconciliationLine>();
            foreach (var campaign in campaigns)
            {
                var t = totals[campaign.Slug];
                var byReceipts = new Dictionary<string, decimal> { [campaign.Currency] = t.Raised };
                foreach (var (currency, amount) in t.Others)
                {
                    byReceipts[currency] = amount;
                }

                foreach (var currency in byReceipts.Keys.Union(ledger.Keys.Where(k => k.Slug == campaign.Slug).Select(k => k.Currency)).OrderBy(c => c, StringComparer.Ordinal))
                {
                    var fromReceipts = byReceipts.GetValueOrDefault(currency);
                    var fromLedger = ledger.GetValueOrDefault((campaign.Slug, currency));
                    lines.Add(new CampaignReconciliationLine(campaign.Slug, currency, Fmt(fromReceipts), Fmt(fromLedger), Fmt(fromReceipts - fromLedger), currency == campaign.Currency ? cache.PeekRaised(campaign.Slug) : null));
                }
            }

            return Results.Ok(new CampaignReconciliation(lines.All(l => l.Difference == "0.00"), lines));
        })
        .WithName("ReconcileFundraisingCampaigns")
        .WithSummary("Compare each campaign's receipts with the ledger and with the public cache")
        .WithDescription("Per campaign and currency: the receipts' total, the net designated-donation income in the ledger, their difference (zero when the books agree), and what the public cache currently holds for the campaign's own currency (it can lag by up to the cache interval, and never exceeds the receipts).")
        .WithTags("Campaigns")
        .RequirePermission(Permissions.FinanceView);
    }

    private sealed record Parsed(List<string> Invalid, string? Title, string? TitleBn, string? Slug, Money? Goal, string? Status, DateOnly? StartsOn, DateOnly? EndsOn);

    private static Parsed Parse(FundraisingCampaignRequest request, bool creating)
    {
        var invalid = new List<string>();
        var title = request.Title?.Trim();
        if (title is not { Length: > 0 and <= 200 })
        {
            invalid.Add("title");
        }

        var titleBn = string.IsNullOrWhiteSpace(request.TitleBn) ? null : request.TitleBn.Trim();
        if (titleBn is { Length: > 200 })
        {
            invalid.Add("titleBn");
        }

        var slug = string.IsNullOrWhiteSpace(request.Slug) ? null : request.Slug.Trim();
        if (slug is not null && (slug.Length > 100 || !SlugPattern().IsMatch(slug)))
        {
            invalid.Add("slug");
        }

        var goal = Money.Parse(request.Goal, request.Currency);
        if (!goal.IsSuccess || !goal.Value.IsPositive)
        {
            invalid.Add("goal");
        }

        var status = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status.Trim();
        if (status is not null && !CampaignStatuses.All.Contains(status))
        {
            invalid.Add("status");
        }

        DateOnly? starts = null, ends = null;
        if (!string.IsNullOrWhiteSpace(request.StartsOn))
        {
            starts = DateOnly.TryParseExact(request.StartsOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s) ? s : null;
            if (starts is null)
            {
                invalid.Add("startsOn");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.EndsOn))
        {
            ends = DateOnly.TryParseExact(request.EndsOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e) ? e : null;
            if (ends is null)
            {
                invalid.Add("endsOn");
            }
        }

        if (starts is not null && ends is not null && ends < starts)
        {
            invalid.Add("endsOn");
        }

        _ = creating;
        return new Parsed(invalid, title, titleBn, slug, goal.IsSuccess ? goal.Value : null, status, starts, ends);
    }

    private static async Task<string> UniqueSlugAsync(FinanceDbContext db, string title)
    {
        var baseSlug = SlugPattern().IsMatch(Slugify(title)) ? Slugify(title) : $"campaign-{Guid.NewGuid():N}"[..17];
        var candidate = baseSlug.Length > 90 ? baseSlug[..90].TrimEnd('-') : baseSlug;
        for (var i = 2; await db.Campaigns.AnyAsync(c => c.Slug == candidate); i++)
        {
            candidate = $"{baseSlug}-{i.ToString(CultureInfo.InvariantCulture)}";
        }

        return candidate;
    }

    // Latin letters and digits only: a Bengali title falls back to a generated slug rather than an unreadable one.
    private static string Slugify(string title)
    {
        var builder = new StringBuilder();
        foreach (var c in title.ToLowerInvariant())
        {
            builder.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        }

        return Regex.Replace(builder.ToString(), "-{2,}", "-").Trim('-');
    }

    private static FundraisingCampaignRow Row(CampaignEntity c, CampaignTotals t) => new(
        c.Id, c.Slug, c.Title, c.TitleBn, Fmt(c.GoalAmount), c.Currency, Fmt(t.Raised), Fmt(t.InKind), t.Gifts, [.. t.Others.Select(o => new OtherCurrencyAmount(o.Currency, Fmt(o.Amount)))],
        c.Status, c.StartsOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), c.EndsOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static string Fmt(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
