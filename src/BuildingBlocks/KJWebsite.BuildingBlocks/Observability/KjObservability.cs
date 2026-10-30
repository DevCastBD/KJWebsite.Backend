using System.Net;
using KJWebsite.BuildingBlocks.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace KJWebsite.BuildingBlocks;

public static class KjObservability
{
    public const string LiveTag = "live";
    public const string ReadyTag = "ready";
    public const string StartupTag = "startup";

    /// <summary>
    /// Serilog JSON logs (correlation ID and service name on every event, PII redacted), health checks and
    /// OpenTelemetry metrics. Returns the health-checks builder so a service can add its own readiness checks.
    /// </summary>
    public static IHealthChecksBuilder AddKjObservability(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Host.UseSerilog((context, _, logger) => ConfigureLogging(logger, serviceName, context.Configuration));

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("KJWebsite.Outbox")
                .AddPrometheusExporter());

        return builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);
    }

    /// <summary>Console JSON sink by default; tests pass their own writer. The redaction enricher is last so it sees every property.</summary>
    public static LoggerConfiguration ConfigureLogging(
        LoggerConfiguration logger, string serviceName, IConfiguration? configuration = null, TextWriter? output = null)
    {
        logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            // EF logs executed SQL with parameter values at Information; that is personal data.
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", serviceName);

        if (configuration is not null)
        {
            logger.ReadFrom.Configuration(configuration);
        }

        logger.Enrich.With<PiiRedactionEnricher>();
        return output is null
            ? logger.WriteTo.Console(new RenderedCompactJsonFormatter())
            : logger.WriteTo.Sink(new WriterSink(new RenderedCompactJsonFormatter(), output));
    }

    public static IApplicationBuilder UseKjObservability(this IApplicationBuilder app)
    {
        app.UseCorrelationId();
        app.Use(async (context, next) =>
        {
            using (LogContext.PushProperty("CorrelationId", CorrelationId.Get(context)))
            {
                await next();
            }
        });
        app.UseSerilogRequestLogging();
        return app;
    }

    /// <summary>
    /// <c>/health/live</c>, <c>/health/ready</c>, <c>/health/startup</c> and <c>/metrics</c>. Health responses name each check and
    /// its status but never exception text. Metrics are limited to loopback and the configured networks.
    /// </summary>
    public static IEndpointRouteBuilder MapKjObservability(this WebApplication app)
    {
        MapHealth(app, "/health/live", LiveTag);
        MapHealth(app, "/health/ready", ReadyTag);
        MapHealth(app, "/health/startup", StartupTag);

        var allowed = MetricsAccess.Parse(app.Configuration.GetSection("Observability:MetricsAllowedNetworks").Get<string[]>());
        app.UseWhen(
            context => context.Request.Path.Equals("/metrics", StringComparison.Ordinal),
            branch => branch.Use(async (context, next) =>
            {
                if (MetricsAccess.IsAllowed(context, allowed))
                {
                    await next();
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }));
        app.MapPrometheusScrapingEndpoint("/metrics").AllowAnonymous();
        return app;
    }

    private static void MapHealth(WebApplication app, string path, string tag) =>
        app.MapHealthChecks(path, new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(tag),
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            },
            ResponseWriter = WriteReport
        }).AllowAnonymous();

    private static Task WriteReport(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), data = e.Value.Data.Count == 0 ? null : e.Value.Data })
        });
    }
}

internal static class MetricsAccess
{
    public static List<IPNetwork> Parse(string[]? configured)
    {
        var networks = new List<IPNetwork> { IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("::1/128") };
        foreach (var value in configured ?? [])
        {
            networks.Add(IPNetwork.Parse(value));
        }

        return networks;
    }

    /// <summary>Refuses anything that arrived through a proxy: a forwarded header means the caller is not the local scraper.</summary>
    public static bool IsAllowed(HttpContext context, List<IPNetwork> allowed) =>
        !context.Request.Headers.ContainsKey("X-Forwarded-For")
        && context.Connection.RemoteIpAddress is { } address
        && allowed.Any(network => network.Contains(address));
}

internal sealed class WriterSink(ITextFormatter formatter, TextWriter writer) : ILogEventSink
{
    private readonly object _gate = new();

    public void Emit(LogEvent logEvent)
    {
        lock (_gate)
        {
            formatter.Format(logEvent, writer);
            writer.Flush();
        }
    }
}
