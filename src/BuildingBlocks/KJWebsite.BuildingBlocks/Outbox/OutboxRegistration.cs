using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.BuildingBlocks.Outbox;

public static class OutboxRegistration
{
    public static IServiceCollection AddOutbox<TContext>(this IServiceCollection services, IConfiguration configuration)
        where TContext : DbContext, IOutboxDbContext
    {
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<OutboxDispatcher<TContext>>();
        return services;
    }

    public static IServiceCollection AddOutboxHandler<THandler>(this IServiceCollection services)
        where THandler : class, IOutboxHandler =>
        services.AddSingleton<IOutboxHandler, THandler>();
}
