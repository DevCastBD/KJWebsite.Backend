using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AuthIdentityService.Data.Entities;
using AuthIdentityService.Identity;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace KJWebsite.IntegrationTests;

public sealed class AuthorizationTests : IDisposable
{
    private const string Projects = "/api/v1/admin/content/projects";
    private readonly ContentApiFactory _content = new();
    private readonly HttpClient _client;

    public AuthorizationTests() => _client = _content.CreateClient();

    [Fact]
    public async Task AdminEndpointsRejectAnonymousCallers()
    {
        using var response = await _client.GetAsync(Projects);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString().Should().Be("UNAUTHORIZED");
    }

    [Fact]
    public async Task TheFormerSharedSecretNoLongerWorks()
    {
        using var response = await SendAsync(HttpMethod.Get, Projects, "dev-admin-token");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ATokenWithoutThePermissionIsForbidden()
    {
        using var noPermissions = await SendAsync(HttpMethod.Get, Projects, TestTokens.Issue([]));
        noPermissions.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await noPermissions.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString().Should().Be("FORBIDDEN");

        // content.view can read but not write: permissions are per action, not per role.
        var viewer = TestTokens.Issue([Permissions.ContentView]);
        (await SendAsync(HttpMethod.Get, Projects, viewer)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Delete, $"{Projects}/pathshala", viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, Projects, viewer, new { slug = "x", translations = new[] { new { lang = "en", title = "x" } } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task EachWriteNeedsItsOwnPermission()
    {
        var deleter = TestTokens.Issue([Permissions.ContentDelete]);
        (await SendAsync(HttpMethod.Delete, $"{Projects}/pathshala", deleter)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Get, Projects, deleter)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TokensFromAnotherKeyOrExpiredAreRejected()
    {
        var forged = TestTokens.Issue([Permissions.ContentView], TestJwtKeys.Other);
        (await SendAsync(HttpMethod.Get, Projects, forged)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var expired = TestTokens.Issue([Permissions.ContentView], lifetime: TimeSpan.FromMinutes(-5));
        (await SendAsync(HttpMethod.Get, Projects, expired)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PublicReadsStayOpen()
    {
        (await _client.GetAsync("/api/v1/content/projects")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync("/api/v1/content/bootstrap")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ATokenIssuedByTheAuthServiceWorksOnTheContentService()
    {
        using var auth = new AuthApiFactory();
        using var authClient = auth.CreateClient();
        using var login = await authClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "admin@site.org", password = "admin123" });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;

        var jwt = new JsonWebToken(token);
        jwt.Claims.Where(c => c.Type == "role").Select(c => c.Value).Should().Contain(KjRoles.SuperAdmin);
        jwt.Claims.Where(c => c.Type == "permissions").Select(c => c.Value).Should().BeEquivalentTo(Permissions.All);
        (await SendAsync(HttpMethod.Get, Projects, token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RolesAndPermissionsAreSeeded()
    {
        using var auth = new AuthApiFactory();
        using var client = auth.CreateClient();
        using var scope = auth.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<AuthRoleEntity>>();

        foreach (var (name, expected) in KjRoles.Seed)
        {
            var role = await roles.FindByNameAsync(name);
            role.Should().NotBeNull($"role {name} is seeded");
            (await roles.GetClaimsAsync(role!)).Where(c => c.Type == KjClaims.Permission).Select(c => c.Value).Should().BeEquivalentTo(expected, name);
        }

        // Segregation of duties: nobody who approves an expense can also submit it by role alone.
        KjRoles.Seed[KjRoles.FinanceOfficer].Should().Contain(Permissions.FinanceExpenseApprove).And.NotContain(Permissions.FinanceExpenseSubmit);
    }

    [Fact]
    public async Task EveryEndpointDeclaresAPolicyOrIsExplicitlyAnonymous()
    {
        await using var auth = new AuthApiFactory();
        await using var cta = new CtaApiFactory();
        foreach (var endpoint in AllEndpoints(_content).Concat(AllEndpoints(auth)).Concat(AllEndpoints(cta)))
        {
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any();
            (anonymous || authorize).Should().BeTrue($"{endpoint.DisplayName} must opt in to a policy or to anonymous access");

            if (endpoint is RouteEndpoint { RoutePattern.RawText: { } route } && route.StartsWith("/api/v1/admin/", StringComparison.Ordinal))
            {
                anonymous.Should().BeFalse($"{route} is an admin endpoint");
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).Should().Contain(p => p != null && Permissions.All.Contains(p), route);
            }
        }
    }

    [Fact]
    public async Task AnEndpointWithoutAPolicyIsDeniedEvenForAnAuthenticatedSuperAdmin()
    {
        var policies = _content.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var fallback = await policies.GetFallbackPolicyAsync();
        fallback.Should().NotBeNull();

        var superAdmin = new ClaimsPrincipal(new ClaimsIdentity(Permissions.All.Select(p => new Claim(KjClaims.Permission, p)), "test"));
        var result = await _content.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(superAdmin, fallback!);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public void NoHardCodedAdminCredentialRemainsInSource()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KJWebsite.Backend.slnx")))
        {
            root = root.Parent;
        }

        root.Should().NotBeNull();
        var offenders = Directory.EnumerateFiles(Path.Combine(root!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("dev-admin-token", StringComparison.Ordinal))
            .ToList();

        offenders.Should().BeEmpty();
    }

    public void Dispose()
    {
        _client.Dispose();
        _content.Dispose();
    }

    private static List<Endpoint> AllEndpoints<T>(WebApplicationFactory<T> factory) where T : class
    {
        using var client = factory.CreateClient();
        return factory.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).ToList();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }
}
