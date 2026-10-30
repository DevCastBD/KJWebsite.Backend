using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ApiGateway;

/// <summary>
/// Gateway readiness aggregated from every configured destination's <c>/health/ready</c>. Some destinations down is
/// Degraded (the rest still serve); none reachable is Unhealthy so a load balancer stops sending traffic here.
/// </summary>
internal sealed class DownstreamHealthCheck(IConfiguration configuration, IHttpClientFactory clients) : IHealthCheck
{
    public const string ClientName = "downstream-health";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var targets = configuration.GetSection("ReverseProxy:Clusters").GetChildren()
            .SelectMany(cluster => cluster.GetSection("Destinations").GetChildren()
                .Select(d => (Name: $"{cluster.Key}/{d.Key}", Address: d["Address"])))
            .Where(t => !string.IsNullOrWhiteSpace(t.Address))
            .ToList();

        var results = await Task.WhenAll(targets.Select(async t => (t.Name, Ok: await ProbeAsync(t.Address!, cancellationToken))));
        var data = results.ToDictionary(r => r.Name, r => (object)(r.Ok ? "Healthy" : "Unhealthy"));

        if (results.Length == 0 || results.All(r => !r.Ok))
        {
            return HealthCheckResult.Unhealthy("No downstream service is ready.", data: data);
        }

        return results.All(r => r.Ok)
            ? HealthCheckResult.Healthy("All downstream services are ready.", data)
            : HealthCheckResult.Degraded("Some downstream services are not ready.", data: data);
    }

    private async Task<bool> ProbeAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await clients.CreateClient(ClientName).GetAsync(new Uri(new Uri(address), "/health/ready"), cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return false;
        }
    }
}
