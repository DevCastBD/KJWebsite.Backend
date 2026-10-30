using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class GatewayRateLimitTests : IDisposable
{
    private readonly WebApplicationFactory<ApiGateway.ApiGatewayTestEntryPoint> _factory =
        new GatewayApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Login:PermitLimit", "3");
            builder.UseSetting("RateLimiting:Register:PermitLimit", "2");
            builder.UseSetting("RateLimiting:CtaSubmission:PermitLimit", "2");
            builder.UseSetting("RateLimiting:MembershipIntake:PermitLimit", "2");
            builder.UseSetting("RateLimiting:MembershipStatus:PermitLimit", "2");
            builder.UseSetting("RateLimiting:Global:PermitLimit", "50");
            builder.UseSetting("ForwardedHeaders:TrustAnyProxy", "true");
        });

    public void Dispose() => _factory.Dispose();

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, string? ip = null, string? acceptLanguage = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { }) };
        if (ip is not null)
        {
            request.Headers.Add("X-Forwarded-For", ip);
        }

        if (acceptLanguage is not null)
        {
            request.Headers.Add("Accept-Language", acceptLanguage);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task LoginIsThrottledAfterLimitWithRetryAfterAndProblemDetails()
    {
        using var client = _factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            using var ok = await Post(client, "/api/v1/auth/login", "203.0.113.1");
            ok.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }

        using var limited = await Post(client, "/api/v1/auth/login", "203.0.113.1");

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        limited.Headers.GetValues("Retry-After").Single().Should().MatchRegex("^[1-9][0-9]*$");
        var body = await limited.Content.ReadAsStringAsync();
        body.Should().Contain("urn:kjwebsite:error:rate-limited").And.Contain("\"status\":429");
    }

    [Fact]
    public async Task LimitsArePerClientIp()
    {
        using var client = _factory.CreateClient();
        for (var i = 0; i < 4; i++)
        {
            (await Post(client, "/api/v1/auth/login", "203.0.113.10")).Dispose();
        }

        using var other = await Post(client, "/api/v1/auth/login", "203.0.113.11");
        other.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData("/api/v1/auth/register")]
    [InlineData("/api/v1/cta/submissions")]
    [InlineData("/api/v1/membership/applications")]
    [InlineData("/api/v1/membership/applications/check")]
    [InlineData("/api/v1/membership/applications/status")]
    public async Task RegisterAndSubmissionsUseTheirOwnLimit(string path)
    {
        using var client = _factory.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            using var ok = await Post(client, path, "203.0.113.20");
            ok.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }

        using var limited = await Post(client, path, "203.0.113.20");
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // The budgets are separate: login for the same client is untouched.
        using var login = await Post(client, "/api/v1/auth/login", "203.0.113.20");
        login.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task ThrottleMessageFollowsRequestedLanguage()
    {
        using var client = _factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            (await Post(client, "/api/v1/auth/login", "203.0.113.30")).Dispose();
        }

        using var limited = await Post(client, "/api/v1/auth/login", "203.0.113.30", "bn");
        (await limited.Content.ReadAsStringAsync()).Should().Contain("অনেক বেশি অনুরোধ");
    }

    [Fact]
    public async Task GlobalSlidingWindowThrottlesAnyRoute()
    {
        using var factory = new GatewayApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Global:PermitLimit", "3");
            builder.UseSetting("ForwardedHeaders:TrustAnyProxy", "true");
        });
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
            request.Headers.Add("X-Forwarded-For", "203.0.113.40");
            (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var over = new HttpRequestMessage(HttpMethod.Get, "/health");
        over.Headers.Add("X-Forwarded-For", "203.0.113.40");
        (await client.SendAsync(over)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task ForwardedForIsIgnoredWithoutATrustedProxy()
    {
        // Default config trusts no proxy: spoofing X-Forwarded-For must not mint fresh budgets.
        using var factory = new GatewayApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Login:PermitLimit", "2");
            // TestServer has no socket; give the caller a real, untrusted address like Kestrel would.
            builder.ConfigureTestServices(services => services.AddTransient<IStartupFilter, UntrustedPeerFilter>());
        });
        using var client = factory.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            (await Post(client, "/api/v1/auth/login", $"198.51.100.{i}")).Dispose();
        }

        using var limited = await Post(client, "/api/v1/auth/login", "198.51.100.99");
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task LimitsCanBeDisabledPerEnvironment()
    {
        using var factory = new GatewayApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Enabled", "false");
            builder.UseSetting("RateLimiting:Login:PermitLimit", "1");
        });
        using var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            using var response = await Post(client, "/api/v1/auth/login");
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }
    }

    private sealed class UntrustedPeerFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.250");
                return nextMiddleware();
            });
            next(app);
        };
    }
}
