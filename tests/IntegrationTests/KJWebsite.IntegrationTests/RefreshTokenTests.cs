using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthIdentityService.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

/// <summary>
/// Refresh-token rotation, run against every database provider the service supports.
///
/// The first CI run (issue #11) found POST /auth/refresh returning 500: its query compared
/// `ExpiresAt > DateTimeOffset.UtcNow`, which EF Core cannot translate — SQLite stores
/// DateTimeOffset as text and cannot compare it, and Npgsql rejected the UtcNow form. SQLite is
/// the local-development default, so rotation had never worked there either. Running the same
/// tests on both providers is what keeps that from recurring on either one.
/// </summary>
public abstract class RefreshTokenTestsBase<TFactory> : IDisposable
    where TFactory : WebApplicationFactory<AuthIdentityService.AuthIdentityServiceTestEntryPoint>
{
    private readonly TFactory _factory;
    private readonly HttpClient _client;

    protected RefreshTokenTestsBase(TFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task RefreshReturnsReplacementTokens()
    {
        var tokens = await LoginAsync();
        using var response = await RefreshAsync(tokens.RefreshToken);

        await ShouldBeStatus(response, HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("access_token").GetString().Should().NotBe(tokens.AccessToken);
        payload.GetProperty("refresh_token").GetString().Should().NotBe(tokens.RefreshToken);
    }

    [Fact]
    public async Task RefreshRevokesTheTokenItReplaced()
    {
        var tokens = await LoginAsync();
        using var first = await RefreshAsync(tokens.RefreshToken);
        await ShouldBeStatus(first, HttpStatusCode.OK);

        // Rotation means a refresh token is single-use: replaying it must fail.
        using var replay = await RefreshAsync(tokens.RefreshToken);
        await ShouldBeStatus(replay, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RefreshRejectsAnExpiredToken()
    {
        var tokens = await LoginAsync();
        await ExpireAsync(tokens.RefreshToken);

        using var response = await RefreshAsync(tokens.RefreshToken);
        await ShouldBeStatus(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RefreshRejectsAnUnknownToken()
    {
        using var response = await RefreshAsync("rt_does-not-exist");
        await ShouldBeStatus(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RefreshRejectsALoggedOutToken()
    {
        var tokens = await LoginAsync();
        using var logout = await _client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = tokens.RefreshToken });
        await ShouldBeStatus(logout, HttpStatusCode.NoContent);

        using var response = await RefreshAsync(tokens.RefreshToken);
        await ShouldBeStatus(response, HttpStatusCode.Unauthorized);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });

    private async Task ExpireAsync(string refreshToken)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var row = await db.RefreshTokens.SingleAsync(r => r.Token == refreshToken);
        row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private async Task<(string AccessToken, string RefreshToken)> LoginAsync()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "admin@site.org",
            password = "admin123"
        });
        await ShouldBeStatus(response, HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (payload.GetProperty("access_token").GetString()!, payload.GetProperty("refresh_token").GetString()!);
    }

    // A bare status mismatch hides the cause; in Development the body carries the exception, so
    // include it — that is what made this bug diagnosable from the CI log.
    private static async Task ShouldBeStatus(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(expected, "the response body was: {0}", body.Length > 2000 ? body[..2000] : body);
        }
    }
}

public sealed class SqliteRefreshTokenTests() : RefreshTokenTestsBase<AuthSqliteApiFactory>(new AuthSqliteApiFactory());

[Collection(nameof(PostgreSqlFixtures))]
public sealed class PostgreSqlRefreshTokenTests(PostgreSqlFixture fixture)
    : RefreshTokenTestsBase<AuthApiFactory>(new AuthApiFactory(fixture));
