using KJWebsite.BuildingBlocks.Finance;

namespace FinanceService.Donors;

public static class Segments
{
    public const string New = "new";
    public const string Lapsed = "lapsed";
    public const string Major = "major";
    public const string Recurring = "recurring";

    public static readonly IReadOnlyList<string> All = [Recurring, Major, New, Lapsed];
}

/// <summary>One gift as the segments see it. Built from receipts, so it is exactly what the books say.</summary>
public sealed record GiftFact(DateOnly ReceivedOn, string Currency, decimal Amount, bool InKind, bool Recurring);

/// <summary>
/// Which segments a donor falls in on a given date. A pure function of the donor's gifts, the date and the configured thresholds, with nothing read from "now" or from
/// stored flags, so any segment can be re-run for any past date and give the same answer. Gifts after the date are ignored.
/// </summary>
public static class DonorSegments
{
    public static IReadOnlyList<string> Of(IEnumerable<GiftFact> allGifts, DateOnly asOf, DonorOptions options)
    {
        var gifts = allGifts.Where(g => g.ReceivedOn <= asOf).ToList();
        if (gifts.Count == 0)
        {
            return [];
        }

        var result = new List<string>();
        if (gifts.Any(g => g.Recurring))
        {
            result.Add(Segments.Recurring);
        }

        // Currencies are never added together: a donor is major if any one currency reaches its own threshold.
        if (gifts.GroupBy(g => g.Currency).Any(group => options.MajorThresholds.TryGetValue(group.Key, out var threshold) && group.Sum(g => g.Amount) >= threshold))
        {
            result.Add(Segments.Major);
        }

        if (gifts.Min(g => g.ReceivedOn) >= asOf.AddDays(-options.NewWithinDays))
        {
            result.Add(Segments.New);
        }

        if (gifts.Max(g => g.ReceivedOn) < asOf.AddMonths(-options.LapsedAfterMonths))
        {
            result.Add(Segments.Lapsed);
        }

        return result;
    }

    /// <summary>Per currency totals, money and in-kind value kept apart.</summary>
    public static IReadOnlyList<(string Currency, Money Cash, Money InKind)> Totals(IEnumerable<GiftFact> gifts) =>
        [.. gifts.GroupBy(g => g.Currency).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (
            g.Key,
            Money.Sum(g.Where(x => !x.InKind).Select(x => Money.Of(x.Amount, g.Key)), g.Key),
            Money.Sum(g.Where(x => x.InKind).Select(x => Money.Of(x.Amount, g.Key)), g.Key)))];
}
