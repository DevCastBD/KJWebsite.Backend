using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace KJWebsite.BuildingBlocks.Security;

public static class PrivateResponses
{
    public static IApplicationBuilder UsePrivateResponses(this IApplicationBuilder app, params string[] pathPrefixes) => app.Use(async (context, next) =>
    {
        if (pathPrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix)))
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "private, no-store";
                if (!context.Response.Headers.Vary.ToString().Split(',').Any(v => v.Trim().Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
                    context.Response.Headers.Append("Vary", "Authorization");
                return Task.CompletedTask;
            });
        }
        await next();
    });
}
