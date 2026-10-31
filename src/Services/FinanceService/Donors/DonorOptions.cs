namespace FinanceService.Donors;

/// <summary>Thresholds behind the segments. They are configuration, so a segment can be re-run for any date with the same rules.</summary>
public sealed class DonorOptions
{
    public const string SectionName = "Finance:Donors";

    /// <summary>A donor with no gift in this many months is lapsed.</summary>
    public int LapsedAfterMonths { get; set; } = 12;

    /// <summary>A donor whose first gift is within this many days is new.</summary>
    public int NewWithinDays { get; set; } = 90;

    /// <summary>Lifetime giving in a currency at or above this makes a major donor. Currencies are never added together.</summary>
    public Dictionary<string, decimal> MajorThresholds { get; set; } = new(StringComparer.Ordinal)
    {
        ["BDT"] = 100_000m,
        ["USD"] = 1_000m,
        ["GBP"] = 800m,
        ["EUR"] = 900m
    };
}
