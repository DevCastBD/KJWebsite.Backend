using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KJWebsite.IntegrationTests;

/// <summary>
/// Where the gateway sends the donor paths (BE-107, #214). The gateway runs for real; the Sponsorship and Finance destinations are two small
/// listeners that answer with their own name and the path they were asked for, so each assertion shows which service a request reached.
/// </summary>
public sealed class GatewayDonorRoutesTests : IAsyncLifetime
{
    private readonly ConcurrentQueue<string> _financeSeen = new();
    private readonly ConcurrentQueue<string> _sponsorshipSeen = new();
    private WebApplication _finance = null!;
    private WebApplication _sponsorship = null!;
    private WebApplicationFactory<ApiGateway.ApiGatewayTestEntryPoint> _gateway = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _finance = await Listener("finance", _financeSeen);
        _sponsorship = await Listener("sponsorship", _sponsorshipSeen);
        _gateway = new GatewayApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Services:Finance:Url", Address(_finance));
            builder.UseSetting("Services:Sponsorship:Url", Address(_sponsorship));
        });
        _client = _gateway.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _gateway.DisposeAsync();
        await _finance.DisposeAsync();
        await _sponsorship.DisposeAsync();
    }

    [Theory]
    [InlineData("/api/v1/donors/me/students")]
    [InlineData("/api/v1/donors/me/students/stu_1")]
    [InlineData("/api/v1/donors/me/students/stu_1/photo")]
    [InlineData("/api/v1/admin/sponsorship/donors/dnr_1/students")]
    public async Task DonorStudentPathsAndTheStaffLinkPathsGoToSponsorship(string path)
    {
        using var response = await _client.GetAsync(path);

        (await response.Content.ReadAsStringAsync()).Should().Be($"sponsorship:GET:{path}");
        _financeSeen.Should().BeEmpty();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task WritesOnDonorStudentPathsReachSponsorshipSoItAnswers405Itself(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/v1/donors/me/students/stu_1");
        using var response = await _client.SendAsync(request);

        (await response.Content.ReadAsStringAsync()).Should().Be($"sponsorship:{method}:/api/v1/donors/me/students/stu_1");
        _financeSeen.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/api/v1/donors/me")]
    [InlineData("/api/v1/donors/me/donations")]
    public async Task TheDonorProfileAndGiftsStillGoToFinance(string path)
    {
        using var response = await _client.GetAsync(path);

        (await response.Content.ReadAsStringAsync()).Should().Be($"finance:GET:{path}");
        _sponsorshipSeen.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/api/v1/donors/me/identity")]
    [InlineData("/api/v1/donors/me/identity/")]
    [InlineData("/api/v1/donors/me/anything-else")]
    public async Task TheFinanceIdentityCallIsNotReachableThroughTheGateway(string path)
    {
        using var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _financeSeen.Should().BeEmpty("the identity call is service to service only");
        _sponsorshipSeen.Should().BeEmpty();
    }

    private static async Task<WebApplication> Listener(string name, ConcurrentQueue<string> seen)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(context =>
        {
            var line = $"{name}:{context.Request.Method}:{context.Request.Path}";
            seen.Enqueue(line);
            return context.Response.WriteAsync(line);
        });
        await app.StartAsync();
        return app;
    }

    private static string Address(WebApplication app) => app.Urls.First().TrimEnd('/') + "/";
}
