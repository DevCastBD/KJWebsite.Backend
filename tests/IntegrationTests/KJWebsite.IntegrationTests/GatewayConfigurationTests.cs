using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace KJWebsite.IntegrationTests;

/// <summary>A real Kestrel server on a random port standing in for a downstream service.</summary>
internal sealed class StubService : IAsyncDisposable
{
    private readonly WebApplication _app;

    private StubService(WebApplication app, string name, string address)
    {
        _app = app;
        Name = name;
        Address = address;
    }

    public string Name { get; }

    public string Address { get; }

    public static async Task<StubService> StartAsync(string name, bool healthy = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapGet("/health", () => healthy ? Results.Ok() : Results.StatusCode(503));
        app.MapGet("/api/v1/content/projects", (HttpContext c) =>
            Results.Json(new { name, correlation = c.Request.Headers["X-Correlation-Id"].ToString(), padding = new string('a', 4000) }));
        app.MapGet("/api/v1/cta/echo", (HttpContext c) =>
            Results.Json(new { name, correlation = c.Request.Headers["X-Correlation-Id"].ToString() }));
        await app.StartAsync();
        return new StubService(app, name, app.Urls.First());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public sealed class GatewayConfigurationTests
{
    private static WebApplicationFactory<ApiGateway.ApiGatewayTestEntryPoint> Gateway(Action<IWebHostBuilder> configure) =>
        new GatewayApiFactory().WithWebHostBuilder(configure);

    private static void Destination(IWebHostBuilder builder, string cluster, string key, string address) =>
        builder.UseSetting($"ReverseProxy:Clusters:{cluster}:Destinations:{key}:Address", address);

    [Fact]
    public async Task RoutesToTheDestinationsComingFromConfiguration()
    {
        await using var stub = await StubService.StartAsync("content-a");
        using var gateway = Gateway(b =>
        {
            Destination(b, "content", "content", stub.Address);
            Destination(b, "cta", "cta", stub.Address);
        });
        using var client = gateway.CreateClient();

        using var response = await client.GetAsync("/api/v1/content/projects");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("content-a");
    }

    [Fact]
    public async Task CorrelationIdIsCreatedForwardedAndEchoed()
    {
        await using var stub = await StubService.StartAsync("s");
        using var gateway = Gateway(b => Destination(b, "cta", "cta", stub.Address));
        using var client = gateway.CreateClient();

        using var generated = await client.GetAsync("/api/v1/cta/echo");
        var id = generated.Headers.GetValues("X-Correlation-Id").Single();
        id.Should().MatchRegex("^[0-9a-f]{32}$");
        (await generated.Content.ReadAsStringAsync()).Should().Contain(id);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cta/echo");
        request.Headers.Add("X-Correlation-Id", "frontend-req-12345");
        using var supplied = await client.SendAsync(request);
        supplied.Headers.GetValues("X-Correlation-Id").Single().Should().Be("frontend-req-12345");
        (await supplied.Content.ReadAsStringAsync()).Should().Contain("frontend-req-12345");
    }

    [Fact]
    public async Task ForgedCorrelationIdIsReplaced()
    {
        await using var stub = await StubService.StartAsync("s");
        using var gateway = Gateway(b => Destination(b, "cta", "cta", stub.Address));
        using var client = gateway.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cta/echo");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "bad id with spaces");
        using var response = await client.SendAsync(request);

        response.Headers.GetValues("X-Correlation-Id").Single().Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public async Task PublicReadsAreCompressed()
    {
        await using var stub = await StubService.StartAsync("s");
        using var gateway = Gateway(b => Destination(b, "content", "content", stub.Address));
        using var client = gateway.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/content/projects");
        request.Headers.Add("Accept-Encoding", "gzip");

        using var response = await client.SendAsync(request);

        response.Content.Headers.ContentEncoding.Should().Contain("gzip");
    }

    [Fact]
    public async Task UnhealthyDestinationStopsReceivingTraffic()
    {
        await using var healthy = await StubService.StartAsync("healthy");
        await using var sick = await StubService.StartAsync("sick", healthy: false);
        using var gateway = Gateway(b =>
        {
            Destination(b, "content", "a", healthy.Address);
            Destination(b, "content", "b", sick.Address);
            b.UseSetting("ReverseProxy:Clusters:content:HealthCheck:Active:Interval", "00:00:01");
            b.UseSetting("ReverseProxy:Clusters:content:HealthCheck:Active:Timeout", "00:00:01");
            b.UseSetting("ReverseProxy:Clusters:content:HealthCheck:Active:Policy", "ConsecutiveFailures");
            b.UseSetting("ReverseProxy:Clusters:content:HealthCheck:Active:Path", "/health");
        });
        using var client = gateway.CreateClient();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        var served = new HashSet<string>();
        while (DateTime.UtcNow < deadline)
        {
            served.Clear();
            for (var i = 0; i < 12; i++)
            {
                using var response = await client.GetAsync("/api/v1/content/projects");
                served.Add((await response.Content.ReadAsStringAsync()).Contains("\"sick\"", StringComparison.Ordinal) ? "sick" : "healthy");
            }

            if (served.SetEquals(["healthy"]))
            {
                return;
            }

            await Task.Delay(500);
        }

        throw new Xunit.Sdk.XunitException($"The unhealthy destination was still receiving traffic: {string.Join(",", served)}");
    }

    [Fact]
    public void OutsideDevelopmentAClusterWithoutADestinationFailsStartup()
    {
        using var factory = new WebApplicationFactory<ApiGateway.ApiGatewayTestEntryPoint>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Staging");
        });

        var start = () => factory.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("*has no destination*");
    }
}
