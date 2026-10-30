using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthIdentityService.Data;
using AuthIdentityService.Tokens;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace KJWebsite.IntegrationTests;

public sealed class TokenTests : IDisposable
{
    private readonly AuthApiFactory _factory = new();
    private readonly List<IDisposable> _disposables = [];
    private readonly HttpClient _client;

    public TokenTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task AccessTokenIsAnRs256JwtThatVerifiesAgainstThePublishedKeys()
    {
        var tokens = await LoginAsync();

        var jwt = new JsonWebToken(tokens.AccessToken);
        jwt.Alg.Should().Be("RS256");
        jwt.Kid.Should().NotBeNullOrEmpty();
        jwt.Subject.Should().Be("usr_admin");
        jwt.GetClaim("sid").Value.Should().StartWith("ses_");
        jwt.Claims.Where(c => c.Type == "role").Select(c => c.Value).Should().Contain("SuperAdmin");
        (jwt.ValidTo - jwt.ValidFrom).Should().BeCloseTo(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(2));

        var jwks = await _client.GetStringAsync("/.well-known/jwks.json");
        jwks.Should().NotContain("\"d\"").And.NotContain("\"p\"");
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(tokens.AccessToken, new TokenValidationParameters
        {
            IssuerSigningKeys = new JsonWebKeySet(jwks).GetSigningKeys(),
            ValidIssuer = "kjwebsite-auth",
            ValidAudience = "kjwebsite-api"
        });
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ASessionSurvivesARestartAndIsAcceptedByAnotherReplica()
    {
        var tokens = await LoginAsync();

        // A second host with the same key and database is both "the API after a restart" and "a second replica".
        using var replica = _factory.WithWebHostBuilder(_ => { });
        using var replicaClient = replica.CreateClient();
        using var response = await MeAsync(replicaClient, tokens.AccessToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ATokenSignedWithAnotherKeyIsRejected()
    {
        var tokens = await LoginAsync();

        using var stranger = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:PrivateKeyPem"] = TestJwtKeys.Other })));
        using var strangerClient = stranger.CreateClient();
        using var response = await MeAsync(strangerClient, tokens.AccessToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProtectedEndpointsRejectMissingAndTamperedTokens()
    {
        using var missing = await _client.GetAsync("/api/v1/auth/me");
        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString().Should().Be("UNAUTHORIZED");

        var tokens = await LoginAsync();
        var parts = tokens.AccessToken.Split('.');
        using var tampered = await MeAsync(_client, $"{parts[0]}.{parts[1]}A.{parts[2]}");
        tampered.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RefreshTokensAreStoredOnlyAsHashes()
    {
        var tokens = await LoginAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var row = await db.RefreshTokens.SingleAsync(r => r.UserId == "usr_admin" && r.TokenHash == RefreshTokenService.Hash(tokens.RefreshToken));
        row.TokenHash.Should().NotContain(tokens.RefreshToken).And.HaveLength(64);
        (await db.RefreshTokens.Select(r => r.TokenHash).ToListAsync()).Should().NotContain(h => h.StartsWith("rt_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshRotatesAndAnOldTokenIsRejected()
    {
        var first = await LoginAsync();
        using var rotated = await RefreshAsync(first.RefreshToken);

        rotated.StatusCode.Should().Be(HttpStatusCode.OK, await rotated.Content.ReadAsStringAsync());
        var second = await rotated.Content.ReadFromJsonAsync<JsonElement>();
        second.GetProperty("refresh_token").GetString().Should().NotBe(first.RefreshToken);
        second.GetProperty("access_token").GetString().Should().NotBe(first.AccessToken);
        using var unknown = await RefreshAsync("rt_does-not-exist");
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AReplayedRefreshTokenRevokesTheWholeChain()
    {
        var first = await LoginAsync();
        using var rotated = await RefreshAsync(first.RefreshToken);
        var latest = (await rotated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refresh_token").GetString()!;

        using var replay = await RefreshAsync(first.RefreshToken);
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var legitimateNowRevoked = await RefreshAsync(latest);
        legitimateNowRevoked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var oldAccess = await MeAsync(_client, first.AccessToken);
        oldAccess.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OldRotatedTokensArePrunedButRecentOnesStayForReplayDetection()
    {
        var first = await LoginAsync();
        using var second = await RefreshAsync(first.RefreshToken);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var row = await db.RefreshTokens.SingleAsync(r => r.TokenHash == RefreshTokenService.Hash(first.RefreshToken));
            row.RevokedAt = DateTimeOffset.UtcNow.AddDays(-8);
            await db.SaveChangesAsync();
        }

        var latest = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refresh_token").GetString()!;
        using var third = await RefreshAsync(latest);

        third.StatusCode.Should().Be(HttpStatusCode.OK);
        using var check = _factory.Services.CreateScope();
        var rows = await check.ServiceProvider.GetRequiredService<AuthDbContext>().RefreshTokens.Select(r => r.TokenHash).ToListAsync();
        rows.Should().NotContain(RefreshTokenService.Hash(first.RefreshToken));
        rows.Should().Contain(RefreshTokenService.Hash(latest));
    }

    [Fact]
    public async Task AnExpiredRefreshTokenIsRejected()
    {
        var tokens = await LoginAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var row = await db.RefreshTokens.SingleAsync(r => r.TokenHash == RefreshTokenService.Hash(tokens.RefreshToken));
            row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        using var response = await RefreshAsync(tokens.RefreshToken);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LogoutEndsTheSessionImmediately()
    {
        var tokens = await LoginAsync();
        using var logout = await _client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = tokens.RefreshToken });
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var refresh = await RefreshAsync(tokens.RefreshToken);
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var me = await MeAsync(_client, tokens.AccessToken);
        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LogoutAllEndsEverySessionForTheUser()
    {
        var laptop = await LoginAsync();
        var phone = await LoginAsync();

        using (var request = Authorized(HttpMethod.Post, "/api/v1/auth/logout-all", laptop.AccessToken))
        {
            using var response = await _client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        (await RefreshAsync(laptop.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await RefreshAsync(phone.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SessionsCanBeListedAndOneEnded()
    {
        var laptop = await LoginAsync();
        var phone = await LoginAsync();

        using var list = await _client.SendAsync(Authorized(HttpMethod.Get, "/api/v1/auth/sessions", laptop.AccessToken));
        var items = (await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCountGreaterThanOrEqualTo(2);
        items.Count(i => i.GetProperty("current").GetBoolean()).Should().Be(1);
        var phoneSession = new JsonWebToken(phone.AccessToken).GetClaim("sid").Value;

        using var end = await _client.SendAsync(Authorized(HttpMethod.Delete, $"/api/v1/auth/sessions/{phoneSession}", laptop.AccessToken));
        end.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await RefreshAsync(phone.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await MeAsync(_client, laptop.AccessToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var missing = await _client.SendAsync(Authorized(HttpMethod.Delete, "/api/v1/auth/sessions/ses_unknown", laptop.AccessToken));
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    public void Dispose()
    {
        _client.Dispose();
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _factory.Dispose();
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static Task<HttpResponseMessage> MeAsync(HttpClient client, string accessToken) =>
        client.SendAsync(Authorized(HttpMethod.Get, "/api/v1/auth/me", accessToken));

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });

    private async Task<(string AccessToken, string RefreshToken)> LoginAsync()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = "admin@site.org", password = "admin123" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (payload.GetProperty("access_token").GetString()!, payload.GetProperty("refresh_token").GetString()!);
    }
}
