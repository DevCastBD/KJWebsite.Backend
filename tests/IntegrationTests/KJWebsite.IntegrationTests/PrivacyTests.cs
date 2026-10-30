using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthIdentityService.Data;
using AuthIdentityService.Data.Entities;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Auditing;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Intake;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class PrivacyTests : IDisposable, IAsyncLifetime
{
    private const string Password = "Sup3r-Secret-Pass!";
    private const string Admin = "/api/v1/admin/membership";
    private readonly AuthApiFactory _auth = new();
    private readonly MembershipApiFactory _baseMembership = new();
    private readonly HttpClient _authClient;
    private HttpClient _client = null!;
    private WebApplicationFactoryHolder _holder = null!;
    private string? _officerTok;
    private string? _adminTok;

    public PrivacyTests()
    {
        _authClient = _auth.CreateClient();
        _baseMembership.AuthHandler = _auth.Server.CreateHandler();
        Configure(new Dictionary<string, string?>());
    }

    public async Task InitializeAsync()
    {
        _officerTok = await Account("officer@example.org", "MembershipOfficer");
        _adminTok = await Account("admin@example.org", "Admin");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- retention ----

    [Fact]
    public async Task WithNoWindowSetNothingIsEverPurged()
    {
        await SeedApplication("old-rejected", "rejected", monthsAgo: 60);

        var dry = await Json(await Send(_adminTok!, HttpMethod.Post, $"{Admin}/retention/run"));
        var real = await Json(await Send(_adminTok!, HttpMethod.Post, $"{Admin}/retention/run?dryRun=false"));

        dry.GetProperty("items").GetArrayLength().Should().Be(0);
        real.GetProperty("purged").GetInt32().Should().Be(0);
        (await Count()).Should().Be(1);
    }

    [Fact]
    public async Task ApplicationsPastTheirWindowArePurgedAndEverythingElseIsKept()
    {
        Configure(new() { ["Privacy:RejectedApplicationMonths"] = "6", ["Privacy:PostponedApplicationMonths"] = "12", ["Privacy:NoticeVersion"] = "2026-10" });
        await SeedApplication("old-rejected", "rejected", monthsAgo: 7, withFile: true);
        await SeedApplication("fresh-rejected", "rejected", monthsAgo: 5);
        await SeedApplication("old-withdrawn", "withdrawn", monthsAgo: 40);
        await SeedApplication("old-postponed", "postponed", monthsAgo: 13);
        await SeedApplication("old-awaiting", "awaiting", monthsAgo: 40);
        await SeedApplication("old-approved", "approved", monthsAgo: 40);

        var dry = await Json(await Send(_adminTok!, HttpMethod.Post, $"{Admin}/retention/run"));
        dry.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        dry.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("applicationId").GetString()).Should().BeEquivalentTo(["app_old-rejected", "app_old-postponed"]);
        (await Count()).Should().Be(6, "a dry run deletes nothing");

        var real = await Json(await Send(_adminTok!, HttpMethod.Post, $"{Admin}/retention/run?dryRun=false"));
        var again = await Json(await Send(_adminTok!, HttpMethod.Post, $"{Admin}/retention/run?dryRun=false"));

        real.GetProperty("purged").GetInt32().Should().Be(2);
        again.GetProperty("purged").GetInt32().Should().Be(0);
        using var scope = _holder.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        (await db.Applications.Select(a => a.Id).ToListAsync()).Should().BeEquivalentTo(["app_fresh-rejected", "app_old-withdrawn", "app_old-awaiting", "app_old-approved"]);
        (await db.NotificationOutbox.AnyAsync(o => o.ApplicationId == "app_old-rejected")).Should().BeFalse();
        Directory.Exists(_holder.Factory.AttachmentsPath).Should().BeTrue();
        Directory.GetFiles(_holder.Factory.AttachmentsPath).Should().BeEmpty("the purged application's file is gone");

        var runs = await db.RetentionRuns.ToListAsync();
        runs.Should().HaveCount(3);
        runs.Single(r => r.Purged == 2).PolicyJson.Should().Contain("\"rejectedMonths\":6").And.Contain("\"postponedMonths\":12").And.Contain("2026-10");
        var items = await db.RetentionItems.ToListAsync();
        items.Should().HaveCount(2);
        string.Join('|', items.Select(i => i.ApplicationId + i.Status)).Should().NotContain("Tahmid").And.NotContain("@");
    }

    [Fact]
    public async Task RetentionCanOnlyBeRunByThoseWhoMayDelete()
    {
        (await _client.PostAsync($"{Admin}/retention/run", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(_officerTok!, HttpMethod.Post, $"{Admin}/retention/run")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(_officerTok!, HttpMethod.Get, $"{Admin}/erasure-requests")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(_adminTok!, HttpMethod.Get, $"{Admin}/retention/runs")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ThePublicEndpointStatesWhatActuallyRuns()
    {
        var unset = await Json(await _client.GetAsync("/api/v1/membership/privacy"));
        unset.GetProperty("retention").GetProperty("rejectedApplicationMonths").ValueKind.Should().Be(JsonValueKind.Null);
        unset.GetProperty("retention").GetProperty("automaticPurge").GetBoolean().Should().BeFalse();

        Configure(new() { ["Privacy:RejectedApplicationMonths"] = "6", ["Privacy:NoticeVersion"] = "v3", ["Privacy:DataLocation"] = "Dhaka data centre" });
        var set = await Json(await _client.GetAsync("/api/v1/membership/privacy"));
        set.GetProperty("retention").GetProperty("rejectedApplicationMonths").GetInt32().Should().Be(6);
        set.GetProperty("retention").GetProperty("withdrawnApplicationMonths").ValueKind.Should().Be(JsonValueKind.Null);
        set.GetProperty("retention").GetProperty("automaticPurge").GetBoolean().Should().BeTrue();
        set.GetProperty("noticeVersion").GetString().Should().Be("v3");
        set.GetProperty("dataLocation").GetString().Should().Be("Dhaka data centre");
    }

    // ---- consent ledger ----

    [Fact]
    public async Task SubmittingAnApplicationRecordsConsentWithTheNoticeVersion()
    {
        Configure(new() { ["Privacy:NoticeVersion"] = "2026-10" });

        var id = await Apply("consent@example.org");

        using var scope = _holder.Factory.Services.CreateScope();
        var record = await scope.ServiceProvider.GetRequiredService<MembershipDbContext>().ConsentRecords.SingleAsync(c => c.SubjectId == id);
        record.Purpose.Should().Be("application_processing");
        record.Granted.Should().BeTrue();
        record.Source.Should().Be("public_form");
        record.NoticeVersion.Should().Be("2026-10");
        record.SubjectKind.Should().Be("application");
    }

    // ---- applicants' own rights ----

    [Fact]
    public async Task AnApplicantCanExportTheirDataWithReferenceAndEmailOnly()
    {
        var id = await Apply("export-me@example.org", reason: "I love robots");
        var reference = await ReferenceOf(id);

        using var ok = await _client.PostAsJsonAsync("/api/v1/membership/applications/export", new { reference, email = "EXPORT-ME@example.org" });
        using var wrongEmail = await _client.PostAsJsonAsync("/api/v1/membership/applications/export", new { reference, email = "other@example.org" });
        using var unknown = await _client.PostAsJsonAsync("/api/v1/membership/applications/export", new { reference = "KJ-2026-ZZZZZZZZ", email = "export-me@example.org" });

        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = await Json(ok);
        data.GetProperty("application").GetProperty("reasonForJoining").GetString().Should().Be("I love robots");
        data.GetProperty("application").GetProperty("email").GetString().Should().Be("export-me@example.org");
        data.GetProperty("consentRecords").GetArrayLength().Should().Be(1);
        wrongEmail.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(wrongEmail)).GetProperty("error").GetRawText().Should().Be((await Json(unknown)).GetProperty("error").GetRawText());
    }

    [Fact]
    public async Task AnApplicantErasureIsARequestStaffCarryOut()
    {
        var id = await Apply("leave-us@example.org");
        var reference = await ReferenceOf(id);

        using var first = await _client.PostAsJsonAsync("/api/v1/membership/applications/erasure-request", new { reference, email = "leave-us@example.org" });
        using var second = await _client.PostAsJsonAsync("/api/v1/membership/applications/erasure-request", new { reference, email = "leave-us@example.org" });
        using var stranger = await _client.PostAsJsonAsync("/api/v1/membership/applications/erasure-request", new { reference, email = "stranger@example.org" });

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Json(second)).GetProperty("reference").GetString().Should().Be((await Json(first)).GetProperty("reference").GetString());
        stranger.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Count()).Should().Be(1, "nothing is deleted by asking");

        var pending = (await Json(await Send(_adminTok!, HttpMethod.Get, $"{Admin}/erasure-requests"))).GetProperty("items");
        pending.GetArrayLength().Should().Be(1);
        pending[0].GetProperty("subjectKind").GetString().Should().Be("application");
        var requestId = pending[0].GetProperty("id").GetString();

        using var done = await Send(_adminTok!, HttpMethod.Post, $"{Admin}/erasure-requests/{requestId}/execute");
        done.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Count()).Should().Be(0);
        (await _client.PostAsJsonAsync("/api/v1/membership/applications/export", new { reference, email = "leave-us@example.org" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(_adminTok!, HttpMethod.Post, $"{Admin}/erasure-requests/{requestId}/execute")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(await Send(_adminTok!, HttpMethod.Get, $"{Admin}/erasure-requests?status=completed"))).GetProperty("items").GetArrayLength().Should().Be(1);
    }

    // ---- members' erasure ----

    [Fact]
    public async Task ErasingAMemberRemovesThemEverywhereAndLeavesOnlyAnonymousRecords()
    {
        var m = await NewMember("erase-me@example.org", "Zubayer", "Haque");
        await Send(m.Token, HttpMethod.Put, "/api/v1/members/me/consents", new { name = true, photograph = false, expertise = true });
        await Send(m.Token, HttpMethod.Patch, "/api/v1/members/me/profile", new { changes = new { phone = "01811119999", firstName = "Zubair" } });
        var committee = (await Json(await Send(_officerTok!, HttpMethod.Post, $"{Admin}/committees", new { nameEn = "Exec", nameBn = "নির্বাহী", kind = "executive" }))).GetProperty("id").GetString();
        await Send(_officerTok!, HttpMethod.Post, $"{Admin}/committees/{committee}/members", new { memberId = m.Id, positionKey = "chair", positionEn = "Chair", startsOn = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
        await Send(_officerTok!, HttpMethod.Post, $"{Admin}/applications/{m.ApplicationId}/notes", new { text = "Impressive NOTE-ZUBAYER" });
        using (var seed = _holder.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<MembershipDbContext>();
            var store = seed.ServiceProvider.GetRequiredService<IAttachmentStore>();
            var key = Guid.NewGuid().ToString("N");
            await store.SaveAsync(key, new MemoryStream([1, 2, 3]), CancellationToken.None);
            var member = await db.Members.SingleAsync(x => x.Id == m.Id);
            member.PhotoKey = key;
            member.PhotoContentType = "image/png";
            await db.SaveChangesAsync();
        }

        await Send(m.Token, HttpMethod.Post, "/api/v1/members/me/deletion-request");
        var requestId = (await Json(await Send(_adminTok!, HttpMethod.Get, $"{Admin}/erasure-requests"))).GetProperty("items")[0].GetProperty("id").GetString();

        using var executed = await Send(_adminTok!, HttpMethod.Post, $"{Admin}/erasure-requests/{requestId}/execute");
        executed.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _holder.Factory.Services.CreateScope();
        var mdb = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        var member2 = await mdb.Members.AsNoTracking().SingleAsync(x => x.Id == m.Id);
        member2.FirstName.Should().Be("Erased");
        member2.Status.Should().Be("erased");
        new[] { member2.PhoneProtected, member2.AddressProtected, member2.DateOfBirthProtected, member2.BloodGroupProtected, member2.NameBn, member2.Expertise, member2.PhotoKey }.Should().AllBeEquivalentTo((string?)null);
        new[] { member2.ConsentName, member2.ConsentExpertise }.Should().AllBeEquivalentTo(false);
        (await mdb.Applications.AnyAsync(a => a.Id == m.ApplicationId)).Should().BeFalse();
        (await mdb.NotificationOutbox.AnyAsync(o => o.ApplicationId == m.ApplicationId)).Should().BeFalse();
        (await mdb.ProfileChanges.AnyAsync(c => c.MemberId == m.Id)).Should().BeFalse();
        (await mdb.MemberStatusEvents.Where(e => e.MemberId == m.Id).AnyAsync(e => e.Note != null || e.ReasonProtected != null)).Should().BeFalse();
        (await mdb.CommitteeMembers.SingleAsync()).EndsOn.Should().NotBeNull();
        Directory.GetFiles(_holder.Factory.AttachmentsPath).Should().BeEmpty("the photo and attachments are gone");
        (await mdb.DeletionRequests.SingleAsync()).CompletedAt.Should().NotBeNull();
        var record = await mdb.ErasureRecords.SingleAsync();
        record.SubjectId.Should().Be(m.Id);
        (record.SubjectId + record.SummaryJson).Should().NotContain("Zubayer").And.NotContain("erase-me");

        // Public views no longer show them.
        (await _client.GetStringAsync("/api/v1/members/directory")).Should().NotContain("Zubayer").And.NotContain("Erased");
        (await _client.GetStringAsync("/api/v1/committees")).Should().NotContain("Zubayer").And.NotContain("Erased");

        // Identity: no email, no profile, no way in.
        using var authScope = _auth.Services.CreateScope();
        var users = authScope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>();
        var user = await authScope.ServiceProvider.GetRequiredService<AuthDbContext>().Users.Include(u => u.Profile).SingleAsync(u => u.Id == m.UserId);
        user.Status.Should().Be("erased");
        user.Email.Should().EndWith("@erased.invalid");
        user.Profile.Should().BeNull();
        user.PasswordHash.Should().BeNull();
        (await users.GetRolesAsync(user)).Should().BeEmpty();
        (await _authClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "erase-me@example.org", password = Password })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Audit: what happened stays, who they were does not.
        var adb = authScope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var mAudit = await mdb.Set<AuditEvent>().Where(a => a.ActorId == m.UserId).ToListAsync();
        mAudit.Should().NotBeEmpty("their consent and profile edits were audited");
        mAudit.Should().OnlyContain(a => a.ActorEmail == null && a.Ip == null && a.UserAgent == null);
        var everything = string.Join('\n', (await mdb.Set<AuditEvent>().ToListAsync()).Concat(await adb.Set<AuditEvent>().ToListAsync()).Select(a => a.Before + a.After + a.ActorEmail));
        everything.Should().NotContain("Zubayer").And.NotContain("Zubair").And.NotContain("erase-me@").And.NotContain("01811119999").And.NotContain("NOTE-ZUBAYER");
        (await adb.Set<AuditEvent>().Where(a => a.ActorId == m.UserId).AnyAsync(a => a.ActorEmail != null)).Should().BeFalse();
    }

    [Fact]
    public async Task ErasureNeedsThePrivacyPermissionAndSurvivesIdentityBeingDown()
    {
        var m = await NewMember("flaky@example.org");
        await Send(m.Token, HttpMethod.Post, "/api/v1/members/me/deletion-request");
        var requestId = (await Json(await Send(_adminTok!, HttpMethod.Get, $"{Admin}/erasure-requests"))).GetProperty("items")[0].GetProperty("id").GetString();

        (await Send(_officerTok!, HttpMethod.Post, $"{Admin}/erasure-requests/{requestId}/execute")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var healthy = _baseMembership.AuthHandler;
        _baseMembership.AuthHandler = new DownHandler();
        using (var down = _holder.Factory.WithWebHostBuilder(_ => { }).CreateClient())
        {
            using var failed = await Send(_adminTok!, HttpMethod.Post, $"{Admin}/erasure-requests/{requestId}/execute", client: down);
            failed.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }

        _baseMembership.AuthHandler = healthy;
        using (var scope = _holder.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
            (await db.Members.SingleAsync(x => x.Id == m.Id)).Status.Should().Be("active", "nothing changed while Identity was down");
            (await db.Applications.AnyAsync(a => a.Id == m.ApplicationId)).Should().BeTrue();
        }

        (await Send(_adminTok!, HttpMethod.Post, $"{Admin}/erasure-requests/{requestId}/execute")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnApplicantWhoBecameAMemberAsksAsThatMember()
    {
        var m = await NewMember("both@example.org");
        var reference = await ReferenceOf(m.ApplicationId);

        using var response = await _client.PostAsJsonAsync("/api/v1/membership/applications/erasure-request", new { reference, email = "both@example.org" });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var pending = (await Json(await Send(_adminTok!, HttpMethod.Get, $"{Admin}/erasure-requests"))).GetProperty("items")[0];
        pending.GetProperty("subjectKind").GetString().Should().Be("member");
        pending.GetProperty("memberCode").GetString().Should().Be(m.Code);
    }

    public void Dispose()
    {
        _client.Dispose();
        _authClient.Dispose();
        _holder.Dispose();
        _baseMembership.Dispose();
        _auth.Dispose();
    }

    // ---- helpers ----

    private sealed class WebApplicationFactoryHolder(MembershipApiFactory root, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<MembershipService.MembershipServiceTestEntryPoint> factory) : IDisposable
    {
        public MembershipApiFactory Root { get; } = root;

        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<MembershipService.MembershipServiceTestEntryPoint> Inner { get; } = factory;

        public MembershipApiFactory Factory => Root;

        public void Dispose() => Inner.Dispose();
    }

    private void Configure(Dictionary<string, string?> settings)
    {
        _client?.Dispose();
        _holder?.Dispose();
        var derived = _baseMembership.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }
        });
        _holder = new WebApplicationFactoryHolder(_baseMembership, derived);
        _client = derived.CreateClient();
    }

    private async Task<int> Count()
    {
        using var scope = _holder.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MembershipDbContext>().Applications.CountAsync();
    }

    private async Task SeedApplication(string key, string status, int monthsAgo, bool withFile = false)
    {
        using var scope = _holder.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IFieldProtector>();
        var at = DateTimeOffset.UtcNow.AddMonths(-monthsAgo).AddMinutes(-1);
        var application = new ApplicationEntity
        {
            Id = $"app_{key}",
            Reference = $"KJ-2026-{key}",
            Language = "en",
            Status = status,
            FirstName = "Tahmid",
            LastName = "Rahman",
            AcademicInstitute = "x",
            Occupation = "x",
            EmailProtected = protector.Protect("t@example.org"),
            PhoneProtected = protector.Protect("01711112222"),
            BloodGroupProtected = protector.Protect("O+"),
            CurrentAddressProtected = protector.Protect("Dhaka"),
            EmailHash = key,
            PhoneHash = key,
            CreatedAt = at,
            SubmittedAt = at
        };
        application.StatusEvents.Add(new ApplicationStatusEventEntity { Status = "awaiting", At = at.AddDays(-30), By = "system" });
        application.StatusEvents.Add(new ApplicationStatusEventEntity { Status = status, At = at, By = "usr_x" });
        if (withFile)
        {
            var storageKey = Guid.NewGuid().ToString("N");
            await scope.ServiceProvider.GetRequiredService<IAttachmentStore>().SaveAsync(storageKey, new MemoryStream([1, 2, 3]), CancellationToken.None);
            application.Attachments.Add(new AttachmentEntity { Kind = "cv", StorageKey = storageKey, FileName = "cv.pdf", ContentType = "application/pdf", Bytes = 3, UploadedAt = at });
        }

        db.Applications.Add(application);
        db.NotificationOutbox.Add(new NotificationOutboxEntity { ApplicationId = application.Id, Template = "x", Language = "en", RecipientProtected = application.EmailProtected, PayloadJson = "x", CreatedAt = at });
        await db.SaveChangesAsync();
    }

    private async Task<string> Apply(string email, string? reason = null)
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
            language = "en",
            reasonForJoining = reason
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(created)).GetProperty("id").GetString()!;
    }

    private async Task<string> ReferenceOf(string applicationId)
    {
        using var scope = _holder.Factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<MembershipDbContext>().Applications.SingleAsync(a => a.Id == applicationId)).Reference;
    }

    private sealed record Made(string Id, string UserId, string ApplicationId, string Code, string Token);

    private async Task<Made> NewMember(string email, string first = "Tahmid", string last = "Rahman")
    {
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
            language = "en"
        });
        var id = (await Json(created)).GetProperty("id").GetString()!;
        foreach (var to in new[] { "under_review", "shortlisted", "interview_scheduled", "approved" })
        {
            using var moved = await Send(_officerTok!, HttpMethod.Post, $"{Admin}/applications/transition", new { ids = new[] { id }, to, interviewAt = DateTimeOffset.UtcNow.AddDays(2) });
            (await Json(moved)).GetProperty("results")[0].GetProperty("ok").GetBoolean().Should().BeTrue(to);
        }

        using var scope = _holder.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        var welcome = await db.NotificationOutbox.SingleAsync(o => o.ApplicationId == id && o.Template == "member.welcome");
        var setup = JsonDocument.Parse(scope.ServiceProvider.GetRequiredService<IFieldProtector>().Unprotect(welcome.PayloadJson)).RootElement.GetProperty("setupToken").GetString()!;
        var member = await db.Members.SingleAsync(x => x.ApplicationId == id);
        (await _authClient.PostAsJsonAsync("/api/v1/auth/reset-password", new { email, token = setup, newPassword = Password })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        return new Made(member.Id, member.UserId, id, member.MemberCode, await Login(email));
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

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private sealed class DownHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("identity is down");
    }
}
