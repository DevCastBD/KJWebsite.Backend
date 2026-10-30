using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using KJWebsite.BuildingBlocks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ApiGateway;

public sealed class RateLimitRule
{
    public int PermitLimit { get; set; }

    public int WindowSeconds { get; set; }
}

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; set; } = true;

    /// <summary>Sliding window over every request from one client IP.</summary>
    public RateLimitRule Global { get; set; } = new() { PermitLimit = 300, WindowSeconds = 60 };

    public RateLimitRule Login { get; set; } = new() { PermitLimit = 5, WindowSeconds = 60 };

    public RateLimitRule Register { get; set; } = new() { PermitLimit = 10, WindowSeconds = 3600 };

    public RateLimitRule CtaSubmission { get; set; } = new() { PermitLimit = 10, WindowSeconds = 3600 };

    /// <summary>Public membership intake: submitting an application and the duplicate check, per client IP.</summary>
    public RateLimitRule MembershipIntake { get; set; } = new() { PermitLimit = 20, WindowSeconds = 3600 };

    /// <summary>Public status lookups (reference + email): tighter than intake, because each call is a guess.</summary>
    public RateLimitRule MembershipStatus { get; set; } = new() { PermitLimit = 10, WindowSeconds = 900 };

    /// <summary>Public directory searches (GET): enough to browse, too slow to harvest into a contact list.</summary>
    public RateLimitRule MembershipDirectory { get; set; } = new() { PermitLimit = 60, WindowSeconds = 600 };

    public int SlidingSegments { get; set; } = 6;
}

/// <summary>
/// How the gateway learns the real client address when a CDN or reverse proxy sits in front of it.
/// Without a trusted proxy the forwarded headers are ignored, so a client cannot spoof its own address.
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    public string[] KnownProxies { get; set; } = [];

    /// <summary>CIDR ranges, e.g. <c>10.0.0.0/8</c>.</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>Trust any upstream. Only safe when the gateway is reachable solely through the CDN/proxy.</summary>
    public bool TrustAnyProxy { get; set; }
}

public static class RateLimitingConfiguration
{
    internal const string UnknownClient = "unknown";

    public static IServiceCollection AddGatewayRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.SectionName));
        services.Configure<ForwardedHeadersSettings>(configuration.GetSection(ForwardedHeadersSettings.SectionName));

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ForwardedHeadersSettings>>((options, settings) =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                var value = settings.Value;
                if (value.TrustAnyProxy)
                {
                    options.KnownProxies.Clear();
                    options.KnownIPNetworks.Clear();
                    options.ForwardLimit = null;
                    return;
                }

                // Replace the loopback-only default only when the operator names the proxies.
                if (value.KnownProxies.Length > 0 || value.KnownNetworks.Length > 0)
                {
                    options.KnownProxies.Clear();
                    options.KnownIPNetworks.Clear();
                }

                foreach (var proxy in value.KnownProxies)
                {
                    options.KnownProxies.Add(IPAddress.Parse(proxy));
                }

                foreach (var network in value.KnownNetworks)
                {
                    var parts = network.Split('/', 2);
                    options.KnownIPNetworks.Add(new System.Net.IPNetwork(
                        IPAddress.Parse(parts[0]), int.Parse(parts[1], CultureInfo.InvariantCulture)));
                }
            });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = WriteRejection;
        });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitingOptions>>((options, rules) =>
        {
            if (rules.Value.Enabled)
            {
                options.GlobalLimiter = BuildLimiter(rules.Value);
            }
        });
        return services;
    }

    private static PartitionedRateLimiter<HttpContext> BuildLimiter(RateLimitingOptions rules) =>
        PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetSlidingWindowLimiter(
                ClientKey(context),
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = rules.Global.PermitLimit,
                    Window = TimeSpan.FromSeconds(rules.Global.WindowSeconds),
                    SegmentsPerWindow = rules.SlidingSegments,
                    QueueLimit = 0
                })),
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var rule = RuleFor(context.Request, rules);
                if (rule is null)
                {
                    return RateLimitPartition.GetNoLimiter("none");
                }

                return RateLimitPartition.GetFixedWindowLimiter(
                    $"{rule.Value.Name}:{ClientKey(context)}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = rule.Value.Rule.PermitLimit,
                        Window = TimeSpan.FromSeconds(rule.Value.Rule.WindowSeconds),
                        QueueLimit = 0
                    });
            }));

    internal static (string Name, RateLimitRule Rule)? RuleFor(HttpRequest request, RateLimitingOptions rules)
    {
        if (HttpMethods.IsGet(request.Method) && request.Path.StartsWithSegments("/api/v1/members/directory", StringComparison.OrdinalIgnoreCase))
        {
            return ("membership-directory", rules.MembershipDirectory);
        }

        if (!HttpMethods.IsPost(request.Method))
        {
            return null;
        }

        var path = request.Path;
        if (path.Equals("/api/v1/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            return ("login", rules.Login);
        }

        if (path.Equals("/api/v1/auth/register", StringComparison.OrdinalIgnoreCase))
        {
            return ("register", rules.Register);
        }

        if (path.Equals("/api/v1/cta/submissions", StringComparison.OrdinalIgnoreCase))
        {
            return ("cta", rules.CtaSubmission);
        }

        if (path.Equals("/api/v1/membership/applications/status", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/membership/applications/export", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/membership/applications/erasure-request", StringComparison.OrdinalIgnoreCase))
        {
            return ("membership-status", rules.MembershipStatus);
        }

        if (path.Equals("/api/v1/membership/applications", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/membership/applications/check", StringComparison.OrdinalIgnoreCase))
        {
            return ("membership", rules.MembershipIntake);
        }

        return null;
    }

    private static string ClientKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? UnknownClient;

    private static async ValueTask WriteRejection(OnRejectedContext rejected, CancellationToken cancellationToken)
    {
        var context = rejected.HttpContext;
        var language = LanguageResolver.Resolve(context.Request.Query["lang"], context.Request.Headers.AcceptLanguage);

        // Never advertise a zero wait: the limiter may not know when the window frees up.
        var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
            ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))
            : 60;
        context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

        var detail = language == LanguageResolver.Bengali
            ? $"অনেক বেশি অনুরোধ করা হয়েছে। অনুগ্রহ করে {retryAfter} সেকেন্ড পরে আবার চেষ্টা করুন।"
            : $"Too many requests. Please try again in {retryAfter} seconds.";

        var result = ProblemDetailsFactory.Create(context, StatusCodes.Status429TooManyRequests, "rate_limited", detail);
        await result.ExecuteAsync(context);
    }
}
