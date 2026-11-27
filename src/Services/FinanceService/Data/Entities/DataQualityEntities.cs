namespace FinanceService.Data.Entities;

public static class AnomalyRules
{
    public const string LargeAmount = "large_amount";
    public const string RapidRepeat = "rapid_repeat";
    public const string ConfirmedWithoutPosting = "confirmed_without_posting";
    public const string GatewayFailureSpike = "gateway_failure_spike";
}

public static class AnomalyStatuses
{
    public const string Open = "open";
    public const string Confirmed = "confirmed";
    public const string Dismissed = "dismissed";
}

/// <summary>
/// A donation (or a pattern) flagged for a person to look at. Detection only ever writes this row: it never changes or deletes a donation, a posting or a donor. `DedupeKey` keeps a
/// flag from being raised twice, so a reviewed one stays reviewed however often the scan runs.
/// </summary>
public sealed class DonationAnomalyEntity
{
    public string Id { get; set; } = string.Empty;
    public string DedupeKey { get; set; } = string.Empty;
    public string Rule { get; set; } = string.Empty;
    public string? DonationId { get; set; }
    public string Detail { get; set; } = string.Empty;
    public DateTimeOffset DetectedAt { get; set; }
    public string Status { get; set; } = AnomalyStatuses.Open;
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
}

/// <summary>A person's ruling on a pair of donor records: not the same donor, or confirmed to be the same one. It changes no donor and no donation; it is the audited decision a merge would act on.</summary>
public sealed class DonorDuplicateDecisionEntity
{
    public string Id { get; set; } = string.Empty;
    public string PairKey { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string? DecidedBy { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}
