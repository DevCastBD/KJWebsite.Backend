using System.Globalization;
using System.Text.RegularExpressions;
using KJWebsite.BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using ReportingService.Aggregates;
using ReportingService.Data;
using ReportingService.Data.Entities;

namespace ReportingService.Metrics;

public static class MetricKinds
{
    public const string Monthly = "monthly";
    public const string Snapshot = "snapshot";
    public static readonly IReadOnlyList<string> All = [Monthly, Snapshot];
}

/// <summary>The warehouse metrics an impact definition may be built on, with the service each comes from.</summary>
public static class MetricCatalog
{
    public static readonly IReadOnlyDictionary<string, string> Sources = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["volunteer.minutes.approved"] = "membership",
        ["sessions.delivered"] = "membership",
        ["sessions.attendance"] = "membership",
        ["students.enrolled.snapshot"] = "membership",
        ["institutions.engaged.snapshot"] = "membership",
        ["members.joined"] = "membership",
        ["events.registrations"] = "content",
        ["events.attended"] = "content",
        ["donations.confirmed.count"] = "finance"
    };
}

public sealed record MetricVersionRequest(string? Name, string? Description, string? Methodology, string? Unit, string? Metric, string? Kind, decimal? Scale, string? EffectiveFrom);

public sealed record MetricCreateRequest(string? Key, string? Name, string? Description, string? Methodology, string? Unit, string? Metric, string? Kind, decimal? Scale, string? EffectiveFrom);

public sealed record MetricVersionView(string Key, int Version, string Name, string Description, string Methodology, string Unit, string Metric, string Source, string Kind, decimal Scale, string EffectiveFrom, DateTimeOffset CreatedAt, string? CreatedBy);

public sealed record MetricPointView(string Period, string Value, int DefinitionVersion);

public sealed record MetricSeriesView(MetricVersionView Definition, IReadOnlyList<MetricVersionView> Versions, string? Project, IReadOnlyList<MetricPointView> Points, string? Total, DashboardTraceView Trace, DateTimeOffset? AsOf, bool Stale);

public sealed record DashboardTraceView(string Metric, string Source, string Drilldown);

/// <summary>
/// Impact metric definitions as dated, append-only versions. A version applies from its effective month onward and a new one may not start earlier than the current month, so changing
/// what "students reached" means never rewrites a figure already published for an earlier month; each point says which version produced it.
/// </summary>
public sealed partial class MetricService(ReportingDbContext db, AggregateQuery aggregates, TimeProvider clock)
{
    [GeneratedRegex("^[a-z][a-z0-9-]{2,63}$")]
    private static partial Regex KeyPattern();

    private DateOnly ThisMonth() { var t = clock.GetUtcNow(); return new DateOnly(t.Year, t.Month, 1); }

    public static MetricVersionView View(MetricDefinitionEntity e) => new(e.Key, e.Version, e.Name, e.Description, e.Methodology, e.Unit, e.Metric, MetricCatalog.Sources[e.Metric], e.Kind, e.Scale,
        e.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e.CreatedAt, e.CreatedBy);

    public async Task<Result<MetricVersionView>> CreateAsync(MetricCreateRequest request, string? actor, CancellationToken ct)
    {
        var key = request.Key?.Trim() ?? string.Empty;
        if (!KeyPattern().IsMatch(key))
        {
            return Result.Failure<MetricVersionView>("VALIDATION_ERROR", "key must be 3-64 lowercase letters, digits or dashes, starting with a letter.");
        }

        if (await db.MetricDefinitions.AnyAsync(m => m.Key == key, ct))
        {
            return Result.Failure<MetricVersionView>("DUPLICATE", "A metric with this key exists; add a new version instead.");
        }

        return await AddAsync(key, 1, request.Name, request.Description, request.Methodology, request.Unit, request.Metric, request.Kind, request.Scale, request.EffectiveFrom, actor, firstVersion: true, ct);
    }

    public async Task<Result<MetricVersionView>> AddVersionAsync(string key, MetricVersionRequest request, string? actor, CancellationToken ct)
    {
        var latest = (await db.MetricDefinitions.AsNoTracking().Where(m => m.Key == key).ToListAsync(ct)).OrderByDescending(m => m.Version).FirstOrDefault();
        if (latest is null)
        {
            return Result.Failure<MetricVersionView>("NOT_FOUND", "No such metric.");
        }

        return await AddAsync(key, latest.Version + 1, request.Name ?? latest.Name, request.Description ?? latest.Description, request.Methodology, request.Unit ?? latest.Unit, request.Metric ?? latest.Metric,
            request.Kind ?? latest.Kind, request.Scale ?? latest.Scale, request.EffectiveFrom, actor, firstVersion: false, ct);
    }

    private async Task<Result<MetricVersionView>> AddAsync(string key, int version, string? name, string? description, string? methodology, string? unit, string? metric, string? kind, decimal? scale, string? effectiveFrom,
        string? actor, bool firstVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || string.IsNullOrWhiteSpace(unit) || unit.Length > 32)
        {
            return Result.Failure<MetricVersionView>("VALIDATION_ERROR", "name (up to 120) and unit (up to 32) are required.");
        }

        if (string.IsNullOrWhiteSpace(methodology) || methodology.Length > 2000)
        {
            return Result.Failure<MetricVersionView>("VALIDATION_ERROR", "methodology is required (up to 2000 characters): it is the definition shown beside the figure, and a changed definition must say what changed.");
        }

        if (description is { Length: > 500 })
        {
            return Result.Failure<MetricVersionView>("VALIDATION_ERROR", "description is at most 500 characters.");
        }

        if (metric is null || !MetricCatalog.Sources.ContainsKey(metric))
        {
            return Result.Failure<MetricVersionView>("VALIDATION_ERROR", $"metric must be one of: {string.Join(", ", MetricCatalog.Sources.Keys.Order(StringComparer.Ordinal))}.");
        }

        if (kind is null || !MetricKinds.All.Contains(kind))
        {
            return Result.Failure<MetricVersionView>("VALIDATION_ERROR", "kind must be 'monthly' or 'snapshot'.");
        }

        if (scale is not > 0 or > 100000)
        {
            return Result.Failure<MetricVersionView>("VALIDATION_ERROR", "scale must be greater than 0 (for example 0.0166667 turns minutes into hours).");
        }

        var thisMonth = ThisMonth();
        DateOnly from = thisMonth;
        if (!string.IsNullOrWhiteSpace(effectiveFrom))
        {
            if (!DateOnly.TryParseExact(effectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out from) || from.Day != 1)
            {
                return Result.Failure<MetricVersionView>("VALIDATION_ERROR", "effectiveFrom must be the first day of a month (yyyy-MM-01).");
            }
        }

        if (!firstVersion && from < thisMonth)
        {
            return Result.Failure<MetricVersionView>("HISTORY_LOCKED", "A new definition cannot start before the current month: figures already published for earlier months keep the definition they were published under.");
        }

        var entity = new MetricDefinitionEntity
        {
            Id = $"mtd_{Guid.NewGuid():N}", Key = key, Version = version, Name = name.Trim(), Description = description?.Trim() ?? string.Empty, Methodology = methodology.Trim(), Unit = unit.Trim(),
            Metric = metric, Kind = kind, Scale = scale.Value, EffectiveFrom = from, CreatedBy = actor, CreatedAt = clock.GetUtcNow()
        };
        db.MetricDefinitions.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Result.Failure<MetricVersionView>("CONFLICT", "Another change to this metric was saved first; reload and try again.");
        }

        return Result.Success(View(entity));
    }

    public async Task<IReadOnlyList<MetricVersionView>> CurrentAsync(CancellationToken ct)
    {
        var all = await db.MetricDefinitions.AsNoTracking().ToListAsync(ct);
        var month = ThisMonth();
        return [.. all.GroupBy(m => m.Key).Select(g => InForce(g, month) ?? g.OrderBy(m => m.Version).First()).OrderBy(m => m.Key, StringComparer.Ordinal).Select(View)];
    }

    public async Task<IReadOnlyList<MetricVersionView>> VersionsAsync(string key, CancellationToken ct) =>
        [.. (await db.MetricDefinitions.AsNoTracking().Where(m => m.Key == key).ToListAsync(ct)).OrderBy(m => m.Version).Select(View)];

    private static MetricDefinitionEntity? InForce(IEnumerable<MetricDefinitionEntity> versions, DateOnly period) =>
        versions.Where(v => v.EffectiveFrom <= period).OrderByDescending(v => v.Version).FirstOrDefault();

    public async Task<Result<MetricSeriesView>> SeriesAsync(string key, string? project, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var versions = (await db.MetricDefinitions.AsNoTracking().Where(m => m.Key == key).ToListAsync(ct)).OrderBy(m => m.Version).ToList();
        if (versions.Count == 0)
        {
            return Result.Failure<MetricSeriesView>("NOT_FOUND", "No such metric.");
        }

        var month = ThisMonth();
        var current = InForce(versions, month) ?? versions[0];
        var start = from ?? new DateOnly(month.Year - 3, month.Month, 1);
        var end = to ?? month;

        // Each month is computed under the version in force for it, so a later change leaves earlier months exactly as published.
        var points = new List<MetricPointView>();
        foreach (var group in versions.GroupBy(v => v.Metric))
        {
            var rows = await aggregates.SeriesAsync(group.Key, null, null, project, null, ct);
            foreach (var byPeriod in rows.Where(r => r.Period >= start && r.Period <= end).GroupBy(r => r.Period))
            {
                var version = InForce(versions, byPeriod.Key);
                if (version is null || version.Metric != group.Key)
                {
                    continue;
                }

                points.Add(new MetricPointView(byPeriod.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), (byPeriod.Sum(r => r.Value) * version.Scale).ToString("0.00##", CultureInfo.InvariantCulture), version.Version));
            }
        }

        points = [.. points.OrderBy(p => p.Period, StringComparer.Ordinal)];
        string? total = current.Kind == MetricKinds.Monthly
            ? points.Sum(p => decimal.Parse(p.Value, CultureInfo.InvariantCulture)).ToString("0.00##", CultureInfo.InvariantCulture)
            : null;
        var freshness = await aggregates.FreshnessAsync(ct);
        var source = MetricCatalog.Sources[current.Metric];
        return Result.Success(new MetricSeriesView(View(current), [.. versions.Select(View)], project, points, total,
            new DashboardTraceView(current.Metric, source, $"/api/v1/admin/reporting/aggregates?metric={current.Metric}&source={source}"), freshness.AsOf, freshness.Stale));
    }
}

public static class MetricSeeder
{
    /// <summary>The definitions the organisation starts with. Each can be superseded by a new dated version; none is ever edited.</summary>
    public static async Task EnsureAsync(ReportingDbContext db, TimeProvider clock, CancellationToken ct = default)
    {
        if (await db.MetricDefinitions.AnyAsync(ct))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var origin = new DateOnly(2000, 1, 1);
        (string Key, string Name, string Unit, string Metric, string Kind, decimal Scale, string Methodology)[] seeds =
        [
            ("students-reached", "Students reached", "students", "students.enrolled.snapshot", MetricKinds.Snapshot, 1m,
                "Students currently enrolled in a programme at a partner institution, each counted once however many sessions they attend. Taken from the enrolment counts programme staff record per institution and project."),
            ("student-attendances", "Student attendances", "attendances", "sessions.attendance", MetricKinds.Monthly, 1m,
                "The headcount recorded at each delivered session, added up. A student who attends ten sessions counts ten times. Use alongside students reached."),
            ("sessions-delivered", "Sessions delivered", "sessions", "sessions.delivered", MetricKinds.Monthly, 1m,
                "Classes or sessions logged as delivered by volunteers in the field, counted once each however late they were logged."),
            ("institutions-engaged", "Institutions engaged", "institutions", "institutions.engaged.snapshot", MetricKinds.Snapshot, 1m,
                "Schools and other institutions with at least one enrolled student on a project right now."),
            ("volunteer-hours", "Volunteer hours", "hours", "volunteer.minutes.approved", MetricKinds.Monthly, 1m / 60m,
                "Hours volunteers logged that an organiser other than the volunteer approved. Hours that are submitted but not approved, or were rejected, are not counted.")
        ];
        foreach (var s in seeds)
        {
            db.MetricDefinitions.Add(new MetricDefinitionEntity
            {
                Id = $"mtd_{Guid.NewGuid():N}", Key = s.Key, Version = 1, Name = s.Name, Description = string.Empty, Methodology = s.Methodology, Unit = s.Unit, Metric = s.Metric, Kind = s.Kind,
                Scale = s.Scale, EffectiveFrom = origin, CreatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
