namespace FinanceService.Data.Entities;

public static class ControlKinds
{
    public const string Adjustment = "adjustment";
    public const string PostingReversal = "posting_reversal";
    public const string Refund = "refund";
    public const string PeriodReopen = "period_reopen";
    public const string FiscalYearReopen = "fiscal_year_reopen";

    public static readonly IReadOnlyList<string> All = [Adjustment, PostingReversal, Refund, PeriodReopen, FiscalYearReopen];
}

public static class ControlDecisions
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Withdrawn = "withdrawn";
}

/// <summary>
/// Someone asked for a sensitive financial action: a manual adjustment, reversing a posting, refunding a donation, reopening a period or a fiscal year. Nothing happens until a
/// different person approves it. The request is what was asked, exactly as asked, and never changes; its outcome is a <see cref="ControlDecisionEntity"/>. Append-only.
/// </summary>
public sealed class ControlRequestEntity
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;

    /// <summary>What it is about, for lookup: a donation id, a posting id, a period or a fiscal-year label; empty for a new adjustment.</summary>
    public string Subject { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = "{}";
    public string Reason { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset RequestedAt { get; set; }
}

/// <summary>
/// How a request ended. One per request (unique, so two people cannot both approve and the action cannot run twice). An approval or rejection must come from someone other than
/// the requester and a withdrawal only from the requester; PostgreSQL enforces the same. `ResultRef` names what an approval produced (a posting id, a donation id). Append-only.
/// </summary>
public sealed class ControlDecisionEntity
{
    public string RequestId { get; set; } = string.Empty;
    public ControlRequestEntity? Request { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string DecidedBy { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string? ResultRef { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>Every close and reopen of a period or fiscal year, in order. A reopen names the requester and the distinct approver. Append-only; the close tables themselves may lose a row only when an approved reopen is recorded here.</summary>
public sealed class CloseEventEntity
{
    public long Id { get; set; }

    /// <summary>`period` or `year`.</summary>
    public string Scope { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    /// <summary>`closed` or `reopened`.</summary>
    public string Kind { get; set; } = string.Empty;

    public string? By { get; set; }
    public string? ApprovedBy { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset At { get; set; }
}
