using System.Text.Json;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Intake;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Privacy;

public sealed record RetentionCandidate(string ApplicationId, string Status, DateOnly StatusSince);

public sealed record RetentionResult(string RunId, bool DryRun, string StartedBy, object Policy, IReadOnlyList<RetentionCandidate> Items, int Purged);

/// <summary>
/// Removes applications that outlived their retention window. The age of an application is how long it has been in its current
/// status (rejected, withdrawn or postponed); an application with no window set for its status is never touched. Each run leaves a
/// record of the policy used and the opaque ids removed, and nothing personal.
/// </summary>
public static class RetentionPurger
{
    public static async Task<RetentionResult> RunAsync(MembershipDbContext db, IAttachmentStore store, PrivacyOptions options, TimeProvider clock, string startedBy, bool dryRun, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var policy = new { rejectedMonths = options.WindowFor(ApplicationStatuses.Rejected), withdrawnMonths = options.WindowFor(ApplicationStatuses.Withdrawn), postponedMonths = options.WindowFor(ApplicationStatuses.Postponed), noticeVersion = options.NoticeVersion };
        var run = new RetentionRunEntity { Id = $"ret_{Guid.NewGuid():N}", StartedAt = now, DryRun = dryRun, StartedBy = startedBy, PolicyJson = JsonSerializer.Serialize(policy) };
        var items = new List<RetentionCandidate>();

        foreach (var status in new[] { ApplicationStatuses.Rejected, ApplicationStatuses.Withdrawn, ApplicationStatuses.Postponed })
        {
            if (options.WindowFor(status) is not { } months)
            {
                continue;
            }

            var cutoff = now.AddMonths(-months);
            var applications = await db.Applications.AsNoTracking().Where(a => a.Status == status).Select(a => a.Id).ToListAsync(cancellationToken);
            var events = (await db.StatusEvents.AsNoTracking().Where(e => e.Status == status && applications.Contains(e.ApplicationId)).ToListAsync(cancellationToken))
                .GroupBy(e => e.ApplicationId).ToDictionary(g => g.Key, g => g.Max(e => e.At));
            foreach (var id in applications)
            {
                // The day the application entered this status; one with no recorded entry is left alone rather than guessed at.
                if (events.TryGetValue(id, out var since) && since <= cutoff)
                {
                    items.Add(new RetentionCandidate(id, status, DateOnly.FromDateTime(since.UtcDateTime)));
                }
            }
        }

        if (!dryRun)
        {
            foreach (var item in items)
            {
                await Erasure.DeleteApplicationAsync(db, store, item.ApplicationId, cancellationToken);
                db.RetentionItems.Add(new RetentionItemEntity { RunId = run.Id, ApplicationId = item.ApplicationId, Status = item.Status, StatusSince = item.StatusSince });
            }

            run.Purged = items.Count;
        }

        run.FinishedAt = clock.GetUtcNow();
        db.RetentionRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return new RetentionResult(run.Id, dryRun, startedBy, policy, items, run.Purged);
    }
}

/// <summary>Looks for expired applications every `Privacy:WorkerIntervalHours` hours. Does nothing until a retention window is set.</summary>
public sealed partial class RetentionWorker(IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<PrivacyOptions> options, TimeProvider clock, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, options.Value.WorkerIntervalHours));
        using var timer = new PeriodicTimer(interval, clock);
        while (await WaitAsync(timer, stoppingToken))
        {
            if (!options.Value.WorkerEnabled || !options.Value.AnyWindow)
            {
                continue;
            }

            try
            {
                using var scope = scopes.CreateScope();
                var result = await RetentionPurger.RunAsync(scope.ServiceProvider.GetRequiredService<MembershipDbContext>(), scope.ServiceProvider.GetRequiredService<IAttachmentStore>(), options.Value, clock, "worker", dryRun: false, stoppingToken);
                LogRun(logger, result.Purged);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The exception type only: its message could carry a row's contents.
                LogFailed(logger, ex.GetType().Name);
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken token)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention run purged {Count} applications")]
    private static partial void LogRun(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Retention run failed: {Error}")]
    private static partial void LogFailed(ILogger logger, string error);
}
