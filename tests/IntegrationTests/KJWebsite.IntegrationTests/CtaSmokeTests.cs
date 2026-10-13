using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace KJWebsite.IntegrationTests;

public sealed class CtaSmokeTests : IDisposable
{
    private readonly CtaApiFactory _factory = new();
    private readonly HttpClient _client;

    public CtaSmokeTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task HealthReturnsOk()
    {
        using var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateSubmissionReturnsCreated()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/cta/submissions", new
        {
            formName = "contact",
            ctaType = "contact",
            language = "en",
            sourcePath = "/contact",
            values = new Dictionary<string, object?> { ["firstName"] = "Test" },
            submittedAt = DateTimeOffset.UtcNow
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateSubmissionAcceptsTimestampWithOffset()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/cta/submissions", new
        {
            formName = "contact",
            ctaType = "contact",
            language = "bn",
            sourcePath = "/contact",
            values = new Dictionary<string, object?> { ["firstName"] = "বাংলা" },
            submittedAt = new DateTimeOffset(2026, 1, 1, 6, 0, 0, TimeSpan.FromHours(6))
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
