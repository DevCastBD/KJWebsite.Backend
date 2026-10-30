using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KJWebsite.BuildingBlocks.Outbox;

public interface IOutboxHandler
{
    string MessageType { get; }

    /// <summary>Do the side effect. Throw to retry; the dispatcher applies backoff and, eventually, dead-lettering. Must be idempotent: delivery is at-least-once.</summary>
    Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken);
}

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public int MaxAttempts { get; set; } = 8;

    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Random spread (0–1) so a burst of failures does not retry in lockstep.</summary>
    public double Jitter { get; set; } = 0.1;

    public int BatchSize { get; set; } = 50;

    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);
}

internal static class OutboxMetrics
{
    public static readonly Meter Meter = new("KJWebsite.Outbox");
    public static readonly Counter<long> Dispatched = Meter.CreateCounter<long>("kj_outbox_dispatched", description: "Messages handled successfully");
    public static readonly Counter<long> Failed = Meter.CreateCounter<long>("kj_outbox_failed", description: "Handler attempts that failed");
    public static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>("kj_outbox_dead_lettered", description: "Messages moved to the dead-letter table; alert on any increase");
}

/// <summary>
/// Claims due messages, runs their handlers and records the outcome. Safe to run concurrently in several processes:
/// each message is claimed with a single conditional UPDATE, so only one dispatcher owns it at a time.
/// </summary>
public sealed partial class OutboxDispatcher<TContext>(
    IServiceScopeFactory scopes,
    IEnumerable<IOutboxHandler> handlers,
    IOptions<OutboxOptions> options,
    TimeProvider time,
    ILogger<OutboxDispatcher<TContext>> logger)
    where TContext : DbContext, IOutboxDbContext
{
    private readonly Dictionary<string, IOutboxHandler> _handlers = handlers.ToDictionary(h => h.MessageType, StringComparer.Ordinal);

    /// <summary>Returns the number of messages handled successfully.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = time.GetUtcNow().UtcDateTime;

        var due = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null && m.AvailableAt <= now && (m.LockedUntil == null || m.LockedUntil < now))
            .OrderBy(m => m.AvailableAt)
            .Select(m => m.Id)
            .Take(settings.BatchSize)
            .ToListAsync(cancellationToken);

        var succeeded = 0;
        foreach (var id in due)
        {
            if (!await TryClaimAsync(db, id, now, settings.Lease, cancellationToken))
            {
                continue;
            }

            var message = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == id, cancellationToken);
            if (await HandleAsync(db, message, settings, cancellationToken))
            {
                succeeded++;
            }
        }

        await PurgeAsync(db, now - settings.Retention, cancellationToken);
        return succeeded;
    }

    private static async Task<bool> TryClaimAsync(TContext db, Guid id, DateTime now, TimeSpan lease, CancellationToken cancellationToken)
    {
        var until = now + lease;
        var claimed = await db.OutboxMessages
            .Where(m => m.Id == id && m.ProcessedAt == null && (m.LockedUntil == null || m.LockedUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LockedUntil, until), cancellationToken);
        return claimed == 1;
    }

    private async Task<bool> HandleAsync(TContext db, OutboxMessage message, OutboxOptions settings, CancellationToken cancellationToken)
    {
        try
        {
            if (!_handlers.TryGetValue(message.Type, out var handler))
            {
                throw new InvalidOperationException($"No handler is registered for outbox message type '{message.Type}'.");
            }

            await handler.HandleAsync(message, cancellationToken);
            var done = time.GetUtcNow().UtcDateTime;
            await db.OutboxMessages.Where(m => m.Id == message.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedAt, done).SetProperty(m => m.LockedUntil, (DateTime?)null).SetProperty(m => m.LastError, (string?)null), cancellationToken);
            OutboxMetrics.Dispatched.Add(1, new KeyValuePair<string, object?>("type", message.Type));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: release the lease so another dispatcher can pick the message up straight away.
            await db.OutboxMessages.Where(m => m.Id == message.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LockedUntil, (DateTime?)null), CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(db, message, ex, settings, cancellationToken);
            return false;
        }
    }

    private async Task RecordFailureAsync(TContext db, OutboxMessage message, Exception exception, OutboxOptions settings, CancellationToken cancellationToken)
    {
        var attempts = message.Attempts + 1;
        var now = time.GetUtcNow().UtcDateTime;
        var error = Truncate($"{exception.GetType().Name}: {Observability.PiiRedactionEnricher.Scrub(exception.Message)}");
        OutboxMetrics.Failed.Add(1, new KeyValuePair<string, object?>("type", message.Type));

        if (attempts >= settings.MaxAttempts)
        {
            // Dead-letter and retire the original in one transaction so a message is never both retried and parked.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            db.DeadLetters.Add(new DeadLetterMessage
            {
                Id = Guid.NewGuid(),
                OutboxMessageId = message.Id,
                Type = message.Type,
                Payload = message.Payload,
                Attempts = attempts,
                Error = error,
                CreatedAt = message.CreatedAt,
                FailedAt = now
            });
            await db.SaveChangesAsync(cancellationToken);
            await db.OutboxMessages.Where(m => m.Id == message.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedAt, now).SetProperty(m => m.Attempts, attempts)
                    .SetProperty(m => m.LastError, error).SetProperty(m => m.LockedUntil, (DateTime?)null), cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            OutboxMetrics.DeadLettered.Add(1, new KeyValuePair<string, object?>("type", message.Type));
            LogDeadLettered(message.Id, message.Type, attempts, error);
            return;
        }

        var retryAt = now + BackoffFor(attempts, settings);
        await db.OutboxMessages.Where(m => m.Id == message.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Attempts, attempts).SetProperty(m => m.AvailableAt, retryAt)
                .SetProperty(m => m.LastError, error).SetProperty(m => m.LockedUntil, (DateTime?)null), cancellationToken);
        LogRetry(message.Id, message.Type, attempts, settings.MaxAttempts, retryAt, error);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} of type {MessageType} was dead-lettered after {Attempts} attempts: {Error}")]
    private partial void LogDeadLettered(Guid messageId, string messageType, int attempts, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} of type {MessageType} failed (attempt {Attempts}/{MaxAttempts}); retrying at {RetryAt:O}: {Error}")]
    private partial void LogRetry(Guid messageId, string messageType, int attempts, int maxAttempts, DateTime retryAt, string error);

    internal static TimeSpan BackoffFor(int attempts, OutboxOptions settings)
    {
        var seconds = Math.Min(settings.MaxDelay.TotalSeconds, settings.BaseDelay.TotalSeconds * Math.Pow(2, attempts - 1));
        var spread = settings.Jitter <= 0 ? 0 : (Random.Shared.NextDouble() * 2 - 1) * settings.Jitter;
        return TimeSpan.FromSeconds(Math.Max(0, seconds * (1 + spread)));
    }

    private static async Task PurgeAsync(TContext db, DateTime olderThan, CancellationToken cancellationToken) =>
        await db.OutboxMessages.Where(m => m.ProcessedAt != null && m.ProcessedAt < olderThan).ExecuteDeleteAsync(cancellationToken);

    private static string Truncate(string text) => text.Length <= 1900 ? text : text[..1900];
}
