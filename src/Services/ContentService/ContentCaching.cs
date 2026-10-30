using Microsoft.AspNetCore.OutputCaching;

namespace ContentService;

/// <summary>
/// Server-side output cache for the public content reads. Responses vary by every query value (`lang`, paging, status) and by
/// `Accept-Language`, so a bn response is never served to an en request. Admin writes evict the whole `content` tag, so a publish shows at once.
/// With `Redis:ConnectionString` the cache is shared between instances; otherwise it lives in process memory.
/// </summary>
public static class ContentCaching
{
    public const string PolicyName = "public-content";
    public const string Tag = "content";

    public static IServiceCollection AddContentCaching(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOutputCache(options => options.AddPolicy(PolicyName, policy => policy
            .Expire(TimeSpan.FromSeconds(60))
            .SetVaryByQuery("*")
            .SetVaryByHeader("Accept-Language")
            .Tag(Tag)));
        if (configuration["Redis:ConnectionString"] is { Length: > 0 } redis)
        {
            services.AddStackExchangeRedisOutputCache(o => o.Configuration = redis);
        }

        return services;
    }

    public static RouteHandlerBuilder CachePublicContent(this RouteHandlerBuilder builder) => builder.CacheOutput(PolicyName);

    /// <summary>After a successful admin write, drops every cached public response.</summary>
    public static RouteHandlerBuilder EvictsContentCache(this RouteHandlerBuilder builder) => builder.AddEndpointFilter(async (context, next) =>
    {
        var result = await next(context);
        var succeeded = result is not IStatusCodeHttpResult { StatusCode: < 200 or >= 300 };
        if (succeeded)
        {
            await context.HttpContext.RequestServices.GetRequiredService<IOutputCacheStore>()
                .EvictByTagAsync(Tag, context.HttpContext.RequestAborted);
        }

        return result;
    });
}
