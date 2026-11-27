using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Ledger;
using KJWebsite.BuildingBlocks.Aggregates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Reports;

/// <summary>
/// The monthly figures Finance gives the reporting warehouse (the feed behind `/internal/v1/aggregates`). Money comes from confirmed donations and the ledger only. The two
/// `ledger.*` checks are read straight from the ledger so the warehouse can prove its donation totals still agree with the books before it publishes them.
/// </summary>
public sealed class FinanceAggregateSource(FinanceDbContext db, IOptions<LedgerOptions> ledger)
{
    public const string Source = "finance";
    public const string DonationAmount = "donations.confirmed.amount";
    public const string DonationCount = "donations.confirmed.count";
    public const string ExpenseAmount = "expenses.ledger.amount";
    public const string BudgetPlanned = "budget.planned.amount";
    public const string LedgerDonationCheck = "ledger.donations.net";

    public async Task<AggregateBatch> CollectAsync(CancellationToken ct)
    {
        var points = new List<AggregatePoint>();

        // A donation counts once its latest event is "confirmed"; reversals are confirmed rows with negative amounts, so they net out.
        var latest = db.DonationEvents.GroupBy(e => e.DonationId).Select(g => new { DonationId = g.Key, EventId = g.Max(e => e.Id) });
        var confirmed = await (from d in db.Donations.AsNoTracking()
                               join l in latest on d.Id equals l.DonationId
                               join e in db.DonationEvents on l.EventId equals e.Id
                               where e.Status == DonationStatuses.Confirmed
                               select new { d.ReceivedOn, d.Amount, d.Currency, d.DesignationType }).ToListAsync(ct);
        foreach (var group in confirmed.GroupBy(d => (Month: AggregateFeedExtensions.MonthOf(d.ReceivedOn), d.DesignationType, d.Currency)))
        {
            points.Add(new(DonationAmount, group.Key.Month, group.Key.DesignationType, group.Sum(d => d.Amount), group.Key.Currency));
            points.Add(new(DonationCount, group.Key.Month, group.Key.DesignationType, group.Count(), group.Key.Currency));
        }

        var expenseRows = await (from le in db.LedgerEntries.AsNoTracking()
                                 join p in db.Postings on le.PostingId equals p.Id
                                 join a in db.Accounts on le.AccountId equals a.Id
                                 where a.Type == AccountTypes.Expense
                                 select new { p.PostedOn, a.Code, le.Debit, le.Credit, le.Currency }).ToListAsync(ct);
        foreach (var group in expenseRows.GroupBy(r => (Month: AggregateFeedExtensions.MonthOf(r.PostedOn), r.Code, r.Currency)))
        {
            points.Add(new(ExpenseAmount, group.Key.Month, group.Key.Code, group.Sum(r => r.Debit - r.Credit), group.Key.Currency));
        }

        var start = ledger.Value.FiscalYearStartMonth;
        foreach (var group in (await db.Budgets.AsNoTracking().ToListAsync(ct)).GroupBy(b => (b.FiscalYear, b.Currency)))
        {
            if (FiscalCalendar.TryRange(group.Key.FiscalYear, start, out var from, out _))
            {
                points.Add(new(BudgetPlanned, AggregateFeedExtensions.MonthOf(from), string.Empty, group.Sum(b => b.PlannedAmount), group.Key.Currency));
            }
        }

        var donationRows = await (from le in db.LedgerEntries.AsNoTracking()
                                  join p in db.Postings on le.PostingId equals p.Id
                                  join a in db.Accounts on le.AccountId equals a.Id
                                  where (a.Code == LedgerAccounts.DonationsGeneral || a.Code == LedgerAccounts.DonationsDesignated)
                                        && (p.SourceType == PostingSources.Donation || p.SourceType == PostingSources.Reversal)
                                  select new { le.Debit, le.Credit, le.Currency }).ToListAsync(ct);
        var checks = donationRows.GroupBy(r => r.Currency).Select(g => new AggregateCheck(LedgerDonationCheck, g.Sum(r => r.Credit - r.Debit), g.Key)).ToList();

        return new AggregateBatch(points, checks);
    }
}
