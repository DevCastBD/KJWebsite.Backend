using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;

namespace ApiGateway;

internal static class GatewayConfigurationGuard
{
    /// <summary>
    /// Outside Development every cluster must have a destination from configuration. Failing at startup beats
    /// a gateway that boots fine and answers 503 to everything.
    /// </summary>
    public static void EnsureDestinations(IHostEnvironment environment, IConfiguration configuration)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        var clusters = configuration.GetSection("ReverseProxy:Clusters").GetChildren().ToList();
        if (clusters.Count == 0)
        {
            throw new InvalidOperationException("ReverseProxy:Clusters is not configured.");
        }

        foreach (var cluster in clusters.Where(c => !c.GetSection("Destinations").GetChildren().Any(d => !string.IsNullOrWhiteSpace(d["Address"]))))
        {
            throw new InvalidOperationException(
                $"ReverseProxy cluster '{cluster.Key}' has no destination. Set ReverseProxy:Clusters:{cluster.Key}:Destinations:<name>:Address for the {environment.EnvironmentName} environment.");
        }
    }
}

internal static class GatewayCompression
{
    private const string PublicReadPrefix = "/api/v1/content";

    public static IServiceCollection AddGatewayCompression(this IServiceCollection services)
    {
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        return services;
    }

    /// <summary>Compress public read traffic only: it is the cacheable, text-heavy part. Admin and auth responses stay uncompressed.</summary>
    public static IApplicationBuilder UseGatewayCompression(this IApplicationBuilder app) =>
        app.UseWhen(
            context => HttpMethods.IsGet(context.Request.Method) && context.Request.Path.StartsWithSegments(PublicReadPrefix),
            branch => branch.UseResponseCompression());
}
