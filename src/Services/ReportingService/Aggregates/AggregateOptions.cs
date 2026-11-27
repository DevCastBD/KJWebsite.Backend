namespace ReportingService.Aggregates;

public sealed class AggregateOptions
{
    public const string SectionName = "Reporting:Aggregates";

    /// <summary>The hour (UTC, 0-23) the nightly refresh runs. Default 21:00 UTC, which is 03:00 in Dhaka.</summary>
    public int RefreshHourUtc { get; set; } = 21;

    /// <summary>False turns the nightly worker off (tests and the OpenAPI export); manual refresh still works.</summary>
    public bool WorkerEnabled { get; set; } = true;

    /// <summary>A source whose last good refresh is older than this is reported as stale. Default 30 hours: one missed night shows.</summary>
    public int MaxAgeHours { get; set; } = 30;

    /// <summary>The largest gap between warehouse donation totals and the ledger that still counts as agreeing (rounding only).</summary>
    public decimal Tolerance { get; set; } = 0.005m;

    public int FeedTimeoutSeconds { get; set; } = 30;
    public int Attempts { get; set; } = 3;
}

/// <summary>The services the warehouse reads, with the default address each runs on in development. Overridden by `Services:{Name}:Url`.</summary>
public static class AggregateSourceCatalog
{
    public static readonly IReadOnlyList<(string Source, string ConfigName, string DefaultUrl)> All =
    [
        ("finance", "Finance", "http://localhost:7005/"),
        ("membership", "Membership", "http://localhost:7004/"),
        ("cta", "Cta", "http://localhost:7002/"),
        ("content", "Content", "http://localhost:7001/")
    ];
}
