using System;
using System.Threading.Tasks;
using KJWebsite.BuildingBlocks.Idempotency;

namespace KJWebsite.BuildingBlocks.UnitTests;

public sealed class MemoryIdempotencyStoreTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task OnlyTheFirstCallerOwnsAKey()
    {
        var store = new MemoryIdempotencyStore(new ManualTime());

        var first = await store.BeginAsync("k", "f", TimeSpan.FromSeconds(30), default);
        var second = await store.BeginAsync("k", "f", TimeSpan.FromSeconds(30), default);

        Assert.Equal(IdempotencyBegin.Started, first.Outcome);
        Assert.Equal(IdempotencyBegin.Existing, second.Outcome);
        Assert.False(second.Existing!.IsComplete);
    }

    [Fact]
    public async Task ACompletedKeyReplaysUntilTheWindowEnds()
    {
        var time = new ManualTime();
        var store = new MemoryIdempotencyStore(time);
        await store.BeginAsync("k", "f", TimeSpan.FromSeconds(30), default);
        await store.CompleteAsync("k", new IdempotencyRecord("f", 201, "application/json", "/x", [1, 2]), TimeSpan.FromHours(24), default);

        time.Now += TimeSpan.FromHours(23);
        var within = await store.BeginAsync("k", "f", TimeSpan.FromSeconds(30), default);
        time.Now += TimeSpan.FromHours(2);
        var after = await store.BeginAsync("k", "f", TimeSpan.FromSeconds(30), default);

        Assert.Equal(201, within.Existing!.StatusCode);
        Assert.Equal(IdempotencyBegin.Started, after.Outcome);
    }

    [Fact]
    public async Task AnAbandonedKeyCanBeClaimedAgain()
    {
        var store = new MemoryIdempotencyStore(new ManualTime());
        await store.BeginAsync("k", "f", TimeSpan.FromSeconds(30), default);

        await store.AbandonAsync("k", default);

        Assert.Equal(IdempotencyBegin.Started, (await store.BeginAsync("k", "f", TimeSpan.FromSeconds(30), default)).Outcome);
    }
}
