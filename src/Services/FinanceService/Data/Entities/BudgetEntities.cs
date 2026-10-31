namespace FinanceService.Data.Entities;

/// <summary>A fiscal year closed for good: from then on the ledger refuses any posting dated in it and its budgets are frozen. Appended, never edited, and there is no reopen in this version.</summary>
public sealed class FiscalYearCloseEntity
{
    public string Label { get; set; } = string.Empty;
    public DateTimeOffset ClosedAt { get; set; }
    public string? ClosedBy { get; set; }
}

/// <summary>
/// What is planned to be spent in one category (and optionally one project) in one fiscal year, in one currency. It is a plan, not evidence, so it can be edited while the year
/// is open (every change is audited). What was actually spent is never stored here: it is read from the ledger.
/// </summary>
public sealed class BudgetEntity
{
    public string Id { get; set; } = string.Empty;
    public string FiscalYear { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;

    /// <summary>The project this line covers; null means the whole organisation for that category.</summary>
    public string? ProjectSlug { get; set; }

    public string CategoryKey { get; set; } = string.Empty;
    public decimal PlannedAmount { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A threshold a budget line has crossed. Unique per line and threshold, so each alert fires once; append-only.</summary>
public sealed class BudgetAlertEntity
{
    public long Id { get; set; }
    public string BudgetId { get; set; } = string.Empty;
    public int ThresholdPercent { get; set; }
    public decimal PlannedAmount { get; set; }
    public decimal ActualAmount { get; set; }
    public DateTimeOffset At { get; set; }
}
