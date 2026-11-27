using System.Globalization;
using System.Text.Json;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using ReportingService.Data;
using ReportingService.Data.Entities;

namespace ReportingService.Aggregates;

public sealed record AggregateItemView(string Source, string Metric, string Period, string Dimension, string? Currency, string Value);

public sealed record AggregateListView(IEnumerable<AggregateItemView> Items, DateTimeOffset? AsOf, bool Stale, IReadOnlyList<SourceFreshness> Sources);

public sealed record AggregateRunView(string Id, string Trigger, string Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? Error, int Points, JsonElement? Reconciliation);

public sealed record AggregateStatusView(IReadOnlyList<SourceFreshness> Sources, DateTimeOffset? AsOf, bool Stale, bool Alert, AggregateRunView? LastRun, AggregateRunView? LastSuccess);

public sealed record AggregateRunsView(IEnumerable<AggregateRunView> Items);

public static class AggregateEndpoints
{
    private const string Route = "/api/v1/admin/reporting/aggregates";

    public static void MapAggregates(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (HttpContext context, AggregateQuery query, string? metric, string? source, string? dimension, string? from, string? to) =>
        {
            if (string.IsNullOrWhiteSpace(metric))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "metric is required.");
            }

            if (!TryDay(from, out var start) || !TryDay(to, out var end))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "from and to must be dates (yyyy-MM-dd).");
            }

            var rows = await query.SeriesAsync(metric.Trim(), start, end, dimension, source, context.RequestAborted);
            var freshness = await query.FreshnessAsync(context.RequestAborted);
            return Results.Ok(new AggregateListView(rows.Select(Present).ToList(), freshness.AsOf, freshness.Stale, freshness.Sources));
        })
        .WithName("ListAggregates")
        .WithSummary("Monthly warehouse figures for one metric")
        .WithDescription("`metric` is required (for example `donations.confirmed.amount`); `source`, `dimension`, `from` and `to` narrow it. Every answer carries `asOf` (the oldest source refresh) and `stale`, so a page can label the figure honestly. Money is a decimal string with its currency. Requires `reporting.view`.")
        .Produces<AggregateListView>()
        .WithTags("Aggregates")
        .RequirePermission(Permissions.ReportingView);

        app.MapGet($"{Route}/status", async (AggregateQuery query, ReportingDbContext db, CancellationToken ct) =>
        {
            var freshness = await query.FreshnessAsync(ct);
            var runs = (await db.AggregateRuns.AsNoTracking().ToListAsync(ct)).OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).ToList();
            var last = runs.FirstOrDefault();
            return Results.Ok(new AggregateStatusView(freshness.Sources, freshness.AsOf, freshness.Stale, last?.Status == RunStatuses.Failed, last is null ? null : View(last),
                runs.Where(r => r.Status == RunStatuses.Succeeded).Select(View).FirstOrDefault()));
        })
        .WithName("AggregateStatus")
        .WithSummary("Freshness of the warehouse and whether the last refresh failed")
        .WithDescription("`alert` is true while the most recent run failed; `stale` is true when any source is older than the allowed age or the last run failed. Requires `reporting.view`.")
        .Produces<AggregateStatusView>()
        .WithTags("Aggregates")
        .RequirePermission(Permissions.ReportingView);

        app.MapGet($"{Route}/runs", async (ReportingDbContext db, int? limit, CancellationToken ct) =>
        {
            var runs = (await db.AggregateRuns.AsNoTracking().ToListAsync(ct)).OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).Take(Math.Clamp(limit ?? 20, 1, 100));
            return Results.Ok(new AggregateRunsView(runs.Select(View).ToList()));
        })
        .WithName("ListAggregateRuns")
        .WithSummary("Recent refresh runs, newest first, with the ledger reconciliation of each")
        .Produces<AggregateRunsView>()
        .WithTags("Aggregates")
        .RequirePermission(Permissions.ReportingView);

        app.MapPost($"{Route}/refresh", async (AggregateRefresher refresher, CancellationToken ct) =>
        {
            var run = await refresher.RunAsync("manual", ct);
            return run.Status == RunStatuses.Succeeded ? Results.Ok(View(run)) : Results.Json(View(run), statusCode: StatusCodes.Status502BadGateway);
        })
        .WithName("RefreshAggregates")
        .WithSummary("Refresh the warehouse now")
        .WithDescription("Reads every service's feed, checks the donation totals against the ledger and replaces the figures only if both succeed (200). Otherwise nothing changes and the run is returned with its error (502). Requires `reporting.manage`.")
        .Produces<AggregateRunView>().Produces<AggregateRunView>(StatusCodes.Status502BadGateway)
        .WithTags("Aggregates")
        .RequirePermission(Permissions.ReportingManage);
    }

    private static bool TryDay(string? text, out DateOnly? day)
    {
        day = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        day = parsed;
        return true;
    }

    private static AggregateItemView Present(AggregateRow row) => new(row.Source, row.Metric, row.Period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row.Dimension,
        row.Currency.Length == 0 ? null : row.Currency, row.Value.ToString("0.00##", CultureInfo.InvariantCulture));

    private static AggregateRunView View(AggregateRunEntity run) => new(run.Id, run.Trigger, run.Status, run.StartedAt, run.FinishedAt, run.Error, run.Points,
        run.ReconciliationJson is null ? null : JsonSerializer.Deserialize<JsonElement>(run.ReconciliationJson));
}
