using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthIdentityService.Data.Entities;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Auditing;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Intake;
using MembershipService.Lifecycle;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class MemberLifecycleTests : IDisposable
{
    private const string Password = "Sup3r-Secret-Pass!";
    private const string Reason = "Repeated conduct complaints, PRIVATE-REASON";
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private readonly AuthApiFactory _auth = new();
    private readonly MembershipApiFactory _membership = new();
    private readonly HttpClient _authClient;
    private readonly HttpClient _client;
    private string? _officer;
    private string? _admin;

    public MemberLifecycleTests()
    {
        _authClient = _auth.CreateClient();
        _membership.AuthHandler = _auth.Server.CreateHandler();
        _client = _membership.CreateClient();
    }

    // ---- the rules, no HTTP ----

    [Theory]
    [InlineData("active", "on_leave", null)]
    [InlineData("active", "inactive", null)]
    [InlineData("active", "alumni", null)]
    [InlineData("active", "terminated", null)]
    [InlineData("active", "active", "invalid_transition")]
    [InlineData("on_leave", "active", null)]
    [InlineData("on_leave", "alumni", null)]
    [InlineData("inactive", "active", null)]
    [InlineData("inactive", "on_leave", "invalid_transition")]
    [InlineData("alumni", "active", null)]
    [InlineData("alumni", "terminated", null)]
    [InlineData("alumni", "inactive", "invalid_transition")]
    [InlineData("terminated", "active", null)]
    [InlineData("terminated", "alumni", "invalid_transition")]
    [InlineData("active", "retired", "unknown_status")]
    public void TheStateMachineAllowsOnlyDefinedMoves(string from, string to, string? expected) =>
        MemberStatusMachine.Check(from, to).Should().Be(expected);

    [Fact]
    public void EveryDepartureCanBeReversed()
    {
        foreach (var status in new[] { "on_leave", "inactive", "alumni", "terminated" })
        {
            MemberStatusMachine.NextFrom(status).Should().Contain("active", status);
        }
    }

    [Fact]
    public void TenureSumsEveryStretchOfServiceAndSkipsTheGaps()
    {
        var events = new[]
        {
            Event(1, "active", new DateOnly(2020, 1, 1)),
            Event(2, "on_leave", new DateOnly(2021, 1, 1)),
            Event(3, "active", new DateOnly(2021, 7, 1)),
            Event(4, "alumni", new DateOnly(2022, 7, 1)),
            Event(5, "active", new DateOnly(2024, 1, 1))
        };

        var tenure = TenureCalculator.Calculate(events, new DateOnly(2024, 1, 11));

        tenure.Periods.Select(p => p.Days).Should().Equal(366, 365, 10);
        tenure.TotalDays.Should().Be(741, "2020 is a leap year; leave and the alumni years do not count");
        tenure.CurrentlyActive.Should().BeTrue();
        tenure.ActiveSince.Should().Be(new DateOnly(2024, 1, 1));
        tenure.Years.Should().Be(2);
    }

    [Fact]
    public void TenureHandlesSameDayMovesUnorderedInputAndNeverActive()
    {
        var shuffled = new[]
        {
            Event(3, "terminated", new DateOnly(2023, 3, 1)),
            Event(1, "active", new DateOnly(2023, 3, 1)),
            Event(2, "active", new DateOnly(2022, 1, 1)),
        };

        var tenure = TenureCalculator.Calculate(shuffled, new DateOnly(2025, 1, 1));

        tenure.TotalDays.Should().Be(424, "2022-01-01 to 2023-03-01; the same-day events add nothing");
        tenure.CurrentlyActive.Should().BeFalse();
        TenureCalculator.Calculate([], new DateOnly(2025, 1, 1)).TotalDays.Should().Be(0);
        TenureCalculator.Calculate([Event(1, "on_leave", new DateOnly(2024, 1, 1))], new DateOnly(2025, 1, 1)).TotalDays.Should().Be(0);
    }

    // ---- the endpoints ----

    [Fact]
    public async Task OfficersHandleLeaveAlumniAndReturnWithFullHistory()
    {
        var m = await NewMember("leave@example.org");

        (await Status(_officer!, m.Id, "on_leave", note: "Exams")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Status(_officer!, m.Id, "active")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Status(_officer!, m.Id, "alumni")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Status(_officer!, m.Id, "active")).StatusCode.Should().Be(HttpStatusCode.OK);

        var view = await Json(await Get(_officer!, $"/api/v1/admin/membership/members/{m.Id}/lifecycle"));
        view.GetProperty("status").GetString().Should().Be("active");
        view.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("to").GetString()).Should().Equal("active", "on_leave", "active", "alumni", "active");
        view.GetProperty("history")[1].GetProperty("note").GetString().Should().Be("Exams");
        view.GetProperty("tenure").GetProperty("currentlyActive").GetBoolean().Should().BeTrue();
        view.GetProperty("nextStatuses").GetArrayLength().Should().Be(4);
    }

    [Fact]
    public async Task InvalidMovesAndDatesAreRefused()
    {
        var m = await NewMember("rules@example.org");

        using var same = await Status(_officer!, m.Id, "active");
        using var unknown = await Status(_officer!, m.Id, "retired");
        using var future = await Status(_officer!, m.Id, "on_leave", effectiveOn: Today.AddDays(2));
        using var beforeJoin = await Status(_officer!, m.Id, "on_leave", effectiveOn: Today.AddDays(-3));
        using var missing = await Status(_officer!, "mem_missing", "on_leave");

        same.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await same.Content.ReadAsStringAsync()).Should().Contain("INVALID_TRANSITION");
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        future.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        beforeJoin.StatusCode.Should().Be(HttpStatusCode.BadRequest, "service cannot start before the joining day");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TerminationNeedsItsOwnPermissionAndAReason()
    {
        var m = await NewMember("term-perm@example.org");

        (await Status(_officer!, m.Id, "terminated", reason: Reason)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Status(_admin!, m.Id, "terminated")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Status(_admin!, m.Id, "terminated", reason: Reason)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Status(_officer!, m.Id, "active")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "lifting a termination is as sensitive as making one");
        (await Status(_admin!, m.Id, "active")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TerminationReasonsAreOnlyReadByThoseWhoMayTerminate()
    {
        var m = await NewMember("secret@example.org");
        await Status(_admin!, m.Id, "terminated", reason: Reason);

        var asOfficer = await (await Get(_officer!, $"/api/v1/admin/membership/members/{m.Id}/lifecycle")).Content.ReadAsStringAsync();
        var asAdmin = await (await Get(_admin!, $"/api/v1/admin/membership/members/{m.Id}/lifecycle")).Content.ReadAsStringAsync();
        var officerEvent = JsonDocument.Parse(asOfficer).RootElement.GetProperty("history")[1];

        asOfficer.Should().NotContain("PRIVATE-REASON");
        officerEvent.GetProperty("reasonRestricted").GetBoolean().Should().BeTrue();
        asAdmin.Should().Contain("PRIVATE-REASON");

        using var scope = _membership.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        (await db.MemberStatusEvents.SingleAsync(e => e.ToStatus == "terminated")).ReasonProtected.Should().NotContain("PRIVATE-REASON", "stored encrypted");
        var audit = string.Join('\n', (await db.Set<AuditEvent>().ToListAsync()).Select(a => a.Before + a.After));
        audit.Should().NotContain("PRIVATE-REASON");
        (await db.Set<AuditEvent>().AnyAsync(a => a.Entity == "Member" && a.Action == "update" && a.After!.Contains("terminated"))).Should().BeTrue("the status change itself is audited");
    }

    [Fact]
    public async Task TerminatingSwitchesTheLoginOffAndReinstatingSwitchesItOn()
    {
        var m = await NewMember("login@example.org");
        (await TryLogin("login@example.org")).Should().Be(HttpStatusCode.OK);

        (await Status(_admin!, m.Id, "terminated", reason: Reason)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await TryLogin("login@example.org")).Should().Be(HttpStatusCode.Unauthorized);
        (await Get(m.Token, "/api/v1/auth/sessions", _authClient)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the old session is gone too");
        (await Status(_admin!, m.Id, "active")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await TryLogin("login@example.org")).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task IfIdentityCannotBeReachedNothingChanges()
    {
        var m = await NewMember("down@example.org");
        var healthy = _membership.AuthHandler;
        _membership.AuthHandler = new DownHandler();
        using var down = _membership.WithWebHostBuilder(_ => { }).CreateClient();

        using var response = await Send(down, _admin!, HttpMethod.Post, $"/api/v1/admin/membership/members/{m.Id}/status", new { to = "terminated", reason = Reason });
        _membership.AuthHandler = healthy;

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        using var scope = _membership.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        (await db.Members.SingleAsync(x => x.Id == m.Id)).Status.Should().Be("active");
        (await db.MemberStatusEvents.CountAsync(e => e.MemberId == m.Id)).Should().Be(1, "only the joining event");
        (await TryLogin("down@example.org")).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LeavingEndsCommitteeTermsAndTerminationWithdrawsConsentWithoutDeletingHistory()
    {
        var m = await NewMember("seats@example.org");
        await Send(_client, m.Token, HttpMethod.Put, "/api/v1/members/me/consents", new { name = true, photograph = false, expertise = true, alumni = true });
        var committee = (await Json(await Send(_client, _officer!, HttpMethod.Post, "/api/v1/admin/membership/committees", new { nameEn = "Exec", nameBn = "নির্বাহী", kind = "executive" }))).GetProperty("id").GetString();
        await Send(_client, _officer!, HttpMethod.Post, $"/api/v1/admin/membership/committees/{committee}/members", new { memberId = m.Id, positionKey = "chair", positionEn = "Chair", startsOn = Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
        (await _client.GetStringAsync("/api/v1/committees")).Should().Contain("First seats");

        await Status(_officer!, m.Id, "alumni");
        using (var scope = _membership.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
            (await db.CommitteeMembers.SingleAsync()).EndsOn.Should().Be(Today, "the seat ended the day they left, and is kept");
            (await db.Members.SingleAsync(x => x.Id == m.Id)).ConsentName.Should().BeTrue("alumni keep their consents");
        }

        await Status(_officer!, m.Id, "active");
        await Status(_admin!, m.Id, "terminated", reason: Reason);
        using var scope2 = _membership.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<MembershipDbContext>();
        var member = await db2.Members.SingleAsync(x => x.Id == m.Id);
        new[] { member.ConsentName, member.ConsentPhotograph, member.ConsentExpertise, member.ConsentAlumni }.Should().AllBeEquivalentTo(false);
        (await db2.ConsentRecords.Where(c => c.SubjectId == m.Id && !c.Granted).Select(c => c.Purpose).ToListAsync()).Should().Contain(["publish_name", "publish_expertise", "publish_alumni"]);

        await Status(_admin!, m.Id, "active");
        (await db2.Members.AsNoTracking().SingleAsync(x => x.Id == m.Id)).ConsentName.Should().BeFalse("reactivation does not re-publish anything");
    }

    [Fact]
    public async Task AlumniAreListedPubliclyOnlyAfterTheirOwnOptIn()
    {
        var m = await NewMember("alum@example.org");
        await Send(_client, m.Token, HttpMethod.Put, "/api/v1/members/me/consents", new { name = true, photograph = false, expertise = false });
        (await DirectoryIds()).Should().Contain(m.Id);

        using var toAlumni = await Status(_officer!, m.Id, "alumni");
        (await toAlumni.Content.ReadAsStringAsync()).Should().Contain("alumni");
        (await DirectoryIds()).Should().NotContain(m.Id, "leaving is not consent to be listed as alumni");

        using var optIn = await Send(_client, m.Token, HttpMethod.Put, "/api/v1/members/me/consents", new { name = true, photograph = false, expertise = false, alumni = true });
        (await Json(optIn)).GetProperty("alumni").GetBoolean().Should().BeTrue();
        var page = await Json(await _client.GetAsync("/api/v1/members/directory"));
        page.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == m.Id).GetProperty("alumni").GetBoolean().Should().BeTrue();

        await Status(_officer!, m.Id, "active");
        await Status(_officer!, m.Id, "on_leave");
        (await DirectoryIds()).Should().NotContain(m.Id, "a member on leave is not listed");
    }

    [Fact]
    public async Task ThePortalAndTheExportShowTheMembersOwnLifecycle()
    {
        var m = await NewMember("own@example.org");
        await Status(_admin!, m.Id, "terminated", reason: Reason);
        await Status(_admin!, m.Id, "active");
        var token = await Login("own@example.org");

        var profile = await Json(await Get(token, "/api/v1/members/me/profile"));
        var export = await Json(await Get(token, "/api/v1/members/me/export"));

        profile.GetProperty("status").GetString().Should().Be("active");
        export.GetProperty("lifecycle").GetProperty("history").GetArrayLength().Should().Be(3);
        export.GetProperty("lifecycle").GetProperty("history")[1].GetProperty("reason").GetString().Should().Contain("PRIVATE-REASON", "the member's own record is theirs to read");
    }

    [Fact]
    public async Task OnlyTheRightRolesReachTheLifecycleEndpoints()
    {
        var m = await NewMember("perm@example.org");

        (await _client.PostAsJsonAsync($"/api/v1/admin/membership/members/{m.Id}/status", new { to = "on_leave" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Status(m.Token, m.Id, "on_leave")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "a member cannot change their own status");
        (await Get(m.Token, $"/api/v1/admin/membership/members/{m.Id}/lifecycle")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    public void Dispose()
    {
        _client.Dispose();
        _authClient.Dispose();
        _membership.Dispose();
        _auth.Dispose();
    }

    private static MemberStatusEventEntity Event(long id, string to, DateOnly on) => new() { Id = id, MemberId = "m", ToStatus = to, EffectiveOn = on, At = DateTimeOffset.UtcNow.AddSeconds(id), ActorId = "t" };

    private sealed record Made(string Id, string Token);

    private async Task<Made> NewMember(string email)
    {
        _officer ??= await Account("officer@example.org", "MembershipOfficer");
        _admin ??= await Account("admin@example.org", "Admin");
        using var created = await _client.PostAsJsonAsync("/api/v1/membership/applications", new
        {
            firstName = "First " + email.Split('@')[0].Split('-')[0],
            lastName = "Last",
            email,
            phone = $"0171{Random.Shared.Next(1000000, 9999999)}",
            bloodGroup = "O+",
            currentAddress = "Dhaka",
            academicInstitute = "DU",
            occupation = "Student",
            language = "en"
        });
        var id = (await Json(created)).GetProperty("id").GetString()!;
        foreach (var to in new[] { "under_review", "shortlisted", "interview_scheduled", "approved" })
        {
            using var moved = await Send(_client, _officer, HttpMethod.Post, "/api/v1/admin/membership/applications/transition", new { ids = new[] { id }, to, interviewAt = DateTimeOffset.UtcNow.AddDays(2) });
            (await Json(moved)).GetProperty("results")[0].GetProperty("ok").GetBoolean().Should().BeTrue(to);
        }

        string setup;
        string memberId;
        using (var scope = _membership.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
            var welcome = await db.NotificationOutbox.SingleAsync(o => o.ApplicationId == id && o.Template == "member.welcome");
            setup = JsonDocument.Parse(scope.ServiceProvider.GetRequiredService<IFieldProtector>().Unprotect(welcome.PayloadJson)).RootElement.GetProperty("setupToken").GetString()!;
            memberId = (await db.Members.SingleAsync(m => m.ApplicationId == id)).Id;
        }

        (await _authClient.PostAsJsonAsync("/api/v1/auth/reset-password", new { email, token = setup, newPassword = Password })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        return new Made(memberId, await Login(email));
    }

    private async Task<string> Account(string email, string role)
    {
        using (var scope = _auth.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>();
            var user = new AuthUserEntity { UserName = email, Email = email, EmailConfirmed = true, Status = "active", CreatedAt = DateTimeOffset.UtcNow };
            (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();
            await users.AddToRoleAsync(user, role);
        }

        return await Login(email);
    }

    private async Task<string> Login(string email)
    {
        using var response = await _authClient.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, email);
        return (await Json(response)).GetProperty("access_token").GetString()!;
    }

    private async Task<HttpStatusCode> TryLogin(string email)
    {
        using var response = await _authClient.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        return response.StatusCode;
    }

    private async Task<List<string>> DirectoryIds()
    {
        var page = await Json(await _client.GetAsync("/api/v1/members/directory"));
        return [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!)];
    }

    private Task<HttpResponseMessage> Status(string token, string id, string to, string? note = null, string? reason = null, DateOnly? effectiveOn = null) =>
        Send(_client, token, HttpMethod.Post, $"/api/v1/admin/membership/members/{id}/status", new { to, note, reason, effectiveOn = effectiveOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });

    private Task<HttpResponseMessage> Get(string token, string path, HttpClient? client = null) => Send(client ?? _client, token, HttpMethod.Get, path);

    private static async Task<HttpResponseMessage> Send(HttpClient client, string token, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private sealed class DownHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("identity is down");
    }
}
