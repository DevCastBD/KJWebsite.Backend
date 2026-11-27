using ContentService.Data;
using KJWebsite.BuildingBlocks.Aggregates;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Aggregates;

/// <summary>Content's monthly figures for the reporting warehouse: what was published, the pipeline now, and event registrations and check-ins.</summary>
public static class ContentAggregates
{
    public const string Source = "content";

    public static async Task<AggregateBatch> CollectAsync(ContentDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var points = new List<AggregatePoint>();
        var items = await db.Items.AsNoTracking().Where(i => i.DeletedAt == null).Select(i => new { i.Type, i.Status, i.PublishedAt }).ToListAsync(ct);

        foreach (var g in items.Where(i => i.PublishedAt != null).GroupBy(i => (Month: AggregateFeedExtensions.MonthOf(i.PublishedAt!.Value), i.Type)))
        {
            points.Add(new("content.published", g.Key.Month, g.Key.Type, g.Count()));
        }

        var current = AggregateFeedExtensions.MonthOf(clock.GetUtcNow());
        foreach (var g in items.GroupBy(i => i.Status))
        {
            points.Add(new("content.pipeline.snapshot", current, g.Key, g.Count()));
        }

        var registrations = await db.EventRegistrations.AsNoTracking().Select(r => new { r.CreatedAt, r.CheckedInAt }).ToListAsync(ct);
        foreach (var g in registrations.GroupBy(r => AggregateFeedExtensions.MonthOf(r.CreatedAt)))
        {
            points.Add(new("events.registrations", g.Key, string.Empty, g.Count()));
        }

        foreach (var g in registrations.Where(r => r.CheckedInAt != null).GroupBy(r => AggregateFeedExtensions.MonthOf(r.CheckedInAt!.Value)))
        {
            points.Add(new("events.attended", g.Key, string.Empty, g.Count()));
        }

        return new AggregateBatch(points);
    }
}
