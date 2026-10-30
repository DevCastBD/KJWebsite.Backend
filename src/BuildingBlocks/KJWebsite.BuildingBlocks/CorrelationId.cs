using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace KJWebsite.BuildingBlocks;

/// <summary>
/// One id per request, shared by every hop. The caller may supply it (the frontend does, F1-6); otherwise the first
/// service to see the request creates it. It is echoed on the response; <see cref="KjObservability"/> adds it to every log event.
/// </summary>
public static partial class CorrelationId
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "kj.correlation-id";

    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}$")]
    private static partial Regex Accepted();

    /// <summary>The id of the current request, or <c>null</c> outside the middleware.</summary>
    public static string? Get(HttpContext context) => context.Items[ItemKey] as string;

    /// <summary>Ids from callers are only trusted when they look like ids; anything else could forge log lines.</summary>
    internal static string Resolve(string? supplied) =>
        supplied is not null && Accepted().IsMatch(supplied) ? supplied : Guid.NewGuid().ToString("N");

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var id = Resolve(context.Request.Headers[HeaderName].FirstOrDefault());
        context.Items[ItemKey] = id;

        // Rewriting the request header makes the reverse proxy forward the sanitized value downstream.
        context.Request.Headers[HeaderName] = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        });

        await next();
    });
}
