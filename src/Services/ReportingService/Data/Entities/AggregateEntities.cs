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

/// <summary>
/// One dated version of an impact metric definition. Append-only: a change is a new version that applies from its effective month onward, so earlier months keep the definition
/// they were published under. `Methodology` is the plain-language definition shown beside the figure.
/// </summary>
public sealed class MetricDefinitionEntity
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Methodology { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;

    /// <summary>The warehouse metric this definition counts.</summary>
    public string Metric { get; set; } = string.Empty;

    /// <summary>`monthly` (a month figure adds up) or `snapshot` (a state at a point in time, never summed over months).</summary>
    public string Kind { get; set; } = "monthly";

    public decimal Scale { get; set; } = 1m;
    public DateOnly EffectiveFrom { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
