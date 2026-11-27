using CtaSubmissionService.Data;
using KJWebsite.BuildingBlocks.Aggregates;
using Microsoft.EntityFrameworkCore;

namespace CtaSubmissionService.Aggregates;

/// <summary>The inbox's monthly figures for the reporting warehouse: what arrived, what got a first reply, what was closed, and what is open now.</summary>
public static class InboxAggregates
{
    public const string Source = "cta";

    public static async Task<AggregateBatch> CollectAsync(CtaDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var points = new List<AggregatePoint>();
        var rows = await db.Submissions.AsNoTracking().Select(s => new { s.Category, s.Priority, s.SubmittedAt, s.FirstResponseAt, s.ClosedAt }).ToListAsync(ct);

        foreach (var g in rows.GroupBy(r => (Month: AggregateFeedExtensions.MonthOf(r.SubmittedAt), r.Category)))
        {
            points.Add(new("inbox.received", g.Key.Month, g.Key.Category, g.Count()));
        }

        foreach (var g in rows.Where(r => r.FirstResponseAt != null).GroupBy(r => (Month: AggregateFeedExtensions.MonthOf(r.FirstResponseAt!.Value), r.Category)))
        {
            points.Add(new("inbox.first_responded", g.Key.Month, g.Key.Category, g.Count()));
        }

        foreach (var g in rows.Where(r => r.ClosedAt != null).GroupBy(r => (Month: AggregateFeedExtensions.MonthOf(r.ClosedAt!.Value), r.Category)))
        {
            points.Add(new("inbox.closed", g.Key.Month, g.Key.Category, g.Count()));
        }

        var current = AggregateFeedExtensions.MonthOf(clock.GetUtcNow());
        foreach (var g in rows.Where(r => r.ClosedAt == null).GroupBy(r => r.Priority))
        {
            points.Add(new("inbox.open.snapshot", current, g.Key, g.Count()));
        }

        return new AggregateBatch(points);
    }
}
