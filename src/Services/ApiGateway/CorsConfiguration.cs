using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace ApiGateway;

public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    public string[] SiteOrigins { get; set; } = [];

    public string[] AdminOrigins { get; set; } = [];
}

/// <summary>
/// Chooses the CORS policy per request: credentials are granted only when the caller's origin is an admin origin.
/// Any other listed origin gets a credential-less policy; unlisted origins get no CORS headers.
/// </summary>
internal sealed class OriginCorsPolicyProvider : ICorsPolicyProvider
{
    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];
    private static readonly string[] Headers = ["Content-Type", "Idempotency-Key", "Authorization"];

    private readonly CorsPolicy _site;
    private readonly CorsPolicy _admin;
    private readonly HashSet<string> _adminOrigins;

    public OriginCorsPolicyProvider(IOptions<CorsOptions> options)
    {
        var siteOrigins = Normalize(options.Value.SiteOrigins);
        var adminOrigins = Normalize(options.Value.AdminOrigins);
        _adminOrigins = new HashSet<string>(adminOrigins, StringComparer.OrdinalIgnoreCase);

        _site = Build(siteOrigins, allowCredentials: false);
        _admin = Build(adminOrigins, allowCredentials: true);
    }

    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var origin = context.Request.Headers.Origin.ToString();
        return Task.FromResult<CorsPolicy?>(_adminOrigins.Contains(origin) ? _admin : _site);
    }

    private static CorsPolicy Build(string[] origins, bool allowCredentials)
    {
        var builder = new CorsPolicyBuilder()
            .WithMethods(Methods)
            .WithHeaders(Headers)
            .SetPreflightMaxAge(TimeSpan.FromHours(1));

        // An empty origin list must reject everything, not fall through to "any".
        if (origins.Length > 0)
        {
            builder.WithOrigins(origins);
        }

        if (allowCredentials)
        {
            builder.AllowCredentials();
        }

        return builder.Build();
    }

    /// <summary>Origins must be exact scheme://host[:port] values — no wildcards, paths or trailing slashes.</summary>
    internal static string[] Normalize(string[] origins)
    {
        var result = new List<string>();
        foreach (var raw in origins.Where(o => !string.IsNullOrWhiteSpace(o)))
        {
            var value = raw.Trim().TrimEnd('/');
            if (value.Contains('*')
                || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || uri.PathAndQuery != "/")
            {
                throw new InvalidOperationException(
                    $"Cors origin '{raw}' is invalid: use an exact http(s) origin such as https://example.com (no wildcards or paths).");
            }

            result.Add(uri.GetLeftPart(UriPartial.Authority));
        }

        return [.. result];
    }
}

internal static class SecurityHeadersMiddleware
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            return Task.CompletedTask;
        });
        await next();
    });
}
