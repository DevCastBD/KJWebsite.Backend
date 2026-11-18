using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthIdentityService.Data.Entities;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace KJWebsite.IntegrationTests;

public sealed class AuthPermissionsResponseTests : IDisposable
{
    private const string Password = "Permissions-Response-235!";
    private const string Email = "permissions@example.org";
    private const string AdminBase = "/api/v1/admin/auth";
    private readonly AuthApiFactory _factory = new();
    private readonly HttpClient _client;

    public AuthPermissionsResponseTests() => _client = _factory.CreateClient();

    public static IEnumerable<object[]> SeededRoles => KjRoles.Seed.Keys.Select(role => new object[] { role });

    [Theory]
    [MemberData(nameof(SeededRoles))]
    public async Task EveryAuthResponsePublishesTheSeededPermissions(string role)
    {
        var expected = KjRoles.Seed[role];
        await CreateUser([role]);
        await AssertAllResponses([role], expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomRolesPublishTheirPermissionsIncludingEmptySets(bool empty)
    {
        var admin = await AdminToken();
        string[] expected = empty ? [] : [Permissions.MediaView, Permissions.ContentView];
        using var created = await Send(HttpMethod.Post, $"{AdminBase}/roles", admin, new { name = "CustomReaders", permissions = expected });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        await CreateUser(["CustomReaders"]);
        await AssertAllResponses(["CustomReaders"], expected);
    }

    [Fact]
    public async Task TwoRolesPublishASortedUnionWithoutDuplicates()
    {
        string[] roles = [KjRoles.Moderator, KjRoles.ContentEditor];
        await CreateUser(roles);
        await AssertAllResponses(roles, roles.SelectMany(role => KjRoles.Seed[role]));
    }

    [Fact]
    public async Task NoAssignedRolesPublishAnEmptyArrayWithTheExistingMemberFallback()
    {
        await CreateUser([]);
        await AssertAllResponses([], []);
    }

    [Fact]
    public async Task AMemberWhosePermissionsWereRemovedPublishesAnEmptyArray()
    {
        var admin = await AdminToken();
        using var edited = await Send(HttpMethod.Put, $"{AdminBase}/roles/Member/permissions", admin, new { permissions = Array.Empty<string>() });
        edited.StatusCode.Should().Be(HttpStatusCode.OK);
        await CreateUser([KjRoles.Member]);
        await AssertAllResponses([KjRoles.Member], []);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermissionAndRoleEditsRevokeOldSessionsAndFreshSignInPublishesTheNewList(bool changeRole)
    {
        var admin = await AdminToken();
        var id = await CreateUser([KjRoles.Moderator]);
        var old = await Login();
        await AssertTokenResponse(old, id, [KjRoles.Moderator], KjRoles.Seed[KjRoles.Moderator], true);
        string[] expected;
        string[] roles;
        if (changeRole)
        {
            roles = [KjRoles.Member];
            expected = [Permissions.MemberProfileEdit];
            using var edited = await Send(HttpMethod.Put, $"{AdminBase}/users/{id}/roles", admin, new { roles });
            edited.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        else
        {
            roles = [KjRoles.Moderator];
            expected = [Permissions.ContentView];
            using var edited = await Send(HttpMethod.Put, $"{AdminBase}/roles/Moderator/permissions", admin, new { permissions = expected });
            edited.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        using var refused = await Send(HttpMethod.Get, "/api/v1/auth/me", Token(old));
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var refresh = await Send(HttpMethod.Post, "/api/v1/auth/refresh", body: new { refreshToken = old.GetProperty("refresh_token").GetString() });
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertTokenResponse(await Login(), id, roles, expected, true);
    }

    [Fact]
    public async Task OpenApiDescribesTheAuthResponseBodiesAndPermissions()
    {
        var document = await OkJson(await _client.GetAsync("/openapi/v1.json"));
        var schemas = document.GetProperty("components").GetProperty("schemas");
        foreach (var (path, method, name) in new[]
        {
            ("login", "post", "AuthTokenResponse"),
            ("refresh", "post", "AuthTokenResponse"),
            ("change-password", "post", "AuthTokenResponse"),
            ("me", "get", "CurrentUserResponse")
        })
        {
            var schema = document.GetProperty("paths").GetProperty($"/api/v1/auth/{path}")
                .GetProperty(method).GetProperty("responses").GetProperty("200")
                .GetProperty("content").GetProperty("application/json").GetProperty("schema");
            schema.GetProperty("$ref").GetString().Should().Be($"#/components/schemas/{name}");
        }
        var token = schemas.GetProperty("AuthTokenResponse").GetProperty("properties");
        token.GetProperty("access_token").GetProperty("type").GetString().Should().Be("string");
        token.GetProperty("user").GetProperty("$ref").GetString().Should().Be("#/components/schemas/AuthUserResponse");
        foreach (var name in new[] { "AuthUserResponse", "CurrentUserResponse" })
        {
            var permissions = schemas.GetProperty(name).GetProperty("properties").GetProperty("permissions");
            permissions.GetProperty("type").GetString().Should().Be("array");
            permissions.GetProperty("items").GetProperty("type").GetString().Should().Be("string");
        }
    }

    private async Task AssertAllResponses(string[] roles, IEnumerable<string> expected)
    {
        var login = await Login();
        var id = login.GetProperty("user").GetProperty("id").GetString()!;
        await AssertTokenResponse(login, id, roles, expected, true);
        var refreshed = await OkJson(await Send(HttpMethod.Post, "/api/v1/auth/refresh", body: new { refreshToken = login.GetProperty("refresh_token").GetString() }));
        refreshed.GetProperty("refresh_token").GetString().Should().NotBe(login.GetProperty("refresh_token").GetString());
        await AssertTokenResponse(refreshed, id, roles, expected, true);
        var changed = await OkJson(await Send(HttpMethod.Post, "/api/v1/auth/change-password", Token(refreshed), new { currentPassword = Password, newPassword = "Changed-Permissions-235!" }));
        await AssertTokenResponse(changed, id, roles, expected, false);
    }

    private async Task AssertTokenResponse(JsonElement body, string id, string[] roles, IEnumerable<string> expected, bool mustChange)
    {
        var sorted = expected.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["access_token", "refresh_token", "expires_in", "token_type", "mustChangePassword", "user"]);
        body.GetProperty("refresh_token").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("expires_in").GetInt32().Should().Be(900);
        body.GetProperty("token_type").GetString().Should().Be("Bearer");
        body.GetProperty("mustChangePassword").GetBoolean().Should().Be(mustChange);
        var user = body.GetProperty("user");
        user.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["id", "email", "role", "roles", "mustChangePassword", "permissions"]);
        AssertUser(user, id, roles, sorted, mustChange);

        // Read the JWT payload directly: one claim is a string, multiple claims are an array, and no claims omit the key.
        using var jwt = JsonDocument.Parse(Base64UrlEncoder.Decode(Token(body).Split('.')[1]));
        var claims = jwt.RootElement.TryGetProperty("permissions", out var claim)
            ? claim.ValueKind == JsonValueKind.String ? [claim.GetString()!] : Strings(claim)
            : Array.Empty<string>();
        claims.OrderBy(p => p, StringComparer.Ordinal).Should().Equal(sorted);

        using var response = await Send(HttpMethod.Get, "/api/v1/auth/me", Token(body));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.NoStore.Should().BeTrue();
        var me = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertUser(me, id, roles, sorted, mustChange);
        me.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["id", "email", "role", "roles", "mustChangePassword", "permissions", "profile"]);
        var profile = me.GetProperty("profile");
        profile.EnumerateObject().Should().HaveCount(19);
        profile.GetProperty("firstName").GetString().Should().Be("Permission");
        profile.GetProperty("lastName").GetString().Should().Be("Reader");
        profile.GetProperty("isMailingAddressSameAsPermanentAddress").GetBoolean().Should().BeTrue();
    }

    private static void AssertUser(JsonElement user, string id, string[] roles, string[] expected, bool mustChange)
    {
        user.GetProperty("id").GetString().Should().Be(id);
        user.GetProperty("email").GetString().Should().Be(Email);
        Strings(user.GetProperty("roles")).Should().BeEquivalentTo(roles);
        user.GetProperty("role").GetString().Should().Be(Strings(user.GetProperty("roles")).FirstOrDefault() ?? "Member");
        user.GetProperty("mustChangePassword").GetBoolean().Should().Be(mustChange);
        user.GetProperty("permissions").ValueKind.Should().Be(JsonValueKind.Array);
        Strings(user.GetProperty("permissions")).Should().Equal(expected);
    }

    private async Task<string> CreateUser(string[] roles)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>();
        var user = new AuthUserEntity
        {
            UserName = Email,
            Email = Email,
            EmailConfirmed = true,
            Status = "active",
            MustChangePassword = true,
            Profile = new MemberProfileEntity { FirstName = "Permission", LastName = "Reader", IsMailingAddressSameAsPermanentAddress = true }
        };
        (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();
        if (roles.Length > 0)
        {
            (await users.AddToRolesAsync(user, roles)).Succeeded.Should().BeTrue();
        }
        return user.Id;
    }

    private async Task<JsonElement> Login() => await OkJson(await Send(HttpMethod.Post, "/api/v1/auth/login", body: new { email = Email, password = Password }));

    private async Task<string> AdminToken() => Token(await OkJson(await Send(HttpMethod.Post, "/api/v1/auth/login", body: new { email = "admin@site.org", password = "admin123" })));

    private static string Token(JsonElement body) => body.GetProperty("access_token").GetString()!;
    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(p => p.GetString()!).ToArray();

    private static async Task<JsonElement> OkJson(HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await _client.SendAsync(request);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
