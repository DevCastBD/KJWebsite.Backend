using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Intake;
using MembershipService.MemberDirectory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class MemberDirectoryTests : IDisposable
{
    private const string Directory = "/api/v1/members/directory";
    private readonly MembershipApiFactory _factory = new();
    private readonly HttpClient _client;

    public MemberDirectoryTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task OnlyConsentedMembersAppearAndOnlyWithTheFieldsTheyApproved()
    {
        await Seed(
            Member("none", name: false, expertise: false, photo: false),
            Member("name-only", name: true, expertise: false, photo: false),
            Member("skill-only", name: false, expertise: true, photo: false),
            Member("photo-only", name: false, expertise: false, photo: true, scan: "clean"),
            Member("all", name: true, expertise: true, photo: true, scan: "clean"),
            Member("suspended", name: true, expertise: true, photo: false, status: "suspended"),
            Member("leaving", name: true, expertise: true, photo: false, deletionRequested: true));

        using var response = await _client.GetAsync(Directory);
        var text = await response.Content.ReadAsStringAsync();
        var page = JsonDocument.Parse(text).RootElement;
        var byId = page.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        byId.Keys.Should().BeEquivalentTo("mem_name-only", "mem_skill-only", "mem_photo-only", "mem_all");
        page.GetProperty("total").GetInt32().Should().Be(4);
        page.GetProperty("pageSize").GetInt32().Should().Be(24);

        byId["mem_name-only"].GetProperty("name").GetString().Should().Be("First name-only Last");
        byId["mem_name-only"].GetProperty("expertise").ValueKind.Should().Be(JsonValueKind.Null);
        byId["mem_skill-only"].GetProperty("name").ValueKind.Should().Be(JsonValueKind.Null);
        byId["mem_skill-only"].GetProperty("expertise").EnumerateArray().Select(e => e.GetString()).Should().Equal("Robotics", "Teaching");
        byId["mem_photo-only"].GetProperty("photoUrl").GetString().Should().EndWith("/api/v1/members/directory/mem_photo-only/photo");
        byId["mem_photo-only"].GetProperty("name").ValueKind.Should().Be(JsonValueKind.Null);
        byId["mem_all"].GetProperty("nameBn").GetString().Should().Be("বাংলা নাম");

        // What was not approved is in neither the entry nor anywhere else in the response.
        text.Should().NotContain("HIDDEN-SKILL-name-only").And.NotContain("First skill-only").And.NotContain("First none");
        text.Should().NotContain("@example.org").And.NotContain("017").And.NotContain("Dhaka").And.NotContainEquivalentOf("bloodGroup").And.NotContain("KJ-20");
    }

    [Fact]
    public async Task UnscannedPhotosAreNeverOffered()
    {
        await Seed(Member("fresh", name: true, expertise: false, photo: true, scan: "pending"));

        var page = await Json(await _client.GetAsync(Directory));

        page.GetProperty("items")[0].GetProperty("photoUrl").ValueKind.Should().Be(JsonValueKind.Null);
        (await _client.GetAsync($"{Directory}/mem_fresh/photo")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ConsentedCleanPhotosAreServedAndHiddenOnesLookIdenticalToMissingOnes()
    {
        await Seed(Member("seen", name: true, expertise: false, photo: true, scan: "clean"), Member("shy", name: true, expertise: false, photo: false, hasPhoto: true, scan: "clean"));

        using var ok = await _client.GetAsync($"{Directory}/mem_seen/photo");
        using var withheld = await _client.GetAsync($"{Directory}/mem_shy/photo");
        using var missing = await _client.GetAsync($"{Directory}/mem_nobody/photo");

        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ok.Content.ReadAsByteArrayAsync()).Should().Equal(PngBytes);
        withheld.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(withheld)).GetProperty("error").GetRawText().Should().Be((await Json(missing)).GetProperty("error").GetRawText(), "no way to tell a withheld photo from no member");
    }

    [Fact]
    public async Task SearchSeesOnlyPublishedFields()
    {
        await Seed(
            Member("a", name: true, expertise: false, photo: false, expertiseText: "Quantum hiding"),
            Member("b", name: false, expertise: true, photo: false, expertiseText: "Quantum visible"));

        (await Ids("?q=quantum")).Should().Equal("mem_b");
        (await Ids("?q=first%20a")).Should().Equal("mem_a");
        (await Ids("?q=first%20b")).Should().BeEmpty("b published no name, so the name cannot be probed");
        (await Ids("?q=HIDING")).Should().BeEmpty("a did not publish expertise");
        (await Ids("?q=বাংলা")).Should().Equal("mem_a");
    }

    [Fact]
    public async Task WithdrawingConsentRemovesTheMemberOnTheNextRequest()
    {
        await Seed(Member("usr_test", name: true, expertise: true, photo: false, userId: "usr_test"));
        (await Ids("")).Should().Equal("mem_usr_test");

        using var withdraw = await Send(HttpMethod.Put, "/api/v1/members/me/consents", TestTokens.Issue([Permissions.MemberProfileEdit]), new { name = false, photograph = false, expertise = false });
        withdraw.StatusCode.Should().Be(HttpStatusCode.OK);

        (await Ids("")).Should().BeEmpty();
        (await Ids("?q=first")).Should().BeEmpty();
        using var again = await Send(HttpMethod.Put, "/api/v1/members/me/consents", TestTokens.Issue([Permissions.MemberProfileEdit]), new { name = true, photograph = false, expertise = false });
        (await Ids("")).Should().Equal("mem_usr_test");
        again.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListsArePagedAndDeepPagingIsRefused()
    {
        await Seed([.. Enumerable.Range(0, 30).Select(i => Member($"p{i:D2}", name: true, expertise: false, photo: false))]);

        var first = await Json(await _client.GetAsync(Directory));
        var second = await Json(await _client.GetAsync($"{Directory}?page=2"));

        first.GetProperty("items").GetArrayLength().Should().Be(24);
        second.GetProperty("items").GetArrayLength().Should().Be(6);
        first.GetProperty("total").GetInt32().Should().Be(30);
        first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(i => i.GetProperty("id").GetString())
            .Should().OnlyHaveUniqueItems().And.HaveCount(30);
        (await _client.GetAsync($"{Directory}?page=0")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.GetAsync($"{Directory}?page=51")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(await _client.GetAsync($"{Directory}?page=50"))).GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task TheUnapprovedColumnsAreNeverReadFromTheDatabase()
    {
        using var scope = _factory.Services.CreateScope();
        var sql = DirectoryEndpoints.PublicQuery(scope.ServiceProvider.GetRequiredService<MembershipDbContext>(), "x").ToQueryString();

        sql.Should().NotContain("PhoneProtected").And.NotContain("AddressProtected").And.NotContain("DateOfBirthProtected")
            .And.NotContain("BloodGroupProtected").And.NotContain("UserId").And.NotContain("MemberCode").And.NotContain("ApplicationId");
        sql.Should().Contain("CASE", "each published field is gated by its consent inside the statement");
    }

    [Fact]
    public async Task ChaptersAreAcceptedButNotYetModelled()
    {
        await Seed(Member("c", name: true, expertise: false, photo: false));

        var page = await Json(await _client.GetAsync($"{Directory}?chapter=Dhaka"));

        page.GetProperty("chapters").GetArrayLength().Should().Be(0);
        page.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task StaffSearchHasNoContactDetailsUntilOneMemberIsOpened()
    {
        await Seed(Member("s1", name: false, expertise: false, photo: false), Member("s2", name: false, expertise: false, photo: false));
        var officer = TestTokens.Issue([Permissions.MemberProfileView]);

        using var list = await Send(HttpMethod.Get, "/api/v1/admin/membership/members?q=first%20s1", officer);
        var text = await list.Content.ReadAsStringAsync();
        var items = JsonDocument.Parse(text).RootElement.GetProperty("items");

        list.StatusCode.Should().Be(HttpStatusCode.OK);
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("memberCode").GetString().Should().Be("KJ-2026-s1");
        text.Should().NotContain("@example.org").And.NotContain("017").And.NotContain("Dhaka").And.NotContainEquivalentOf("phone");

        var detail = await Json(await Send(HttpMethod.Get, "/api/v1/admin/membership/members/mem_s1", officer));
        detail.GetProperty("phone").GetString().Should().Be("01711112222");
        detail.GetProperty("email").GetString().Should().Be("s1@example.org");
        detail.GetProperty("bloodGroup").GetString().Should().Be("O+");
    }

    [Fact]
    public async Task StaffDirectoryNeedsThePermission()
    {
        (await _client.GetAsync("/api/v1/admin/membership/members")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var forbidden = await Send(HttpMethod.Get, "/api/v1/admin/membership/members", TestTokens.Issue([Permissions.ContentView]));
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var member = await Send(HttpMethod.Get, "/api/v1/admin/membership/members/mem_x", TestTokens.Issue([Permissions.MemberProfileEdit]));
        member.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9];

    private sealed record Spec(string Key, bool Name, bool Expertise, bool Photo, string Status, bool Deletion, string? Scan, string? ExpertiseText, string UserId, bool HasPhoto);

    private static Spec Member(string key, bool name, bool expertise, bool photo, string status = "active", bool deletionRequested = false, string? scan = null, string? expertiseText = null, string? userId = null, bool hasPhoto = false) =>
        new(key, name, expertise, photo, status, deletionRequested, scan, expertiseText ?? $"Robotics, Teaching ; HIDDEN-SKILL-{key}".Replace($" ; HIDDEN-SKILL-{key}", expertise ? string.Empty : $" ; HIDDEN-SKILL-{key}"), userId ?? $"usr_{key}", hasPhoto || photo);

    private async Task Seed(params Spec[] specs)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IFieldProtector>();
        var store = scope.ServiceProvider.GetRequiredService<IAttachmentStore>();
        foreach (var s in specs)
        {
            var member = new MemberEntity
            {
                Id = $"mem_{s.Key}",
                UserId = s.UserId,
                MemberCode = $"KJ-2026-{s.Key}",
                ApplicationId = $"app_{s.Key}",
                FirstName = $"First {s.Key}",
                LastName = "Last",
                NameBn = "বাংলা নাম",
                Status = s.Status,
                JoinedAt = DateTimeOffset.UtcNow,
                Expertise = s.ExpertiseText,
                PhoneProtected = protector.Protect("01711112222"),
                AddressProtected = protector.Protect("Dhaka"),
                DateOfBirthProtected = protector.Protect("1999-01-01"),
                BloodGroupProtected = protector.Protect("O+"),
                ConsentName = s.Name,
                ConsentExpertise = s.Expertise,
                ConsentPhotograph = s.Photo
            };
            if (s.HasPhoto)
            {
                var key = Guid.NewGuid().ToString("N");
                await store.SaveAsync(key, new MemoryStream(PngBytes), CancellationToken.None);
                member.PhotoKey = key;
                member.PhotoContentType = "image/png";
                member.PhotoScanStatus = s.Scan ?? "pending";
            }

            db.Members.Add(member);
            db.Applications.Add(new ApplicationEntity
            {
                Id = $"app_{s.Key}",
                Reference = $"KJ-2026-{s.Key}",
                Language = "en",
                FirstName = member.FirstName,
                LastName = "Last",
                AcademicInstitute = "x",
                Occupation = "x",
                EmailProtected = protector.Protect($"{s.Key}@example.org"),
                PhoneProtected = protector.Protect("01711112222"),
                BloodGroupProtected = protector.Protect("O+"),
                CurrentAddressProtected = protector.Protect("Dhaka"),
                EmailHash = s.Key,
                PhoneHash = s.Key
            });
            if (s.Deletion)
            {
                db.DeletionRequests.Add(new DeletionRequestEntity { Id = $"del_{s.Key}", MemberId = member.Id, Reference = "DEL-1", RequestedAt = DateTimeOffset.UtcNow });
            }
        }

        await db.SaveChangesAsync();
    }

    private async Task<List<string>> Ids(string query)
    {
        var page = await Json(await _client.GetAsync($"{Directory}{query}"));
        return [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!)];
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

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}
