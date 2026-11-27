using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReportingService.Data;
using ReportingService.Data.Entities;

namespace ReportingService.Aggregates;

public sealed record SourceFreshness(string Source, DateTimeOffset? AsOf, bool Stale);

public sealed record AggregateRow(string Source, string Metric, DateOnly Period, string Dimension, string Currency, decimal Value);

public sealed record FreshnessView(IReadOnlyList<SourceFreshness> Sources, bool Stale, DateTimeOffset? AsOf);

/// <summary>
/// The only way figures leave the warehouse. Dashboards, metric series and reports read through this, never from another service's tables, and every answer says how fresh it is:
/// a source is stale when it was never refreshed, when the last good refresh is older than the allowed age, or when the most recent run failed.
/// </summary>
public sealed class AggregateQuery(ReportingDbContext db, IOptions<AggregateOptions> options, TimeProvider clock)
{
    public async Task<FreshnessView> FreshnessAsync(CancellationToken ct)
    {
        var sources = await db.AggregateSources.AsNoTracking().ToListAsync(ct);
        var runs = await db.AggregateRuns.AsNoTracking().ToListAsync(ct);
        var lastRunFailed = runs.OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).FirstOrDefault()?.Status == RunStatuses.Failed;
        var limit = clock.GetUtcNow().AddHours(-options.Value.MaxAgeHours);
        var views = AggregateSourceCatalog.All.Select(c =>
        {
            var known = sources.FirstOrDefault(s => s.Source == c.Source);
            return new SourceFreshness(c.Source, known?.AsOf, known is null || known.AsOf < limit || lastRunFailed);
        }).ToList();
        return new FreshnessView(views, views.Any(v => v.Stale), views.Where(v => v.AsOf != null).Select(v => v.AsOf).Min());
    }

    public async Task<IReadOnlyList<AggregateRow>> SeriesAsync(string metric, DateOnly? from, DateOnly? to, string? dimension, string? source, CancellationToken ct)
    {
        var query = db.AggregatePoints.AsNoTracking().Where(p => p.Metric == metric);
        if (source is not null)
        {
            query = query.Where(p => p.Source == source);
        }

        if (dimension is not null)
        {
            query = query.Where(p => p.Dimension == dimension);
        }

        if (from is { } f)
        {
            query = query.Where(p => p.Period >= f);
        }

        if (to is { } t)
        {
            query = query.Where(p => p.Period <= t);
        }

        return [.. (await query.ToListAsync(ct)).OrderBy(p => p.Period).ThenBy(p => p.Dimension, StringComparer.Ordinal).ThenBy(p => p.Currency, StringComparer.Ordinal)
            .Select(p => new AggregateRow(p.Source, p.Metric, p.Period, p.Dimension, p.Currency, p.Value))];
    }

    /// <summary>The newest period's rows for a snapshot metric (`*.snapshot`), summed per dimension.</summary>
    public async Task<IReadOnlyList<AggregateRow>> LatestAsync(string metric, CancellationToken ct)
    {
        var rows = await SeriesAsync(metric, null, null, null, null, ct);
        var newest = rows.Select(r => r.Period).DefaultIfEmpty().Max();
        return [.. rows.Where(r => r.Period == newest)];
    }
}
