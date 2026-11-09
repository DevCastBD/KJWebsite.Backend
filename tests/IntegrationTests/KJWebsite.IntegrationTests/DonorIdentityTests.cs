using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AuthIdentityService.Data.Entities;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

/// <summary>
/// Donor sign-in accounts (BE-103, #210): staff choose the first password and the donor must change it before anything else works.
/// High blast radius (auth, permissions, tokens), so the failure paths carry most of the tests.
/// </summary>
public sealed class DonorIdentityTests : IDisposable
{
    private const string Provision = "/api/v1/admin/auth/donors";
    private const string Initial = "Staff-Chosen-Pass-1";
    private const string Own = "My-Own-Strong-Pass-2";
    private readonly AuthApiFactory _auth = new();
    private readonly HttpClient _client;

    public DonorIdentityTests() => _client = _auth.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _auth.Dispose();
    }

    // ---------------------------------------------------------------- permissions and roles

    [Fact]
    public void TheDonorRoleHoldsNoPermissionAndOnlySuperAdminMayVoidALink()
    {
        KjRoles.Seed[KjRoles.Donor].Should().BeEmpty();
        KjRoles.Seed[KjRoles.Admin].Should().Contain(
            [Permissions.SponsorshipView, Permissions.SponsorshipStudentManage, Permissions.SponsorshipLinkManage, Permissions.FinanceDonorProvision]);
        KjRoles.Seed[KjRoles.Admin].Should().NotContain(Permissions.SponsorshipLinkVoid);
        KjRoles.Seed[KjRoles.SuperAdmin].Should().Contain(Permissions.SponsorshipLinkVoid);
        foreach (var (role, permissions) in KjRoles.Seed.Where(r => r.Key is not (KjRoles.SuperAdmin or KjRoles.Admin)))
        {
            permissions.Should().NotContain(Permissions.SponsorshipLinkVoid, role);
        }
    }

    [Fact]
    public async Task TheDonorRoleIsSeededWithNoPermissionClaims()
    {
        using var scope = _auth.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<AuthRoleEntity>>();
        var donor = await roles.FindByNameAsync(KjRoles.Donor);
        donor.Should().NotBeNull();
        (await roles.GetClaimsAsync(donor!)).Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyAHolderOfTheProvisionPermissionMayCreateOrChangeDonorAccounts()
    {
        var volunteer = await Token("vol@example.org", KjRoles.Volunteer);
        var body = new { donorId = "d1", email = "d1@example.org", password = Initial };

        (await Send(HttpMethod.Post, Provision, null, body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Post, Provision, volunteer, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Put, $"{Provision}/usr_x/password", volunteer, new { password = Own })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Put, $"{Provision}/usr_x/status", volunteer, new { status = "disabled" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ADonorTokenIsRefusedByEveryStaffEndpoint()
    {
        var admin = await Token("admin1@example.org", KjRoles.Admin);
        await ProvisionDonor(admin, "d2", "d2@example.org");
        var donor = await SignInAndChangePassword("d2@example.org");

        (await Send(HttpMethod.Post, Provision, donor, new { donorId = "d9", email = "d9@example.org", password = Initial })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Get, "/api/v1/admin/auth/users", donor)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------------------------------------------------------------- provisioning

    [Fact]
    public async Task ProvisioningCreatesAConfirmedDonorAccountWithTheMustChangeFlagAndNeverReturnsThePassword()
    {
        var admin = await Token("admin2@example.org", KjRoles.Admin);

        using var response = await Send(HttpMethod.Post, Provision, admin, new { donorId = "d3", email = "Rahim@Example.org", password = Initial });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(Initial);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("created").GetBoolean().Should().BeTrue();
        body.GetProperty("linkedToMember").GetBoolean().Should().BeFalse();
        body.GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();

        using var scope = _auth.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>();
        var user = await users.FindByEmailAsync("rahim@example.org");
        user.Should().NotBeNull();
        user!.EmailConfirmed.Should().BeTrue();
        user.MustChangePassword.Should().BeTrue();
        user.ProvisionedFor.Should().Be("donor:d3");
        (await users.GetRolesAsync(user)).Should().Equal(KjRoles.Donor);
    }

    [Fact]
    public async Task ProvisioningTheSameDonorTwiceReturnsTheSameAccountAndKeepsTheFirstPassword()
    {
        var admin = await Token("admin3@example.org", KjRoles.Admin);
        var first = await ProvisionDonor(admin, "d4", "d4@example.org");

        using var again = await Send(HttpMethod.Post, Provision, admin, new { donorId = "d4", email = "d4@example.org", password = "Different-Pass-Xyz-3" });

        again.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(again);
        body.GetProperty("userId").GetString().Should().Be(first);
        body.GetProperty("created").GetBoolean().Should().BeFalse();
        (await Login("d4@example.org", Initial)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Login("d4@example.org", "Different-Pass-Xyz-3")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("", "d@example.org", Initial)]
    [InlineData("d5", "not-an-email", Initial)]
    [InlineData("d5", "d5@example.org", "")]
    [InlineData("d5", "d5@example.org", "short")]
    public async Task InvalidInputIsRefusedAndNothingIsCreated(string donorId, string email, string password)
    {
        var admin = await Token("admin4@example.org", KjRoles.Admin);

        using var response = await Send(HttpMethod.Post, Provision, admin, new { donorId, email, password });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(response)).Should().Be("VALIDATION_ERROR");
        (await response.Content.ReadAsStringAsync()).Should().NotContain(password.Length > 3 ? password : "\u0000");
        using var scope = _auth.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>().FindByEmailAsync(email)).Should().BeNull();
    }

    [Fact]
    public async Task ADonorEmailThatBelongsToAMemberGainsTheRoleAndKeepsItsPasswordAndSessions()
    {
        var admin = await Token("admin5@example.org", KjRoles.Admin);
        await CreateUser("member@example.org", KjRoles.Member, "Member-Own-Pass-77");
        using var memberLogin = await Login("member@example.org", "Member-Own-Pass-77");
        var memberRefresh = (await Json(memberLogin)).GetProperty("refresh_token").GetString();

        using var response = await Send(HttpMethod.Post, Provision, admin, new { donorId = "d6", email = "member@example.org", password = Initial });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("linkedToMember").GetBoolean().Should().BeTrue();
        body.GetProperty("created").GetBoolean().Should().BeFalse();
        body.GetProperty("mustChangePassword").GetBoolean().Should().BeFalse();

        using var scope = _auth.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>();
        var user = (await users.FindByEmailAsync("member@example.org"))!;
        (await users.GetRolesAsync(user)).Should().BeEquivalentTo([KjRoles.Member, KjRoles.Donor]);
        user.MustChangePassword.Should().BeFalse();

        // The member's own password still works, the staff-chosen one does not, and the earlier session survives.
        using var after = await Login("member@example.org", "Member-Own-Pass-77");
        after.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(after)).GetProperty("user").GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Contain([KjRoles.Member, KjRoles.Donor]);
        (await Login("member@example.org", Initial)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = memberRefresh })).StatusCode.Should().Be(HttpStatusCode.OK);

        // Doing it again changes nothing and does not fail.
        (await Send(HttpMethod.Post, Provision, admin, new { donorId = "d6", email = "member@example.org", password = Initial })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnEmailUsedByAStaffAccountIsRefused()
    {
        var admin = await Token("admin6@example.org", KjRoles.Admin);
        await CreateUser("editor@example.org", KjRoles.ContentEditor, Initial);

        using var response = await Send(HttpMethod.Post, Provision, admin, new { donorId = "d7", email = "editor@example.org", password = Initial });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCode(response)).Should().Be("EMAIL_TAKEN");
        using var scope = _auth.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>();
        (await users.GetRolesAsync((await users.FindByEmailAsync("editor@example.org"))!)).Should().Equal(KjRoles.ContentEditor);
    }

    // ---------------------------------------------------------------- sign-in reports the flag and the roles

    [Fact]
    public async Task SignInAndMeReportTheRolesAndTheFlagAndTheTokenCarriesTheClaim()
    {
        var admin = await Token("admin7@example.org", KjRoles.Admin);
        await ProvisionDonor(admin, "d8", "d8@example.org");

        using var login = await Login("d8@example.org", Initial);

        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(login);
        body.GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        body.GetProperty("user").GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal(KjRoles.Donor);
        var access = body.GetProperty("access_token").GetString()!;
        new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(access).Claims
            .Should().Contain(c => c.Type == KjClaims.PasswordChangeRequired && c.Value == "true");

        using var me = await Send(HttpMethod.Get, "/api/v1/auth/me", access);
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var meBody = await Json(me);
        meBody.GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        meBody.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal(KjRoles.Donor);
    }

    // ---------------------------------------------------------------- the gate

    [Fact]
    public async Task WhileTheFlagIsSetEverythingExceptChangePasswordLogoutAndMeIsRefused()
    {
        var admin = await Token("admin8@example.org", KjRoles.Admin);
        await ProvisionDonor(admin, "d9", "d9@example.org");
        var access = (await Json(await Login("d9@example.org", Initial))).GetProperty("access_token").GetString()!;

        foreach (var url in new[] { "/api/v1/auth/sessions", "/api/v1/auth/me/export" })
        {
            using var refused = await Send(HttpMethod.Get, url, access);
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
            (await ErrorCode(refused)).Should().Be("PASSWORD_CHANGE_REQUIRED", url);
        }

        (await Send(HttpMethod.Get, "/api/v1/auth/me", access)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Post, "/api/v1/auth/logout-all", access)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task AStaffTokenThatCarriesTheClaimIsRefusedWithTheSpecificCodeNotAGenericForbidden()
    {
        // A real staff account (so its session is real) that has the flag set holds users.view yet is refused with the specific code.
        var userId = await CreateUser("flagged-admin@example.org", KjRoles.Admin, Initial);
        using (var scope = _auth.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>();
            var user = (await users.FindByIdAsync(userId))!;
            user.MustChangePassword = true;
            (await users.UpdateAsync(user)).Succeeded.Should().BeTrue();
        }

        var token = (await Json(await Login("flagged-admin@example.org", Initial))).GetProperty("access_token").GetString()!;
        using var refused = await Send(HttpMethod.Get, "/api/v1/admin/auth/users", token);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(refused)).Should().Be("PASSWORD_CHANGE_REQUIRED");
    }

    [Fact]
    public async Task EveryAuthorizedEndpointOfEveryServiceCarriesTheGateExceptTheThreeThatClearIt()
    {
        await using var content = new ContentApiFactory();
        await using var cta = new CtaApiFactory();
        await using var finance = new FinanceApiFactory();
        await using var membership = new MembershipApiFactory();
        var allowed = new List<string>();

        foreach (var factory in new Func<IServiceProvider>[] { () => _auth.Services, () => content.Services, () => cta.Services, () => finance.Services, () => membership.Services })
        {
            var services = factory();
            var provider = services.GetRequiredService<IAuthorizationPolicyProvider>();
            var defaultPolicy = await provider.GetDefaultPolicyAsync();
            defaultPolicy.Requirements.Should().ContainSingle(r => r is PasswordChangeGateRequirement);

            foreach (var endpoint in services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints)
            {
                foreach (var data in endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>())
                {
                    var policy = data.Policy is null ? defaultPolicy : (await provider.GetPolicyAsync(data.Policy))!;
                    policy.Should().NotBeNull(endpoint.DisplayName);
                    if (policy.Requirements.OfType<PasswordChangeGateRequirement>().Any())
                    {
                        continue;
                    }

                    data.Policy.Should().Be(KjPolicies.AuthenticatedAllowingPasswordChange, endpoint.DisplayName);
                    allowed.Add(endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName!);
                }
            }
        }

        allowed.Should().BeEquivalentTo(["GetCurrentUser", "LogoutAll", "ChangePassword"]);
    }

    [Fact]
    public async Task AnyServiceRefusesAFlaggedTokenAtAnAdminEndpoint()
    {
        await using var content = new ContentApiFactory();
        using var client = content.CreateClient();
        var route = content.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .First(e => e.RoutePattern.RawText!.StartsWith("/api/v1/admin/", StringComparison.Ordinal)
                && !e.RoutePattern.RawText.Contains('{')
                && e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true
                && e.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Any(a => a.Policy is not null && Permissions.All.Contains(a.Policy)));
        var permission = route.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().First(a => a.Policy is not null && Permissions.All.Contains(a.Policy)).Policy!;

        using var flagged = new HttpRequestMessage(HttpMethod.Get, route.RoutePattern.RawText);
        flagged.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue([permission], extraClaims: [new Claim(KjClaims.PasswordChangeRequired, "true")]));
        using var refused = await client.SendAsync(flagged);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(refused)).Should().Be("PASSWORD_CHANGE_REQUIRED");

        using var clear = new HttpRequestMessage(HttpMethod.Get, route.RoutePattern.RawText);
        clear.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue([permission]));
        using var served = await client.SendAsync(clear);
        (served.StatusCode == HttpStatusCode.Forbidden && await ErrorCode(served) == "PASSWORD_CHANGE_REQUIRED").Should().BeFalse("the same token without the claim is not gated");
    }

    // ---------------------------------------------------------------- change-password

    [Fact]
    public async Task ChoosingAPasswordClearsTheFlagEndsOldSessionsAndReturnsAFreshUnflaggedPair()
    {
        var admin = await Token("admin9@example.org", KjRoles.Admin);
        await ProvisionDonor(admin, "d10", "d10@example.org");
        using var first = await Login("d10@example.org", Initial);
        var firstBody = await Json(first);
        var access = firstBody.GetProperty("access_token").GetString()!;
        var oldRefresh = firstBody.GetProperty("refresh_token").GetString();

        using var changed = await Send(HttpMethod.Post, "/api/v1/auth/change-password", access, new { currentPassword = Initial, newPassword = Own });

        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(changed);
        body.GetProperty("mustChangePassword").GetBoolean().Should().BeFalse();
        var fresh = body.GetProperty("access_token").GetString()!;
        new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(fresh).Claims.Should().NotContain(c => c.Type == KjClaims.PasswordChangeRequired);
        (await Send(HttpMethod.Get, "/api/v1/auth/sessions", fresh)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = oldRefresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Login("d10@example.org", Initial)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Json(await Login("d10@example.org", Own))).GetProperty("mustChangePassword").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ChangePasswordRefusesAWrongCurrentPasswordTheSamePasswordAWeakOneAndNoSignIn()
    {
        var admin = await Token("admin10@example.org", KjRoles.Admin);
        await ProvisionDonor(admin, "d11", "d11@example.org");
        var access = (await Json(await Login("d11@example.org", Initial))).GetProperty("access_token").GetString()!;
        const string url = "/api/v1/auth/change-password";

        (await Send(HttpMethod.Post, url, null, new { currentPassword = Initial, newPassword = Own })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Post, url, access, new { currentPassword = "", newPassword = Own })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, url, access, new { currentPassword = "Not-The-Password-1", newPassword = Own })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var same = await Send(HttpMethod.Post, url, access, new { currentPassword = Initial, newPassword = Initial });
        same.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await same.Content.ReadAsStringAsync()).Should().Contain("newPassword");
        using var weak = await Send(HttpMethod.Post, url, access, new { currentPassword = Initial, newPassword = "short" });
        weak.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Nothing changed: the staff-chosen password still signs in, and the flag is still set.
        (await Json(await Login("d11@example.org", Initial))).GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task WrongCurrentPasswordsCountTowardTheLockoutSoAStolenTokenCannotGuess()
    {
        var admin = await Token("admin11@example.org", KjRoles.Admin);
        await ProvisionDonor(admin, "d12", "d12@example.org");
        var access = (await Json(await Login("d12@example.org", Initial))).GetProperty("access_token").GetString()!;

        HttpStatusCode last = default;
        for (var i = 0; i < 6; i++)
        {
            using var attempt = await Send(HttpMethod.Post, "/api/v1/auth/change-password", access, new { currentPassword = $"Wrong-Guess-{i}-x", newPassword = Own });
            last = attempt.StatusCode;
        }

        last.Should().Be((HttpStatusCode)423);
        using var correct = await Send(HttpMethod.Post, "/api/v1/auth/change-password", access, new { currentPassword = Initial, newPassword = Own });
        correct.StatusCode.Should().Be((HttpStatusCode)423, "a locked account does not accept even the right password");
    }

    // ---------------------------------------------------------------- reset password and status

    [Fact]
    public async Task ResettingAPasswordSetsTheFlagAgainEndsSessionsAndClearsALockout()
    {
        var admin = await Token("admin12@example.org", KjRoles.Admin);
        var userId = await ProvisionDonor(admin, "d13", "d13@example.org");
        var access = (await Json(await Login("d13@example.org", Initial))).GetProperty("access_token").GetString()!;
        using var set = await Send(HttpMethod.Post, "/api/v1/auth/change-password", access, new { currentPassword = Initial, newPassword = Own });
        var refresh = (await Json(set)).GetProperty("refresh_token").GetString();

        using var reset = await Send(HttpMethod.Put, $"{Provision}/{userId}/password", admin, new { password = "Replacement-Pass-4" });

        reset.StatusCode.Should().Be(HttpStatusCode.OK);
        (await reset.Content.ReadAsStringAsync()).Should().NotContain("Replacement-Pass-4");
        (await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Login("d13@example.org", Own)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Json(await Login("d13@example.org", "Replacement-Pass-4"))).GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        (await Send(HttpMethod.Put, $"{Provision}/{userId}/password", admin, new { password = "short" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Put, $"{Provision}/{userId}/password", admin, new { password = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Put, $"{Provision}/usr_nobody/password", admin, new { password = Own })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DisablingEndsSessionsAndSignInSaysSoOnlyAfterTheRightPassword()
    {
        var admin = await Token("admin13@example.org", KjRoles.Admin);
        var userId = await ProvisionDonor(admin, "d14", "d14@example.org");
        var refresh = (await Json(await Login("d14@example.org", Initial))).GetProperty("refresh_token").GetString();

        (await Send(HttpMethod.Put, $"{Provision}/{userId}/status", admin, new { status = "disabled" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var right = await Login("d14@example.org", Initial);
        right.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(right)).Should().Be("ACCOUNT_DISABLED");
        using var wrong = await Login("d14@example.org", "Wrong-Password-99");
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCode(wrong)).Should().Be("UNAUTHORIZED");
        using var unknown = await Login("nobody@example.org", Initial);
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCode(unknown)).Should().Be("UNAUTHORIZED");

        (await Send(HttpMethod.Put, $"{Provision}/{userId}/status", admin, new { status = "active" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Login("d14@example.org", Initial)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Put, $"{Provision}/{userId}/status", admin, new { status = "banana" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WrongGuessesAtADisabledAccountNeitherLockItNorRevealItsState()
    {
        var admin = await Token("admin14@example.org", KjRoles.Admin);
        var userId = await ProvisionDonor(admin, "d15", "d15@example.org");
        await Send(HttpMethod.Put, $"{Provision}/{userId}/status", admin, new { status = "disabled" });

        for (var i = 0; i < 8; i++)
        {
            using var wrong = await Login("d15@example.org", $"Wrong-Password-{i}-x");
            wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        (await ErrorCode(await Login("d15@example.org", Initial))).Should().Be("ACCOUNT_DISABLED");
    }

    [Fact]
    public async Task AnAccountSharedWithAMemberCannotBePasswordResetOrDisabledThroughTheDonorEndpoints()
    {
        var admin = await Token("admin15@example.org", KjRoles.Admin);
        await CreateUser("both@example.org", KjRoles.Member, "Member-Own-Pass-77");
        await Send(HttpMethod.Post, Provision, admin, new { donorId = "d16", email = "both@example.org", password = Initial });
        string sharedId;
        using (var scope = _auth.Services.CreateScope())
        {
            sharedId = (await scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>().FindByEmailAsync("both@example.org"))!.Id;
        }

        foreach (var (url, body) in new (string, object)[] { ($"{Provision}/{sharedId}/password", new { password = Own }), ($"{Provision}/{sharedId}/status", new { status = "disabled" }) })
        {
            using var refused = await Send(HttpMethod.Put, url, admin, body);
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict, url);
            (await ErrorCode(refused)).Should().Be("ACCOUNT_SHARED", url);
        }

        (await Login("both@example.org", "Member-Own-Pass-77")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheMemberEndpointsDoNotTouchDonorAccounts()
    {
        var admin = await Token("admin16@example.org", KjRoles.Admin);
        var userId = await ProvisionDonor(admin, "d17", "d17@example.org");

        using var status = await Send(HttpMethod.Put, $"/api/v1/admin/auth/members/{userId}/status", admin, new { status = "disabled" });
        using var erase = await Send(HttpMethod.Post, $"/api/v1/admin/auth/members/{userId}/erase", admin);

        status.StatusCode.Should().Be(HttpStatusCode.NotFound);
        erase.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Login("d17@example.org", Initial)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task APasswordNeverAppearsInAnAuditRow()
    {
        var admin = await Token("admin17@example.org", KjRoles.Admin);
        var userId = await ProvisionDonor(admin, "d18", "d18@example.org");
        await Send(HttpMethod.Put, $"{Provision}/{userId}/password", admin, new { password = "Audit-Check-Pass-5" });

        using var scope = _auth.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthIdentityService.Data.AuthDbContext>();
        var rows = await db.Set<KJWebsite.BuildingBlocks.Auditing.AuditEvent>().ToListAsync();
        rows.Should().NotBeEmpty();
        JsonSerializer.Serialize(rows).Should().NotContain(Initial).And.NotContain("Audit-Check-Pass-5");
    }

    // ---------------------------------------------------------------- helpers

    private async Task<string> CreateUser(string email, string role, string password)
    {
        using var scope = _auth.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>();
        var user = new AuthUserEntity { UserName = email, Email = email, EmailConfirmed = true, Status = "active", CreatedAt = DateTimeOffset.UtcNow };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        await users.AddToRoleAsync(user, role);
        return user.Id;
    }

    private async Task<string> Token(string email, string role)
    {
        await CreateUser(email, role, Initial);
        using var login = await Login(email, Initial);
        login.StatusCode.Should().Be(HttpStatusCode.OK, email);
        return (await Json(login)).GetProperty("access_token").GetString()!;
    }

    private async Task<string> ProvisionDonor(string adminToken, string donorId, string email)
    {
        using var response = await Send(HttpMethod.Post, Provision, adminToken, new { donorId, email, password = Initial });
        response.StatusCode.Should().Be(HttpStatusCode.Created, email);
        return (await Json(response)).GetProperty("userId").GetString()!;
    }

    private async Task<string> SignInAndChangePassword(string email)
    {
        var access = (await Json(await Login(email, Initial))).GetProperty("access_token").GetString()!;
        using var changed = await Send(HttpMethod.Post, "/api/v1/auth/change-password", access, new { currentPassword = Initial, newPassword = Own });
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await Json(changed)).GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> Login(string email, string password) =>
        _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, string? token = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
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

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string?> ErrorCode(HttpResponseMessage response) =>
        (await Json(response)).GetProperty("error").GetProperty("code").GetString();
}
