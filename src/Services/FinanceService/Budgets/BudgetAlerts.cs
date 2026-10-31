using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Budgets;

public sealed record AlertRunResult(int Checked, int Fired);

/// <summary>
/// Fires an alert the first time a budget line's spend reaches each configured percentage of its plan, and never again for that line and threshold (unique in the database, so two
/// runs at once still fire it once). An alert is a record, a Warning in the log and, when addresses are configured, a message in the outbox. Closed years are not checked.
/// </summary>
public sealed class BudgetAlertService(FinanceDbContext db, BudgetMath math, IDonorProtector protector, IOptions<BudgetOptions> options, ILogger<BudgetAlertService> logger, TimeProvider clock)
{
    private static readonly Action<ILogger, string, string, int, string, string, Exception?> Fired = LoggerMessage.Define<string, string, int, string, string>(
        LogLevel.Warning, new EventId(20, nameof(Fired)), "Budget alert: {Category} ({Year}) has reached {Threshold}% of its plan: spent {Actual} of {Planned}");

    public async Task<AlertRunResult> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var closed = (await db.FiscalYearCloses.AsNoTracking().Select(c => c.Label).ToListAsync(cancellationToken)).ToHashSet();
        var lines = (await db.Budgets.AsNoTracking().ToListAsync(cancellationToken)).Where(b => !closed.Contains(b.FiscalYear)).ToList();
        var actuals = await math.ActualsAsync(lines, cancellationToken);
        var already = (await db.BudgetAlerts.AsNoTracking().Select(a => new { a.BudgetId, a.ThresholdPercent }).ToListAsync(cancellationToken)).Select(a => (a.BudgetId, a.ThresholdPercent)).ToHashSet();
        var thresholds = options.Value.EffectiveThresholds;
        var now = clock.GetUtcNow();
        var fired = 0;

        foreach (var line in lines)
        {
            var actual = actuals[line.Id];
            foreach (var threshold in thresholds.Where(t => actual * 100m >= line.PlannedAmount * t && !already.Contains((line.Id, t))))
            {
                db.BudgetAlerts.Add(new BudgetAlertEntity { BudgetId = line.Id, ThresholdPercent = threshold, PlannedAmount = line.PlannedAmount, ActualAmount = actual, At = now });
                foreach (var address in options.Value.AlertEmails.Where(a => !string.IsNullOrWhiteSpace(a)))
                {
                    db.Notifications.Add(new FinanceNotificationEntity
                    {
                        Template = "budget.alert",
                        Language = "en",
                        RecipientProtected = protector.Protect(address.Trim()),
                        CreatedAt = now,
                        PayloadProtected = protector.Protect(JsonSerializer.Serialize(new { line.FiscalYear, line.CategoryKey, project = line.ProjectSlug, line.Currency, threshold, planned = BudgetMath.Fmt(line.PlannedAmount), actual = BudgetMath.Fmt(actual) }))
                    });
                }

                Fired(logger, line.CategoryKey + (line.ProjectSlug is null ? string.Empty : $"/{line.ProjectSlug}"), line.FiscalYear, threshold, $"{line.Currency} {BudgetMath.Fmt(actual)}", $"{line.Currency} {BudgetMath.Fmt(line.PlannedAmount)}", null);
                fired++;
            }
        }

        if (fired > 0)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A run at the same moment fired some of these first; the unique index kept each to one. Count what we can see.
                db.ChangeTracker.Clear();
                fired = 0;
            }
        }

        return new AlertRunResult(lines.Count, fired);
    }
}

/// <summary>Runs the budget alert check on a timer (`Finance:Budgets:CheckIntervalMinutes`, 0 = off).</summary>
public sealed class BudgetAlertWorker(IServiceScopeFactory scopes, IOptions<BudgetOptions> options, ILogger<BudgetAlertWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> RunFailed = LoggerMessage.Define(LogLevel.Error, new EventId(21, nameof(RunFailed)), "Budget alert check failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = options.Value.CheckIntervalMinutes;
        if (minutes <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        while (await Next(timer, stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<BudgetAlertService>().EvaluateAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RunFailed(logger, exception);
            }
        }
    }

    private static async Task<bool> Next(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
