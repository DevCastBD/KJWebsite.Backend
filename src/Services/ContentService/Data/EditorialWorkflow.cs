using ContentService.Data.Entities;
using KJWebsite.BuildingBlocks.Auditing;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

/// <summary>
/// The status machine: draft, in_review, scheduled, published, archived. Anyone who may edit can send an item to review or pull it back to
/// draft; every move into or out of scheduled, published and archived needs content.publish. Applies to all eight content types.
/// </summary>
public sealed class EditorialWorkflow(ContentDbContext db, TimeProvider time, RevisionStore revisions, IConfiguration configuration)
{
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [StatusRules.Draft] = [StatusRules.InReview, StatusRules.Scheduled, StatusRules.Published],
        [StatusRules.InReview] = [StatusRules.Draft, StatusRules.Scheduled, StatusRules.Published],
        [StatusRules.Scheduled] = [StatusRules.Draft, StatusRules.Scheduled, StatusRules.Published],
        [StatusRules.Published] = [StatusRules.Draft, StatusRules.Archived],
        [StatusRules.Archived] = [StatusRules.Draft]
    };

    public async Task<UpsertResult<WorkflowView>> TransitionAsync(string type, string slug, string to, DateTimeOffset? publishAt, bool canPublish, IReadOnlyCollection<string>? ifMatch)
    {
        if (!StatusRules.Workflow.Contains(to))
        {
            return new UpsertResult<WorkflowView>(UpsertOutcome.Invalid, Error: $"to must be one of: {string.Join(", ", StatusRules.Workflow)}.");
        }

        var item = await db.Items.Include(i => i.Translations).FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug && i.DeletedAt == null);
        if (item is null)
        {
            return new UpsertResult<WorkflowView>(UpsertOutcome.NotFound);
        }

        if (ifMatch is not null && !ContentETag.Satisfies(ifMatch, item.UpdatedAt))
        {
            return new UpsertResult<WorkflowView>(UpsertOutcome.Conflict);
        }

        var from = StatusRules.Canonical(item.Status);
        if (!Allowed.TryGetValue(from, out var next) || !next.Contains(to))
        {
            return new UpsertResult<WorkflowView>(UpsertOutcome.Invalid, Error: $"An item cannot move from {from} to {to}.");
        }

        var needsPermission = StatusRules.NeedsPublishPermission(from) || StatusRules.NeedsPublishPermission(to);
        if (needsPermission && !canPublish)
        {
            return new UpsertResult<WorkflowView>(UpsertOutcome.Forbidden, Error: "Scheduling, publishing, unpublishing and archiving need the content.publish permission.");
        }

        // Going live is when a missing translation becomes visible, so the editor is told (or, if configured, stopped) here.
        var warnings = new List<string>();
        if (to is StatusRules.Published or StatusRules.Scheduled)
        {
            warnings = [.. TranslationCompleteness.Evaluate(type, item.Translations).Languages.Where(l => !l.Complete)
                .Select(l => l.Present ? $"{l.Lang} translation is incomplete: {string.Join(", ", l.Missing)}" : $"{l.Lang} translation is missing")];
            if (warnings.Count > 0 && configuration.GetValue<bool>("Content:Translations:RequireAllLanguages"))
            {
                return new UpsertResult<WorkflowView>(UpsertOutcome.Invalid, Error: $"Every language must be complete before publishing: {string.Join("; ", warnings)}.");
            }
        }

        var now = Now();
        if (to == StatusRules.Scheduled)
        {
            if (publishAt is null || publishAt.Value.UtcTicks <= now.UtcTicks)
            {
                return new UpsertResult<WorkflowView>(UpsertOutcome.Invalid, Error: "publishAt must be a time in the future.");
            }

            item.ScheduledPublishAt = new DateTimeOffset(publishAt.Value.UtcTicks / 10 * 10, TimeSpan.Zero);
        }
        else if (publishAt is not null)
        {
            return new UpsertResult<WorkflowView>(UpsertOutcome.Invalid, Error: "publishAt only applies when scheduling.");
        }
        else
        {
            item.ScheduledPublishAt = null;
        }

        if (to == StatusRules.Published)
        {
            item.PublishedAt ??= now;
        }

        item.Status = to == StatusRules.Published ? StatusRules.Live(type) : to;
        item.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return new UpsertResult<WorkflowView>(UpsertOutcome.Conflict);
        }

        await revisions.RecordAsync(item.Id, $"workflow:{to}");
        return new UpsertResult<WorkflowView>(UpsertOutcome.Updated, View(item) with { Warnings = warnings }, ETag: ContentETag.For(item.UpdatedAt));
    }

    public static WorkflowView View(ContentItemEntity item) => new(item.Type, item.Slug, item.Status, item.ScheduledPublishAt, item.PublishedAt);

    private DateTimeOffset Now() => new(time.GetUtcNow().UtcTicks / 10 * 10, TimeSpan.Zero);
}

/// <summary>
/// Publishes scheduled items when their time comes, with no one present. It polls (default every 30 s) and on startup, so a schedule missed
/// while the service was down is published late rather than never. A failed publish leaves the item scheduled and is retried on the next
/// poll; each item runs in its own scope so one failure cannot block the rest. If several instances run, the item's concurrency token
/// makes the loser's save fail harmlessly.
/// </summary>
public sealed partial class ScheduledPublisher(IServiceScopeFactory scopes, TimeProvider time, IConfiguration configuration, ILogger<ScheduledPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Publishing:Enabled", true) || configuration.GetValue<bool>("OpenApi:Export"))
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(0.05, configuration.GetValue("Publishing:PollSeconds", 30.0)));
        using var timer = new PeriodicTimer(interval, time);
        do
        {
            try
            {
                await PublishDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogCycleFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass: publishes every scheduled item that is due. Returns how many went live. Public so tests can drive it with a fake clock.</summary>
    public async Task<int> PublishDueAsync(CancellationToken ct = default)
    {
        List<(string Id, DateTimeOffset At)> due;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
            var now = time.GetUtcNow();
            // Compared in memory: SQLite (the test provider) cannot compare DateTimeOffset in SQL, and few items are ever scheduled at once.
            var scheduled = await db.Items.AsNoTracking().Where(i => i.Status == StatusRules.Scheduled && i.DeletedAt == null).Select(i => new { i.Id, i.ScheduledPublishAt }).ToListAsync(ct);
            due = [.. scheduled.Where(i => i.ScheduledPublishAt is { } at && at <= now).Select(i => (i.Id, i.ScheduledPublishAt!.Value))];
        }

        var published = 0;
        foreach (var (id, at) in due)
        {
            try
            {
                if (await PublishOneAsync(id, at, ct))
                {
                    published++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPublishFailed(logger, ex, id);
            }
        }

        if (published > 0)
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IOutputCacheStore>().EvictByTagAsync(ContentCaching.Tag, ct);
        }

        return published;
    }

    private async Task<bool> PublishOneAsync(string id, DateTimeOffset at, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        using var actor = AuditActor.Begin("system:scheduler", "scheduled publish");
        var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id && i.Status == StatusRules.Scheduled && i.DeletedAt == null, ct);
        if (item is null)
        {
            return false;
        }

        var now = time.GetUtcNow();
        item.Status = StatusRules.Live(item.Type);
        item.PublishedAt = at;
        item.ScheduledPublishAt = null;
        item.UpdatedAt = new DateTimeOffset(now.UtcTicks / 10 * 10, TimeSpan.Zero);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }

        await scope.ServiceProvider.GetRequiredService<RevisionStore>().RecordAsync(item.Id, "scheduled-publish");
        LogPublished(logger, item.Type, item.Slug);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled publish: {Type}/{Slug} is now live.")]
    private static partial void LogPublished(ILogger logger, string type, string slug);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled publish of {ItemId} failed; it stays scheduled and will be retried.")]
    private static partial void LogPublishFailed(ILogger logger, Exception ex, string itemId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled publisher cycle failed; retrying at the next poll.")]
    private static partial void LogCycleFailed(ILogger logger, Exception ex);
}
