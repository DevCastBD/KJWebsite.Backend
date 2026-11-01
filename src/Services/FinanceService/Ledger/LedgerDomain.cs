using System.Globalization;
using FinanceService.Data.Entities;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Finance;

namespace FinanceService.Ledger;

public sealed class LedgerOptions
{
    public const string SectionName = "Finance:Ledger";

    /// <summary>The month the fiscal year starts: 7 is the Bangladesh government year (July-June), 1 the calendar year. Postings keep the label they were given.</summary>
    public int FiscalYearStartMonth { get; set; } = 7;

    public void Validate()
    {
        if (FiscalYearStartMonth is < 1 or > 12)
        {
            throw new InvalidOperationException("Finance:Ledger:FiscalYearStartMonth must be between 1 and 12.");
        }
    }
}

public static class FiscalCalendar
{
    /// <summary>`2026-27` for a July start (the year runs July 2026 to June 2027), or `2026` for a calendar year.</summary>
    public static string FiscalYear(DateOnly date, int startMonth)
    {
        var startYear = date.Month >= startMonth ? date.Year : date.Year - 1;
        return startMonth == 1 ? startYear.ToString(CultureInfo.InvariantCulture) : $"{startYear}-{(startYear + 1) % 100:D2}";
    }

    /// <summary>The first and last day of a fiscal year label under the given start month, or false when the label is not one this calendar would produce (`2026-27` for July, `2026` for January).</summary>
    public static bool TryRange(string? label, int startMonth, out DateOnly start, out DateOnly end)
    {
        start = end = default;
        if (label is null || label.Length < 4 || !int.TryParse(label.AsSpan(0, 4), System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year is < 1900 or > 2999)
        {
            return false;
        }

        start = new DateOnly(year, startMonth, 1);
        end = start.AddYears(1).AddDays(-1);
        return FiscalYear(start, startMonth) == label;
    }

    public static string PeriodKey(DateOnly date) => date.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static bool TryParsePeriod(string? key, out DateOnly firstDay) =>
        DateOnly.TryParseExact(key is { Length: 7 } ? key + "-01" : null, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out firstDay);
}

public enum LedgerSide
{
    Debit,
    Credit
}

public sealed record LedgerLine(string AccountCode, LedgerSide Side, Money Amount)
{
    public static LedgerLine Debit(string accountCode, Money amount) => new(accountCode, LedgerSide.Debit, amount);

    public static LedgerLine Credit(string accountCode, Money amount) => new(accountCode, LedgerSide.Credit, amount);
}

/// <summary>What a caller wants posted. It becomes a posting only if <see cref="PostingRules"/> accepts it.</summary>
public sealed record PostingDraft(DateOnly PostedOn, string Memo, string SourceType, string? SourceId, IReadOnlyList<LedgerLine> Lines, string? Reason = null);

public static class PostingRules
{
    public const int MaxMemo = 500;

    /// <summary>
    /// A posting needs at least two lines, each a positive amount on one side of a known, active account, and in every currency the
    /// debits must equal the credits exactly. Nothing is rounded: an amount already has two decimals or it was refused when the Money was made.
    /// </summary>
    public static Result Validate(PostingDraft draft, IReadOnlyDictionary<string, AccountEntity> accounts)
    {
        if (string.IsNullOrWhiteSpace(draft.Memo) || draft.Memo.Length > MaxMemo)
        {
            return Result.Failure("POSTING_MEMO_INVALID", $"A posting needs a memo of up to {MaxMemo} characters.");
        }

        if (draft.Lines.Count < 2)
        {
            return Result.Failure("POSTING_TOO_FEW_LINES", "A posting needs at least two lines.");
        }

        foreach (var line in draft.Lines)
        {
            if (!line.Amount.IsPositive)
            {
                return Result.Failure("POSTING_AMOUNT_INVALID", "Every line is a positive amount; the side says whether it is a debit or a credit.");
            }

            if (!accounts.TryGetValue(line.AccountCode, out var account))
            {
                return Result.Failure("POSTING_UNKNOWN_ACCOUNT", $"Account {line.AccountCode} does not exist.");
            }

            if (account.ArchivedAt is not null)
            {
                return Result.Failure("POSTING_ACCOUNT_ARCHIVED", $"Account {line.AccountCode} is archived.");
            }
        }

        return Balanced(draft.Lines.Select(l => (l.Amount.Currency, l.Side == LedgerSide.Debit ? l.Amount.Amount : 0m, l.Side == LedgerSide.Credit ? l.Amount.Amount : 0m)))
            ? Result.Success()
            : Result.Failure("POSTING_UNBALANCED", "Debits must equal credits in every currency.");
    }

    /// <summary>True when, per currency, the debits and credits are equal. Shared by validation and by the database-context guard.</summary>
    public static bool Balanced(IEnumerable<(string Currency, decimal Debit, decimal Credit)> lines) =>
        lines.GroupBy(l => l.Currency, StringComparer.Ordinal).All(g => g.Sum(l => l.Debit) == g.Sum(l => l.Credit));
}

/// <summary>The chart's seeded accounts and the rules that pick one for a donation.</summary>
public static class LedgerAccounts
{
    public const string Cash = "1010";
    public const string Bank = "1020";
    public const string MobileWallets = "1030";
    public const string SslCommerzClearing = "1110";
    public const string BkashClearing = "1120";
    public const string NagadClearing = "1130";
    public const string StripeClearing = "1140";
    public const string PayPalClearing = "1150";
    public const string OtherClearing = "1190";
    public const string InKindGifts = "1250";
    public const string DonationsGeneral = "4010";
    public const string DonationsDesignated = "4020";
    public const string FeeContributions = "4090";
    public const string PaymentProcessingFees = "5040";

    public static readonly IReadOnlyList<(string Code, string Name, string Type)> Seed =
    [
        (Cash, "Cash on hand", AccountTypes.Asset),
        (Bank, "Bank accounts", AccountTypes.Asset),
        (MobileWallets, "Mobile wallet balances", AccountTypes.Asset),
        (SslCommerzClearing, "Gateway clearing - SSLCommerz", AccountTypes.Asset),
        (BkashClearing, "Gateway clearing - bKash", AccountTypes.Asset),
        (NagadClearing, "Gateway clearing - Nagad", AccountTypes.Asset),
        (StripeClearing, "Gateway clearing - Stripe", AccountTypes.Asset),
        (PayPalClearing, "Gateway clearing - PayPal", AccountTypes.Asset),
        (OtherClearing, "Gateway clearing - other", AccountTypes.Asset),
        ("1200", "Receivables", AccountTypes.Asset),
        (InKindGifts, "In-kind gifts received", AccountTypes.Asset),
        ("2010", "Refunds payable", AccountTypes.Liability),
        ("2020", "Expense reimbursements payable", AccountTypes.Liability),
        ("2100", "Accounts payable", AccountTypes.Liability),
        ("3010", "Unrestricted net assets", AccountTypes.Equity),
        ("3020", "Restricted net assets", AccountTypes.Equity),
        (DonationsGeneral, "Donations - general", AccountTypes.Income),
        (DonationsDesignated, "Donations - campaigns and projects", AccountTypes.Income),
        (FeeContributions, "Transaction fee contributions", AccountTypes.Income),
        ("4100", "Membership dues", AccountTypes.Income),
        ("4900", "Other income", AccountTypes.Income),
        ("5010", "Programme expenses", AccountTypes.Expense),
        ("5020", "Fundraising expenses", AccountTypes.Expense),
        ("5030", "Administrative expenses", AccountTypes.Expense),
        ("5040", "Payment processing fees", AccountTypes.Expense),
        ("5050", "Event expenses", AccountTypes.Expense),
        ("5900", "Other expenses", AccountTypes.Expense)
    ];

    /// <summary>Where the money sits until it is paid out: the gateway's clearing account, or cash/bank for money received offline.</summary>
    public static string ClearingFor(string method) => method switch
    {
        "sslcommerz" => SslCommerzClearing,
        "bkash" => BkashClearing,
        "nagad" => NagadClearing,
        "stripe" => StripeClearing,
        "paypal" => PayPalClearing,
        "cash" => Cash,
        "in_kind" => InKindGifts,
        "bank" or "cheque" => Bank,
        _ => OtherClearing
    };
}
