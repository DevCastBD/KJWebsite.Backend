using System.Net;
using FluentAssertions;

namespace KJWebsite.IntegrationTests;

public sealed class GatewaySmokeTests : IDisposable
{
    private readonly GatewayApiFactory _factory = new();
    private readonly HttpClient _client;

    public GatewaySmokeTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task HealthReturnsOk()
    {
        using var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
