namespace ReportingService.Data.Entities;

/// <summary>One monthly figure in the warehouse. Replaced wholesale per source on each successful refresh; nothing else writes it. `Currency` is "" for counts and durations.</summary>
public sealed class AggregatePointEntity
{
    public long Id { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;
    public DateOnly Period { get; set; }
    public string Dimension { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public decimal Value { get; set; }
}

/// <summary>When a source's rows were last replaced, and by which run: the as-of date shown beside every figure.</summary>
public sealed class AggregateSourceEntity
{
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset AsOf { get; set; }
    public string RunId { get; set; } = string.Empty;
}

public static class RunStatuses
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>One refresh attempt. A failed run keeps its reason; the figures from the last good run stay in place and are reported as stale.</summary>
public sealed class AggregateRunEntity
{
    public string Id { get; set; } = string.Empty;
    public string Trigger { get; set; } = "schedule";
    public string Status { get; set; } = RunStatuses.Running;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }

    /// <summary>JSON list of the ledger checks and their outcome; null until reconciliation ran.</summary>
    public string? ReconciliationJson { get; set; }

    public int Points { get; set; }
}
