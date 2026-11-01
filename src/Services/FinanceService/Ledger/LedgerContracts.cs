namespace FinanceService.Ledger;

/// <summary>One line of the ledger as the back office lists it. `Debit` or `Credit` is set, never both; amounts are decimal strings in `Currency`.</summary>
public sealed record LedgerEntryRow(
    string Id,
    string PostingId,
    string Date,
    string Account,
    string AccountCode,
    string Memo,
    string? Debit,
    string? Credit,
    string Currency,
    string FiscalYear,
    string? ReversalOf,
    string? ReversedBy);

public sealed record PostingLineView(string AccountCode, string AccountName, string? Debit, string? Credit, string Currency);

public sealed record PostingView(
    string Id,
    string Date,
    string PeriodKey,
    string FiscalYear,
    string Memo,
    string SourceType,
    string? SourceId,
    string? Reason,
    string? CreatedBy,
    string? ReversesPostingId,
    string? ReversedByPostingId,
    IReadOnlyList<PostingLineView> Lines);

public sealed record AdjustmentLineRequest(string? Account, string? Debit, string? Credit, string? Currency);

public sealed record AdjustmentRequest(string? PostedOn, string? Memo, string? Reason, IReadOnlyList<AdjustmentLineRequest>? Lines);

public sealed record PostingReversalRequest(string? Reason);

public sealed record AccountView(string Id, string Code, string Name, string Type, bool IsSystem, bool Archived);

public sealed record AccountRequest(string? Code, string? Name, string? Type);

public sealed record PeriodView(string PeriodKey, bool Closed, string? ClosedAt, string? ClosedBy, int Postings);

public sealed record TrialBalanceAccount(string Code, string Name, string Type, string Debit, string Credit, string Balance);

public sealed record TrialBalanceSection(string Currency, IReadOnlyList<TrialBalanceAccount> Accounts, string TotalDebit, string TotalCredit, bool Balanced);

public sealed record TrialBalanceView(string AsOf, IReadOnlyList<TrialBalanceSection> Currencies);
