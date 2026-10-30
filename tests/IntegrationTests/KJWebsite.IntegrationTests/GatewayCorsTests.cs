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
    public async Task PreflightFromUnlistedOriginIsNotApproved()
    {
        using var client = _factory.CreateClient();
        var request = Request(HttpMethod.Options, "/api/v1/cta/submissions", "https://evil.example.com");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        using var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
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
