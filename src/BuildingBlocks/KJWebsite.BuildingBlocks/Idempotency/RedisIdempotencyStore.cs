using System.Text.Json;
using StackExchange.Redis;

namespace KJWebsite.BuildingBlocks.Idempotency;

/// <summary>Shared store for several instances: SET NX claims the key atomically, and Redis expires entries after the window.</summary>
public sealed class RedisIdempotencyStore(IConnectionMultiplexer redis) : IIdempotencyStore
{
    private const string Prefix = "kj:idem:";

    public async Task<(IdempotencyBegin Outcome, IdempotencyRecord? Existing)> BeginAsync(string key, string fingerprint, TimeSpan inFlightTtl, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var claim = JsonSerializer.Serialize(new IdempotencyRecord(fingerprint, 0, null, null, []));
        if (await db.StringSetAsync(Prefix + key, claim, inFlightTtl, When.NotExists))
        {
            return (IdempotencyBegin.Started, null);
        }

        var stored = await db.StringGetAsync(Prefix + key);
        // The entry may have expired between the failed SET and the read; the caller then sees a free-for-all and can simply retry.
        return stored.IsNullOrEmpty
            ? (IdempotencyBegin.Existing, new IdempotencyRecord(fingerprint, 0, null, null, []))
            : (IdempotencyBegin.Existing, JsonSerializer.Deserialize<IdempotencyRecord>((string)stored!));
    }

    public Task CompleteAsync(string key, IdempotencyRecord record, TimeSpan ttl, CancellationToken ct) =>
        redis.GetDatabase().StringSetAsync(Prefix + key, JsonSerializer.Serialize(record), ttl);

    public Task AbandonAsync(string key, CancellationToken ct) => redis.GetDatabase().KeyDeleteAsync(Prefix + key);
}
