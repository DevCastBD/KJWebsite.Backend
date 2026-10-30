using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthIdentityService.Data.Entities;
using AuthIdentityService.Identity;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class RoleManagementTests : IDisposable
{
    private const string Base = "/api/v1/admin/auth";
    private const string Password = "Correct-Horse-9!battery";
    private readonly AuthApiFactory _factory = new();
    private readonly HttpClient _client;

    public RoleManagementTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task ThePermissionCatalogIsGroupedByModule()
    {
        using var response = await Send(HttpMethod.Get, $"{Base}/permissions", await SuperAdminToken());

        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("key").GetString()).Should().BeEquivalentTo(Permissions.All);
        items.Select(i => i.GetProperty("module").GetString()).Should().Contain(["content", "media", "finance", "audit"]);
    }

    [Fact]
    public async Task ACustomRoleCanBeCreatedChangedAndDeleted()
    {
        var token = await SuperAdminToken();

        using var created = await Send(HttpMethod.Post, $"{Base}/roles", token, new { name = "Translators", permissions = new[] { Permissions.ContentView, Permissions.ContentUpdate } });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Role("Translators", token)).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().Equal(Permissions.ContentUpdate, Permissions.ContentView);

        using var changed = await Send(HttpMethod.Put, $"{Base}/roles/Translators/permissions", token, new { permissions = new[] { Permissions.ContentView } });
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Role("Translators", token)).GetProperty("permissions").GetArrayLength().Should().Be(1);

        (await Send(HttpMethod.Delete, $"{Base}/roles/Translators", token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Get, $"{Base}/roles/Translators", token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RoleValidationRejectsBadInput()
    {
        var token = await SuperAdminToken();

        (await Send(HttpMethod.Post, $"{Base}/roles", token, new { name = "1bad", permissions = Array.Empty<string>() })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Base}/roles", token, new { name = "Fine", permissions = new[] { "made.up" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Base}/roles", token, new { name = "moderator", permissions = Array.Empty<string>() })).StatusCode.Should().Be(HttpStatusCode.Conflict, "role names are case-insensitive");
        (await Send(HttpMethod.Put, $"{Base}/roles/Ghost/permissions", token, new { permissions = Array.Empty<string>() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SuperAdminAndBuiltInRolesAreProtected()
    {
        var token = await SuperAdminToken();

        await AssertProblem(await Send(HttpMethod.Delete, $"{Base}/roles/SuperAdmin", token), HttpStatusCode.Conflict, "SUPERADMIN_PROTECTED");
        await AssertProblem(await Send(HttpMethod.Put, $"{Base}/roles/SuperAdmin/permissions", token, new { permissions = new[] { Permissions.ContentView } }), HttpStatusCode.Conflict, "SUPERADMIN_PROTECTED");
        await AssertProblem(await Send(HttpMethod.Delete, $"{Base}/roles/Moderator", token), HttpStatusCode.Conflict, "SYSTEM_ROLE");
    }

    [Fact]
    public async Task ARoleWithMembersCannotBeDeleted()
    {
        var token = await SuperAdminToken();
        await Send(HttpMethod.Post, $"{Base}/roles", token, new { name = "Temps", permissions = Array.Empty<string>() });
        var user = await CreateUser("temp@example.org");
        (await Send(HttpMethod.Put, $"{Base}/users/{user}/roles", token, new { roles = new[] { "Temps" } })).StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertProblem(await Send(HttpMethod.Delete, $"{Base}/roles/Temps", token), HttpStatusCode.Conflict, "ROLE_IN_USE");
        (await Role("Temps", token)).GetProperty("memberCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task TheLastActiveSuperAdminCannotBeRemoved()
    {
        var token = await SuperAdminToken();

        await AssertProblem(await Send(HttpMethod.Put, $"{Base}/users/usr_admin/roles", token, new { roles = Array.Empty<string>() }), HttpStatusCode.Conflict, "LAST_SUPERADMIN");
        await AssertProblem(await Send(HttpMethod.Put, $"{Base}/users/usr_admin/roles", token, new { roles = new[] { "Admin" } }), HttpStatusCode.Conflict, "LAST_SUPERADMIN");

        var second = await CreateUser("second@example.org");
        (await Send(HttpMethod.Put, $"{Base}/users/{second}/roles", token, new { roles = new[] { KjRoles.SuperAdmin } })).StatusCode.Should().Be(HttpStatusCode.OK);
        var secondToken = await Login("second@example.org", Password);

        // With two, either can step down — but not both.
        (await Send(HttpMethod.Put, $"{Base}/users/usr_admin/roles", secondToken, new { roles = new[] { "Admin" } })).StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertProblem(await Send(HttpMethod.Put, $"{Base}/users/{second}/roles", secondToken, new { roles = Array.Empty<string>() }), HttpStatusCode.Conflict, "LAST_SUPERADMIN");
    }

    [Fact]
    public async Task ADisabledSuperAdminDoesNotCountTowardsTheLastOne()
    {
        var token = await SuperAdminToken();
        var second = await CreateUser("dormant@example.org", [KjRoles.SuperAdmin], status: "disabled");

        await AssertProblem(await Send(HttpMethod.Put, $"{Base}/users/usr_admin/roles", token, new { roles = Array.Empty<string>() }), HttpStatusCode.Conflict, "LAST_SUPERADMIN");
        second.Should().NotBeNull();
    }

    [Fact]
    public async Task OnlyASuperAdminCanGrantSuperAdmin()
    {
        var adminId = await CreateUser("admin2@example.org", [KjRoles.Admin]);
        var target = await CreateUser("target@example.org");
        var admin = await Login("admin2@example.org", Password);

        (await Send(HttpMethod.Put, $"{Base}/users/{target}/roles", admin, new { roles = new[] { KjRoles.SuperAdmin } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        adminId.Should().NotBeNull();
    }

    [Fact]
    public async Task NobodyCanGrantAPermissionTheyDoNotHold()
    {
        var superAdmin = await SuperAdminToken();
        await Send(HttpMethod.Post, $"{Base}/roles", superAdmin, new { name = "RoleKeepers", permissions = new[] { Permissions.RolesManage } });
        await CreateUser("admin3@example.org", [KjRoles.Admin]);
        var target = await CreateUser("target2@example.org");
        var admin = await Login("admin3@example.org", Password);

        // Admin holds users.manage but not roles.manage, so it cannot hand the role out.
        await AssertProblem(await Send(HttpMethod.Put, $"{Base}/users/{target}/roles", admin, new { roles = new[] { "RoleKeepers" } }), HttpStatusCode.Forbidden, "FORBIDDEN");
        (await Send(HttpMethod.Put, $"{Base}/users/{target}/roles", admin, new { roles = new[] { KjRoles.FinanceOfficer } })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task EndpointsNeedTheMatchingPermission()
    {
        await CreateUser("admin4@example.org", [KjRoles.Admin]);
        await CreateUser("officer@example.org", [KjRoles.MembershipOfficer]);
        await CreateUser("editor@example.org", [KjRoles.ContentEditor]);

        (await _client.GetAsync($"{Base}/roles")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Get, $"{Base}/roles", await Login("admin4@example.org", Password))).StatusCode.Should().Be(HttpStatusCode.Forbidden, "Admin lacks roles.manage");
        var officer = await Login("officer@example.org", Password);
        (await Send(HttpMethod.Get, $"{Base}/users", officer)).StatusCode.Should().Be(HttpStatusCode.OK, "users.view");
        (await Send(HttpMethod.Put, $"{Base}/users/usr_admin/roles", officer, new { roles = Array.Empty<string>() })).StatusCode.Should().Be(HttpStatusCode.Forbidden, "needs users.manage");
        (await Send(HttpMethod.Get, $"{Base}/users", await Login("editor@example.org", Password))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ChangingARolesEndsTheUsersSessions()
    {
        var superAdmin = await SuperAdminToken();
        var user = await CreateUser("session@example.org", [KjRoles.ContentEditor]);
        var userToken = await Login("session@example.org", Password);
        (await Send(HttpMethod.Get, "/api/v1/auth/sessions", userToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        await Send(HttpMethod.Put, $"{Base}/users/{user}/roles", superAdmin, new { roles = Array.Empty<string>() });

        (await Send(HttpMethod.Get, "/api/v1/auth/sessions", userToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the stale token must not keep its permissions");
    }

    [Fact]
    public async Task ChangingARolesPermissionsEndsItsMembersSessions()
    {
        var superAdmin = await SuperAdminToken();
        await CreateUser("mod@example.org", [KjRoles.Moderator]);
        var modToken = await Login("mod@example.org", Password);

        (await Send(HttpMethod.Put, $"{Base}/roles/Moderator/permissions", superAdmin, new { permissions = new[] { Permissions.ContentView } })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Send(HttpMethod.Get, "/api/v1/auth/sessions", modToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var fresh = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(await Login("mod@example.org", Password));
        fresh.Claims.Where(c => c.Type == "permissions").Select(c => c.Value).Should().Equal(Permissions.ContentView);
    }

    [Fact]
    public async Task UsersAreListedWithTheirRolesAndCanBeFilteredByRole()
    {
        var token = await SuperAdminToken();
        await CreateUser("listed@example.org", [KjRoles.Volunteer]);

        using var all = await Send(HttpMethod.Get, $"{Base}/users?limit=100", token);
        var items = (await all.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        items.Single(u => u.GetProperty("email").GetString() == "admin@site.org").GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal(KjRoles.SuperAdmin);

        using var filtered = await Send(HttpMethod.Get, $"{Base}/users?role=volunteer", token);
        (await filtered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().Select(u => u.GetProperty("email").GetString()).Should().Equal("listed@example.org");
        (await Send(HttpMethod.Get, $"{Base}/users?limit=0", token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Put, $"{Base}/users/ghost/roles", token, new { roles = Array.Empty<string>() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Put, $"{Base}/users/usr_admin/roles", token, new { roles = new[] { "Ghost" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ARestartDoesNotUndoRoleEditsButAlwaysRestoresSuperAdmin()
    {
        var token = await SuperAdminToken();
        await Send(HttpMethod.Put, $"{Base}/roles/Moderator/permissions", token, new { permissions = new[] { Permissions.ContentView } });

        using (var scope = _factory.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<AuthRoleEntity>>();
            var superAdmin = (await roles.FindByNameAsync(KjRoles.SuperAdmin))!;
            var claim = (await roles.GetClaimsAsync(superAdmin)).First(c => c.Value == Permissions.AuditView);
            await roles.RemoveClaimAsync(superAdmin, claim);

            await AuthSeeder.SeedRolesAsync(roles);

            (await roles.GetClaimsAsync(superAdmin)).Select(c => c.Value).Should().Contain(Permissions.AuditView);
        }

        (await Role("Moderator", token)).GetProperty("permissions").GetArrayLength().Should().Be(1, "the edit survives the reseed");
    }

    [Fact]
    public async Task RoleAndAssignmentChangesAreWrittenToTheAuditLog()
    {
        var token = await SuperAdminToken();
        await Send(HttpMethod.Post, $"{Base}/roles", token, new { name = "Audited", permissions = new[] { Permissions.ContentView } });
        var user = await CreateUser("audited@example.org");
        await Send(HttpMethod.Put, $"{Base}/users/{user}/roles", token, new { roles = new[] { "Audited" } });

        using var assignments = await Send(HttpMethod.Get, $"{Base}/audit?entity=IdentityUserRole&action=create", token);
        var item = (await assignments.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        item.GetProperty("actorId").GetString().Should().Be("usr_admin");
        item.GetProperty("after").GetProperty("userId").GetString().Should().Be(user);
        using var claims = await Send(HttpMethod.Get, $"{Base}/audit?entity=IdentityRoleClaim", token);
        (await claims.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
        (await Send(HttpMethod.Get, $"{Base}/audit", await Login("audited@example.org", Password))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<JsonElement> Role(string name, string token)
    {
        using var response = await Send(HttpMethod.Get, $"{Base}/roles/{name}", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            response.StatusCode.Should().Be(status);
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString().Should().Be(code);
        }
    }

    private Task<string> SuperAdminToken() => Login("admin@site.org", "admin123");

    private async Task<string> Login(string email, string password)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, email);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }

    private async Task<string> CreateUser(string email, string[]? roleNames = null, string status = "active")
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>();
        var user = new AuthUserEntity { UserName = email, Email = email, EmailConfirmed = true, Status = status, CreatedAt = DateTimeOffset.UtcNow };
        (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();
        if (roleNames is { Length: > 0 })
        {
            await users.AddToRolesAsync(user, roleNames);
        }

        return user.Id;
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
