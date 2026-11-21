using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KJWebsite.IntegrationTests;

public sealed class GatewayCorsTests : IDisposable
{
    private const string SiteOrigin = "https://site.example.com";
    private const string AdminOrigin = "https://admin.example.com";

    private readonly WebApplicationFactory<ApiGateway.ApiGatewayTestEntryPoint> _factory =
        new GatewayApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Cors:SiteOrigins:0", SiteOrigin);
            builder.UseSetting("Cors:AdminOrigins:0", AdminOrigin);
        });

    private static HttpRequestMessage Request(HttpMethod method, string path, string origin)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", origin);
        return request;
    }

    [Fact]
    public async Task SiteOriginHealthProbeIsAllowedWithoutCredentials()
    {
        using var client = _factory.CreateClient();
        using var response = await client.SendAsync(Request(HttpMethod.Get, "/health", SiteOrigin));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(SiteOrigin);
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    [Fact]
    public async Task UnlistedOriginGetsNoCorsHeaders()
    {
        using var client = _factory.CreateClient();
        using var response = await client.SendAsync(Request(HttpMethod.Get, "/health", "https://evil.example.com"));

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task AdminOriginGetsCredentials()
    {
        using var client = _factory.CreateClient();
        using var response = await client.SendAsync(Request(HttpMethod.Get, "/health", AdminOrigin));

        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(AdminOrigin);
        response.Headers.GetValues("Access-Control-Allow-Credentials").Should().Equal("true");
    }

    [Fact]
    public async Task PreflightAllowsIdempotencyKeyAndContentType()
    {
        using var client = _factory.CreateClient();
        var request = Request(HttpMethod.Options, "/api/v1/cta/submissions", SiteOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,idempotency-key");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(SiteOrigin);
        string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"))
            .Should().ContainEquivalentOf("Idempotency-Key").And.ContainEquivalentOf("Content-Type");
    }

    [Fact]
    public async Task PreflightAllowsTheAttachmentUploadHeader()
    {
        using var client = _factory.CreateClient();
        var request = Request(HttpMethod.Options, "/api/v1/membership/applications/app_1/attachments/cv", SiteOrigin);
        request.Headers.Add("Access-Control-Request-Method", "PUT");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-filename");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")).Should().ContainEquivalentOf("X-Filename");
    }

    [Fact]
    public async Task PreflightAllowsTheCorrelationHeaderTheFrontendSendsOnEveryCall()
    {
        using var client = _factory.CreateClient();
        var request = Request(HttpMethod.Options, "/api/v1/content/settings", SiteOrigin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "x-correlation-id");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")).Should().ContainEquivalentOf("X-Correlation-ID");
    }

    [Fact]
    public async Task PreflightFromUnlistedOriginIsNotApproved()
    {
        using var client = _factory.CreateClient();
        var request = Request(HttpMethod.Options, "/api/v1/cta/submissions", "https://evil.example.com");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        using var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Theory]
    [InlineData(SiteOrigin)]
    [InlineData(AdminOrigin)]
    public async Task PreflightAllowsIfMatchForOptimisticConcurrencyEdits(string origin)
    {
        using var client = _factory.CreateClient();
        var request = Request(HttpMethod.Options, "/api/v1/admin/sponsorship/students/stu_1", origin);
        request.Headers.Add("Access-Control-Request-Method", "PATCH");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,if-match,authorization,idempotency-key");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(origin);
        string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")).Should().ContainEquivalentOf("If-Match");
        string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods")).Should().ContainEquivalentOf("PATCH");
        response.Headers.GetValues("Access-Control-Max-Age").Should().Equal("3600");
    }

    [Fact]
    public async Task PreflightStillRefusesAHeaderThatIsNotOnTheAllowList()
    {
        using var client = _factory.CreateClient();
        var request = Request(HttpMethod.Options, "/api/v1/admin/sponsorship/students/stu_1", SiteOrigin);
        request.Headers.Add("Access-Control-Request-Method", "PATCH");
        request.Headers.Add("Access-Control-Request-Headers", "if-match,x-not-allowed");

        using var response = await client.SendAsync(request);

        // The browser enforces the preflight by looking for every requested header in Allow-Headers.
        var allowed = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
        allowed.Should().ContainEquivalentOf("If-Match").And.NotContainEquivalentOf("x-not-allowed");
    }

    [Fact]
    public async Task ETagFromAProxiedServiceReachesAListedOriginReadableByScripts()
    {
        var clusters = new[] { "auth", "content", "cta", "membership", "finance", "sponsorship" };
        using var factory = new GatewayApiFactory { ClusterHandlers = clusters.ToDictionary(c => c, c => (Func<HttpMessageHandler>)(() => new EtagHandler())) }
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Cors:SiteOrigins:0", SiteOrigin);
                builder.UseSetting("Cors:AdminOrigins:0", AdminOrigin);
            });
        using var client = factory.CreateClient();

        foreach (var origin in new[] { SiteOrigin, AdminOrigin })
        {
            using var response = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/admin/sponsorship/students/stu_1", origin));

            response.Headers.ETag!.Tag.Should().Be("\"3\"");
            string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers")).Should().Be("ETag,Retry-After");
            response.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(origin);
        }
    }

    private sealed class EtagHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"3\"");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task PreflightForIfMatchFromUnlistedOriginIsNotApproved()
    {
        using var client = _factory.CreateClient();
        var request = Request(HttpMethod.Options, "/api/v1/admin/sponsorship/students/stu_1", "https://evil.example.com");
        request.Headers.Add("Access-Control-Request-Method", "PATCH");
        request.Headers.Add("Access-Control-Request-Headers", "if-match");

        using var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        response.Headers.Contains("Access-Control-Allow-Headers").Should().BeFalse();
    }

    [Fact]
    public async Task ListedOriginsCanReadTheETagResponseHeader()
    {
        using var client = _factory.CreateClient();
        using var site = await client.SendAsync(Request(HttpMethod.Get, "/health", SiteOrigin));
        using var admin = await client.SendAsync(Request(HttpMethod.Get, "/health", AdminOrigin));

        string.Join(",", site.Headers.GetValues("Access-Control-Expose-Headers")).Should().Be("ETag,Retry-After");
        string.Join(",", admin.Headers.GetValues("Access-Control-Expose-Headers")).Should().Be("ETag,Retry-After");
        site.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
        admin.Headers.GetValues("Access-Control-Allow-Credentials").Should().Equal("true");
    }

    private static async Task<HttpResponseMessage> LimitedLogin(HttpClient client, string origin)
    {
        using var request = Request(HttpMethod.Post, "/api/v1/auth/login", origin);
        request.Headers.Add("X-Forwarded-For", "203.0.113.201");
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static WebApplicationFactory<ApiGateway.ApiGatewayTestEntryPoint> LimitedGateway() =>
        new GatewayApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Cors:SiteOrigins:0", SiteOrigin);
            builder.UseSetting("Cors:AdminOrigins:0", AdminOrigin);
            builder.UseSetting("RateLimiting:Login:PermitLimit", "1");
            builder.UseSetting("ForwardedHeaders:TrustAnyProxy", "true");
        });

    [Theory]
    [InlineData(SiteOrigin)]
    [InlineData(AdminOrigin)]
    public async Task AGatewayRateLimitAnswerLetsAListedOriginReadRetryAfter(string origin)
    {
        using var factory = LimitedGateway();
        using var client = factory.CreateClient();

        using var first = await LimitedLogin(client, origin);
        using var limited = await LimitedLogin(client, origin);

        first.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.GetValues("Retry-After").Single().Should().MatchRegex("^[1-9][0-9]*$");
        limited.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(origin);
        string.Join(",", limited.Headers.GetValues("Access-Control-Expose-Headers")).Should().Be("ETag,Retry-After");
    }

    [Fact]
    public async Task AGatewayRateLimitAnswerGivesAnUnlistedOriginNoCorsHeaders()
    {
        using var factory = LimitedGateway();
        using var client = factory.CreateClient();
        const string unlisted = "https://evil.example.com";

        using var first = await LimitedLogin(client, unlisted);
        using var limited = await LimitedLogin(client, unlisted);

        first.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        limited.Headers.Contains("Access-Control-Expose-Headers").Should().BeFalse();
    }

    [Fact]
    public async Task UnlistedOriginDoesNotLearnWhichHeadersAreExposed()
    {
        using var client = _factory.CreateClient();
        using var response = await client.SendAsync(Request(HttpMethod.Get, "/health", "https://evil.example.com"));

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        response.Headers.Contains("Access-Control-Expose-Headers").Should().BeFalse();
    }

    [Fact]
    public async Task ResponsesCarrySecurityHeaders()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/health");

        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.Contains("Referrer-Policy").Should().BeTrue();
        response.Headers.Contains("Permissions-Policy").Should().BeTrue();
    }

    [Fact]
    public void WildcardOriginFailsStartup()
    {
        using var factory = new GatewayApiFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("Cors:SiteOrigins:0", "*"));

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Cors origin*");
    }

    public void Dispose() => _factory.Dispose();
}
