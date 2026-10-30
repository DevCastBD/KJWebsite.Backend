using CtaSubmissionService.Data;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KJWebsite.IntegrationTests;

public sealed class OutboxPostgresTests
{
    [RequiresPostgresFact]
    public async Task ClaimRetryAndDeadLetterWorkOnPostgreSql()
    {
        TestPostgres.EnsureDatabaseExists("cta_outbox_test");
        using var factory = new PostgresCtaApiFactory(TestPostgres.ConnectionStringFor("cta_outbox_test"));
        factory.CreateClient().Dispose();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CtaDbContext>();
            await db.OutboxMessages.ExecuteDeleteAsync();
            await db.DeadLetters.ExecuteDeleteAsync();
            db.Enqueue("pg.ok", new { n = 1 });
            db.Enqueue("pg.bad", new { n = 2 });
            await db.SaveChangesAsync();
        }

        var ok = new ScriptedHandler("pg.ok", _ => Task.CompletedTask);
        var bad = new ScriptedHandler("pg.bad", _ => throw new InvalidOperationException("boom"));
        var dispatcher = new OutboxDispatcher<CtaDbContext>(factory.Services.GetRequiredService<IServiceScopeFactory>(), [ok, bad],
            Options.Create(new OutboxOptions { Jitter = 0, MaxAttempts = 2, BaseDelay = TimeSpan.Zero }), TimeProvider.System,
            NullLogger<OutboxDispatcher<CtaDbContext>>.Instance);

        (await dispatcher.RunAsync()).Should().Be(1);
        await dispatcher.RunAsync();

        await using var check = factory.Services.CreateAsyncScope();
        var context = check.ServiceProvider.GetRequiredService<CtaDbContext>();
        (await context.OutboxMessages.CountAsync(m => m.ProcessedAt != null)).Should().Be(2);
        (await context.DeadLetters.SingleAsync()).Type.Should().Be("pg.bad");
    }
}
