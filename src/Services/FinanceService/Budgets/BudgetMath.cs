using System.Globalization;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Expenses;
using FinanceService.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Budgets;

public sealed class BudgetOptions
{
    public const string SectionName = "Finance:Budgets";

    /// <summary>Percent of a line's plan spent at which an alert fires, each once. Empty means 80 and 100. (A default array here would be appended to, not replaced, by configuration.)</summary>
    public int[] AlertThresholdPercents { get; set; } = [];

    /// <summary>Addresses that get a `budget.alert` message in the outbox when an alert fires. Empty: alerts are recorded and logged only.</summary>
    public string[] AlertEmails { get; set; } = [];

    /// <summary>How often the background check runs; 0 switches it off (the admin endpoint still works).</summary>
    public int CheckIntervalMinutes { get; set; } = 15;

    public static readonly int[] DefaultThresholds = [80, 100];

    public IReadOnlyList<int> EffectiveThresholds => [.. (AlertThresholdPercents.Length > 0 ? AlertThresholdPercents : DefaultThresholds).Where(t => t > 0).Distinct().Order()];
}

/// <summary>What the ledger says was spent against each budget line. The only place that works it out, so the budget screen, the alerts and the tests cannot disagree.</summary>
public sealed class BudgetMath(FinanceDbContext db, IOptions<ExpenseOptions> expenseOptions, IOptions<LedgerOptions> ledgerOptions)
{
    private sealed record Entry(long PostingId, DateOnly PostedOn, string Code, string Currency, decimal Debit, decimal Credit);

    /// <summary>
    /// Actual spend per line: debits less credits on the line's category account, in the line's currency, on postings dated inside the fiscal year. A project line counts only
    /// postings that came from an expense coded to that project (a reversal follows the posting it reverses); the no-project line counts everything on the account, manual
    /// adjustments included. Nothing is tallied separately: this reads the ledger.
    /// </summary>
    public async Task<Dictionary<string, decimal>> ActualsAsync(IReadOnlyCollection<BudgetEntity> lines, CancellationToken cancellationToken = default)
    {
        var result = lines.ToDictionary(l => l.Id, _ => 0m);
        if (lines.Count == 0)
        {
            return result;
        }

        var ranges = lines.Select(l => l.FiscalYear).Distinct().ToDictionary(y => y, y => FiscalCalendar.TryRange(y, ledgerOptions.Value.FiscalYearStartMonth, out var s, out var e) ? (s, e) : default);
        var categories = expenseOptions.Value.EffectiveCategories;

        var entries = (await (from e in db.LedgerEntries.AsNoTracking()
                              join p in db.Postings.AsNoTracking() on e.PostingId equals p.Id
                              join a in db.Accounts.AsNoTracking() on e.AccountId equals a.Id
                              where a.Type == AccountTypes.Expense
                              select new Entry(e.PostingId, p.PostedOn, a.Code, e.Currency, e.Debit, e.Credit)).ToListAsync(cancellationToken));

        var postings = await db.Postings.AsNoTracking().Where(p => p.SourceType == PostingSources.Expense || p.ReversesPostingId != null)
            .Select(p => new { p.Id, p.SourceType, p.SourceId, p.ReversesPostingId }).ToListAsync(cancellationToken);
        var expenseOfPosting = postings.Where(p => p.SourceType == PostingSources.Expense && p.SourceId != null && !p.SourceId.EndsWith(":payment", StringComparison.Ordinal)).ToDictionary(p => p.Id, p => p.SourceId!);
        foreach (var reversal in postings.Where(p => p.ReversesPostingId is not null))
        {
            if (expenseOfPosting.TryGetValue(reversal.ReversesPostingId!.Value, out var expenseId))
            {
                expenseOfPosting[reversal.Id] = expenseId;
            }
        }

        var projectOfExpense = (await db.Expenses.AsNoTracking().Select(e => new { e.Id, e.ProjectSlug }).ToListAsync(cancellationToken)).ToDictionary(e => e.Id, e => e.ProjectSlug);
        string? ProjectOf(long postingId) => expenseOfPosting.TryGetValue(postingId, out var id) ? projectOfExpense.GetValueOrDefault(id) : null;

        foreach (var line in lines)
        {
            if (!categories.TryGetValue(line.CategoryKey, out var account) || ranges[line.FiscalYear] == default)
            {
                continue;
            }

            var (start, end) = ranges[line.FiscalYear];
            result[line.Id] = entries.Where(e => e.Code == account && e.Currency == line.Currency && e.PostedOn >= start && e.PostedOn <= end
                && (line.ProjectSlug is null || ProjectOf(e.PostingId) == line.ProjectSlug)).Sum(e => e.Debit - e.Credit);
        }

        return result;
    }

    public static decimal PercentUsed(decimal planned, decimal actual) => planned <= 0m ? 0m : Math.Round(actual / planned * 100m, 1, MidpointRounding.AwayFromZero);

    public static string Status(decimal planned, decimal actual, IReadOnlyList<int> thresholds)
    {
        if (actual > planned)
        {
            return "over";
        }

        return thresholds.Count > 0 && actual * 100m >= planned * thresholds[0] ? "warning" : "ok";
    }

    public static string Fmt(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
