using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AuthIdentityService.Data.Entities;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using MembershipService.Intake;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

/// <summary>The portal end to end: members are really approved (Identity + Membership), then act with their own tokens.</summary>
public sealed class MemberSelfServiceTests : IDisposable
{
    private const string Password = "Sup3r-Secret-Pass!";
    private const string Me = "/api/v1/members/me";
    private readonly AuthApiFactory _auth = new();
    private readonly MembershipApiFactory _membership = new();
    private readonly HttpClient _authClient;
    private readonly HttpClient _client;
    private string? _officer;

    public MemberSelfServiceTests()
    {
        _authClient = _auth.CreateClient();
        _membership.AuthHandler = _auth.Server.CreateHandler();
        _client = _membership.CreateClient();
    }

    [Fact]
    public async Task ProfileStartsFromTheApplicationAndEverythingIsPrivateByDefault()
    {
        var token = await NewMember("rahim@example.org", "Rahim", "Uddin");

        var profile = await Json(await Get(token, $"{Me}/profile"));
        profile.GetProperty("firstName").GetString().Should().Be("Rahim");
        profile.GetProperty("email").GetString().Should().Be("rahim@example.org");
        profile.GetProperty("bloodGroup").GetString().Should().Be("O+");
        profile.GetProperty("address").GetString().Should().Be("Dhaka");
        profile.GetProperty("memberId").GetString().Should().MatchRegex(@"^KJ-\d{4}-\d{4}$");
        profile.GetProperty("pending").EnumerateObject().Should().BeEmpty();

        var consents = await Json(await Get(token, $"{Me}/consents"));
        consents.GetProperty("name").GetBoolean().Should().BeFalse();
        consents.GetProperty("photograph").GetBoolean().Should().BeFalse();
        consents.GetProperty("expertise").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task DirectFieldsApplyAtOnceAndBadValuesAreRejectedByName()
    {
        var token = await NewMember("direct@example.org");

        var updated = await Json(await Send(token, HttpMethod.Patch, $"{Me}/profile", new { changes = new { phone = "01811112222", address = "Chattogram", expertise = "Robotics" } }));
        updated.GetProperty("phone").GetString().Should().Be("01811112222");
        updated.GetProperty("address").GetString().Should().Be("Chattogram");
        updated.GetProperty("expertise").GetString().Should().Be("Robotics");

        using var bad = await Send(token, HttpMethod.Patch, $"{Me}/profile", new { changes = new { phone = "1", bloodGroup = "Q+", nickname = "x" } });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await bad.Content.ReadAsStringAsync();
        text.Should().Contain("phone").And.Contain("bloodGroup").And.Contain("nickname");
    }

    [Fact]
    public async Task SensitiveEditsWaitForReviewAndTheMemberSeesTheirStatus()
    {
        var token = await NewMember("sens@example.org", "Karim", "Ali");

        var after = await Json(await Send(token, HttpMethod.Patch, $"{Me}/profile", new { changes = new { firstName = "Abdul", bloodGroup = "a+", nameBn = "আবদুল করিম" } }));
        after.GetProperty("firstName").GetString().Should().Be("Karim", "the record is unchanged until a reviewer approves");
        after.GetProperty("pending").GetProperty("firstName").GetProperty("status").GetString().Should().Be("pending");
        after.GetProperty("pending").GetProperty("bloodGroup").GetProperty("value").GetString().Should().Be("A+");

        var queue = await Json(await Get(_officer!, "/api/v1/admin/membership/profile-changes"));
        var items = queue.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(3);
        var first = items.Single(i => i.GetProperty("field").GetString() == "firstName");
        first.GetProperty("currentValue").GetString().Should().Be("Karim");
        first.GetProperty("newValue").GetString().Should().Be("Abdul");

        (await Send(_officer!, HttpMethod.Post, $"/api/v1/admin/membership/profile-changes/{first.GetProperty("id").GetString()}/approve")).StatusCode.Should().Be(HttpStatusCode.OK);
        var blood = items.Single(i => i.GetProperty("field").GetString() == "bloodGroup");
        using var noReason = await Send(_officer!, HttpMethod.Post, $"/api/v1/admin/membership/profile-changes/{blood.GetProperty("id").GetString()}/reject", new { reason = " " });
        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(_officer!, HttpMethod.Post, $"/api/v1/admin/membership/profile-changes/{blood.GetProperty("id").GetString()}/reject", new { reason = "Please attach a lab report." })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(_officer!, HttpMethod.Post, $"/api/v1/admin/membership/profile-changes/{blood.GetProperty("id").GetString()}/approve")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var profile = await Json(await Get(token, $"{Me}/profile"));
        profile.GetProperty("firstName").GetString().Should().Be("Abdul");
        profile.GetProperty("bloodGroup").GetString().Should().Be("O+");
        profile.GetProperty("pending").TryGetProperty("firstName", out _).Should().BeFalse("approved edits are simply the current value");
        var rejected = profile.GetProperty("pending").GetProperty("bloodGroup");
        rejected.GetProperty("status").GetString().Should().Be("rejected");
        rejected.GetProperty("reason").GetString().Should().Be("Please attach a lab report.");
    }

    [Fact]
    public async Task ANewRequestReplacesThePendingOneAndNoOpsAreIgnored()
    {
        var token = await NewMember("replace@example.org", "Same", "Name");

        await Send(token, HttpMethod.Patch, $"{Me}/profile", new { changes = new { lastName = "One" } });
        await Send(token, HttpMethod.Patch, $"{Me}/profile", new { changes = new { lastName = "Two", firstName = "Same" } });

        using var scope = _membership.Services.CreateScope();
        var pending = await scope.ServiceProvider.GetRequiredService<MembershipDbContext>().ProfileChanges.ToListAsync();
        pending.Should().ContainSingle().Which.Field.Should().Be("lastName");
    }

    [Fact]
    public async Task ConsentIsPerFieldOptInAndEveryChangeIsLogged()
    {
        var token = await NewMember("consent@example.org");

        var set = await Json(await Send(token, HttpMethod.Put, $"{Me}/consents", new { name = true, photograph = false, expertise = false }));
        set.GetProperty("name").GetBoolean().Should().BeTrue();
        await Send(token, HttpMethod.Put, $"{Me}/consents", new { name = true, photograph = false, expertise = false });
        await Send(token, HttpMethod.Put, $"{Me}/consents", new { name = false, photograph = true, expertise = false });

        using var scope = _membership.Services.CreateScope();
        var log = (await scope.ServiceProvider.GetRequiredService<MembershipDbContext>().ConsentEvents.ToListAsync()).OrderBy(e => e.Id).ToList();
        log.Select(e => (e.Field, e.Granted)).Should().Equal(("name", true), ("name", false), ("photograph", true));
    }

    [Fact]
    public async Task NotificationPreferencesCanBeChosen()
    {
        var token = await NewMember("prefs@example.org");

        (await Json(await Get(token, $"{Me}/notifications"))).GetProperty("news").GetBoolean().Should().BeTrue();
        var set = await Json(await Send(token, HttpMethod.Put, $"{Me}/notifications", new { news = false, events = true, membership = true }));

        set.GetProperty("news").GetBoolean().Should().BeFalse();
        (await Json(await Get(token, $"{Me}/notifications"))).GetProperty("news").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task AMemberCanOnlyEverReachTheirOwnData()
    {
        var alice = await NewMember("alice@example.org", "Alice", "Aziz");
        var bob = await NewMember("bob@example.org", "Bob", "Bose");
        await Send(bob, HttpMethod.Patch, $"{Me}/profile", new { changes = new { phone = "01899990000", firstName = "Robert" } });
        await Send(bob, HttpMethod.Put, $"{Me}/consents", new { name = true, photograph = true, expertise = true });

        var seen = await (await Get(alice, $"{Me}/profile")).Content.ReadAsStringAsync();
        var export = await (await Get(alice, $"{Me}/export")).Content.ReadAsStringAsync();

        foreach (var text in new[] { seen, export })
        {
            text.Should().Contain("Alice").And.NotContain("Bob").And.NotContain("Robert").And.NotContain("bob@example.org").And.NotContain("01899990000");
        }

        (await Json(await Get(alice, $"{Me}/consents"))).GetProperty("name").GetBoolean().Should().BeFalse("Bob's consent is not Alice's");
        (await Get(alice, "/api/v1/admin/membership/profile-changes")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.GetAsync($"{Me}/profile")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnAccountThatIsNotAMemberGetsNothing()
    {
        var staff = await Account("staff-only@example.org", "Member");

        using var response = await Get(staff, $"{Me}/profile");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("NOT_A_MEMBER");
    }

    [Fact]
    public async Task PhotoIsStoredCheckedAndOnlyServedToItsOwner()
    {
        var owner = await NewMember("photo@example.org");
        var other = await NewMember("nophoto@example.org");
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };

        using var put = await Put(owner, $"{Me}/photo", "image/png", png);
        using var fake = await Put(owner, $"{Me}/photo", "image/png", Encoding.UTF8.GetBytes("not an image"));
        using var big = await Put(owner, $"{Me}/photo", "image/png", [.. png, .. new byte[2 * 1024 * 1024]]);

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(put)).GetProperty("scanStatus").GetString().Should().Be("pending");
        fake.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        big.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await (await Get(owner, $"{Me}/photo")).Content.ReadAsByteArrayAsync()).Should().Equal(png);
        (await Get(other, $"{Me}/photo")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(await Get(owner, $"{Me}/profile"))).GetProperty("hasPhoto").GetBoolean().Should().BeTrue();

        (await Send(owner, HttpMethod.Delete, $"{Me}/photo")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Get(owner, $"{Me}/photo")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeletionRequestIsRecordedOnce()
    {
        var token = await NewMember("leave@example.org");

        using var first = await Send(token, HttpMethod.Post, $"{Me}/deletion-request");
        using var second = await Send(token, HttpMethod.Post, $"{Me}/deletion-request");

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Json(first)).GetProperty("reference").GetString().Should().StartWith("DEL-");
        (await Json(second)).GetProperty("reference").GetString().Should().Be((await Json(first)).GetProperty("reference").GetString());
        (await Json(await Get(token, $"{Me}/profile"))).GetProperty("deletionRequest").GetProperty("reference").GetString().Should().StartWith("DEL-");
    }

    [Fact]
    public async Task TheExportHoldsEverythingKnownAboutTheMember()
    {
        var token = await NewMember("export@example.org", "Nasrin", "Sultana", reason: "I enjoy teaching kids");
        await Send(_officer!, HttpMethod.Post, $"/api/v1/admin/membership/applications/{await ApplicationIdOf("export@example.org")}/notes", new { text = "Great interview, NOTE-MARKER" });
        await Send(token, HttpMethod.Put, $"{Me}/consents", new { name = true, photograph = false, expertise = false });
        await Send(token, HttpMethod.Patch, $"{Me}/profile", new { changes = new { lastName = "Begum" } });

        using var response = await Get(token, $"{Me}/export");
        var text = await response.Content.ReadAsStringAsync();
        var export = JsonDocument.Parse(text).RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        export.GetProperty("profile").GetProperty("firstName").GetString().Should().Be("Nasrin");
        export.GetProperty("application").GetProperty("reasonForJoining").GetString().Should().Be("I enjoy teaching kids");
        export.GetProperty("application").GetProperty("history").EnumerateArray().Select(h => h.GetProperty("status").GetString())
            .Should().Equal("awaiting", "under_review", "shortlisted", "interview_scheduled", "approved");
        export.GetProperty("application").GetProperty("reviewerNotes")[0].GetProperty("text").GetString().Should().Contain("NOTE-MARKER");
        export.GetProperty("consentHistory").GetArrayLength().Should().Be(1);
        export.GetProperty("profileChanges")[0].GetProperty("value").GetString().Should().Be("Begum");
        export.GetProperty("messagesSent").EnumerateArray().Select(m => m.GetProperty("template").GetString()).Should().Contain("member.welcome");
        export.GetProperty("audit").GetArrayLength().Should().BeGreaterThan(0);
        export.GetProperty("identityAvailable").GetBoolean().Should().BeTrue();
        export.GetProperty("identity").GetProperty("account").GetProperty("email").GetString().Should().Be("export@example.org");
        export.GetProperty("identity").GetProperty("profile").GetProperty("firstName").GetString().Should().Be("Nasrin");
        export.GetProperty("sessions").GetArrayLength().Should().BeGreaterThan(0);
        text.Should().NotContainEquivalentOf("passwordHash").And.NotContainEquivalentOf("securityStamp").And.NotContain("setupToken");
    }

    [Fact]
    public async Task TheCardCarriesTheCodeAQrAndABengaliPdf()
    {
        var token = await NewMember("card@example.org", "Tahmid", "Rahman");
        await Send(token, HttpMethod.Patch, $"{Me}/profile", new { changes = new { nameBn = "তাহমিদ রহমান ক্ষত্রিয়" } });
        var id = (await Json(await Get(_officer!, "/api/v1/admin/membership/profile-changes"))).GetProperty("items")[0].GetProperty("id").GetString();
        await Send(_officer!, HttpMethod.Post, $"/api/v1/admin/membership/profile-changes/{id}/approve");

        var card = await Json(await Get(token, $"{Me}/card"));
        using var pdf = await Get(token, $"{Me}/card.pdf");
        var bytes = await pdf.Content.ReadAsByteArrayAsync();

        card.GetProperty("memberCode").GetString().Should().MatchRegex(@"^KJ-\d{4}-\d{4}$");
        card.GetProperty("name").GetProperty("bn").GetString().Should().Be("তাহমিদ রহমান ক্ষত্রিয়");
        card.GetProperty("qrSvg").GetString().Should().StartWith("<svg");
        card.GetProperty("qrPayload").GetString().Should().EndWith(card.GetProperty("memberCode").GetString());
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
        bytes.Length.Should().BeGreaterThan(10_000, "the Bengali font is embedded");
        var output = Environment.GetEnvironmentVariable("KJ_CARD_PDF_OUT");
        if (output is { Length: > 0 })
        {
            await File.WriteAllBytesAsync(output, bytes);
        }
    }

    [Fact]
    public async Task SessionsAreListedAndRevokedForTheOwnerOnly()
    {
        var token = await NewMember("sessions@example.org");
        var other = await NewMember("sessions2@example.org");

        var list = await Json(await Get(token, $"{Me}/sessions", _authClient));
        var items = list.GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty();
        items[0].TryGetProperty("device", out _).Should().BeTrue();
        items[0].TryGetProperty("lastSeenAt", out _).Should().BeTrue();

        var id = items[0].GetProperty("id").GetString();
        (await Send(other, HttpMethod.Delete, $"{Me}/sessions/{id}", client: _authClient)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(token, HttpMethod.Delete, $"{Me}/sessions/{id}", client: _authClient)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    public void Dispose()
    {
        _client.Dispose();
        _authClient.Dispose();
        _membership.Dispose();
        _auth.Dispose();
    }

    private async Task<string> ApplicationIdOf(string email)
    {
        using var scope = _membership.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        var hash = scope.ServiceProvider.GetRequiredService<ContactHasher>().HashEmail(email);
        return (await db.Applications.SingleAsync(a => a.EmailHash == hash)).Id;
    }

    /// <summary>Applies, is approved through the real flow, sets a password from the welcome token, and logs in. Returns the member's token.</summary>
    private async Task<string> NewMember(string email, string first = "Tahmid", string last = "Rahman", string? reason = null)
    {
        _officer ??= await Account("officer@example.org", "MembershipOfficer");
        using var created = await _client.PostAsJsonAsync("/api/v1/membership/applications", new
        {
            firstName = first,
            lastName = last,
            email,
            phone = $"0171{Random.Shared.Next(1000000, 9999999)}",
            bloodGroup = "O+",
            currentAddress = "Dhaka",
            academicInstitute = "DU",
            occupation = "Student",
            language = "en",
            reasonForJoining = reason
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(created)).GetProperty("id").GetString()!;
        foreach (var to in new[] { "under_review", "shortlisted", "interview_scheduled", "approved" })
        {
            using var moved = await Send(_officer, HttpMethod.Post, "/api/v1/admin/membership/applications/transition", new { ids = new[] { id }, to, interviewAt = DateTimeOffset.UtcNow.AddDays(2) });
            (await Json(moved)).GetProperty("results")[0].GetProperty("ok").GetBoolean().Should().BeTrue(to);
        }

        string setupToken;
        using (var scope = _membership.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
            var welcome = await db.NotificationOutbox.SingleAsync(o => o.ApplicationId == id && o.Template == "member.welcome");
            var payload = JsonDocument.Parse(scope.ServiceProvider.GetRequiredService<IFieldProtector>().Unprotect(welcome.PayloadJson)).RootElement;
            setupToken = payload.GetProperty("setupToken").GetString()!;
        }

        (await _authClient.PostAsJsonAsync("/api/v1/auth/reset-password", new { email, token = setupToken, newPassword = Password })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        return await Login(email);
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

    private Task<HttpResponseMessage> Get(string token, string path, HttpClient? client = null) => Send(token, HttpMethod.Get, path, client: client);

    private async Task<HttpResponseMessage> Send(string token, HttpMethod method, string path, object? body = null, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await (client ?? _client).SendAsync(request);
    }

    private async Task<HttpResponseMessage> Put(string token, string path, string contentType, byte[] body)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}
