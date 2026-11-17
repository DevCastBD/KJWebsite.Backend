using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthIdentityService.Data;
using AuthIdentityService.Data.Entities;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class SessionCurrentTests : IDisposable
{
    private const string Current = "/api/v1/auth/sessions/current";
    private const string Provision = "/api/v1/admin/auth/donors";
    private const string Password = "Session-Current-Pass-229";
    private readonly AuthApiFactory _auth = new();
    private readonly HttpClient _client;

    public SessionCurrentTests() => _client = _auth.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _auth.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveSessionReturnsEmpty204WithPrivateHeadersEvenWhenPasswordChangeIsRequired(bool mustChange)
    {
        var login = await CreateAndLogin(KjRoles.Member, mustChange);
        new JwtSecurityTokenHandler().ReadJwtToken(login.Access).Claims
            .Any(c => c.Type == KjClaims.PasswordChangeRequired && c.Value == "true").Should().Be(mustChange);

        using var response = await Send(HttpMethod.Get, Current, login.Access);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        AssertPrivate(response);
    }

    [Fact]
    public async Task MissingMalformedAndExpiredTokensReturn401WithPrivateHeaders()
    {
        foreach (var token in new string?[] { null }.Concat(TestTokens.Malformed()).Append(TestTokens.Expired([])))
        {
            using var response = await Send(HttpMethod.Get, Current, token);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            AssertPrivate(response);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogoutRevokesAnUnexpiredAccessToken(bool allSessions)
    {
        var login = await CreateAndLogin(KjRoles.Member);
        using var logout = allSessions
            ? await Send(HttpMethod.Post, "/api/v1/auth/logout-all", login.Access)
            : await Send(HttpMethod.Post, "/api/v1/auth/logout", body: new { refreshToken = login.Refresh });
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        new JwtSecurityTokenHandler().ReadJwtToken(login.Access).ValidTo.Should().BeAfter(DateTime.UtcNow);

        using var response = await Send(HttpMethod.Get, Current, login.Access);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertPrivate(response);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("erased")]
    public async Task InactiveAccountWithLiveSessionReturnsAccountDisabled(string status)
    {
        var login = await CreateAndLogin(KjRoles.Member);
        using (var scope = _auth.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await db.Users.Where(u => u.Id == login.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, status));
            var sessions = scope.ServiceProvider.GetRequiredService<AuthIdentityService.Tokens.RefreshTokenService>();
            var sid = new JwtSecurityTokenHandler().ReadJwtToken(login.Access).Claims.Single(c => c.Type == KjClaims.SessionId).Value;
            (await sessions.IsSessionActiveAsync(sid)).Should().BeTrue();
        }

        using var response = await Send(HttpMethod.Get, Current, login.Access);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = (await Json(response)).GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("ACCOUNT_DISABLED");
        error.GetProperty("message").GetString().Should().Be("This account has been switched off.");
        AssertPrivate(response);
    }

    [Fact]
    public async Task ProvisioningAnExistingDonorMemberKeepsItsAccessAndRefreshSession()
    {
        var admin = await CreateAndLogin(KjRoles.Admin);
        var member = await CreateAndLogin(KjRoles.Member, donor: true);

        using var provisioned = await Send(HttpMethod.Post, Provision, admin.Access,
            new { donorId = Guid.NewGuid().ToString("N"), email = member.Email, password = Password });
        provisioned.StatusCode.Should().Be(HttpStatusCode.OK);
        using var current = await Send(HttpMethod.Get, Current, member.Access);
        current.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var refreshed = await Send(HttpMethod.Post, "/api/v1/auth/refresh", body: new { refreshToken = member.Refresh });
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NewlyProvisionedDonorCanLoginAndRetryWithoutLosingItsSession()
    {
        var admin = await CreateAndLogin(KjRoles.Admin);
        var email = $"session-donor-{Guid.NewGuid():N}@example.org";
        var payload = new { donorId = Guid.NewGuid().ToString("N"), email, password = Password };
        using var provisioned = await Send(HttpMethod.Post, Provision, admin.Access, payload);
        provisioned.StatusCode.Should().Be(HttpStatusCode.Created);
        var login = await Login(email, (await Json(provisioned)).GetProperty("userId").GetString()!);
        using var retry = await Send(HttpMethod.Post, Provision, admin.Access, payload);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);

        using var current = await Send(HttpMethod.Get, Current, login.Access);
        current.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var refreshed = await Send(HttpMethod.Post, "/api/v1/auth/refresh", body: new { refreshToken = login.Refresh });
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<Session> CreateAndLogin(string role, bool mustChange = false, bool donor = false)
    {
        var email = $"session-{Guid.NewGuid():N}@example.org";
        using var scope = _auth.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>();
        var user = new AuthUserEntity
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            MustChangePassword = mustChange
        };
        (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
        if (donor) (await users.AddToRoleAsync(user, KjRoles.Donor)).Succeeded.Should().BeTrue();
        return await Login(email, user.Id);
    }

    private async Task<Session> Login(string email, string userId)
    {
        using var response = await Send(HttpMethod.Post, "/api/v1/auth/login", body: new { email, password = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        return new Session(userId, email, body.GetProperty("access_token").GetString()!, body.GetProperty("refresh_token").GetString()!);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private static void AssertPrivate(HttpResponseMessage response)
    {
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.NoStore.Should().BeTrue();
        response.Headers.Vary.Should().Contain("Authorization");
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private sealed record Session(string UserId, string Email, string Access, string Refresh);
}
