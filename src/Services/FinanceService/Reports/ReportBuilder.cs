using System.Globalization;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Reports;

public sealed class ReportOptions
{
    public const string SectionName = "Finance:Reports";

    /// <summary>Printed on the auditor pack and the monthly pack. Empty means "Kolpojontro".</summary>
    public string EntityName { get; set; } = string.Empty;

    public string EffectiveEntityName => string.IsNullOrWhiteSpace(EntityName) ? "Kolpojontro" : EntityName.Trim();

    /// <summary>Public origin used to build absolute download links (the frontend only follows https links). Empty means the origin of the request.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Minutes a signed download link stays valid. Zero or less means 15.</summary>
    public int LinkMinutes { get; set; }

    public int EffectiveLinkMinutes => LinkMinutes > 0 ? LinkMinutes : 15;
}

/// <summary>One ledger line with its posting and account, the only thing every report is computed from.</summary>
public sealed record LedgerRow(long PostingId, DateOnly PostedOn, string SourceType, string? SourceId, long? Reverses, string Memo, string Code, string Name, string Type, string Currency, decimal Debit, decimal Credit);

public sealed record AmountLine(string Code, string Name, decimal Amount);

public sealed record IncomeStatement(string Currency, DateOnly From, DateOnly To, IReadOnlyList<AmountLine> Income, decimal TotalIncome, IReadOnlyList<AmountLine> Expenses, decimal TotalExpenses, decimal Surplus);

public sealed record BalanceSheet(string Currency, DateOnly AsOf, IReadOnlyList<AmountLine> Assets, decimal TotalAssets, IReadOnlyList<AmountLine> Liabilities, decimal TotalLiabilities,
    IReadOnlyList<AmountLine> Equity, decimal TotalEquity, decimal AccumulatedSurplus, decimal TotalLiabilitiesAndEquity)
{
    public bool Balanced => TotalAssets == TotalLiabilitiesAndEquity;
}

public sealed record CashFlowLine(string Bucket, string Label, decimal In, decimal Out)
{
    public decimal Net => In - Out;
}

public sealed record CashFlow(string Currency, DateOnly From, DateOnly To, decimal Opening, IReadOnlyList<CashFlowLine> Lines, decimal TotalIn, decimal TotalOut, decimal Closing, decimal LedgerClosing)
{
    public decimal Net => TotalIn - TotalOut;

    public bool Reconciles => Opening + Net == Closing && Closing == LedgerClosing;
}

public sealed record BreakdownLine(string Key, string Label, decimal Amount, int Gifts);

public sealed record DonationSummary(string Currency, DateOnly From, DateOnly To, decimal Total, int Gifts, IReadOnlyList<BreakdownLine> ByChannel, IReadOnlyList<BreakdownLine> ByDesignation, IReadOnlyList<BreakdownLine> ByMonth);

public sealed record ProjectCostLine(string Project, string Category, decimal Amount);

public sealed record ProjectCosts(string Currency, DateOnly From, DateOnly To, IReadOnlyList<ProjectCostLine> Lines, decimal Total);

public sealed record DonorReport(string Currency, DateOnly From, DateOnly To, int Donors, int NewDonors, int ReturningDonors, int Gifts, decimal Total, decimal Average, decimal Largest, int UnidentifiedGifts, decimal UnidentifiedTotal);

public sealed record JournalRow(long PostingId, DateOnly Date, string Code, string Account, string Memo, string SourceType, decimal Debit, decimal Credit, string Currency);

/// <summary>A donation posting seen as a gift: signed (a reversal is negative), with the channel read from the ledger and the donation it belongs to.</summary>
public sealed record GiftFact(long PostingId, DateOnly Date, string Currency, decimal Amount, string ChannelCode, string ChannelName, string? DonationId);

public sealed class ReportBuilder(FinanceDbContext db, IOptions<ReportOptions> options)
{
    private static readonly string[] IncomeCodes = [LedgerAccounts.DonationsGeneral, LedgerAccounts.DonationsDesignated];
    private static readonly string[] BaseCash = [LedgerAccounts.Cash, LedgerAccounts.Bank, LedgerAccounts.MobileWallets];

    public ReportOptions Options => options.Value;

    /// <summary>Every ledger line dated up to and including <paramref name="to"/>, optionally for one currency. Reports are built from these rows in memory (SQLite cannot sum a decimal), so a report can never disagree with the ledger.</summary>
    public async Task<IReadOnlyList<LedgerRow>> LoadAsync(DateOnly to, string? currency, CancellationToken cancellationToken)
    {
        var rows = await (from e in db.LedgerEntries.AsNoTracking()
                          join p in db.Postings.AsNoTracking() on e.PostingId equals p.Id
                          join a in db.Accounts.AsNoTracking() on e.AccountId equals a.Id
                          where p.PostedOn <= to && (currency == null || e.Currency == currency)
                          orderby p.PostedOn, p.Id, e.Id
                          select new LedgerRow(p.Id, p.PostedOn, p.SourceType, p.SourceId, p.ReversesPostingId, p.Memo, a.Code, a.Name, a.Type, e.Currency, e.Debit, e.Credit)).ToListAsync(cancellationToken);
        return rows;
    }

    public async Task<IReadOnlySet<string>> CashCodesAsync(CancellationToken cancellationToken)
    {
        var banks = await db.BankAccounts.AsNoTracking().Select(b => b.LedgerAccount!.Code).ToListAsync(cancellationToken);
        return new HashSet<string>(BaseCash.Concat(banks), StringComparer.Ordinal);
    }

    private static IEnumerable<string> Currencies(IEnumerable<LedgerRow> rows) => rows.Select(r => r.Currency).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal);

    private static decimal Normal(string type, decimal debit, decimal credit) => AccountTypes.IsDebitNormal(type) ? debit - credit : credit - debit;

    private static IReadOnlyList<AmountLine> ByAccount(IEnumerable<LedgerRow> rows, string type) =>
        [.. rows.Where(r => r.Type == type).GroupBy(r => (r.Code, r.Name)).OrderBy(g => g.Key.Code, StringComparer.Ordinal)
            .Select(g => new AmountLine(g.Key.Code, g.Key.Name, Normal(type, g.Sum(r => r.Debit), g.Sum(r => r.Credit)))).Where(l => l.Amount != 0m)];

    public IReadOnlyList<IncomeStatement> IncomeStatements(IReadOnlyList<LedgerRow> rows, DateOnly from, DateOnly to) =>
        [.. Currencies(rows).Select(currency =>
        {
            var period = rows.Where(r => r.Currency == currency && r.PostedOn >= from && r.PostedOn <= to).ToList();
            var income = ByAccount(period, AccountTypes.Income);
            var expenses = ByAccount(period, AccountTypes.Expense);
            return new IncomeStatement(currency, from, to, income, income.Sum(l => l.Amount), expenses, expenses.Sum(l => l.Amount), income.Sum(l => l.Amount) - expenses.Sum(l => l.Amount));
        }).Where(s => s.Income.Count > 0 || s.Expenses.Count > 0)];

    /// <summary>Assets = liabilities + equity + accumulated surplus. There are no year-end closing entries, so income less expense since the books began is shown as its own line.</summary>
    public IReadOnlyList<BalanceSheet> BalanceSheets(IReadOnlyList<LedgerRow> rows, DateOnly asOf) =>
        [.. Currencies(rows).Select(currency =>
        {
            var upTo = rows.Where(r => r.Currency == currency && r.PostedOn <= asOf).ToList();
            var assets = ByAccount(upTo, AccountTypes.Asset);
            var liabilities = ByAccount(upTo, AccountTypes.Liability);
            var equity = ByAccount(upTo, AccountTypes.Equity);
            var surplus = ByAccount(upTo, AccountTypes.Income).Sum(l => l.Amount) - ByAccount(upTo, AccountTypes.Expense).Sum(l => l.Amount);
            return new BalanceSheet(currency, asOf, assets, assets.Sum(l => l.Amount), liabilities, liabilities.Sum(l => l.Amount), equity, equity.Sum(l => l.Amount), surplus,
                liabilities.Sum(l => l.Amount) + equity.Sum(l => l.Amount) + surplus);
        }).Where(s => s.Assets.Count > 0 || s.Liabilities.Count > 0 || s.Equity.Count > 0 || s.AccumulatedSurplus != 0m)];

    private static (string Key, string Label) Bucket(string code) => code switch
    {
        _ when code.StartsWith("40", StringComparison.Ordinal) => ("donations", "Donations received"),
        _ when code.StartsWith('4') => ("other-income", "Other income"),
        _ when code.StartsWith('5') => ("expenses", "Expenses paid"),
        _ when code.StartsWith("11", StringComparison.Ordinal) => ("gateway-settlements", "Gateway settlements"),
        _ when code.StartsWith('2') => ("payables", "Payables and refunds"),
        _ => ("other", "Other movements")
    };

    /// <summary>
    /// Direct method: the movement on cash, bank and wallet accounts, each posting's cash assigned to one bucket by the largest non-cash line on the other side (lowest code on a tie),
    /// so the buckets add up to the movement exactly. Opening plus movement is checked against the ledger balance at the end.
    /// </summary>
    public IReadOnlyList<CashFlow> CashFlows(IReadOnlyList<LedgerRow> rows, IReadOnlySet<string> cashCodes, DateOnly from, DateOnly to) =>
        [.. Currencies(rows).Select(currency =>
        {
            var mine = rows.Where(r => r.Currency == currency).ToList();
            decimal Balance(Func<LedgerRow, bool> where) => mine.Where(where).Where(r => cashCodes.Contains(r.Code)).Sum(r => r.Debit - r.Credit);
            var opening = Balance(r => r.PostedOn < from);
            var ledgerClosing = Balance(r => r.PostedOn <= to);
            var buckets = new Dictionary<string, (string Label, decimal In, decimal Out)>(StringComparer.Ordinal);
            foreach (var posting in mine.Where(r => r.PostedOn >= from && r.PostedOn <= to).GroupBy(r => r.PostingId))
            {
                var cash = posting.Where(r => cashCodes.Contains(r.Code)).Sum(r => r.Debit - r.Credit);
                if (cash == 0m)
                {
                    continue;
                }

                var counter = posting.Where(r => !cashCodes.Contains(r.Code)).OrderByDescending(r => r.Debit + r.Credit).ThenBy(r => r.Code, StringComparer.Ordinal).FirstOrDefault();
                var (key, label) = counter is null ? ("other", "Other movements") : Bucket(counter.Code);
                var current = buckets.GetValueOrDefault(key, (label, 0m, 0m));
                buckets[key] = cash > 0m ? (label, current.Item2 + cash, current.Item3) : (label, current.Item2, current.Item3 - cash);
            }

            var lines = buckets.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => new CashFlowLine(b.Key, b.Value.Label, b.Value.In, b.Value.Out)).ToList();
            var totalIn = lines.Sum(l => l.In);
            var totalOut = lines.Sum(l => l.Out);
            return new CashFlow(currency, from, to, opening, lines, totalIn, totalOut, opening + totalIn - totalOut, ledgerClosing);
        }).Where(f => f.Opening != 0m || f.Lines.Count > 0 || f.LedgerClosing != 0m)];

    /// <summary>Gifts as the ledger records them: donation postings, and the reversals of donation postings as negative gifts. The channel is the account the money landed in.</summary>
    public static IReadOnlyList<GiftFact> Gifts(IReadOnlyList<LedgerRow> rows)
    {
        var donationOf = rows.Where(r => r.SourceType == PostingSources.Donation && r.SourceId is not null).GroupBy(r => r.PostingId).ToDictionary(g => g.Key, g => g.First().SourceId!);
        var facts = new List<GiftFact>();
        foreach (var posting in rows.GroupBy(r => r.PostingId))
        {
            var first = posting.First();
            string? donationId = null;
            if (first.SourceType == PostingSources.Donation)
            {
                donationId = first.SourceId;
            }
            else if (first.Reverses is { } original && donationOf.TryGetValue(original, out var reversedDonation))
            {
                donationId = reversedDonation;
            }
            else
            {
                continue;
            }

            foreach (var currency in posting.Select(r => r.Currency).Distinct(StringComparer.Ordinal))
            {
                var lines = posting.Where(r => r.Currency == currency).ToList();
                var income = lines.Where(r => IncomeCodes.Contains(r.Code)).Sum(r => r.Credit - r.Debit);
                if (income == 0m)
                {
                    continue;
                }

                var channel = lines.Where(r => !r.Code.StartsWith('4')).OrderByDescending(r => r.Debit + r.Credit).ThenBy(r => r.Code, StringComparer.Ordinal).FirstOrDefault();
                facts.Add(new GiftFact(first.PostingId, first.PostedOn, currency, income, channel?.Code ?? "-", channel?.Name ?? "Unknown", donationId));
            }
        }

        return facts;
    }

    public IReadOnlyList<DonationSummary> DonationSummaries(IReadOnlyList<GiftFact> gifts, IReadOnlyDictionary<string, string> designationOf, DateOnly from, DateOnly to) =>
        [.. gifts.Select(g => g.Currency).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).Select(currency =>
        {
            var mine = gifts.Where(g => g.Currency == currency && g.Date >= from && g.Date <= to).ToList();
            static List<BreakdownLine> Group(IEnumerable<GiftFact> facts, Func<GiftFact, (string Key, string Label)> by) =>
                [.. facts.GroupBy(f => by(f)).OrderBy(g => g.Key.Key, StringComparer.Ordinal).Select(g => new BreakdownLine(g.Key.Key, g.Key.Label, g.Sum(f => f.Amount), g.Count(f => f.Amount > 0m) - g.Count(f => f.Amount < 0m)))];
            return new DonationSummary(currency, from, to, mine.Sum(g => g.Amount), mine.Count(g => g.Amount > 0m) - mine.Count(g => g.Amount < 0m),
                Group(mine, g => (g.ChannelCode, g.ChannelName)),
                Group(mine, g => { var key = g.DonationId is not null && designationOf.TryGetValue(g.DonationId, out var d) ? d : "unknown"; return (key, key); }),
                Group(mine, g => { var month = g.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture); return (month, month); }));
        })];

    /// <summary>Spend by project and category: postings from approved expenses carry their expense's project and category; every other posting on an expense account is shown as manual so the total equals the income statement.</summary>
    public IReadOnlyList<ProjectCosts> ProjectCostReports(IReadOnlyList<LedgerRow> rows, IReadOnlyDictionary<string, (string? Project, string Category)> expenses, DateOnly from, DateOnly to)
    {
        var expenseOfPosting = rows.Where(r => r.SourceType == PostingSources.Expense && r.SourceId is not null && !r.SourceId.EndsWith(":payment", StringComparison.Ordinal))
            .GroupBy(r => r.PostingId).ToDictionary(g => g.Key, g => g.First().SourceId!);
        return
        [.. Currencies(rows).Select(currency =>
        {
            var lines = new Dictionary<(string Project, string Category), decimal>();
            foreach (var posting in rows.Where(r => r.Currency == currency && r.PostedOn >= from && r.PostedOn <= to && r.Type == AccountTypes.Expense).GroupBy(r => r.PostingId))
            {
                var net = posting.Sum(r => r.Debit - r.Credit);
                var first = posting.First();
                var expenseId = expenseOfPosting.GetValueOrDefault(first.PostingId) ?? (first.Reverses is { } original ? expenseOfPosting.GetValueOrDefault(original) : null);
                var key = expenseId is not null && expenses.TryGetValue(expenseId, out var info) ? (info.Project ?? "(organisation)", info.Category) : ("(manual postings)", "-");
                lines[key] = lines.GetValueOrDefault(key) + net;
            }

            var ordered = lines.Where(l => l.Value != 0m).OrderBy(l => l.Key.Project, StringComparer.Ordinal).ThenBy(l => l.Key.Category, StringComparer.Ordinal).Select(l => new ProjectCostLine(l.Key.Project, l.Key.Category, l.Value)).ToList();
            return new ProjectCosts(currency, from, to, ordered, ordered.Sum(l => l.Amount));
        })];
    }

    /// <summary>Aggregates only: how many donors, how many were new, average and largest gift. No name, email or other personal detail leaves this report.</summary>
    public IReadOnlyList<DonorReport> DonorReports(IReadOnlyList<GiftFact> gifts, IReadOnlyDictionary<string, string> donorOf, DateOnly from, DateOnly to) =>
        [.. gifts.Select(g => g.Currency).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).Select(currency =>
        {
            var all = gifts.Where(g => g.Currency == currency && g.Amount > 0m).ToList();
            var period = all.Where(g => g.Date >= from && g.Date <= to).ToList();
            string? Donor(GiftFact g) => g.DonationId is not null && donorOf.TryGetValue(g.DonationId, out var key) ? key : null;
            var identified = period.Where(g => Donor(g) is not null).ToList();
            var donors = identified.Select(g => Donor(g)!).Distinct(StringComparer.Ordinal).ToList();
            var earlier = all.Where(g => g.Date < from && Donor(g) is not null).Select(g => Donor(g)!).ToHashSet(StringComparer.Ordinal);
            var unidentified = period.Where(g => Donor(g) is null).ToList();
            var total = period.Sum(g => g.Amount);
            return new DonorReport(currency, from, to, donors.Count, donors.Count(d => !earlier.Contains(d)), donors.Count(earlier.Contains), period.Count, total,
                period.Count == 0 ? 0m : decimal.Round(total / period.Count, 2, MidpointRounding.AwayFromZero), period.Count == 0 ? 0m : period.Max(g => g.Amount), unidentified.Count, unidentified.Sum(g => g.Amount));
        })];

    public static IReadOnlyList<JournalRow> Journal(IReadOnlyList<LedgerRow> rows, DateOnly from, DateOnly to) =>
        [.. rows.Where(r => r.PostedOn >= from && r.PostedOn <= to).Select(r => new JournalRow(r.PostingId, r.PostedOn, r.Code, r.Name, r.Memo, r.SourceType, r.Debit, r.Credit, r.Currency))];

    public async Task<Dictionary<string, string>> DesignationsAsync(IEnumerable<string> donationIds, CancellationToken cancellationToken)
    {
        var ids = donationIds.Distinct(StringComparer.Ordinal).ToList();
        var rows = await db.Donations.AsNoTracking().Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.DesignationType, d.DesignationId }).ToListAsync(cancellationToken);
        return rows.ToDictionary(d => d.Id, d => d.DesignationId is null ? d.DesignationType : $"{d.DesignationType}:{d.DesignationId}", StringComparer.Ordinal);
    }

    public async Task<Dictionary<string, (string? Project, string Category)>> ExpensesAsync(IEnumerable<string> expenseIds, CancellationToken cancellationToken)
    {
        var ids = expenseIds.Select(i => i.EndsWith(":payment", StringComparison.Ordinal) ? i[..^8] : i).Distinct(StringComparer.Ordinal).ToList();
        var rows = await db.Expenses.AsNoTracking().Where(e => ids.Contains(e.Id)).Select(e => new { e.Id, e.ProjectSlug, e.CategoryKey }).ToListAsync(cancellationToken);
        return rows.ToDictionary(e => e.Id, e => (e.ProjectSlug, e.CategoryKey), StringComparer.Ordinal);
    }

    /// <summary>Donation id to the donor's opaque key, from the receipt issued for it; a gift without a receipt is unidentified.</summary>
    public async Task<Dictionary<string, string>> DonorKeysAsync(IEnumerable<string> donationIds, CancellationToken cancellationToken)
    {
        var ids = donationIds.Distinct(StringComparer.Ordinal).ToList();
        var rows = await db.Receipts.AsNoTracking().Where(r => ids.Contains(r.DonationId) && !r.IsAnonymous).Select(r => new { r.DonationId, r.DonorKey }).ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.DonationId).ToDictionary(g => g.Key, g => g.First().DonorKey, StringComparer.Ordinal);
    }
}
