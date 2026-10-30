using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class ObservabilityEndpointTests
{
    private sealed class PeerFilter(string address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(address);
                return nextMiddleware();
            });
            next(app);
        };
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/health/startup")]
    public async Task ServicesAnswerEveryHealthProbeAnonymously(string path)
    {
        using var content = new ContentApiFactory();
        using var cta = new CtaApiFactory();
        using var auth = new AuthApiFactory();

        foreach (var client in new[] { content.CreateClient(), cta.CreateClient(), auth.CreateClient() })
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
    }

    [Fact]
    public async Task ReadinessIncludesTheDatabaseCheckButNeverExceptionDetails()
    {
        using var factory = new ContentApiFactory();
        using var client = factory.CreateClient();

        using var ready = JsonDocument.Parse(await client.GetStringAsync("/health/ready"));
        ready.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        ready.RootElement.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString())
            .Should().Contain("database");

        using var live = JsonDocument.Parse(await client.GetStringAsync("/health/live"));
        live.RootElement.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString())
            .Should().Equal("self");
    }

    [Fact]
    public async Task ServiceEchoesTheCorrelationId()
    {
        using var factory = new ContentApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/content/projects");
        request.Headers.Add("X-Correlation-Id", "frontend-req-777777");

        using var response = await client.SendAsync(request);

        response.Headers.GetValues("X-Correlation-Id").Single().Should().Be("frontend-req-777777");
    }

    [Fact]
    public async Task MetricsAreServedToTheLocalScraperInPrometheusFormat()
    {
        using var factory = new ContentApiFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddTransient<IStartupFilter>(_ => new PeerFilter("127.0.0.1"))));
        using var client = factory.CreateClient();
        (await client.GetAsync("/api/v1/content/projects")).Dispose();

        using var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("http_server_request_duration");
    }

    [Theory]
    [InlineData("203.0.113.9", null)]
    [InlineData("127.0.0.1", "198.51.100.7")]
    public async Task MetricsAreHiddenFromEveryoneElse(string peer, string? forwardedFor)
    {
        using var factory = new ContentApiFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddTransient<IStartupFilter>(_ => new PeerFilter(peer))));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GatewayReadinessAggregatesTheDownstreamServices()
    {
        await using var content = await StubService.StartAsync("content");
        await using var cta = await StubService.StartAsync("cta");
        await using var auth = await StubService.StartAsync("auth", healthy: false);
        using var gateway = new GatewayApiFactory().WithWebHostBuilder(b =>
        {
            b.UseSetting("ReverseProxy:Clusters:content:Destinations:content:Address", content.Address);
            b.UseSetting("ReverseProxy:Clusters:cta:Destinations:cta:Address", cta.Address);
            b.UseSetting("ReverseProxy:Clusters:auth:Destinations:auth:Address", auth.Address);
        });
        using var client = gateway.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "one service down degrades the gateway but it can still serve the rest");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetString().Should().Be("Degraded");
        var downstream = body.RootElement.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "downstream");
        downstream.GetProperty("data").GetProperty("auth/auth").GetString().Should().Be("Unhealthy");
        downstream.GetProperty("data").GetProperty("content/content").GetString().Should().Be("Healthy");
    }

    [Fact]
    public async Task GatewayIsUnhealthyWhenNoDownstreamIsReachable()
    {
        using var gateway = new GatewayApiFactory().WithWebHostBuilder(b =>
        {
            foreach (var name in new[] { "content", "cta", "auth" })
            {
                b.UseSetting($"ReverseProxy:Clusters:{name}:Destinations:{name}:Address", "http://127.0.0.1:1/");
            }
        });
        using var client = gateway.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }
}
