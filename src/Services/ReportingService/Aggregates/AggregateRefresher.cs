using System.Net.Http.Json;
using System.Text.Json;
using KJWebsite.BuildingBlocks.Aggregates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReportingService.Data;
using ReportingService.Data.Entities;

namespace ReportingService.Aggregates;

public sealed record ReconciliationLine(string Name, string Currency, decimal Warehouse, decimal Ledger, bool Agrees);

/// <summary>
/// Rebuilds the warehouse from the services' aggregate feeds. It is all or nothing: every source must answer and the donation totals must agree with the ledger, or nothing is
/// replaced, the run is recorded as failed (and logged as an error so it alerts), and the figures from the last good run are reported as stale rather than passed off as current.
/// </summary>
public sealed class AggregateRefresher(ReportingDbContext db, IHttpClientFactory http, IConfiguration configuration, IOptions<AggregateOptions> options, TimeProvider clock, ILogger<AggregateRefresher> logger)
{
    public const string ClientName = "aggregate-feed";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly Action<ILogger, string, string, Exception?> RefreshFailed = LoggerMessage.Define<string, string>(
        LogLevel.Error, new EventId(30, nameof(RefreshFailed)), "ALERT: aggregate refresh {RunId} failed, figures are stale: {Reason}");

    public async Task<AggregateRunEntity> RunAsync(string trigger, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var run = new AggregateRunEntity { Id = $"run_{Guid.NewGuid():N}", Trigger = trigger, StartedAt = clock.GetUtcNow() };
            db.AggregateRuns.Add(run);
            await db.SaveChangesAsync(ct);

            try
            {
                var feeds = new List<AggregateFeedResponse>();
                foreach (var (source, configName, defaultUrl) in AggregateSourceCatalog.All)
                {
                    feeds.Add(await FetchAsync(source, configuration[$"Services:{configName}:Url"] ?? defaultUrl, ct));
                }

                var lines = Reconcile(feeds, options.Value.Tolerance);
                run.ReconciliationJson = JsonSerializer.Serialize(lines, JsonSerializerOptions.Web);
                var broken = lines.Where(l => !l.Agrees).ToList();
                if (broken.Count > 0)
                {
                    throw new AggregateRefreshException("Donation totals do not agree with the ledger: " + string.Join("; ", broken.Select(l => $"{l.Currency} warehouse {l.Warehouse:0.00} vs ledger {l.Ledger:0.00}")));
                }

                await ReplaceAsync(feeds, run, ct);
            }
            catch (Exception ex) when (ex is AggregateRefreshException or JsonException && !ct.IsCancellationRequested)
            {
                db.ChangeTracker.Clear();
                run.Status = RunStatuses.Failed;
                run.Error = Truncate(ex.Message, 2000);
                run.FinishedAt = clock.GetUtcNow();
                db.AggregateRuns.Update(run);
                await db.SaveChangesAsync(CancellationToken.None);
                RefreshFailed(logger, run.Id, run.Error, null);
            }

            return run;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<AggregateFeedResponse> FetchAsync(string source, string baseUrl, CancellationToken ct)
    {
        var attempts = Math.Max(1, options.Value.Attempts);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), AggregateFeedExtensions.Route));
                request.Headers.Add(AggregateFeedExtensions.KeyHeader, configuration[AggregateFeedExtensions.KeySetting] ?? string.Empty);
                using var response = await http.CreateClient(ClientName).SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new AggregateRefreshException($"{source} answered {(int)response.StatusCode}.");
                }

                var feed = await response.Content.ReadFromJsonAsync<AggregateFeedResponse>(ct) ?? throw new AggregateRefreshException($"{source} sent an empty feed.");
                return feed with { Source = source };
            }
            catch (Exception ex) when (attempt < attempts && ex is HttpRequestException or TaskCanceledException or AggregateRefreshException && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new AggregateRefreshException($"{source} could not be reached: {ex.Message}");
            }
        }
    }

    /// <summary>The warehouse's own donation total per currency, against the figure Finance read straight from the ledger.</summary>
    public static IReadOnlyList<ReconciliationLine> Reconcile(IEnumerable<AggregateFeedResponse> feeds, decimal tolerance = 0.005m)
    {
        var finance = feeds.FirstOrDefault(f => f.Source == "finance");
        if (finance is null)
        {
            return [];
        }

        var warehouse = finance.Points.Where(p => p.Metric == "donations.confirmed.amount").GroupBy(p => p.Currency ?? string.Empty).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
        var ledger = finance.Checks.Where(c => c.Name == "ledger.donations.net").GroupBy(c => c.Currency ?? string.Empty).ToDictionary(g => g.Key, g => g.Sum(c => c.Amount));
        return [.. warehouse.Keys.Union(ledger.Keys).Order(StringComparer.Ordinal).Select(currency =>
        {
            var w = warehouse.GetValueOrDefault(currency);
            var l = ledger.GetValueOrDefault(currency);
            return new ReconciliationLine("donations.confirmed.amount vs ledger", currency, w, l, Math.Abs(w - l) <= tolerance);
        })];
    }

    private async Task ReplaceAsync(List<AggregateFeedResponse> feeds, AggregateRunEntity run, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var sources = feeds.Select(f => f.Source).ToList();
        await db.AggregatePoints.Where(p => sources.Contains(p.Source)).ExecuteDeleteAsync(ct);
        var total = 0;
        foreach (var feed in feeds)
        {
            foreach (var group in feed.Points.GroupBy(p => (p.Metric, p.Period, p.Dimension, Currency: p.Currency ?? string.Empty)))
            {
                db.AggregatePoints.Add(new AggregatePointEntity { Source = feed.Source, Metric = group.Key.Metric, Period = group.Key.Period, Dimension = group.Key.Dimension, Currency = group.Key.Currency, Value = group.Sum(p => p.Value) });
                total++;
            }

            var known = await db.AggregateSources.FindAsync([feed.Source], ct);
            if (known is null)
            {
                db.AggregateSources.Add(new AggregateSourceEntity { Source = feed.Source, AsOf = feed.AsOf, RunId = run.Id });
            }
            else
            {
                known.AsOf = feed.AsOf;
                known.RunId = run.Id;
            }
        }

        run.Status = RunStatuses.Succeeded;
        run.Points = total;
        run.FinishedAt = clock.GetUtcNow();
        db.AggregateRuns.Update(run);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}

public sealed class AggregateRefreshException(string message) : Exception(message);

/// <summary>Runs the refresh once a day at `Reporting:Aggregates:RefreshHourUtc`. A failure is recorded and logged by the refresher; the worker keeps its schedule.</summary>
public sealed class AggregateWorker(IServiceScopeFactory scopes, IOptions<AggregateOptions> options, TimeProvider clock, ILogger<AggregateWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> Crashed = LoggerMessage.Define(LogLevel.Error, new EventId(31, nameof(Crashed)), "ALERT: the aggregate refresh worker threw before it could record a run");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.GetUtcNow();
            var next = new DateTimeOffset(now.Year, now.Month, now.Day, Math.Clamp(options.Value.RefreshHourUtc, 0, 23), 0, 0, TimeSpan.Zero);
            if (next <= now)
            {
                next = next.AddDays(1);
            }

            try
            {
                await Task.Delay(next - now, clock, stoppingToken);
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AggregateRefresher>().RunAsync("schedule", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                Crashed(logger, ex);
            }
        }
    }
}
