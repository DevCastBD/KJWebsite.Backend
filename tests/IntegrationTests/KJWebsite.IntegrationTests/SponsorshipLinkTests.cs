using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SponsorshipService.Data;
using SponsorshipService.Links;
using SponsorshipService.Students;

namespace KJWebsite.IntegrationTests;

public sealed class SponsorshipLinkApiFactory : SqliteWebApplicationFactory<SponsorshipService.SponsorshipServiceTestEntryPoint, SponsorshipDbContext>
{
    public string FilesPath { get; } = Path.Combine(Path.GetTempPath(), $"kj-test-links-{Guid.NewGuid():N}");
    public LinkProjects Projects { get; } = new();
    public string TimeZoneId { get; set; } = "Asia/Dhaka";
    public StudentConflictInterceptor Conflict { get; } = new();
    public ProgrammeCacheClock ProgrammeClock { get; } = new();
    public FakeFinanceDonors Finance { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sponsorship:FilesPath"] = FilesPath,
            ["Sponsorship:TimeZone"] = TimeZoneId,
            ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary)
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IProjectCatalogue>();
            services.AddSingleton<IProjectCatalogue>(Projects);
            services.RemoveAll<ProgrammeNames>();
            services.AddSingleton(new ProgrammeNames(Projects, ProgrammeClock));
            services.RemoveAll<IFinanceDonors>();
            services.AddSingleton<IFinanceDonors>(Finance);
        });
    }

    protected override void ConfigureDbContext(DbContextOptionsBuilder options, IServiceProvider provider) => options.AddInterceptors(Conflict);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(FilesPath)) Directory.Delete(FilesPath, recursive: true);
    }
}

public sealed class ProgrammeCacheClock : TimeProvider
{
    public TimeSpan Advance { get; set; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Advance;
}

public sealed class LinkProjects : IProjectCatalogue
{
    public bool Down { get; set; }
    public Task<IReadOnlyList<ProgrammeRef>?> ListAsync(string language, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProgrammeRef>?>(Down ? null : [new("pathshala", language == "bn" ? "পাঠশালা" : "Pathshala"), new("stipend", "Stipend Fund")]);
    public Task<bool?> ExistsAsync(string slug, CancellationToken cancellationToken) => Task.FromResult<bool?>(Down ? null : slug is "pathshala" or "stipend");
}

public sealed class StudentConflictInterceptor : SaveChangesInterceptor
{
    public bool Armed { get; set; }
    public bool FailNotice { get; set; }
    public bool Fired { get; private set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var db = eventData.Context!;
        if (FailNotice && db.ChangeTracker.Entries<LinkEntity>().Any(e => e.State == EntityState.Modified))
            throw new DbUpdateException("Notice status save failed.");
        var student = db.ChangeTracker.Entries<StudentEntity>().FirstOrDefault(e => e.State == EntityState.Modified);
        if (Armed && student is not null)
        {
            Armed = false;
            await using var connection = new SqliteConnection(db.Database.GetConnectionString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE students SET Version = Version + 1 WHERE Id = $id";
            command.Parameters.AddWithValue("$id", student.Entity.Id);
            (await command.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
            Fired = true;
        }
        return result;
    }
}

public sealed class FakeFinanceDonors : IFinanceDonors
{
    public sealed record Donor(string Id, string AccountSubject, string Status = "active", string AccountStatus = "active", string Language = "en");
    public ConcurrentDictionary<string, Donor> Donors { get; } = new();
    public ConcurrentDictionary<string, string> NoticeOutcomes { get; } = new();
    public ConcurrentQueue<(string DonorId, string LinkId)> Notices { get; } = new();
    public ConcurrentQueue<string> Calls { get; } = new();
    public bool Down { get; set; }

    public Task<FinanceResult<FinanceTarget>> TargetAsync(string donorId, string bearer, CancellationToken ct)
    {
        Calls.Enqueue("target");
        return Task.FromResult(Down ? new FinanceResult<FinanceTarget>(FinanceOutcome.Unavailable) :
            Donors.TryGetValue(donorId, out var d) ? new(FinanceOutcome.Ok, new(d.Id, d.AccountSubject, d.Status, d.AccountStatus)) : new(FinanceOutcome.NotFound));
    }

    public Task<FinanceResult<string>> NotifyAsync(string donorId, string linkId, string bearer, CancellationToken ct)
    {
        Calls.Enqueue("notice");
        Notices.Enqueue((donorId, linkId));
        var outcome = NoticeOutcomes.GetValueOrDefault(donorId, "queued");
        return Task.FromResult(Down || outcome is not ("queued" or "suppressed") ? new FinanceResult<string>(FinanceOutcome.Unavailable) : new(FinanceOutcome.Ok, outcome));
    }

    public Task<FinanceResult<FinanceMe>> MeAsync(string bearer, CancellationToken ct)
    {
        Calls.Enqueue("me");
        if (Down) return Task.FromResult(new FinanceResult<FinanceMe>(FinanceOutcome.Unavailable));
        var payload = bearer.Split('.')[1].Replace('-', '+').Replace('_', '/');
        using var json = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
        var sub = json.RootElement.GetProperty("sub").GetString();
        var donor = Donors.Values.SingleOrDefault(d => d.AccountSubject == sub);
        return Task.FromResult(donor is null ? new FinanceResult<FinanceMe>(FinanceOutcome.NotFound, Code: "NOT_A_DONOR") :
            donor.Status == "inactive" ? new(FinanceOutcome.Forbidden, Code: "DONOR_INACTIVE") :
            donor.AccountStatus == "disabled" ? new(FinanceOutcome.Forbidden, Code: "ACCOUNT_DISABLED") :
            new(FinanceOutcome.Ok, new(donor.Id, donor.Language)));
    }
}

public sealed partial class SponsorshipLinkTests : IDisposable
{
    private const string Base = "/api/v1/admin/sponsorship";
    private const string Mine = "/api/v1/donors/me/students";
    private const string DonorId = "dnr_x";
    private static readonly string Staff = TestTokens.Issue([Permissions.SponsorshipView, Permissions.SponsorshipStudentManage, Permissions.SponsorshipLinkManage, Permissions.AuditView]);
    private static readonly string Super = TestTokens.Issue([Permissions.SponsorshipView, Permissions.SponsorshipStudentManage, Permissions.SponsorshipLinkManage, Permissions.AuditView, Permissions.SponsorshipLinkVoid]);
    private static readonly string Donor = DonorToken("usr_donor_x");
    private static readonly string NoKeys = TestTokens.Issue([]);
    private SponsorshipLinkApiFactory _factory = new();
    private HttpClient _client;

    public SponsorshipLinkTests()
    {
        _factory.Finance.Donors[DonorId] = new(DonorId, "usr_donor_x");
        _factory.Finance.Donors["dnr_y"] = new("dnr_y", "usr_donor_y");
        _client = _factory.CreateClient();
    }

    public void Dispose() { _client.Dispose(); _factory.Dispose(); }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("POST", false)]
    [InlineData("POST", true)]
    public async Task StaffRoutesRefuseMissingAndWrongPermissionsAndPreventCaching(string method, bool unlink)
    {
        var path = Links() + (unlink ? "/stu_missing/unlink" : "");
        foreach (var (token, status) in new[] { ((string?)null, 401), (Donor, 403), (NoKeys, 403),
            (TestTokens.Issue(method == "GET" ? [Permissions.SponsorshipLinkManage] : [Permissions.SponsorshipView]), 403) })
        {
            using var response = await Send(new(method), path, token, new { });
            await Status(response, status);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        await Counts(0, 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/stu_missing")]
    [InlineData("/stu_missing/photo")]
    public async Task DonorRoutesRequireDonorRoleAndPreventCachingEvenOnRefusal(string suffix)
    {
        foreach (var (token, status) in new[] { ((string?)null, 401), (Staff, 403), (NoKeys, 403), (Donor, suffix == "" ? 200 : 404) })
        {
            using var response = await Send(HttpMethod.Get, Mine + suffix, token);
            await Status(response, status);
            response.Headers.CacheControl!.Private.Should().BeTrue();
            response.Headers.CacheControl.NoStore.Should().BeTrue();
            response.Headers.Vary.Should().Contain("Authorization");
        }
    }

    [Theory]
    [InlineData(null, "today", "studentId")]
    [InlineData("", "today", "studentId")]
    [InlineData("long", "today", "studentId")]
    [InlineData("student", null, "startDate")]
    [InlineData("student", "not-a-date", "startDate")]
    [InlineData("student", "2026-02-30", "startDate")]
    [InlineData("student", "2026-1-1", "startDate")]
    public async Task LinkValidationNamesTheInvalidFieldAndHasNoSideEffects(string? studentId, string? startDate, string field)
    {
        if (startDate == "today") startDate = Date();
        if (studentId == "long") studentId = new string('x', 65);
        using var response = await Send(HttpMethod.Post, Links(), Staff, new { studentId, startDate });
        await Error(response, 400, "VALIDATION_ERROR");
        (await response.Content.ReadAsStringAsync()).Should().Contain(field);
        await Counts(0, 0);
    }

    [Theory]
    [InlineData("inactive-student", 409, "STUDENT_NOT_ACTIVE")]
    [InlineData("unknown-student", 404, "NOT_FOUND")]
    [InlineData("unknown-donor", 404, "NOT_FOUND")]
    [InlineData("inactive-donor", 409, "DONOR_INACTIVE")]
    [InlineData("down", 503, "FINANCE_UNAVAILABLE")]
    public async Task LinkRefusalsLeaveNoLinkOrNotice(string scenario, int status, string code)
    {
        var id = scenario == "unknown-student" ? "stu_missing" : await Create(scenario == "inactive-student" ? "inactive" : "active");
        if (scenario == "inactive-donor") _factory.Finance.Donors[DonorId] = new(DonorId, "usr_donor_x", Status: "inactive");
        _factory.Finance.Down = scenario == "down";
        using var response = await Link(id, scenario == "unknown-donor" ? "missing" : DonorId);
        await Error(response, status, code);
        await Counts(0, 0);
    }

    [Theory]
    [InlineData("queued", "queued")]
    [InlineData("suppressed", "suppressed")]
    [InlineData("failure", "pending")]
    public async Task LinkingCreatesTheLinkAndKeepsItWhenTheNoticeFails(string outcome, string expected)
    {
        var id = await Create();
        _factory.Finance.NoticeOutcomes[DonorId] = outcome;
        using var response = await Link(id);
        await Status(response, 201);
        var link = await Json(response);
        link.GetProperty("noticeStatus").GetString().Should().Be(expected);
        response.Headers.Location!.OriginalString.Should().Be(Links());
        (await Items(Links() + "?status=all", Staff)).Single().GetProperty("noticeStatus").GetString().Should().Be(expected);
        _factory.Finance.Notices.Should().ContainSingle().Which.Should().Be((DonorId, link.GetProperty("id").GetString()!));
        (await Items(Mine, Donor)).Should().ContainSingle().Which.GetProperty("id").GetString().Should().Be(id);
        await Counts(1, 1);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task DonorsSeeLinksOnlyFromTheirStartDate(int days, bool visible)
    {
        var id = await Create();
        using var response = await Link(id, start: Date(days));
        await Status(response, 201);
        (await Items(Mine, Donor)).Count.Should().Be(visible ? 1 : 0);
        if (!visible)
        {
            using var detail = await Send(HttpMethod.Get, $"{Mine}/{id}", Donor);
            await Error(detail, 404, "NOT_FOUND");
        }
    }

    [Theory]
    [InlineData(false, "LINK_EXISTS")]
    [InlineData(true, "STUDENT_ALREADY_SPONSORED")]
    public async Task DuplicateActiveLinksAreRefused(bool otherDonor, string code)
    {
        var id = await Create();
        await CreatedLink(id);
        using var response = await Link(id, otherDonor ? "dnr_y" : DonorId);
        await Error(response, 409, code);
        await Counts(1, 1);
    }

    [Theory]
    [InlineData(false, "LINK_EXISTS")]
    [InlineData(true, "STUDENT_ALREADY_SPONSORED")]
    public async Task ParallelLinksHaveExactlyOneWinner(bool otherDonor, string code)
    {
        var id = await Create();
        var responses = await Task.WhenAll(Link(id), Link(id, otherDonor ? "dnr_y" : DonorId));
        try
        {
            responses.Select(r => (int)r.StatusCode).Should().BeEquivalentTo([201, 409]);
            await Error(responses.Single(r => (int)r.StatusCode == 409), 409, code);
            await Counts(1, 1);
            (await Rows()).Should().ContainSingle(l => l.EndType == null);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("future")]
    [InlineData("before")]
    public async Task EndDateMustExistAndBeBetweenStartAndToday(string? date)
    {
        var id = await Create();
        await CreatedLink(id);
        using var response = await Unlink(id, endDate: date == "future" ? Date(1) : date == "before" ? Date(-11) : null);
        await Error(response, 400, "VALIDATION_ERROR");
        (await response.Content.ReadAsStringAsync()).Should().Contain("endDate");
        (await Rows()).Should().ContainSingle(l => l.EndType == null);
        (await Frozen()).Should().BeEmpty();
    }

    [Fact]
    public async Task UnlinkRequiresAnActiveLinkAndCannotBeRepeated()
    {
        var id = await Create();
        using var missing = await Unlink(id);
        await Error(missing, 409, "LINK_NOT_ACTIVE");
        await CreatedLink(id);
        using var first = await Unlink(id);
        await Status(first, 200);
        using var second = await Unlink(id);
        await Error(second, 409, "LINK_NOT_ACTIVE");
        (await Frozen()).Should().ContainSingle();
    }

    [Fact]
    public async Task EndedLinkKeepsFrozenDetailAndPhotoAfterLiveEditsAndWithdrawal()
    {
        var id = await Consented();
        await CreatedLink(id);
        var photo = await Photo(id);
        using var beforeEnd = await Send(HttpMethod.Get, $"{Base}/students/{id}", Staff);
        var financeCalls = _factory.Finance.Calls.ToArray();
        using var ended = await Unlink(id);
        await Status(ended, 200);
        var copy = (await Frozen()).Should().ContainSingle().Subject;
        copy.StoryEn.Should().Be("Loves maths.");
        copy.PhotoKey.Should().NotBeNullOrEmpty();
        using var live = await Send(HttpMethod.Get, $"{Base}/students/{id}", Staff);
        live.Headers.ETag!.Tag.Should().NotBe(beforeEnd.Headers.ETag!.Tag);
        using var edit = await Send(HttpMethod.Patch, $"{Base}/students/{id}", Staff,
            new { preferredName = "Changed", storyEn = "New live story" }, live.Headers.ETag!.Tag);
        await Status(edit, 200);
        await Withdraw(id);
        _factory.Finance.Calls.Should().Equal(financeCalls);
        using var changed = await Send(HttpMethod.Get, $"{Base}/students/{id}", Staff);
        var liveJson = await Json(changed);
        liveJson.GetProperty("preferredName").GetString().Should().Be("Changed");
        liveJson.GetProperty("storyEn").GetString().Should().Be("New live story");
        var row = (await Items(Mine, Donor)).Should().ContainSingle().Subject;
        row.GetProperty("sponsorship").GetString().Should().Be("ended");
        row.GetProperty("endedOn").GetString().Should().Be(Date());
        row.GetProperty("preferredName").GetString().Should().Be("Rahima");
        row.GetProperty("story").GetString().Should().Be("Loves maths.");
        row.GetProperty("hasPhoto").GetBoolean().Should().BeTrue();
        using var detail = await Send(HttpMethod.Get, $"{Mine}/{id}", Donor);
        await Status(detail, 200);
        var frozen = await Json(detail);
        frozen.GetProperty("preferredName").GetString().Should().Be("Rahima");
        frozen.GetProperty("story").GetString().Should().Be("Loves maths.");
        (await Photo(id)).Should().Equal(photo);
        _factory.Finance.Calls.Should().OnlyContain(c => c == "target" || c == "notice" || c == "me");
        _factory.Finance.Notices.Should().ContainSingle();
    }

    [Fact]
    public async Task WithdrawalBeforeEndingExcludesStoryAndPhotoFromFrozenCopy()
    {
        var id = await Consented();
        await CreatedLink(id);
        await Withdraw(id);
        using var response = await Unlink(id);
        await Status(response, 200);
        var copy = (await Frozen()).Should().ContainSingle().Subject;
        copy.StoryEn.Should().BeNull();
        copy.StoryBn.Should().BeNull();
        copy.PhotoKey.Should().BeNull();
        var row = (await Items(Mine, Donor)).Should().ContainSingle().Subject;
        row.GetProperty("hasPhoto").GetBoolean().Should().BeFalse();
        row.GetProperty("story").ValueKind.Should().Be(JsonValueKind.Null);
        using var detail = await Send(HttpMethod.Get, $"{Mine}/{id}", Donor);
        (await Json(detail)).GetProperty("story").ValueKind.Should().Be(JsonValueKind.Null);
        using var photo = await Send(HttpMethod.Get, $"{Mine}/{id}/photo", Donor);
        await Error(photo, 404, "NOT_FOUND");
    }

    [Fact]
    public async Task MistakenUnlinkRequiresVoidPermissionAndLeavesTheLinkUnchangedOnRefusal()
    {
        var id = await Consented();
        await CreatedLink(id);
        using var response = await Unlink(id, "mistaken", token: Staff, reason: "Wrong donor");
        await Error(response, 403, "FORBIDDEN");
        (await Rows()).Should().ContainSingle(l => l.EndType == null);
        (await Frozen()).Should().BeEmpty();
        (await Items(Mine, Donor)).Should().ContainSingle();
        using var detail = await Send(HttpMethod.Get, $"{Mine}/{id}", Donor);
        await Status(detail, 200);
        (await Photo(id)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task MistakenUnlinkRequiresAReason()
    {
        var id = await Create();
        await CreatedLink(id);
        using var response = await Unlink(id, "mistaken", reason: null);
        await Error(response, 400, "VALIDATION_ERROR");
        (await response.Content.ReadAsStringAsync()).Should().Contain("reason");
        (await Rows()).Should().ContainSingle(l => l.EndType == null);
    }

    [Fact]
    public async Task MistakenUnlinkRevokesTheSameTokenWithoutAFrozenCopyAndAllowsRelinking()
    {
        var id = await Consented();
        await CreatedLink(id);
        (await Photo(id)).Should().NotBeEmpty();
        using var before = await Send(HttpMethod.Get, $"{Mine}/{id}", Donor);
        await Status(before, 200);
        using var response = await Unlink(id, "mistaken", reason: "Wrong donor");
        await Status(response, 200);
        foreach (var path in new[] { $"{Mine}/{id}", $"{Mine}/{id}/photo" })
        {
            using var refused = await Send(HttpMethod.Get, path, Donor);
            await Error(refused, 404, "NOT_FOUND");
        }
        (await Frozen()).Should().BeEmpty();
        (await Items(Links(), Staff)).Should().BeEmpty();
        var history = (await Items(Links() + "?status=all", Staff)).Should().ContainSingle().Subject;
        history.GetProperty("endType").GetString().Should().Be("mistaken");
        history.GetProperty("endReason").GetString().Should().Be("Wrong donor");
        await CreatedLink(id);
        (await Rows()).Should().HaveCount(2).And.ContainSingle(l => l.EndType == null);
        (await Items(Mine, Donor)).Should().ContainSingle();
    }

    [Fact]
    public async Task AfterAMistakenUnlinkTheListHasNoEntryAndDetailAndPhotoHaveTheSameNotFoundBody()
    {
        var id = await Consented();
        await CreatedLink(id);
        foreach (var path in new[] { Mine, $"{Mine}/{id}", $"{Mine}/{id}/photo" })
        {
            using var before = await Send(HttpMethod.Get, path, Donor);
            await Status(before, 200);
        }
        using var unlink = await Unlink(id, "mistaken", reason: "Wrong donor");
        await Status(unlink, 200);

        // The same access token: the list is still a 200 but holds nothing (not even an "ended" row), and the student itself is a 404.
        (await Items(Mine, Donor)).Should().BeEmpty();
        var bodies = new List<string>();
        foreach (var path in new[] { $"{Mine}/{id}", $"{Mine}/{id}/photo" })
        {
            using var response = await Send(HttpMethod.Get, path, Donor);
            await Error(response, 404, "NOT_FOUND");
            bodies.Add(await StableBody(response));
        }
        bodies.Distinct().Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/photo")]
    public async Task InaccessibleStudentsHaveIdenticalNotFoundBodies(string suffix)
    {
        var other = await Create();
        await CreatedLink(other, "dnr_y");
        var mistaken = await Create();
        await CreatedLink(mistaken);
        using var unlink = await Unlink(mistaken, "mistaken", reason: "Wrong donor");
        await Status(unlink, 200);
        var never = await Create();
        var bodies = new List<string>();
        foreach (var id in new[] { other, mistaken, never, "stu_nonexistent" })
        {
            using var response = await Send(HttpMethod.Get, $"{Mine}/{id}{suffix}", Donor);
            await Error(response, 404, "NOT_FOUND");
            bodies.Add(await StableBody(response));
        }
        bodies.Distinct().Should().ContainSingle();
    }

    [Theory]
    [InlineData("inactive", 403, "DONOR_INACTIVE")]
    [InlineData("disabled", 403, "ACCOUNT_DISABLED")]
    [InlineData("unknown", 404, "NOT_A_DONOR")]
    [InlineData("down", 503, "FINANCE_UNAVAILABLE")]
    public async Task EveryDonorReadRechecksFinanceState(string state, int status, string code)
    {
        var id = await Consented();
        await CreatedLink(id);
        _factory.Finance.Donors[DonorId] = new(DonorId, "usr_donor_x", Status: state == "inactive" ? "inactive" : "active", AccountStatus: state == "disabled" ? "disabled" : "active");
        if (state == "unknown") _factory.Finance.Donors.TryRemove(DonorId, out _);
        _factory.Finance.Down = state == "down";
        foreach (var suffix in new[] { "", $"/{id}", $"/{id}/photo" })
        {
            using var response = await Send(HttpMethod.Get, Mine + suffix, Donor);
            await Error(response, status, code);
        }
    }

    [Fact]
    public async Task DonorIdInQueryOrBodyCannotChangeOwnership()
    {
        var id = await Consented();
        await CreatedLink(id);
        var other = await Create();
        await CreatedLink(other, "dnr_y");
        foreach (var suffix in new[] { "", $"/{id}", $"/{id}/photo" })
        {
            using var baseline = await Send(HttpMethod.Get, Mine + suffix, Donor);
            using var injected = await Send(HttpMethod.Get, Mine + suffix + "?donorId=dnr_y", Donor, new { donorId = "dnr_y", id = "dnr_y" });
            await Status(baseline, 200);
            await Status(injected, 200);
            (await injected.Content.ReadAsByteArrayAsync()).Should().Equal(await baseline.Content.ReadAsByteArrayAsync());
        }
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task DonorWriteMethodsAreRefusedWithoutChangingAnything(string method)
    {
        var id = await Consented();
        await CreatedLink(id);
        var rows = JsonSerializer.Serialize(await Rows());
        var students = JsonSerializer.Serialize(await Items(Mine, Donor));
        var photo = await Photo(id);
        var calls = _factory.Finance.Calls.Count;
        foreach (var suffix in new[] { "", $"/{id}", $"/{id}/photo" })
        {
            using var response = await Send(new(method), Mine + suffix, Donor, new { preferredName = "Changed" });
            await Error(response, 405, "METHOD_NOT_ALLOWED");
            response.Content.Headers.Allow.Should().BeEquivalentTo(["GET"]);
        }
        _factory.Finance.Calls.Count.Should().Be(calls);
        JsonSerializer.Serialize(await Rows()).Should().Be(rows);
        JsonSerializer.Serialize(await Items(Mine, Donor)).Should().Be(students);
        (await Photo(id)).Should().Equal(photo);
        (await Frozen()).Should().BeEmpty();
    }

    [Fact]
    public async Task ActivePhotoDisappearsFromDonorReadsWhenConsentIsWithdrawn()
    {
        var id = await Consented();
        await CreatedLink(id);
        (await Photo(id)).Should().NotBeEmpty();
        await Withdraw(id);
        (await Items(Mine, Donor)).Single().GetProperty("hasPhoto").GetBoolean().Should().BeFalse();
        using var response = await Send(HttpMethod.Get, $"{Mine}/{id}/photo", Donor);
        await Error(response, 404, "NOT_FOUND");
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task FrozenPhotoCannotBeReplacedOrDeleted(string method)
    {
        var id = await Consented();
        await CreatedLink(id);
        using var ended = await Unlink(id);
        await Status(ended, 200);
        var photo = await Photo(id);
        var files = Directory.GetFiles(_factory.FilesPath, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        using var response = method == "PUT" ? await UploadPhoto(id) : await Send(HttpMethod.Delete, $"{Base}/students/{id}/photo", Staff);
        await Error(response, 409, "PHOTO_FROZEN");
        (await Photo(id)).Should().Equal(photo);
        Directory.GetFiles(_factory.FilesPath, "*", SearchOption.AllDirectories).Should().BeEquivalentTo(files.Keys);
        foreach (var file in files) File.ReadAllBytes(file.Key).Should().Equal(file.Value);
    }

    [Fact]
    public async Task ActiveLinkWithoutFrozenCopyAllowsPhotoReplacementAndDeletion()
    {
        var id = await Consented();
        await CreatedLink(id);
        using var replace = await UploadPhoto(id, "REPLACEDPIXELS");
        await Status(replace, 200);
        Encoding.ASCII.GetString(await Photo(id)).Should().Contain("REPLACEDPIXELS");
        using var delete = await Send(HttpMethod.Delete, $"{Base}/students/{id}/photo", Staff);
        await Status(delete, 204);
        (await Items(Mine, Donor)).Single().GetProperty("hasPhoto").GetBoolean().Should().BeFalse();
        (await Frozen()).Should().BeEmpty();
    }

    [Fact]
    public async Task StaffListDefaultsToActiveAndSupportsHistoryAndPaging()
    {
        var endedId = await Create();
        await CreatedLink(endedId);
        using var ended = await Unlink(endedId);
        await Status(ended, 200);
        var activeId = await Create();
        await CreatedLink(activeId);
        (await Items(Links(), Staff)).Should().ContainSingle().Which.GetProperty("studentId").GetString().Should().Be(activeId);
        var all = await Items(Links() + "?status=all", Staff);
        all.Should().HaveCount(2);
        using var page = await Send(HttpMethod.Get, Links() + "?status=all&limit=1&offset=1", Staff);
        await Status(page, 200);
        var json = await Json(page);
        json.GetProperty("total").GetInt32().Should().Be(2);
        json.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetString().Should().Be(all[1].GetProperty("id").GetString());
        using var invalid = await Send(HttpMethod.Get, Links() + "?status=bogus", Staff);
        await Error(invalid, 400, "VALIDATION_ERROR");
    }

    [Fact]
    public async Task StaffStudentsShowSponsorshipAndLinksAndCanFilterSponsoredStudents()
    {
        var linked = await Create();
        await Create();
        await CreatedLink(linked);
        var rows = await Items($"{Base}/students?sponsored=true", Staff);
        rows.Should().ContainSingle().Which.GetProperty("id").GetString().Should().Be(linked);
        rows.Single().GetProperty("sponsored").GetBoolean().Should().BeTrue();
        using var response = await Send(HttpMethod.Get, $"{Base}/students/{linked}", Staff);
        await Status(response, 200);
        var detail = await Json(response);
        detail.GetProperty("links").GetArrayLength().Should().Be(1);
    }

    [Theory]
    [InlineData("ended")]
    [InlineData("mistaken")]
    public async Task LinkCreationAndUnlinkRemainInTheAuditTrail(string type)
    {
        var id = await Consented();
        var linkId = await CreatedLink(id);
        using var unlink = await Unlink(id, type, reason: "Correcting assignment");
        await Status(unlink, 200);
        var events = await Items($"{Base}/audit?entityId={linkId}&limit=100", Staff);
        var links = events.Where(e => e.GetProperty("entity").GetString() == "Link").ToList();
        links.Select(e => e.GetProperty("action").GetString()).Should().Contain(["create", "update"]);
        foreach (var entry in links)
        {
            entry.GetProperty("actorId").GetString().Should().Be("usr_test");
            entry.GetProperty("at").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));
        }
        var creation = links.Single(e => e.GetProperty("action").GetString() == "create");
        creation.GetProperty("before").ValueKind.Should().Be(JsonValueKind.Null);
        creation.GetProperty("after").GetRawText().Should().Contain(id);
        var ending = links.Single(e => e.GetProperty("after").TryGetProperty("endType", out var endType) && endType.GetString() == type);
        ending.GetProperty("before").ValueKind.Should().Be(JsonValueKind.Object);
        ending.GetProperty("after").GetRawText().Should().Contain(type).And.Contain("Correcting assignment");
        var frozen = events.Where(e => e.GetProperty("entity").GetString() == "FrozenCopy").ToList();
        if (type == "ended") frozen.Should().ContainSingle();
        else frozen.Should().BeEmpty();
        var raw = JsonSerializer.Serialize(events);
        foreach (var secret in new[] { "Loves maths.", "storyEn", "storyBn", "photoKey" }) raw.Should().NotContain(secret);
        foreach (var copy in await Frozen()) raw.Should().NotContain(copy.PhotoKey!);
    }

    [Theory]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=101", "limit")]
    [InlineData("offset=-1", "offset")]
    [InlineData("limit=abc", "limit")]
    [InlineData("offset=abc", "offset")]
    public async Task DonorPagingRejectsInvalidValues(string query, string field)
    {
        using var response = await Send(HttpMethod.Get, Mine + "?" + query, Donor);
        await Error(response, 400, "VALIDATION_ERROR");
        (await response.Content.ReadAsStringAsync()).Should().Contain(field);
    }

    [Fact]
    public async Task DonorPagingUsesOneRowPerStudentAndPrefersTheActiveRelink()
    {
        var id = await Create();
        await CreatedLink(id);
        using var ended = await Unlink(id);
        await Status(ended, 200);
        await CreatedLink(id);
        var second = await Create();
        await CreatedLink(second);
        var all = await Items(Mine, Donor);
        all.Should().HaveCount(2);
        all.Single(i => i.GetProperty("id").GetString() == id).GetProperty("sponsorship").GetString().Should().Be("active");
        using var page = await Send(HttpMethod.Get, Mine + "?limit=1&offset=1", Donor);
        await Status(page, 200);
        var json = await Json(page);
        json.GetProperty("total").GetInt32().Should().Be(2);
        json.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetString().Should().Be(all[1].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DonorRowsContainExactlyTheAllowListAndNoPrivateStudentValues(bool ended)
    {
        var id = await Consented();
        await CreatedLink(id);
        if (ended) { using var unlink = await Unlink(id); await Status(unlink, 200); }
        foreach (var path in new[] { Mine, $"{Mine}/{id}" })
        {
            using var response = await Send(HttpMethod.Get, path, Donor);
            await Status(response, 200);
            var raw = await response.Content.ReadAsStringAsync();
            var json = await Json(response);
            var row = path == Mine ? json.GetProperty("items")[0] : json;
            row.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(new[]
            {
                "id", "preferredName", "class", "school", "programme", "district", "status", "hasPhoto", "story", "storyLanguage", "since", "sponsorship", "endedOn"
            });
            foreach (var secret in new[] { "fullName", "dateOfBirth", "address", "guardian", "Rahima Akter Banu", "2012-03-04", "12 Lake Road", "+8801711000000" })
                raw.Should().NotContain(secret);
        }
    }

    [Fact]
    public async Task FoundationZoneControlsVisibilityAndEndDateValidation()
    {
        var utcDate = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var zone = new[] { "Pacific/Kiritimati", "Pacific/Pago_Pago" }.First(id =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(id)).DateTime) != utcDate);
        _client.Dispose();
        _factory.Dispose();
        _factory = new() { TimeZoneId = zone };
        _factory.Finance.Donors[DonorId] = new(DonorId, "usr_donor_x");
        _client = _factory.CreateClient();
        var id = await Create();
        using var link = await Link(id, start: Date());
        await Status(link, 201);
        (await Items(Mine, Donor)).Should().ContainSingle();
        using var future = await Unlink(id, endDate: Date(1));
        await Error(future, 400, "VALIDATION_ERROR");
        using var ended = await Unlink(id, endDate: Date());
        await Status(ended, 200);
        (await Frozen()).Single().LinkId.Should().Be((await Json(link)).GetProperty("id").GetString());
    }

    [Fact]
    public void UnknownFoundationZoneRefusesHostStartup()
    {
        using var factory = new SponsorshipLinkApiFactory { TimeZoneId = "Not/AZone" };
        Action start = () => { using var client = factory.CreateClient(); };
        start.Should().Throw<InvalidOperationException>().WithMessage("*Sponsorship:TimeZone*Not/AZone*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("today")]
    public async Task NotStartedEndedLinkIsCancelledWithoutCopyAndCanBeRelinked(string? endDate)
    {
        var id = await Consented();
        using var link = await Link(id, start: Date(2));
        await Status(link, 201);
        using var ended = await Unlink(id, endDate: endDate);
        await Status(ended, 200);
        var view = await Json(ended);
        view.GetProperty("endType").GetString().Should().Be("ended");
        view.GetProperty("endDate").ValueKind.Should().Be(JsonValueKind.Null);
        (await Frozen()).Should().BeEmpty();
        (await Items(Mine, Donor)).Should().BeEmpty();
        using var never = await Send(HttpMethod.Get, $"{Mine}/stu_never", Donor);
        await Error(never, 404, "NOT_FOUND");
        foreach (var suffix in new[] { "", "/photo" })
        {
            using var hidden = await Send(HttpMethod.Get, $"{Mine}/{id}{suffix}", Donor);
            await Error(hidden, 404, "NOT_FOUND");
            (await StableBody(hidden)).Should().Be(await StableBody(never));
        }
        var history = (await Items(Links() + "?status=all", Staff)).Should().ContainSingle().Subject;
        history.GetProperty("endType").GetString().Should().Be("ended");
        history.GetProperty("endDate").ValueKind.Should().Be(JsonValueKind.Null);
        await CreatedLink(id);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("future")]
    public async Task CancellationStillRejectsMalformedAndFutureEndDates(string end)
    {
        var id = await Create();
        using var link = await Link(id, start: Date(2));
        await Status(link, 201);
        using var response = await Unlink(id, endDate: end == "future" ? Date(1) : end);
        await Error(response, 400, "VALIDATION_ERROR");
        (await Rows()).Should().ContainSingle(l => l.EndType == null);
        (await Frozen()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("ended")]
    [InlineData("mistaken")]
    public async Task ParallelUnlinksHaveExactlyOneWinner(string type)
    {
        var id = await Create();
        await CreatedLink(id);
        var responses = await Task.WhenAll(Unlink(id, type), Unlink(id, type));
        try
        {
            responses.Select(r => (int)r.StatusCode).Should().BeEquivalentTo([200, 409]);
            await Error(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict), 409, "LINK_NOT_ACTIVE");
            (await Frozen()).Should().HaveCount(type == "ended" ? 1 : 0);
            (await Rows()).Should().ContainSingle().Which.EndType.Should().Be(type);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task PhotoConflictKeepsOldFileAndRemovesAnyNewFile(string method)
    {
        var id = await Consented();
        var files = Directory.GetFiles(_factory.FilesPath, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SponsorshipDbContext>();
        var before = await db.Students.AsNoTracking().SingleAsync(s => s.Id == id);
        _factory.Conflict.Armed = true;
        using var response = method == "PUT" ? await UploadPhoto(id, "NEWPIXELS") : await Send(HttpMethod.Delete, $"{Base}/students/{id}/photo", Staff);
        await Error(response, 409, "STUDENT_CHANGED");
        _factory.Conflict.Fired.Should().BeTrue();
        var after = await db.Students.AsNoTracking().SingleAsync(s => s.Id == id);
        after.PhotoKey.Should().Be(before.PhotoKey);
        after.Version.Should().Be(before.Version + 1);
        Directory.GetFiles(_factory.FilesPath, "*", SearchOption.AllDirectories).Should().BeEquivalentTo(files.Keys);
        foreach (var file in files) File.ReadAllBytes(file.Key).Should().Equal(file.Value);
    }

    [Fact]
    public async Task MistakenWithoutVoidIsForbiddenBeforeCheckingForAnActiveLink()
    {
        using var response = await Unlink("stu_missing", "mistaken", reason: "Wrong donor", token: Staff);
        await Error(response, 403, "FORBIDDEN");
        (await Rows()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("mistaken")]
    [InlineData("cancelled")]
    public async Task EarlierEndedCopySurvivesLaterRelinkBeingVoidedOrCancelled(string later)
    {
        var id = await Consented();
        var firstId = await CreatedLink(id);
        var photo = await Photo(id);
        using var firstEnd = await Unlink(id);
        await Status(firstEnd, 200);
        await Edit(id, new { preferredName = "Changed", storyEn = "New story" });
        using var relink = await Link(id, start: later == "cancelled" ? Date(2) : Date());
        await Status(relink, 201);
        using var secondEnd = await Unlink(id, later == "cancelled" ? "ended" : "mistaken", endDate: null);
        await Status(secondEnd, 200);
        var row = (await Items(Mine, Donor)).Should().ContainSingle().Subject;
        row.GetProperty("sponsorship").GetString().Should().Be("ended");
        row.GetProperty("preferredName").GetString().Should().Be("Rahima");
        using var detail = await Send(HttpMethod.Get, $"{Mine}/{id}", Donor);
        await Status(detail, 200);
        (await Json(detail)).GetProperty("story").GetString().Should().Be("Loves maths.");
        (await Photo(id)).Should().Equal(photo);
        (await Frozen()).Should().ContainSingle().Which.LinkId.Should().Be(firstId);
        (await Items(Links() + "?status=all", Staff)).Should().HaveCount(2);
    }

    [Fact]
    public async Task DifferentDonorRelinkSeesLiveDataWhileFormerDonorKeepsFrozenPhoto()
    {
        var id = await Consented();
        await CreatedLink(id);
        var photo = await Photo(id);
        using var ended = await Unlink(id);
        await Status(ended, 200);
        await Edit(id, new { preferredName = "Changed", storyEn = "Live story" });
        await Withdraw(id);
        await CreatedLink(id, "dnr_y");
        var otherToken = DonorToken("usr_donor_y");
        var live = (await Items(Mine, otherToken)).Should().ContainSingle().Subject;
        live.GetProperty("preferredName").GetString().Should().Be("Changed");
        live.GetProperty("sponsorship").GetString().Should().Be("active");
        live.GetProperty("story").ValueKind.Should().Be(JsonValueKind.Null);
        live.GetProperty("hasPhoto").GetBoolean().Should().BeFalse();
        using var detail = await Send(HttpMethod.Get, $"{Mine}/{id}", otherToken);
        await Status(detail, 200);
        (await Json(detail)).GetProperty("preferredName").GetString().Should().Be("Changed");
        using var hiddenPhoto = await Send(HttpMethod.Get, $"{Mine}/{id}/photo", otherToken);
        await Error(hiddenPhoto, 404, "NOT_FOUND");
        (await Items(Mine, Donor)).Single().GetProperty("preferredName").GetString().Should().Be("Rahima");
        (await Photo(id)).Should().Equal(photo);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("bn")]
    public async Task BengaliDonorReadsBengaliProgrammeAndFrozenStoryWithFallback(string storyLanguage)
    {
        _factory.Finance.Donors[DonorId] = new(DonorId, "usr_donor_x", Language: "bn");
        var id = await Consented();
        await CreatedLink(id);
        (await Items(Mine, Donor)).Single().GetProperty("programme").GetProperty("name").GetString().Should().Be("পাঠশালা");
        await Edit(id, new { storyEn = storyLanguage == "en" ? "English only" : "", storyBn = storyLanguage == "bn" ? "শুধু বাংলা" : "" });
        using var ended = await Unlink(id);
        await Status(ended, 200);
        var copy = (await Frozen()).Should().ContainSingle().Subject;
        copy.ProgrammeNameEn.Should().Be("Pathshala");
        copy.ProgrammeNameBn.Should().Be("পাঠশালা");
        foreach (var path in new[] { Mine, $"{Mine}/{id}" })
        {
            using var response = await Send(HttpMethod.Get, path, Donor);
            await Status(response, 200);
            var json = await Json(response);
            var row = path == Mine ? json.GetProperty("items")[0] : json;
            row.GetProperty("programme").GetProperty("name").GetString().Should().Be("পাঠশালা");
            row.GetProperty("storyLanguage").GetString().Should().Be(storyLanguage);
            row.GetProperty("story").GetString().Should().Be(storyLanguage == "en" ? "English only" : "শুধু বাংলা");
        }
    }

    [Fact]
    public async Task NoticeStatusSaveFailureKeepsCreatedLinkPending()
    {
        var id = await Create();
        _factory.Conflict.FailNotice = true;
        using var response = await Link(id);
        await Status(response, 201);
        response.Headers.Location!.OriginalString.Should().Be(Links());
        (await Items(Links() + "?status=all", Staff)).Should().ContainSingle().Which.GetProperty("noticeStatus").GetString().Should().Be("pending");
    }

    [Fact]
    public async Task ContentOutageDoesNotPreventEndingAndUsesProgrammeSlugInBothLanguages()
    {
        var id = await Create();
        await CreatedLink(id);
        _factory.Projects.Down = true;
        // Student creation warmed the name cache; expire it so unlink must observe the outage.
        _factory.ProgrammeClock.Advance = TimeSpan.FromMinutes(3);
        using var ended = await Unlink(id);
        await Status(ended, 200);
        var copy = (await Frozen()).Should().ContainSingle().Subject;
        copy.ProgrammeNameEn.Should().Be("pathshala");
        copy.ProgrammeNameBn.Should().Be("pathshala");
    }

    private async Task Edit(string id, object body)
    {
        using var current = await Send(HttpMethod.Get, $"{Base}/students/{id}", Staff);
        await Status(current, 200);
        using var edit = await Send(HttpMethod.Patch, $"{Base}/students/{id}", Staff, body, current.Headers.ETag!.Tag);
        await Status(edit, 200);
    }

    private static string DonorToken(string subject) => TestTokens.Issue([], subject: subject, extraClaims: [new Claim(KjClaims.Role, KjRoles.Donor)]);
    private string Date(int days = 0) => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(_factory.TimeZoneId)).AddDays(days).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    private static string Links(string donor = DonorId) => $"{Base}/donors/{donor}/students";
    private Task<HttpResponseMessage> Link(string id, string donor = DonorId, string? start = null) => Send(HttpMethod.Post, Links(donor), Staff, new { studentId = id, startDate = start ?? Date(-10) });
    private Task<HttpResponseMessage> Unlink(string id, string type = "ended", string? endDate = "today", string? reason = "Completed", string? token = null) =>
        Send(HttpMethod.Post, $"{Links()}/{id}/unlink", token ?? Super, new { type, endDate = endDate == "today" ? Date() : endDate, reason });

    private async Task<string> CreatedLink(string id, string donor = DonorId)
    {
        using var response = await Link(id, donor);
        await Status(response, 201);
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private async Task<string> Create(string status = "active")
    {
        using var response = await Send(HttpMethod.Post, $"{Base}/students", Staff, new
        {
            name = "Rahima Akter Banu",
            preferredName = "Rahima",
            dateOfBirth = "2012-03-04",
            gender = "female",
            school = "Lakeview School",
            @class = "Class 6",
            programme = "pathshala",
            district = "Dhaka",
            address = "12 Lake Road, Dhaka",
            guardianName = "Abdul Karim",
            guardianPhone = "+8801711000000",
            storyEn = "Loves maths.",
            storyBn = "অঙ্ক ভালোবাসে।",
            status
        });
        await Status(response, 201);
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private async Task<string> Consented()
    {
        var id = await Create();
        using var form = await Upload(id, "consent-form", "form", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4 signed form"));
        await Status(form, 200);
        await Grant(id);
        using var photo = await UploadPhoto(id);
        await Status(photo, 200);
        return id;
    }

    private async Task Grant(string id)
    {
        using var response = await Send(HttpMethod.Post, $"{Base}/students/{id}/consents", Staff, new { granted = true });
        await Status(response, 201);
    }

    private async Task Withdraw(string id)
    {
        using var response = await Send(HttpMethod.Post, $"{Base}/students/{id}/consents", Staff, new { granted = false });
        await Status(response, 201);
    }

    private Task<HttpResponseMessage> UploadPhoto(string id, string pixels = "PIXELDATA") => Upload(id, "photo", "photo", "image/jpeg",
        [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, .. Encoding.ASCII.GetBytes(pixels), 0xFF, 0xD9]);

    private async Task<HttpResponseMessage> Upload(string id, string route, string field, string type, byte[] bytes)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new(type);
        content.Add(file, field, type == "application/pdf" ? "form.pdf" : "photo.jpg");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{Base}/students/{id}/{route}") { Content = content };
        request.Headers.Authorization = new("Bearer", Staff);
        return await _client.SendAsync(request);
    }

    private async Task<byte[]> Photo(string id)
    {
        using var response = await Send(HttpMethod.Get, $"{Mine}/{id}/photo", Donor);
        await Status(response, 200);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token, object? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (method == HttpMethod.Post) request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private static async Task Status(HttpResponseMessage response, int status)
    {
        ((int)response.StatusCode).Should().Be(status, await response.Content.ReadAsStringAsync());
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        if (response.RequestMessage!.RequestUri!.AbsolutePath.StartsWith(Mine, StringComparison.Ordinal))
        {
            response.Headers.CacheControl.Private.Should().BeTrue();
            response.Headers.Vary.Should().Contain("Authorization");
        }
    }

    private static async Task Error(HttpResponseMessage response, int status, string code)
    {
        await Status(response, status);
        (await Json(response)).GetProperty("error").GetProperty("code").GetString().Should().Be(code);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private async Task<List<JsonElement>> Items(string path, string token)
    {
        using var response = await Send(HttpMethod.Get, path, token);
        await Status(response, 200);
        return (await Json(response)).GetProperty("items").EnumerateArray().ToList();
    }

    private async Task<List<LinkEntity>> Rows()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SponsorshipDbContext>().Links.AsNoTracking().ToListAsync();
    }

    private async Task<List<FrozenCopyEntity>> Frozen()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SponsorshipDbContext>().FrozenCopies.AsNoTracking().ToListAsync();
    }

    private async Task Counts(int rows, int notices)
    {
        (await Rows()).Should().HaveCount(rows);
        _factory.Finance.Notices.Should().HaveCount(notices);
    }

    private static async Task<string> StableBody(HttpResponseMessage response)
    {
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        StripRequestIds(node);
        // `instance` only echoes the path the caller asked for, so it differs by construction; every other part of the body must match exactly.
        node.AsObject().Remove("instance");
        return node.ToJsonString();
    }

    private static void StripRequestIds(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("requestId");
            obj.Remove("traceId");
            foreach (var child in obj.ToList()) if (child.Value is not null) StripRequestIds(child.Value);
        }
        else if (node is JsonArray array) foreach (var child in array) if (child is not null) StripRequestIds(child);
    }
}
