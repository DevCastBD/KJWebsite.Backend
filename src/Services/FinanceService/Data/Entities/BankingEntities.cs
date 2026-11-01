namespace FinanceService.Data.Entities;

public static class BankLineStates
{
    public const string Matched = "matched";
    public const string Dismissed = "dismissed";
}

public static class BankMatchActions
{
    public const string AutoMatched = "auto_matched";
    public const string Matched = "matched";
    public const string PostedAndMatched = "posted_and_matched";
    public const string Dismissed = "dismissed";
    public const string Unmatched = "unmatched";
    public const string Reopened = "reopened";
}

/// <summary>
/// A bank account the organisation holds, tied to one asset account of the ledger so a statement can be proved against the books. It is registry data, not evidence: it can be
/// renamed, re-mapped and archived. The number is stored encrypted; only the last four digits are shown unless the caller holds the reveal permission.
/// </summary>
public sealed class BankAccountEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string NumberProtected { get; set; } = string.Empty;
    public string Last4 { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string LedgerAccountId { get; set; } = string.Empty;
    public AccountEntity? LedgerAccount { get; set; }

    /// <summary>JSON column mapping for CSV statements; null means detect it from the header row.</summary>
    public string? CsvMapping { get; set; }

    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

/// <summary>One imported statement file. Unique per account and file hash, so the same file is never imported twice. Append-only.</summary>
public sealed class BankStatementEntity
{
    public long Id { get; set; }
    public string BankAccountId { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public int LineCount { get; set; }
    public int DuplicateCount { get; set; }
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }
    public string? ImportedBy { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}

/// <summary>
/// One line of a statement: money in (positive) or out (negative). The fingerprint plus an occurrence counter is unique per account, so a line that appears in two overlapping
/// statements is stored once, while two genuinely identical lines on one day stay two. Append-only.
/// </summary>
public sealed class BankLineEntity
{
    public long Id { get; set; }
    public long StatementId { get; set; }
    public BankStatementEntity? Statement { get; set; }
    public string BankAccountId { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public decimal Amount { get; set; }
    public decimal? Balance { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public int Occurrence { get; set; }
}

/// <summary>
/// The current state of a line: matched to a ledger entry or dismissed with a reason. A line without a row is in the unmatched queue. This is working state (undoing a match removes
/// the row); what happened and who did it is kept in <see cref="BankMatchEventEntity"/>. A ledger entry is linked to at most one line.
/// </summary>
public sealed class BankLineLinkEntity
{
    public long LineId { get; set; }
    public string State { get; set; } = BankLineStates.Matched;
    public long? LedgerEntryId { get; set; }
    public string? Reason { get; set; }
    public bool Automatic { get; set; }
    public string? By { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>Every match, post-and-match, dismissal, unmatch and reopen, in order. Append-only.</summary>
public sealed class BankMatchEventEntity
{
    public long Id { get; set; }
    public long LineId { get; set; }
    public string Action { get; set; } = string.Empty;
    public long? LedgerEntryId { get; set; }
    public long? PostingId { get; set; }
    public string? Reason { get; set; }
    public string? By { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>A read of a full account number. Append-only.</summary>
public sealed class BankRevealEntity
{
    public long Id { get; set; }
    public string BankAccountId { get; set; } = string.Empty;
    public string? By { get; set; }
    public DateTimeOffset At { get; set; }
}
