using System.Collections.Concurrent;

namespace KJWebsite.BuildingBlocks.Idempotency;

/// <summary>What was answered for an idempotency key. `StatusCode` is 0 while the first request is still running.</summary>
public sealed record IdempotencyRecord(string Fingerprint, int StatusCode, string? ContentType, string? Location, byte[] Body)
{
    public bool IsComplete => StatusCode != 0;
}

public enum IdempotencyBegin
{
    /// <summary>The key was free; the caller now owns it and must call Complete or Abandon.</summary>
    Started,

    /// <summary>Another request holds the key; `Existing` describes it (complete or still running).</summary>
    Existing
}

/// <summary>Atomically claims a key for the first request and remembers its response. Implementations must make Begin race-free.</summary>
public interface IIdempotencyStore
{
    Task<(IdempotencyBegin Outcome, IdempotencyRecord? Existing)> BeginAsync(string key, string fingerprint, TimeSpan inFlightTtl, CancellationToken ct);

    Task CompleteAsync(string key, IdempotencyRecord record, TimeSpan ttl, CancellationToken ct);

    Task AbandonAsync(string key, CancellationToken ct);
}

/// <summary>Process-local store. Correct for a single instance (and tests); run several instances with the Redis store instead.</summary>
public sealed class MemoryIdempotencyStore(TimeProvider time) : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, (IdempotencyRecord Record, DateTimeOffset Expires)> _entries = new();
    private readonly object _gate = new();

    public Task<(IdempotencyBegin Outcome, IdempotencyRecord? Existing)> BeginAsync(string key, string fingerprint, TimeSpan inFlightTtl, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_entries.Count > 1024)
            {
                foreach (var stale in _entries.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList())
                {
                    _entries.TryRemove(stale, out _);
                }
            }

            if (_entries.TryGetValue(key, out var current) && current.Expires > now)
            {
                return Task.FromResult((IdempotencyBegin.Existing, (IdempotencyRecord?)current.Record));
            }

            _entries[key] = (new IdempotencyRecord(fingerprint, 0, null, null, []), now + inFlightTtl);
            return Task.FromResult((IdempotencyBegin.Started, (IdempotencyRecord?)null));
        }
    }

    public Task CompleteAsync(string key, IdempotencyRecord record, TimeSpan ttl, CancellationToken ct)
    {
        _entries[key] = (record, time.GetUtcNow() + ttl);
        return Task.CompletedTask;
    }

    public Task AbandonAsync(string key, CancellationToken ct)
    {
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
