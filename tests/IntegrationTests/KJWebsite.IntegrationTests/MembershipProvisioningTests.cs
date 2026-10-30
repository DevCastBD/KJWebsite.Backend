using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AuthIdentityService.Data;
using AuthIdentityService.Data.Entities;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using MembershipService.Intake;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

/// <summary>Approval end to end: a real Membership service calling a real Identity service in the same process.</summary>
public sealed class MembershipProvisioningTests : IDisposable
{
    private const string Password = "Sup3r-Secret-Pass!";
    private const string Transition = "/api/v1/admin/membership/applications/transition";
    private readonly AuthApiFactory _auth = new();
    private readonly MembershipApiFactory _membership = new();
    private readonly HttpClient _authClient;
    private readonly HttpClient _client;

    public MembershipProvisioningTests()
    {
        _authClient = _auth.CreateClient();
        _membership.AuthHandler = _auth.Server.CreateHandler();
        _client = _membership.CreateClient();
    }

    [Fact]
    public async Task ApprovingCreatesALoginMemberCodeAndWelcomeInOneAction()
    {
        var officer = await Officer("officer@example.org", MembershipOfficer);
        var id = await ReadyToApprove("jamal@example.org", "bn");

        var result = (await Move(officer, [id], "approved"))[0];

        result.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.GetProperty("memberCode").GetString().Should().Be($"KJ-{DateTime.UtcNow.Year}-0001");

        using (var scope = _auth.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>();
            var user = await users.Users.Include(u => u.Profile).SingleAsync(u => u.Email == "jamal@example.org");
            (await users.GetRolesAsync(user)).Should().Equal("Member");
            user.EmailConfirmed.Should().BeTrue();
            user.ProvisionedFor.Should().Be(id);
            user.Profile!.FirstName.Should().Be("Tahmid");
            user.Profile.BloodGroup.Should().Be("O+");
        }

        using var mScope = _membership.Services.CreateScope();
        var db = mScope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        var member = await db.Members.SingleAsync();
        member.ApplicationId.Should().Be(id);
        var welcome = await db.NotificationOutbox.SingleAsync(o => o.Template == "member.welcome");
        welcome.Language.Should().Be("bn");
        (await db.NotificationOutbox.AnyAsync(o => o.Template == "application.status.approved")).Should().BeFalse("one welcome, not two emails");
        var payload = JsonDocument.Parse(mScope.ServiceProvider.GetRequiredService<IFieldProtector>().Unprotect(welcome.PayloadJson)).RootElement;
        payload.GetProperty("memberCode").GetString().Should().Be(member.MemberCode);

        // The setup token from the welcome message is what makes the login work.
        using var reset = await _authClient.PostAsJsonAsync("/api/v1/auth/reset-password", new { email = "jamal@example.org", token = payload.GetProperty("setupToken").GetString(), newPassword = Password });
        reset.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var login = await _authClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "jamal@example.org", password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NoPasswordMeansNoLoginUntilTheSetupLinkIsUsed()
    {
        var officer = await Officer("officer2@example.org", MembershipOfficer);
        var id = await ReadyToApprove("early@example.org");
        await Move(officer, [id], "approved");

        using var login = await _authClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "early@example.org", password = Password });

        login.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MemberCodesAreUniqueAndNumberedInSequence()
    {
        var officer = await Officer("officer3@example.org", MembershipOfficer);
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await ReadyToApprove($"batch{i}@example.org"));
        }

        var results = await Move(officer, ids, "approved");

        results.Should().OnlyContain(r => r.GetProperty("ok").GetBoolean());
        var year = DateTime.UtcNow.Year;
        results.Select(r => r.GetProperty("memberCode").GetString()).Should().Equal(Enumerable.Range(1, 5).Select(n => $"KJ-{year}-{n:D4}"));
    }

    [Fact]
    public async Task ATakenEmailLeavesNothingHalfDone()
    {
        var officer = await Officer("officer4@example.org", MembershipOfficer);
        await Officer("taken@example.org", "Volunteer");
        var id = await ReadyToApprove("taken@example.org");

        var result = (await Move(officer, [id], "approved"))[0];

        result.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Be("email_taken");
        await AssertUntouched(id);
    }

    [Fact]
    public async Task AnApproverWithoutTheProvisionPermissionIsRefusedAndNothingChanges()
    {
        var reviewer = await Officer("reviewer@example.org", "OnlyReviews", Permissions.MembershipManage);
        var id = await ReadyToApprove("denied@example.org");

        var result = (await Move(reviewer, [id], "approved"))[0];

        result.GetProperty("error").GetString().Should().Be("forbidden");
        await AssertUntouched(id);
        using var scope = _auth.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>().FindByEmailAsync("denied@example.org")).Should().BeNull();
    }

    [Fact]
    public async Task IdentityBeingDownFailsTheItemCleanlyAndARetryWorks()
    {
        var officer = await Officer("officer5@example.org", MembershipOfficer);
        var id = await ReadyToApprove("retry@example.org");

        var healthy = _membership.AuthHandler;
        _membership.AuthHandler = new ThrowingHandler();
        using var down = _membership.WithWebHostBuilder(_ => { }).CreateClient();
        var failed = (await Move(officer, [id], "approved", down))[0];
        failed.GetProperty("error").GetString().Should().Be("provisioning_failed");
        await AssertUntouched(id);

        _membership.AuthHandler = healthy;
        var retried = (await Move(officer, [id], "approved"))[0];
        retried.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ProvisioningIsIdempotentPerApplication()
    {
        var officer = await Officer("officer6@example.org", MembershipOfficer);
        var body = new { applicationId = "app_same", email = "same@example.org", firstName = "A", lastName = "B" };

        using var first = await Authed(_authClient, HttpMethod.Post, "/api/v1/admin/auth/members", officer, body);
        using var second = await Authed(_authClient, HttpMethod.Post, "/api/v1/admin/auth/members", officer, body);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(second)).GetProperty("userId").GetString().Should().Be((await Json(first)).GetProperty("userId").GetString());
        using var scope = _auth.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Users.CountAsync(u => u.Email == "same@example.org")).Should().Be(1);
    }

    [Fact]
    public async Task TheProvisioningEndpointNeedsItsPermission()
    {
        (await _authClient.PostAsJsonAsync("/api/v1/admin/auth/members", new { applicationId = "x", email = "x@example.org" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var token = await Officer("nope@example.org", "Volunteer");
        using var response = await Authed(_authClient, HttpMethod.Post, "/api/v1/admin/auth/members", token, new { applicationId = "x", email = "x@example.org" });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var invalid = await Authed(_authClient, HttpMethod.Post, "/api/v1/admin/auth/members", await Officer("o7@example.org", MembershipOfficer), new { applicationId = "", email = "bad" });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    public void Dispose()
    {
        _client.Dispose();
        _authClient.Dispose();
        _membership.Dispose();
        _auth.Dispose();
    }

    private const string MembershipOfficer = "MembershipOfficer";

    private async Task AssertUntouched(string applicationId)
    {
        using var scope = _membership.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        (await db.Applications.SingleAsync(a => a.Id == applicationId)).Status.Should().Be("interview_scheduled");
        (await db.Members.CountAsync()).Should().Be(0);
        (await db.NotificationOutbox.AnyAsync(o => o.Template == "member.welcome")).Should().BeFalse();
        (await db.StatusEvents.AnyAsync(e => e.ApplicationId == applicationId && e.Status == "approved")).Should().BeFalse();
    }

    /// <summary>Creates an account in Identity with the role (a custom role when permissions are given) and returns its access token.</summary>
    private async Task<string> Officer(string email, string role, params string[] permissions)
    {
        using (var scope = _auth.Services.CreateScope())
        {
            if (permissions.Length > 0)
            {
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<AuthRoleEntity>>();
                var created = new AuthRoleEntity(role);
                (await roles.CreateAsync(created)).Succeeded.Should().BeTrue();
                foreach (var permission in permissions)
                {
                    await roles.AddClaimAsync(created, new Claim(KjClaims.Permission, permission));
                }
            }

            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>();
            var user = new AuthUserEntity { UserName = email, Email = email, EmailConfirmed = true, Status = "active", CreatedAt = DateTimeOffset.UtcNow };
            (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();
            await users.AddToRoleAsync(user, role);
        }

        using var response = await _authClient.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, email);
        return (await Json(response)).GetProperty("access_token").GetString()!;
    }

    private async Task<string> ReadyToApprove(string email, string language = "en")
    {
        using var created = await _client.PostAsJsonAsync("/api/v1/membership/applications", new
        {
            firstName = "Tahmid",
            lastName = "Rahman",
            email,
            phone = $"0171{Random.Shared.Next(1000000, 9999999)}",
            bloodGroup = "O+",
            currentAddress = "Dhaka",
            academicInstitute = "DU",
            occupation = "Student",
            language
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(created)).GetProperty("id").GetString()!;
        var officer = await Officer($"mover-{Guid.NewGuid():N}@example.org", MembershipOfficer);
        await Move(officer, [id], "under_review");
        await Move(officer, [id], "shortlisted");
        await Move(officer, [id], "interview_scheduled", interviewAt: DateTimeOffset.UtcNow.AddDays(2));
        return id;
    }

    private async Task<List<JsonElement>> Move(string token, IEnumerable<string> ids, string to, HttpClient? client = null, DateTimeOffset? interviewAt = null)
    {
        using var response = await Authed(client ?? _client, HttpMethod.Post, Transition, token, new { ids, to, interviewAt });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return [.. (await Json(response)).GetProperty("results").EnumerateArray()];
    }

    private static async Task<HttpResponseMessage> Authed(HttpClient client, HttpMethod method, string path, string token, object body)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("identity is down");
    }
}
