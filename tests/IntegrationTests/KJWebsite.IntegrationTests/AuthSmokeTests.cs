using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace KJWebsite.IntegrationTests;

[Collection(nameof(PostgreSqlFixtures))]
public sealed class AuthSmokeTests : IDisposable
{
    private readonly AuthApiFactory _factory;
    private readonly HttpClient _client;

    public AuthSmokeTests(PostgreSqlFixture fixture)
    {
        _factory = new AuthApiFactory(fixture);
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task HealthReturnsOk()
    {
        using var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RegisterReturnsCreated()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = NewEmail(),
            password = "password123"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task LoginReturnsTokens()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "admin@site.org",
            password = "admin123"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        payload.GetProperty("refresh_token").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RefreshReturnsReplacementTokens()
    {
        var tokens = await LoginAsync();
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("access_token").GetString().Should().NotBe(tokens.AccessToken);
        payload.GetProperty("refresh_token").GetString().Should().NotBe(tokens.RefreshToken);
    }

    [Fact]
    public async Task LogoutRevokesRefreshToken()
    {
        var tokens = await LoginAsync();
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = tokens.RefreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task MeReturnsAuthenticatedProfile()
    {
        var tokens = await LoginAsync();
        _client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        using var response = await _client.GetAsync("/api/v1/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<(string AccessToken, string RefreshToken)> LoginAsync()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "admin@site.org",
            password = "admin123"
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (payload.GetProperty("access_token").GetString()!, payload.GetProperty("refresh_token").GetString()!);
    }

    private static string NewEmail() => $"smoke-{Guid.NewGuid():N}@example.test";
}
