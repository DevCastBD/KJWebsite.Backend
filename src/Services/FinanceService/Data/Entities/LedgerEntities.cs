namespace FinanceService.Data.Entities;

public static class AccountTypes
{
    public const string Asset = "asset";
    public const string Liability = "liability";
    public const string Equity = "equity";
    public const string Income = "income";
    public const string Expense = "expense";

    public static readonly IReadOnlyList<string> All = [Asset, Liability, Equity, Income, Expense];

    /// <summary>Assets and expenses grow with debits; liabilities, equity and income with credits.</summary>
    public static bool IsDebitNormal(string type) => type is Asset or Expense;
}

public static class PostingSources
{
    public const string Donation = "donation";
    public const string Expense = "expense";
    public const string Dues = "dues";
    public const string Adjustment = "adjustment";
    public const string Reversal = "reversal";
}

/// <summary>
/// One account in the chart. Accounts are master data, not ledger records: they can be added and archived, but an account is never deleted
/// and its code and type never change, because postings refer to them.
/// </summary>
public sealed class AccountEntity
{
    public string Id { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = AccountTypes.Asset;

    /// <summary>Seeded accounts the donation posting depends on.</summary>
    public bool IsSystem { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>An archived account takes no new postings but keeps its history and can still be reversed.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }
}

/// <summary>
/// One balanced posting: the header of two or more <see cref="LedgerEntryEntity"/> lines whose debits equal their credits in every currency.
/// Never updated or deleted. `SourceType` + `SourceId` identify what caused it, and are unique, so posting the same donation twice cannot
/// create a second posting. A correction is another posting that mirrors this one and names it in `ReversesPostingId`.
/// </summary>
public sealed class PostingEntity
{
    /// <summary>The posting number: sequential, assigned by the database.</summary>
    public long Id { get; set; }

    public DateOnly PostedOn { get; set; }

    /// <summary>`yyyy-MM`; a closed period takes no new postings.</summary>
    public string PeriodKey { get; set; } = string.Empty;

    /// <summary>The fiscal year when posted, for example `2026-27`. Stored, not recomputed, so a later calendar change cannot rewrite history.</summary>
    public string FiscalYear { get; set; } = string.Empty;

    public string Memo { get; set; } = string.Empty;
    public string SourceType { get; set; } = PostingSources.Adjustment;
    public string? SourceId { get; set; }
    public long? ReversesPostingId { get; set; }
    public string? Reason { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<LedgerEntryEntity> Entries { get; set; } = [];
}

/// <summary>One line of a posting: an amount on the debit or the credit side of an account, in one currency (never converted).</summary>
public sealed class LedgerEntryEntity
{
    public long Id { get; set; }
    public long PostingId { get; set; }
    public PostingEntity? Posting { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public AccountEntity? Account { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Currency { get; set; } = string.Empty;
}

/// <summary>A period is closed by adding this row, never by editing one. There is no reopen in this version.</summary>
public sealed class PeriodCloseEntity
{
    public string PeriodKey { get; set; } = string.Empty;
    public DateTimeOffset ClosedAt { get; set; }
    public string? ClosedBy { get; set; }
}
