using Hangfire;
using Hangfire.Dashboard;
using Hangfire.InMemory;
using Hangfire.PostgreSql;
using KJWebsite.BuildingBlocks.Outbox;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.BuildingBlocks.Jobs;

public sealed class JobsOptions
{
    public const string SectionName = "Jobs";

    /// <summary><c>PostgreSql</c> (default, own <c>hangfire</c> schema, ADR-005) or <c>InMemory</c> for tests.</summary>
    public string Storage { get; set; } = "PostgreSql";

    public string Schema { get; set; } = "hangfire";

    /// <summary>False runs the service without a worker (the dashboard and enqueueing still work).</summary>
    public bool ServerEnabled { get; set; } = true;

    public int WorkerCount { get; set; } = 2;
}

/// <summary>Recurring and one-off wrapper around <see cref="OutboxDispatcher{TContext}"/>. Retries belong to the outbox, not to Hangfire.</summary>
public sealed class OutboxDispatchJob<TContext>(OutboxDispatcher<TContext> dispatcher)
    where TContext : DbContext, IOutboxDbContext
{
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(CancellationToken cancellationToken) => dispatcher.RunAsync(cancellationToken);
}

/// <summary>Wakes the dispatcher right after a commit. Best effort: the recurring sweep is what guarantees delivery.</summary>
public interface IOutboxSignal
{
    void Signal();
}

internal sealed class OutboxSignal<TContext>(IServiceProvider services) : IOutboxSignal
    where TContext : DbContext, IOutboxDbContext
{
    public void Signal()
    {
        try
        {
            services.GetService<IBackgroundJobClient>()?.Enqueue<OutboxDispatchJob<TContext>>(job => job.RunAsync(CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The message is already durable; the next sweep delivers it.
        }
    }
}

public static class KjBackgroundJobs
{
    public const string DashboardPath = "/hangfire";

    /// <summary>
    /// Hangfire with the outbox dispatcher for <typeparamref name="TContext"/>. Skipped entirely while exporting OpenAPI,
    /// which runs the service without a database.
    /// </summary>
    public static IServiceCollection AddKjBackgroundJobs<TContext>(this IServiceCollection services, IConfiguration configuration, string connectionStringName)
        where TContext : DbContext, IOutboxDbContext
    {
        services.AddOutbox<TContext>(configuration);
        services.AddSingleton<OutboxDispatchJob<TContext>>();
        services.AddSingleton<IOutboxSignal, OutboxSignal<TContext>>();
        if (configuration.GetValue<bool>("OpenApi:Export"))
        {
            return services;
        }

        var options = configuration.GetSection(JobsOptions.SectionName).Get<JobsOptions>() ?? new JobsOptions();
        services.AddHangfire(hangfire =>
        {
            hangfire.SetDataCompatibilityLevel(CompatibilityLevel.Version_180).UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings();
            if (string.Equals(options.Storage, "InMemory", StringComparison.OrdinalIgnoreCase))
            {
                hangfire.UseInMemoryStorage(new InMemoryStorageOptions());
            }
            else
            {
                var connectionString = configuration.GetConnectionString(connectionStringName)
                    ?? throw new InvalidOperationException($"ConnectionStrings:{connectionStringName} is required for background jobs.");
                hangfire.UsePostgreSqlStorage(
                    storage => storage.UseNpgsqlConnection(connectionString),
                    new PostgreSqlStorageOptions { SchemaName = options.Schema });
            }
        });

        if (options.ServerEnabled)
        {
            services.AddHangfireServer(server => server.WorkerCount = options.WorkerCount);
        }

        return services;
    }

    /// <summary>
    /// Registers the recurring sweep and maps the dashboard. The dashboard shows job payloads, which can hold personal data, so it
    /// needs a signed-in caller with <see cref="Permissions.RolesManage"/> (SuperAdmin) and is not routed through the gateway.
    /// </summary>
    public static IEndpointConventionBuilder? MapKjBackgroundJobs<TContext>(this WebApplication app)
        where TContext : DbContext, IOutboxDbContext
    {
        if (app.Services.GetService<JobStorage>() is null)
        {
            return null;
        }

        app.Services.GetRequiredService<IRecurringJobManager>()
            .AddOrUpdate<OutboxDispatchJob<TContext>>($"outbox-{typeof(TContext).Name}", job => job.RunAsync(CancellationToken.None), Cron.Minutely());

        return app.MapHangfireDashboard(DashboardPath, new DashboardOptions
        {
            Authorization = [new PermissionDashboardFilter()],
            DashboardTitle = "KJ Background Jobs",
            AppPath = null
        }).RequireAuthorization(Permissions.RolesManage);
    }

    /// <summary>Second lock on the dashboard in case the endpoint policy is ever dropped.</summary>
    private sealed class PermissionDashboardFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context)
        {
            var user = context.GetHttpContext().User;
            return user.Identity?.IsAuthenticated == true && user.HasClaim(KjClaims.Permission, Permissions.RolesManage);
        }
    }
}
