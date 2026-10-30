using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using MembershipService.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class MembershipCommitteeTests : IDisposable
{
    private const string Admin = "/api/v1/admin/membership/committees";
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private readonly MembershipApiFactory _factory = new();
    private readonly HttpClient _client;

    public MembershipCommitteeTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task CommitteesNestAndTheChartShowsTheTree()
    {
        var exec = await Create(new { nameEn = "Executive Committee", nameBn = "নির্বাহী কমিটি", kind = "executive" });
        var programmes = await Create(new { nameEn = "Programmes", nameBn = "কর্মসূচি", kind = "project", parentId = exec });
        var chapter = await Create(new { nameEn = "Dhaka Chapter", nameBn = "ঢাকা শাখা", kind = "chapter", parentId = programmes });

        var flat = (await Json(await Send(HttpMethod.Get, Admin))).GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!);
        flat[programmes].GetProperty("parentId").GetString().Should().Be(exec);
        flat[chapter].GetProperty("parentId").GetString().Should().Be(programmes);

        var chart = await Json(await _client.GetAsync("/api/v1/committees"));
        var root = chart.GetProperty("items")[0];
        root.GetProperty("name").GetProperty("bn").GetString().Should().Be("নির্বাহী কমিটি");
        root.GetProperty("children")[0].GetProperty("name").GetProperty("en").GetString().Should().Be("Programmes");
        root.GetProperty("children")[0].GetProperty("children")[0].GetProperty("kind").GetString().Should().Be("chapter");
    }

    [Fact]
    public async Task CyclesUnknownParentsAndBadKindsAreRejected()
    {
        var a = await Create(new { nameEn = "A", nameBn = "এ", kind = "executive" });
        var b = await Create(new { nameEn = "B", nameBn = "বি", kind = "project", parentId = a });

        using var cycle = await Send(HttpMethod.Put, $"{Admin}/{a}", new { nameEn = "A", nameBn = "এ", kind = "executive", parentId = b });
        using var self = await Send(HttpMethod.Put, $"{Admin}/{a}", new { nameEn = "A", nameBn = "এ", kind = "executive", parentId = a });
        using var unknown = await Send(HttpMethod.Post, Admin, new { nameEn = "C", nameBn = "সি", kind = "project", parentId = "com_missing" });
        using var kind = await Send(HttpMethod.Post, Admin, new { nameEn = "C", nameBn = "সি", kind = "club" });

        foreach (var r in new[] { cycle, self, unknown, kind })
        {
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        (await cycle.Content.ReadAsStringAsync()).Should().Contain("cycle");
    }

    [Fact]
    public async Task BothLanguagesExistAndAMissingBengaliNameIsFlagged()
    {
        using var legacy = await Send(HttpMethod.Post, Admin, new { name = "Finance Committee", parentId = (string?)null, chair = "ignored" });
        var legacyBody = await Json(legacy);
        using var full = await Send(HttpMethod.Post, Admin, new { nameEn = "Education", nameBn = "শিক্ষা", kind = "project" });

        legacy.StatusCode.Should().Be(HttpStatusCode.Created);
        legacyBody.GetProperty("name").GetString().Should().Be("Finance Committee");
        legacyBody.GetProperty("nameBn").GetString().Should().Be("Finance Committee");
        legacyBody.GetProperty("nameBnIsFallback").GetBoolean().Should().BeTrue();
        legacyBody.GetProperty("chair").GetString().Should().BeEmpty("the chair comes from a term, not from text");
        (await Json(full)).GetProperty("nameBnIsFallback").GetBoolean().Should().BeFalse();
        (await Json(await _client.GetAsync("/api/v1/committees/education"))).GetProperty("name").GetProperty("bn").GetString().Should().Be("শিক্ষা");
    }

    [Fact]
    public async Task CurrentAndHistoricalMembershipAreDistinguishedByDate()
    {
        await Members(("m1", true), ("m2", true), ("m3", true), ("m4", true));
        var exec = await Create(new { nameEn = "Executive", nameBn = "নির্বাহী", kind = "executive" });
        await Term(exec, "m1", "chair", Today.AddYears(-2), Today.AddDays(-1));
        await Term(exec, "m2", "chair", Today, null);
        await Term(exec, "m3", "treasurer", Today.AddDays(-30), Today);
        await Term(exec, "m4", "secretary", Today.AddDays(1), null);

        var current = (await Json(await Send(HttpMethod.Get, $"{Admin}/{exec}/members?scope=current"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("memberId").GetString());
        var past = (await Json(await Send(HttpMethod.Get, $"{Admin}/{exec}/members?scope=past"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("memberId").GetString());
        var all = (await Json(await Send(HttpMethod.Get, $"{Admin}/{exec}/members"))).GetProperty("items").GetArrayLength();

        current.Should().BeEquivalentTo(["mem_m2", "mem_m3"], "a term is current from its first through its last day, inclusive");
        past.Should().Equal("mem_m1");
        all.Should().Be(4);

        var detail = await Json(await _client.GetAsync("/api/v1/committees/executive"));
        detail.GetProperty("current").EnumerateArray().Select(s => s.GetProperty("memberId").GetString()).Should().BeEquivalentTo(["mem_m2", "mem_m3"]);
        detail.GetProperty("past").EnumerateArray().Select(s => s.GetProperty("memberId").GetString()).Should().Equal("mem_m1");
        detail.GetProperty("past")[0].GetProperty("endsOn").GetString().Should().Be(Today.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

        var chair = (await Json(await Send(HttpMethod.Get, Admin))).GetProperty("items")[0].GetProperty("chair").GetString();
        chair.Should().Be("First m2 Last");
        var chartSeats = (await Json(await _client.GetAsync("/api/v1/committees"))).GetProperty("items")[0].GetProperty("members");
        chartSeats.EnumerateArray().Select(s => s.GetProperty("position").GetProperty("key").GetString()).Should().Equal("chair", "treasurer");
    }

    [Fact]
    public async Task OverlappingTermsAreRefusedAndTermsAreEndedNotDeleted()
    {
        await Members(("m1", true));
        var c = await Create(new { nameEn = "Board", nameBn = "বোর্ড", kind = "advisory" });
        var termId = await Term(c, "m1", "member", Today.AddDays(-10), null);

        using var overlap = await Send(HttpMethod.Post, $"{Admin}/{c}/members", new { memberId = "mem_m1", positionKey = "member", positionEn = "Member", startsOn = Today.AddDays(-5).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
        using var otherPosition = await Send(HttpMethod.Post, $"{Admin}/{c}/members", new { memberId = "mem_m1", positionKey = "advisor", positionEn = "Advisor", startsOn = Today.AddDays(-5).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
        overlap.StatusCode.Should().Be(HttpStatusCode.Conflict);
        otherPosition.StatusCode.Should().Be(HttpStatusCode.Created);

        using var ended = await Send(HttpMethod.Post, $"{Admin}/{c}/members/{termId}/end", new { endsOn = Today.AddDays(-2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
        (await Json(ended)).GetProperty("current").GetBoolean().Should().BeFalse();
        (await Send(HttpMethod.Post, $"{Admin}/{c}/members/{termId}/end", new { })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // A new stint after the old one ended is fine.
        (await Send(HttpMethod.Post, $"{Admin}/{c}/members", new { memberId = "mem_m1", positionKey = "member", positionEn = "Member", startsOn = Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await (await Send(HttpMethod.Get, $"{Admin}/{c}/members?scope=past")).Content.ReadAsStringAsync()).Should().Contain(termId);
    }

    [Fact]
    public async Task TermInputIsValidated()
    {
        await Members(("m1", true));
        var c = await Create(new { nameEn = "Board", nameBn = "বোর্ড", kind = "advisory" });

        using var bad = await Send(HttpMethod.Post, $"{Admin}/{c}/members", new { memberId = "mem_nobody", positionKey = "Bad Key", positionEn = "", startsOn = Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), endsOn = Today.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });

        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await bad.Content.ReadAsStringAsync();
        text.Should().Contain("memberId").And.Contain("positionKey").And.Contain("positionEn").And.Contain("endsOn");
    }

    [Fact]
    public async Task OnlyMembersWhoAgreedToBeNamedAppearPublicly()
    {
        await Members(("yes", true), ("no", false));
        await Members(("gone", true), ("leaving", true));
        await SetStatus("gone", "suspended");
        await RequestDeletion("leaving");
        var c = await Create(new { nameEn = "Executive", nameBn = "নির্বাহী", kind = "executive" });
        foreach (var key in new[] { "yes", "no", "gone", "leaving" })
        {
            await Term(c, key, "member", Today.AddDays(-5), null);
        }

        var chart = await (await _client.GetAsync("/api/v1/committees")).Content.ReadAsStringAsync();
        var detail = await (await _client.GetAsync("/api/v1/committees/executive")).Content.ReadAsStringAsync();

        foreach (var text in new[] { chart, detail })
        {
            text.Should().Contain("First yes").And.NotContain("First no").And.NotContain("First gone").And.NotContain("First leaving");
            text.Should().NotContain("@example.org").And.NotContain("017").And.NotContainEquivalentOf("phone");
        }
    }

    [Fact]
    public async Task WithdrawingConsentRemovesTheSeatOnTheNextRequest()
    {
        await Members(("usr_test", true), userId: "usr_test");
        var c = await Create(new { nameEn = "Executive", nameBn = "নির্বাহী", kind = "executive" });
        await Term(c, "usr_test", "chair", Today.AddDays(-5), null);
        (await _client.GetStringAsync("/api/v1/committees")).Should().Contain("First usr_test");

        await Send(HttpMethod.Put, "/api/v1/members/me/consents", new { name = false, photograph = false, expertise = false }, TestTokens.Issue([Permissions.MemberProfileEdit]));

        var after = await _client.GetAsync("/api/v1/committees");
        (await after.Content.ReadAsStringAsync()).Should().NotContain("First usr_test");
        after.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task ArchivingHidesACommitteeButKeepsItsHistoryAndOnlyEmptyOnesAreDeleted()
    {
        await Members(("m1", true));
        var used = await Create(new { nameEn = "Old Project", nameBn = "পুরনো", kind = "project" });
        await Term(used, "m1", "member", Today.AddYears(-1), Today.AddMonths(-1));
        var empty = await Create(new { nameEn = "Draft", nameBn = "খসড়া", kind = "project" });

        (await Send(HttpMethod.Delete, $"{Admin}/{used}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Send(HttpMethod.Delete, $"{Admin}/{empty}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Post, $"{Admin}/{used}/archive")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _client.GetAsync("/api/v1/committees/old-project")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetStringAsync("/api/v1/committees")).Should().NotContain("Old Project");
        (await Json(await Send(HttpMethod.Get, Admin))).GetProperty("items").GetArrayLength().Should().Be(0);
        (await Json(await Send(HttpMethod.Get, $"{Admin}?includeArchived=true"))).GetProperty("items")[0].GetProperty("archived").GetBoolean().Should().BeTrue();
        (await Json(await Send(HttpMethod.Get, $"{Admin}/{used}/members"))).GetProperty("items").GetArrayLength().Should().Be(1);
        (await Send(HttpMethod.Post, $"{Admin}/{used}/unarchive")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync("/api/v1/committees/old-project")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DuplicateSlugsAreRefusedAndSlugsAreGenerated()
    {
        var first = await Json(await Send(HttpMethod.Post, Admin, new { nameEn = "Fund Raising!", nameBn = "তহবিল", kind = "project" }));
        using var dup = await Send(HttpMethod.Post, Admin, new { nameEn = "Fund Raising!", nameBn = "তহবিল", kind = "project" });

        first.GetProperty("slug").GetString().Should().Be("fund-raising");
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ChangesAreAudited()
    {
        var c = await Create(new { nameEn = "Audited", nameBn = "অডিট", kind = "project" });
        await Send(HttpMethod.Post, $"{Admin}/{c}/archive");

        using var scope = _factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<MembershipDbContext>().Set<AuditEvent>().Where(a => a.Entity == "Committee").ToListAsync();

        rows.Select(r => r.Action).Should().Contain(["create", "update"]);
    }

    [Fact]
    public async Task AdminNeedsThePermissionAndThePublicChartDoesNot()
    {
        (await _client.GetAsync(Admin)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Get, Admin, token: TestTokens.Issue([Permissions.ContentView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Post, Admin, new { nameEn = "x" }, TestTokens.Issue([Permissions.MemberProfileView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.GetAsync("/api/v1/committees")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheDirectoryListsChaptersAndFiltersByThem()
    {
        await Members(("d1", true), ("d2", true), ("d3", false));
        using (var scope = _factory.Services.CreateScope())
        {
            // d3 is in the directory (expertise consent) but never agreed to be named.
            var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
            (await db.Members.SingleAsync(m => m.Id == "mem_d3")).ConsentExpertise = true;
            await db.SaveChangesAsync();
        }

        var dhaka = await Create(new { nameEn = "Dhaka Chapter", nameBn = "ঢাকা", kind = "chapter" });
        var ctg = await Create(new { nameEn = "Chattogram Chapter", nameBn = "চট্টগ্রাম", kind = "chapter" });
        await Term(dhaka, "d1", "member", Today.AddDays(-5), null);
        await Term(dhaka, "d3", "member", Today.AddDays(-5), null);
        await Term(ctg, "d2", "member", Today.AddDays(-5), null);

        var all = await Json(await _client.GetAsync("/api/v1/members/directory"));
        var filtered = await Json(await _client.GetAsync("/api/v1/members/directory?chapter=Dhaka%20Chapter"));

        all.GetProperty("chapters").EnumerateArray().Select(c => c.GetString()).Should().Equal("Chattogram Chapter", "Dhaka Chapter");
        all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Should().Contain("mem_d3");
        filtered.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Should().Equal("mem_d1");
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task Members(params (string Key, bool NameConsent)[] members) => await Members(members, null);

    private async Task Members((string Key, bool NameConsent) member, string? userId) => await Members([member], userId);

    private async Task Members((string Key, bool NameConsent)[] members, string? userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        foreach (var (key, consent) in members)
        {
            db.Members.Add(new MemberEntity { Id = $"mem_{key}", UserId = userId ?? $"usr_{key}", MemberCode = $"KJ-2026-{key}", ApplicationId = $"app_{key}", FirstName = $"First {key}", LastName = "Last", JoinedAt = DateTimeOffset.UtcNow, ConsentName = consent });
        }

        await db.SaveChangesAsync();
    }

    private async Task SetStatus(string key, string status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        (await db.Members.SingleAsync(m => m.Id == $"mem_{key}")).Status = status;
        await db.SaveChangesAsync();
    }

    private async Task RequestDeletion(string key)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        db.DeletionRequests.Add(new DeletionRequestEntity { Id = $"del_{key}", MemberId = $"mem_{key}", Reference = "DEL-1", RequestedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task<string> Create(object body)
    {
        using var response = await Send(HttpMethod.Post, Admin, body);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private async Task<string> Term(string committeeId, string memberKey, string position, DateOnly starts, DateOnly? ends)
    {
        using var response = await Send(HttpMethod.Post, $"{Admin}/{committeeId}/members", new
        {
            memberId = $"mem_{memberKey}",
            positionKey = position,
            positionEn = position,
            positionBn = position,
            startsOn = starts.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            endsOn = ends?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, string? token = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.Issue([Permissions.MembershipManage]));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}
