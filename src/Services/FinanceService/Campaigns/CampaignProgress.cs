using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FinanceService.Data;
using FinanceService.Data.Entities;
using KJWebsite.BuildingBlocks.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Campaigns;

public sealed class CampaignOptions
{
    public const string SectionName = "Finance:Campaigns";

    /// <summary>How long a computed progress figure is served before it is recomputed: short enough to feel live, long enough that a traffic spike costs one query per interval.</summary>
    public int ProgressCacheSeconds { get; set; } = 30;

    /// <summary>If recomputing fails, an older figure is still served for this many multiples of the cache time; after that the endpoint fails rather than show an old number.</summary>
    public int StaleMultiple { get; set; } = 10;
}

public sealed record OtherCurrencyAmount(string Currency, string Raised);

/// <summary>
/// What the public sees. `Raised` counts only gifts in the campaign's own currency, so it is never inflated by a guess at an exchange rate; gifts in other currencies are listed
/// beside it, never converted. Pending, failed and reversed gifts are not in it: only confirmed, un-voided receipts.
/// </summary>
public sealed record CampaignProgressView(string Raised, string Goal, string Currency, DateTimeOffset AsOf, int CacheSeconds, int Gifts, IReadOnlyList<OtherCurrencyAmount> OtherCurrencies);

public sealed record CampaignTotals(string Slug, string GoalCurrency, decimal Raised, decimal InKind, int Gifts, IReadOnlyList<(string Currency, decimal Amount)> Others);

/// <summary>Works out campaign totals from receipts. The one place that does, so the public figure, the admin figure and the reconciliation can only differ by timing.</summary>
public static class CampaignMath
{
    public static async Task<Dictionary<string, CampaignTotals>> TotalsAsync(FinanceDbContext db, IReadOnlyCollection<CampaignEntity> campaigns, CancellationToken cancellationToken)
    {
        var slugs = campaigns.Select(c => c.Slug).ToList();
        var rows = await (from r in db.Receipts.AsNoTracking()
                          where r.DesignationType == Designations.Campaign && slugs.Contains(r.DesignationId!) && !db.ReceiptVoids.Any(v => v.ReceiptId == r.Id)
                          select new { Slug = r.DesignationId!, r.Currency, r.Amount, r.IsInKind }).ToListAsync(cancellationToken);
        var result = new Dictionary<string, CampaignTotals>(StringComparer.Ordinal);
        foreach (var campaign in campaigns)
        {
            var mine = rows.Where(r => r.Slug == campaign.Slug).ToList();
            var money = mine.Where(r => !r.IsInKind).ToList();
            result[campaign.Slug] = new CampaignTotals(
                campaign.Slug, campaign.Currency,
                money.Where(r => r.Currency == campaign.Currency).Sum(r => r.Amount),
                mine.Where(r => r.IsInKind && r.Currency == campaign.Currency).Sum(r => r.Amount),
                money.Count,
                [.. money.Where(r => r.Currency != campaign.Currency).GroupBy(r => r.Currency).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.Sum(r => r.Amount)))]);
        }

        return result;
    }
}

/// <summary>
/// Serves the public progress figure from memory. One computation per campaign per interval however many requests arrive (concurrent callers share the same in-flight
/// computation), so a press-mention spike reads the database once per interval. If a refresh fails the previous figure is served for a while; beyond that the call fails,
/// because a wrong number is worse than none (the frontend keeps its own snapshot).
/// </summary>
public sealed class CampaignProgressCache(IServiceScopeFactory scopes, IOptions<CampaignOptions> options, TimeProvider clock)
{
    private sealed record Entry(CampaignProgressView? View, DateTimeOffset ComputedAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<Entry>>> _inFlight = new(StringComparer.Ordinal);

    private TimeSpan Ttl => TimeSpan.FromSeconds(Math.Max(1, options.Value.ProgressCacheSeconds));

    /// <summary>Null when there is no such public campaign (unknown or still a draft).</summary>
    public async Task<(CampaignProgressView? View, TimeSpan Remaining)> GetAsync(string slug, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (_entries.TryGetValue(slug, out var cached) && now - cached.ComputedAt < Ttl)
        {
            return (cached.View, Ttl - (now - cached.ComputedAt));
        }

        var flight = _inFlight.GetOrAdd(slug, key => new Lazy<Task<Entry>>(() => RefreshAsync(key), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var fresh = await flight.Value.WaitAsync(cancellationToken);
            return (fresh.View, Ttl);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Serve the last good figure for a while rather than nothing; past that, fail.
            if (_entries.TryGetValue(slug, out var stale) && clock.GetUtcNow() - stale.ComputedAt < Ttl * Math.Max(1, options.Value.StaleMultiple))
            {
                return (stale.View, TimeSpan.Zero);
            }

            throw;
        }
    }

    public void Invalidate(string slug) => _entries.TryRemove(slug, out _);

    /// <summary>What is cached right now for a campaign, without computing anything: used by reconciliation.</summary>
    public string? PeekRaised(string slug) => _entries.TryGetValue(slug, out var entry) ? entry.View?.Raised : null;

    private async Task<Entry> RefreshAsync(string slug)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
            var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == slug);
            Entry entry;
            if (campaign is null || campaign.Status == CampaignStatuses.Draft)
            {
                entry = new Entry(null, clock.GetUtcNow());
            }
            else
            {
                var totals = (await CampaignMath.TotalsAsync(db, [campaign], CancellationToken.None))[slug];
                entry = new Entry(new CampaignProgressView(
                    Money.Of(totals.Raised, campaign.Currency).ToDecimalString(), Money.Of(campaign.GoalAmount, campaign.Currency).ToDecimalString(), campaign.Currency, clock.GetUtcNow(),
                    Math.Max(1, options.Value.ProgressCacheSeconds), totals.Gifts, [.. totals.Others.Select(o => new OtherCurrencyAmount(o.Currency, Money.Of(o.Amount, o.Currency).ToDecimalString()))]), clock.GetUtcNow());
            }

            _entries[slug] = entry;
            return entry;
        }
        finally
        {
            _inFlight.TryRemove(slug, out _);
        }
    }

    /// <summary>A validator for conditional requests: it changes only when the figures do, not each time the cache refreshes.</summary>
    public static string ETagOf(CampaignProgressView view) => "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
        view.Raised, view.Goal, view.Currency, view.Gifts.ToString(CultureInfo.InvariantCulture), string.Join(',', view.OtherCurrencies.Select(o => $"{o.Currency}={o.Raised}")))))).ToLowerInvariant()[..24] + "\"";
}
