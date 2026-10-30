using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthIdentityService.Data;
using AuthIdentityService.Data.Entities;
using AuthIdentityService.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class IdentityTests : IDisposable
{
    private const string Password = "correct-horse-battery";
    private readonly AuthApiFactory _factory = new();
    private readonly HttpClient _client;

    public IdentityTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task StoredCredentialsAreAOneWayHash()
    {
        var email = await RegisterAsync();

        var user = await FindUserAsync(email);
        user.PasswordHash.Should().NotBeNullOrWhiteSpace();
        user.PasswordHash.Should().NotContain(Password);
        using var scope = _factory.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AuthUserEntity>>();
        hasher.VerifyHashedPassword(user, user.PasswordHash!, Password).Should().NotBe(PasswordVerificationResult.Failed);
        hasher.VerifyHashedPassword(user, user.PasswordHash!, "wrong-password-here").Should().Be(PasswordVerificationResult.Failed);
    }

    [Fact]
    public void ThereIsNoPlainTextPasswordColumn()
    {
        using var scope = _factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<AuthDbContext>().Model;

        model.FindEntityType(typeof(AuthUserEntity))!.GetProperties().Select(p => p.Name).Should().NotContain("Password");
    }

    [Fact]
    public async Task ShortPasswordsAreRejected()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/register", new { email = NewEmail(), password = "short" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RegisterIgnoresARequestedAdminRole()
    {
        var email = await RegisterAsync(roles: "admin");
        await ConfirmAsync(email);

        var login = await LoginAsync(email, Password);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("user").GetProperty("role").GetString().Should().Be("Member");
    }

    [Fact]
    public async Task LoginRequiresAConfirmedEmailThenSucceeds()
    {
        var email = await RegisterAsync();

        using var before = await LoginAsync(email, Password);
        before.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await ConfirmAsync(email);
        using var after = await LoginAsync(email, Password);
        after.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ConfirmEmailRejectsABadToken()
    {
        var email = await RegisterAsync();
        var user = await FindUserAsync(email);

        using var response = await _client.PostAsJsonAsync("/api/v1/auth/confirm-email", new { userId = user.Id, token = "nonsense" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AccountLocksAfterRepeatedFailures()
    {
        var email = await RegisterAsync();
        await ConfirmAsync(email);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var wrong = await LoginAsync(email, "wrong-password-here");
            wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"attempt {attempt}");
        }

        using var fifth = await LoginAsync(email, "wrong-password-here");
        fifth.StatusCode.Should().Be(HttpStatusCode.Locked);
        using var correctButLocked = await LoginAsync(email, Password);
        correctButLocked.StatusCode.Should().Be(HttpStatusCode.Locked);
    }

    [Fact]
    public async Task PasswordResetWorksEndToEndAndEndsOldSessions()
    {
        var email = await RegisterAsync();
        await ConfirmAsync(email);
        using var login = await LoginAsync(email, Password);
        var oldRefresh = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refresh_token").GetString();

        using var forgot = await _client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email });
        forgot.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var token = LastToken(AuthEmailKind.ResetPassword, email);
        const string newPassword = "a-brand-new-passphrase";
        using var reset = await _client.PostAsJsonAsync("/api/v1/auth/reset-password", new { email, token, newPassword });
        reset.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await LoginAsync(email, Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LoginAsync(email, newPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var refresh = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = oldRefresh });
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var replay = await _client.PostAsJsonAsync("/api/v1/auth/reset-password", new { email, token, newPassword = "yet-another-passphrase" });
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ForgotPasswordDoesNotRevealWhetherTheAccountExists()
    {
        var sender = _factory.Services.GetRequiredService<InMemoryAuthEmailSender>();
        var before = sender.Sent.Count;

        using var response = await _client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = NewEmail() });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        sender.Sent.Count.Should().Be(before);
    }

    [Fact]
    public async Task TheDevelopmentSeedAccountIsDisabledOutsideDevelopment()
    {
        (await LoginAsync("admin@site.org", "admin123")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var production = _factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var productionClient = production.CreateClient();
        using var response = await productionClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "admin@site.org", password = "admin123" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static string NewEmail() => $"identity-{Guid.NewGuid():N}@example.test";

    private async Task<string> RegisterAsync(string? roles = null)
    {
        var email = NewEmail();
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password, roles });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return email;
    }

    private Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });

    private async Task ConfirmAsync(string email)
    {
        var user = await FindUserAsync(email);
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/confirm-email", new { userId = user.Id, token = LastToken(AuthEmailKind.ConfirmEmail, email) });
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private string LastToken(AuthEmailKind kind, string email) =>
        _factory.Services.GetRequiredService<InMemoryAuthEmailSender>().Sent.Last(m => m.Kind == kind && m.To == email).Token;

    private async Task<AuthUserEntity> FindUserAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Users.SingleAsync(u => u.Email == email);
    }
}
