using System.Net;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using static KJWebsite.IntegrationTests.PrivacyFlows;

namespace KJWebsite.IntegrationTests;

public sealed class PrivacySessionEndToEndTests(PrivacySessionStack stack) : IClassFixture<PrivacySessionStack>, IDisposable
{
    private const string Finance = "/api/v1/admin/finance/donors";
    private const string Membership = "/api/v1/admin/membership/members";
    private const string Content = "/api/v1/admin/content/projects";
    private static readonly string[] DonorRoutes = ["/api/v1/donors/me", "/api/v1/donors/me/donations", "/api/v1/donors/me/students"];
    private static readonly string[] StaffPermissions = [Permissions.FinanceView, Permissions.MemberProfileView, Permissions.ContentView];

    [Fact]
    public async Task SessionStaffDemotionEndsEveryDeviceAcrossThreeServices()
    {
        var staff = await stack.NewSuperAdminAsync();
        var high = await Role(staff.Token, StaffPermissions);
        var low = await Role(staff.Token, [Permissions.ContentView]);
        var user = await stack.NewUserAsync(high);
        var devices = new[] { await Login(user.Email, user.Password), await Login(user.Email, user.Password) };
        foreach (var device in devices) await Gets([Finance, Membership, Content], device.Access, HttpStatusCode.OK, "before demotion");
        await Request(HttpMethod.Put, $"/api/v1/admin/auth/users/{user.UserId}/roles", staff.Token, new { roles = new[] { low } });
        using (new AssertionScope())
            foreach (var device in devices)
            {
                await Gets([Finance, Membership, Content], device.Access, HttpStatusCode.Unauthorized, "demotion ends every device");
                await RejectRefresh(device);
            }
        var fresh = await Login(user.Email, user.Password);
        Claims(fresh, low, [Permissions.ContentView]);
        await Gets([Content], fresh.Access, HttpStatusCode.OK, "new role allows content");
        await Gets([Finance, Membership], fresh.Access, HttpStatusCode.Forbidden, "new role denies finance and membership");
    }

    [Fact]
    public async Task SessionStaffPromotionRequiresFreshLoginForNewAccess()
    {
        var staff = await stack.NewSuperAdminAsync();
        var low = await Role(staff.Token, [Permissions.ContentView]);
        var high = await Role(staff.Token, StaffPermissions);
        var user = await stack.NewUserAsync(low);
        var old = await Login(user.Email, user.Password);
        await Gets([Finance], old.Access, HttpStatusCode.Forbidden, "before promotion");
        await Gets([Content], old.Access, HttpStatusCode.OK, "before promotion");
        await Request(HttpMethod.Put, $"/api/v1/admin/auth/users/{user.UserId}/roles", staff.Token, new { roles = new[] { high } });
        await Gets([Finance, Membership, Content], old.Access, HttpStatusCode.Unauthorized, "promotion ends old session");
        await RejectRefresh(old);
        var fresh = await Login(user.Email, user.Password);
        Claims(fresh, high, StaffPermissions);
        await Gets([Finance, Membership, Content], fresh.Access, HttpStatusCode.OK, "fresh promoted session");
    }

    [Fact]
    public async Task SessionRolePermissionEditEndsBothUsersSessions()
    {
        var staff = await stack.NewSuperAdminAsync();
        var role = await Role(staff.Token, StaffPermissions);
        var first = await stack.NewUserAsync(role);
        var second = await stack.NewUserAsync(role);
        var users = new[] { first, second };
        var sessions = new List<Session>();
        foreach (var user in users)
        {
            var session = await Login(user.Email, user.Password);
            sessions.Add(session);
            await Gets([Finance, Membership, Content], session.Access, HttpStatusCode.OK, "before role permission edit");
        }
        await Request(HttpMethod.Put, $"/api/v1/admin/auth/roles/{role}/permissions", staff.Token, new { permissions = new[] { Permissions.ContentView } });
        using (new AssertionScope())
            foreach (var session in sessions)
            {
                await Gets([Finance, Membership, Content], session.Access, HttpStatusCode.Unauthorized, "role permission edit affects both users");
                await RejectRefresh(session);
            }
        foreach (var user in users)
        {
            var fresh = await Login(user.Email, user.Password);
            Claims(fresh, role, [Permissions.ContentView]);
            await Gets([Finance], fresh.Access, HttpStatusCode.Forbidden, "edited permissions deny finance");
            await Gets([Content], fresh.Access, HttpStatusCode.OK, "edited permissions retain content");
        }
    }

    [Fact]
    public async Task SessionIdentityDisableEndsDonorAccessAcrossServices()
    {
        var staff = await stack.NewSuperAdminAsync();
        var donor = await Donor(staff.Token);
        await Request(HttpMethod.Put, $"/api/v1/admin/auth/donors/{Subject(donor.Session)}/status", staff.Token, new { status = "disabled" });
        await Gets(DonorRoutes, donor.Session.Access, HttpStatusCode.Unauthorized, "Identity disabled donor");
        await RejectRefresh(donor.Session);
        await DisabledLogin(donor.Email, donor.Password);
    }

    [Fact]
    public async Task SessionStaffPasswordResetEndsDonorAccess()
    {
        var staff = await stack.NewSuperAdminAsync();
        var donor = await Donor(staff.Token);
        await Request(HttpMethod.Put, $"/api/v1/admin/auth/donors/{Subject(donor.Session)}/password", staff.Token, new { password = stack.NewSeed("ResetPassword9") });
        await Gets(DonorRoutes, donor.Session.Access, HttpStatusCode.Unauthorized, "staff reset donor password");
        await RejectRefresh(donor.Session);
    }

    [Fact]
    public async Task SessionMemberBecomesDonorOnlyAfterSigningInAgain()
    {
        var staff = await stack.NewSuperAdminAsync();
        var email = stack.Seed("member email", stack.NewSeed("member") + "@example.org");
        var password = stack.NewSeed("MemberPassword9");
        var provisioned = await Request(HttpMethod.Post, "/api/v1/admin/auth/members", staff.Token,
            new { applicationId = stack.NewSeed("application"), email, firstName = stack.NewSeed("first"), lastName = stack.NewSeed("last") }, HttpStatusCode.Created);
        var setup = stack.Seed("member setup token", provisioned.GetProperty("setupToken").GetString()!);
        await Request(HttpMethod.Post, "/api/v1/auth/reset-password", body: new { email, token = setup, newPassword = password }, expected: HttpStatusCode.NoContent);
        var old = await Login(email, password);
        await Gets(["/api/v1/auth/sessions/current"], old.Access, HttpStatusCode.NoContent, "live member session");
        await Gets([DonorRoutes[0]], old.Access, HttpStatusCode.Forbidden, "member has no donor access");
        var created = await Request(HttpMethod.Post, Finance, staff.Token, new { name = stack.NewSeed("donor"), email }, HttpStatusCode.Created);
        var id = created.GetProperty("donor").GetProperty("id").GetString();
        await Request(HttpMethod.Post, $"{Finance}/{id}/login", staff.Token, new { password = stack.NewSeed("UnusedPassword9") });
        await Gets(["/api/v1/auth/sessions/current", DonorRoutes[0]], old.Access, HttpStatusCode.Unauthorized, "Donor role grant ends member session");
        await RejectRefresh(old);
        var fresh = await Login(email, password);
        Roles(fresh).Should().Contain(KjRoles.Donor);
        await Gets([DonorRoutes[0]], fresh.Access, HttpStatusCode.OK, "fresh donor session");
    }

    [Fact]
    public async Task SessionFinanceDisableEndsDonorAccess()
    {
        var staff = await stack.NewSuperAdminAsync();
        var donor = await Donor(staff.Token);
        await Request(HttpMethod.Put, $"{Finance}/{donor.Id}/login/status", staff.Token, new { status = "disabled" });
        await Gets(DonorRoutes, donor.Session.Access, HttpStatusCode.Unauthorized, "Finance disable: revoked session is checked first");
        await RejectRefresh(donor.Session);
        await DisabledLogin(donor.Email, donor.Password);
    }

    [Fact]
    public async Task SessionIdentityDownFailsClosedAndRecoversWithSameTokens()
    {
        var staff = await stack.NewSuperAdminAsync();
        var donor = await Donor(staff.Token);
        await Gets([Finance], staff.Token, HttpStatusCode.OK, "before Identity outage");
        try
        {
            stack.IdentityUnavailable = true;
            foreach (var (path, token) in new[] { (Finance, staff.Token), (DonorRoutes[0], donor.Session.Access) })
            {
                var result = await Request(HttpMethod.Get, path, token, expected: HttpStatusCode.ServiceUnavailable, @case: "Identity unavailable");
                result.GetProperty("error").GetProperty("code").GetString().Should().Be("AUTH_UNAVAILABLE", $"GET {path} (Identity unavailable)");
            }
            await Gets(["/api/v1/content/bootstrap"], null, HttpStatusCode.OK, "public route during Identity outage");
            await Gets(["/api/v1/content/bootstrap"], staff.Token, HttpStatusCode.OK, "anonymous endpoint with live bearer during Identity outage");
        }
        finally { stack.IdentityUnavailable = false; }
        await Gets([Finance], staff.Token, HttpStatusCode.OK, "same token after Identity recovery");
        await Gets(DonorRoutes, donor.Session.Access, HttpStatusCode.OK, "same donor token after Identity recovery");
    }

    [Fact]
    public async Task SessionLogoutAllEndsEveryDeviceOnFinance()
    {
        var staff = await stack.NewSuperAdminAsync();
        var sessions = new[] { await Login(staff.Email, staff.Password), await Login(staff.Email, staff.Password) };
        foreach (var session in sessions) await Gets([Finance], session.Access, HttpStatusCode.OK, "before logout-all");
        await Request(HttpMethod.Post, "/api/v1/auth/logout-all", sessions[0].Access, expected: HttpStatusCode.NoContent);
        foreach (var session in sessions)
        {
            await Gets([Finance], session.Access, HttpStatusCode.Unauthorized, "logout-all");
            await RejectRefresh(session);
        }
    }

    [Fact]
    public async Task SessionSingleLogoutEndsThatSessionOnFinance()
    {
        var staff = await stack.NewSuperAdminAsync();
        var session = await Login(staff.Email, staff.Password);
        await Gets([Finance], session.Access, HttpStatusCode.OK, "before single logout");
        await Request(HttpMethod.Post, "/api/v1/auth/logout", body: new { refreshToken = session.Refresh }, expected: HttpStatusCode.NoContent);
        await Gets([Finance], session.Access, HttpStatusCode.Unauthorized, "single logout with refresh token");
        await RejectRefresh(session);
        await Gets([Finance], staff.Token, HttpStatusCode.OK, "other device remains active after single logout");
    }

    [Fact]
    public async Task SessionLogHygieneWatchesIssuedAccessAndRefreshTokens()
    {
        var staff = await stack.NewSuperAdminAsync();
        var session = await Login(staff.Email, staff.Password);
        await Gets([Finance], session.Access, HttpStatusCode.OK, "log hygiene bearer request");
        var refreshed = Watch(await Request(HttpMethod.Post, "/api/v1/auth/refresh", body: new { refreshToken = session.Refresh }));
        await Gets([Finance], refreshed.Access, HttpStatusCode.OK, "log hygiene refreshed bearer request");
        await Request(HttpMethod.Post, "/api/v1/auth/logout", body: new { refreshToken = refreshed.Refresh }, expected: HttpStatusCode.NoContent);
        GlobalLogSink.Default.AssertNone();
    }

    private async Task<string> Role(string token, string[] permissions)
    {
        // Role names appear in the RBAC audit log and contain no personal data.
        var name = "Role" + stack.NewSeed("role")[^32..];
        await Request(HttpMethod.Post, "/api/v1/admin/auth/roles", token, new { name, permissions }, HttpStatusCode.Created);
        return name;
    }

    private async Task<(string Id, string Email, string Password, Session Session)> Donor(string staff)
    {
        var donor = await CreateDonor(stack, staff);
        await LinkStudent(stack, staff, donor.Id);
        var initial = await Login(donor.Email, donor.Password);
        var password = stack.NewSeed("ChosenPassword9");
        var changed = await Request(HttpMethod.Post, "/api/v1/auth/change-password", initial.Access,
            new { currentPassword = donor.Password, newPassword = password });
        Watch(changed);
        var session = await Login(donor.Email, password);
        await Gets(DonorRoutes, session.Access, HttpStatusCode.OK, "donor baseline after password change and student link");
        return (donor.Id, donor.Email, password, session);
    }

    private async Task<Session> Login(string email, string password) => Watch(await Request(HttpMethod.Post, "/api/v1/auth/login", body: new { email, password }));

    private Session Watch(JsonElement session) => new(
        stack.Seed("access token", session.GetProperty("access_token").GetString()!),
        stack.Seed("refresh token", session.GetProperty("refresh_token").GetString()!));

    private Task<JsonElement> RejectRefresh(Session session) => Request(HttpMethod.Post, "/api/v1/auth/refresh",
        body: new { refreshToken = session.Refresh }, expected: HttpStatusCode.Unauthorized, @case: "revoked refresh token");

    private async Task DisabledLogin(string email, string password)
    {
        var body = await Request(HttpMethod.Post, "/api/v1/auth/login", body: new { email, password }, expected: HttpStatusCode.Forbidden, @case: "disabled account");
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("ACCOUNT_DISABLED", "POST /api/v1/auth/login (disabled account)");
    }

    private async Task Gets(string[] paths, string? token, HttpStatusCode expected, string @case)
    {
        using var scope = new AssertionScope();
        foreach (var path in paths) await Request(HttpMethod.Get, path, token, expected: expected, @case: @case);
    }

    private async Task<JsonElement> Request(HttpMethod method, string path, string? token = null, object? body = null,
        HttpStatusCode expected = HttpStatusCode.OK, string @case = "setup")
    {
        using var response = await Send(stack, method, path, token, body);
        AssertStatus(response, method, path, expected, @case);
        var text = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static void AssertStatus(HttpResponseMessage response, HttpMethod method, string path, HttpStatusCode expected, string @case) =>
        AssertionChain.GetOrCreate().ForCondition(response.StatusCode == expected)
            .FailWith($"{method} {path} expected {(int)expected} but was {(int)response.StatusCode} ({@case})");

    private static string Subject(Session session) => new JsonWebToken(session.Access).Subject;
    private static string[] Roles(Session session) => new JsonWebToken(session.Access).Claims.Where(c => c.Type == KjClaims.Role).Select(c => c.Value).ToArray();

    private static void Claims(Session session, string role, string[] permissions)
    {
        Roles(session).Should().BeEquivalentTo([role], "POST /api/v1/auth/login must issue the new roles");
        new JsonWebToken(session.Access).Claims.Where(c => c.Type == KjClaims.Permission).Select(c => c.Value)
            .Should().BeEquivalentTo(permissions, "POST /api/v1/auth/login must issue the new permissions");
    }

    public void Dispose() => GlobalLogSink.Default.AssertNone();

    private sealed record Session(string Access, string Refresh);
}
