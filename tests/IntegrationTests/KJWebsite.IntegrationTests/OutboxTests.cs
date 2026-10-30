using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CtaSubmissionService.Data;
using CtaSubmissionService.Data.Entities;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KJWebsite.IntegrationTests;

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class ScriptedHandler(string type, Func<OutboxMessage, Task> action) : IOutboxHandler
{
    public int Calls;

    public string MessageType => type;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return action(message);
    }
}

public sealed class OutboxTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly CtaApiFactory _factory = new();
    private readonly ManualTime _time = new(Start);

    public OutboxTests() => _factory.CreateClient().Dispose(); // boots the service and creates the schema

    public void Dispose() => _factory.Dispose();

    private OutboxDispatcher<CtaDbContext> Dispatcher(OutboxOptions? options, params IOutboxHandler[] handlers) =>
        Dispatcher(options, _time, handlers);

    private OutboxDispatcher<CtaDbContext> Dispatcher(OutboxOptions? options, TimeProvider time, params IOutboxHandler[] handlers) =>
        new(_factory.Services.GetRequiredService<IServiceScopeFactory>(), handlers,
            Options.Create(options ?? new OutboxOptions { Jitter = 0 }), time, NullLogger<OutboxDispatcher<CtaDbContext>>.Instance);

    private async Task<T> WithDb<T>(Func<CtaDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<CtaDbContext>());
    }

    private Task<Guid> Enqueue(string type = "test.event", object? payload = null) => WithDb(async db =>
    {
        var message = db.Enqueue(type, payload ?? new { id = 1 }, _time);
        await db.SaveChangesAsync();
        return message.Id;
    });

    private Task<OutboxMessage> Load(Guid id) => WithDb(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id));

    [Fact]
    public async Task AMessageIsDeliveredOnceAndMarkedProcessed()
    {
        var id = await Enqueue();
        var handler = new ScriptedHandler("test.event", _ => Task.CompletedTask);

        (await Dispatcher(null, handler).RunAsync()).Should().Be(1);
        (await Dispatcher(null, handler).RunAsync()).Should().Be(0, "a processed message is never delivered again");

        handler.Calls.Should().Be(1);
        (await Load(id)).ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SideEffectCommitsOrRollsBackWithItsBusinessData()
    {
        await WithDb(async db =>
        {
            db.Submissions.Add(Submission("subm_dup"));
            await db.SaveChangesAsync();
            return 0;
        });

        // A second context, as a second request would have: same primary key, so the write fails.
        await WithDb(async db =>
        {
            db.Submissions.Add(Submission("subm_dup"));
            db.Enqueue("test.event", new { submissionId = "subm_dup" }, _time);
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<Exception>();
            return 0;
        });

        var failed = await WithDb(db => db.OutboxMessages.AsNoTracking().CountAsync());

        failed.Should().Be(0, "a rolled-back write must not leave an event behind");
    }

    [Fact]
    public async Task AcceptedSubmissionAlwaysHasItsEventAndItIsDispatched()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/cta/submissions", new
        {
            formName = "contact",
            ctaType = "contact",
            language = "en",
            sourcePath = "/contact",
            values = new Dictionary<string, object?> { ["firstName"] = "Rahim" },
            submittedAt = DateTimeOffset.UtcNow
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var pending = await WithDb(db => db.OutboxMessages.AsNoTracking().Where(m => m.Type == "cta.submission.received").ToListAsync());
        pending.Should().ContainSingle();
        pending[0].Payload.Should().NotContain("Rahim", "payloads carry ids, not personal data");

        var real = _factory.Services.GetServices<IOutboxHandler>().Single(h => h.MessageType == "cta.submission.received");
        (await Dispatcher(null, TimeProvider.System, real).RunAsync()).Should().Be(1);
        (await Load(pending[0].Id)).ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task FailuresAreRetriedWithExponentialBackoff()
    {
        var id = await Enqueue();
        var handler = new ScriptedHandler("test.event", _ => throw new InvalidOperationException("provider down"));
        var dispatcher = Dispatcher(null, handler);

        await dispatcher.RunAsync();
        var first = await Load(id);
        first.Attempts.Should().Be(1);
        first.AvailableAt.Should().Be(Start.UtcDateTime.AddSeconds(30));
        first.LastError.Should().Contain("provider down");

        await dispatcher.RunAsync();
        handler.Calls.Should().Be(1, "the message is not due yet");

        _time.Advance(TimeSpan.FromSeconds(30));
        await dispatcher.RunAsync();
        var second = await Load(id);
        second.Attempts.Should().Be(2);
        second.AvailableAt.Should().Be(Start.UtcDateTime.AddSeconds(30 + 60), "the delay doubles");
    }

    [Fact]
    public async Task BackoffIsCapped()
    {
        var options = new OutboxOptions { Jitter = 0, MaxAttempts = 50, BaseDelay = TimeSpan.FromMinutes(10), MaxDelay = TimeSpan.FromMinutes(45) };
        var id = await Enqueue();
        var dispatcher = Dispatcher(options, new ScriptedHandler("test.event", _ => throw new InvalidOperationException("x")));

        for (var i = 0; i < 4; i++)
        {
            await dispatcher.RunAsync();
            _time.Advance(TimeSpan.FromHours(2));
        }

        var message = await Load(id);
        (message.AvailableAt - Start.UtcDateTime.AddHours(6)).Should().Be(TimeSpan.FromMinutes(45));
    }

    [Fact]
    public async Task ExhaustedMessagesMoveToTheDeadLetterTableAndStopRetrying()
    {
        var options = new OutboxOptions { Jitter = 0, MaxAttempts = 3 };
        var id = await Enqueue(payload: new { submissionId = "subm_1" });
        var handler = new ScriptedHandler("test.event", _ => throw new InvalidOperationException("mailbox rahim@example.com rejected"));
        var dispatcher = Dispatcher(options, handler);

        for (var i = 0; i < 3; i++)
        {
            await dispatcher.RunAsync();
            _time.Advance(TimeSpan.FromHours(1));
        }

        await dispatcher.RunAsync();
        handler.Calls.Should().Be(3, "a dead-lettered message is not retried");

        var dead = await WithDb(db => db.DeadLetters.AsNoTracking().ToListAsync());
        var letter = dead.Should().ContainSingle().Subject;
        letter.OutboxMessageId.Should().Be(id);
        letter.Attempts.Should().Be(3);
        letter.Payload.Should().Contain("subm_1");
        letter.ResolvedAt.Should().BeNull("an unresolved dead letter is what raises the alert");
        letter.Error.Should().NotContain("rahim@example.com", "error text is scrubbed of personal data");
        (await Load(id)).ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task AMessageWithoutAHandlerEndsUpDeadLetteredNotLost()
    {
        await Enqueue("nobody.handles.this");
        var dispatcher = Dispatcher(new OutboxOptions { Jitter = 0, MaxAttempts = 2 });

        await dispatcher.RunAsync();
        _time.Advance(TimeSpan.FromHours(1));
        await dispatcher.RunAsync();

        (await WithDb(db => db.DeadLetters.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentDispatchersNeverDeliverTheSameMessageTwice()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            ids.Add(await Enqueue());
        }

        var seen = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var handler = new ScriptedHandler("test.event", async m =>
        {
            seen.Add(m.Id);
            await Task.Delay(5);
        });

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Dispatcher(null, handler).RunAsync())));

        seen.Should().BeEquivalentTo(ids);
        seen.Distinct().Count().Should().Be(20);
    }

    [Fact]
    public async Task ALeasedMessageIsSkippedUntilItsLeaseExpires()
    {
        var id = await Enqueue();
        await WithDb(async db =>
        {
            await db.OutboxMessages.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.LockedUntil, Start.UtcDateTime.AddMinutes(5)));
            return 0;
        });
        var handler = new ScriptedHandler("test.event", _ => Task.CompletedTask);
        var dispatcher = Dispatcher(null, handler);

        await dispatcher.RunAsync();
        handler.Calls.Should().Be(0, "another dispatcher holds it");

        _time.Advance(TimeSpan.FromMinutes(6)); // that dispatcher crashed; the lease lapsed
        await dispatcher.RunAsync();
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ProcessedMessagesArePurgedAfterTheRetentionWindow()
    {
        var id = await Enqueue();
        var dispatcher = Dispatcher(new OutboxOptions { Jitter = 0, Retention = TimeSpan.FromDays(1) }, new ScriptedHandler("test.event", _ => Task.CompletedTask));
        await dispatcher.RunAsync();

        _time.Advance(TimeSpan.FromDays(2));
        await dispatcher.RunAsync();

        (await WithDb(db => db.OutboxMessages.AnyAsync(m => m.Id == id))).Should().BeFalse();
    }

    [Fact]
    public async Task TheDashboardRequiresAdminAuthentication()
    {
        using var client = _factory.CreateClient();

        using var anonymous = await client.GetAsync("/hangfire");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var member = new HttpRequestMessage(HttpMethod.Get, "/hangfire");
        member.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue([KJWebsite.BuildingBlocks.Security.Permissions.CtaView]));
        (await client.SendAsync(member)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = new HttpRequestMessage(HttpMethod.Get, "/hangfire");
        admin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue([KJWebsite.BuildingBlocks.Security.Permissions.RolesManage]));
        using var response = await client.SendAsync(admin);
        response.IsSuccessStatusCode.Should().BeTrue($"got {(int)response.StatusCode}");
    }

    private static CtaSubmissionEntity Submission(string id) => new()
    {
        Id = id,
        FormName = "contact",
        CtaType = "contact",
        Language = "en",
        SourcePath = "/",
        ValuesJson = "{}",
        SubmittedAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
