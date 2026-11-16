using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SponsorshipService.Data;

namespace KJWebsite.IntegrationTests;

public sealed partial class SponsorshipLinkTests
{
    [Fact]
    public async Task DonorNamesReturnsOnlyLiveNamesInIdOrderWithPrivateHeadersAndLeavesDetailRouteAvailable()
    {
        var first = await NamedStudent("NamesActiveFirst225");
        var second = await NamedStudent("NamesActiveSecond225");
        await CreatedLink(first);
        await CreatedLink(second);
        _factory.Projects.Down = true;
        using var response = await Names($" {second} ,{first},{second} ");
        await Status(response, 200);
        response.Headers.CacheControl!.ToString().Split(',').Select(value => value.Trim()).Should().BeEquivalentTo("private", "no-store");
        response.Headers.Vary.Should().Contain("Authorization");
        var items = (await Json(response)).GetProperty("items").EnumerateArray().ToArray();
        items.Select(i => i.GetProperty("id").GetString()).Should().Equal(new[] { first, second }.Order(StringComparer.Ordinal));
        items.Should().HaveCount(2);
        foreach (var item in items)
        {
            item.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "preferredName");
            item.GetProperty("preferredName").GetString().Should().Be(item.GetProperty("id").GetString() == first ? "NamesActiveFirst225" : "NamesActiveSecond225");
        }
        using var detail = await Send(HttpMethod.Get, $"{Mine}/{first}", Donor);
        await Status(detail, 200);
        var student = await Json(detail);
        student.GetProperty("id").GetString().Should().Be(first);
        student.GetProperty("preferredName").GetString().Should().Be("NamesActiveFirst225");
    }

    [Fact]
    public async Task DonorNamesUsesFrozenNameAfterEditAndConsentWithdrawalAndLiveNameAfterRelink()
    {
        var id = await Consented();
        await Edit(id, new { preferredName = "NamesFrozenOriginal225" });
        var firstLink = await CreatedLink(id);
        using (var ended = await Unlink(id)) await Status(ended, 200);
        await Edit(id, new { preferredName = "NamesLiveEdited225" });
        await Withdraw(id);
        using (var response = await Names(id))
        {
            await Status(response, 200);
            (await response.Content.ReadAsStringAsync()).Should().Be(NamesBody(id, "NamesFrozenOriginal225"));
        }
        var secondLink = await CreatedLink(id);
        using (var response = await Names(id))
        {
            await Status(response, 200);
            (await response.Content.ReadAsStringAsync()).Should().Be(NamesBody(id, "NamesLiveEdited225"));
        }
        using (var ended = await Unlink(id)) await Status(ended, 200);
        await Edit(id, new { preferredName = "NamesAfterSecondEnd225" });
        var copies = await Frozen();
        copies.Single(c => c.LinkId == firstLink).PreferredName.Should().Be("NamesFrozenOriginal225");
        copies.Single(c => c.LinkId == secondLink).PreferredName.Should().Be("NamesLiveEdited225");
        using var latest = await Names(id);
        await Status(latest, 200);
        (await latest.Content.ReadAsStringAsync()).Should().Be(NamesBody(id, "NamesLiveEdited225"));
    }

    [Fact]
    public async Task DonorNamesHiddenCasesHaveIdenticalWholeBodiesAndDoNotLeakIntoMixedRequests()
    {
        var future = await NamedStudent("NamesFuture225");
        using (var link = await Link(future, start: Date(10))) await Status(link, 201);
        var mistaken = await NamedStudent("NamesMistaken225");
        await CreatedLink(mistaken);
        using (var end = await Unlink(mistaken, "mistaken")) await Status(end, 200);
        var cancelled = await NamedStudent("NamesCancelled225");
        using (var link = await Link(cancelled, start: Date(10))) await Status(link, 201);
        using (var end = await Unlink(cancelled, endDate: null)) await Status(end, 200);
        var other = await NamedStudent("NamesOtherDonor225");
        await CreatedLink(other, "dnr_y");
        var never = await NamedStudent("NamesNeverLinked225");
        var unknown = "stu_ffffffffffffffffffffffffffffffff";
        var hidden = new[] { future, mistaken, cancelled, other, never, unknown };
        foreach (var id in hidden)
        {
            using var response = await Names(id);
            await Status(response, 200);
            (await response.Content.ReadAsStringAsync()).Should().Be("{\"items\":[]}");
        }
        var visible = await NamedStudent("NamesVisibleMixed225");
        await CreatedLink(visible);
        using var mixed = await Names(string.Join(',', hidden.Append(visible)));
        await Status(mixed, 200);
        (await mixed.Content.ReadAsStringAsync()).Should().Be(NamesBody(visible, "NamesVisibleMixed225"));
    }

    [Fact]
    public async Task DonorNamesKeepsAnOlderFrozenCopyBesideALaterCancelledOrMistakenLink()
    {
        var id = await Consented();
        await Edit(id, new { preferredName = "NamesOlderCopy225" });
        await CreatedLink(id);
        using (var ended = await Unlink(id)) await Status(ended, 200);
        await Edit(id, new { preferredName = "NamesLaterLive225" });
        using (var future = await Link(id, start: Date(10))) await Status(future, 201);
        using (var cancelled = await Unlink(id, endDate: null)) await Status(cancelled, 200);
        using (var afterCancel = await Names(id))
        {
            await Status(afterCancel, 200);
            (await afterCancel.Content.ReadAsStringAsync()).Should().Be(NamesBody(id, "NamesOlderCopy225"));
        }
        await CreatedLink(id);
        using (var mistaken = await Unlink(id, "mistaken")) await Status(mistaken, 200);
        using var afterMistaken = await Names(id);
        await Status(afterMistaken, 200);
        (await afterMistaken.Content.ReadAsStringAsync()).Should().Be(NamesBody(id, "NamesOlderCopy225"));
    }

    [Fact]
    public async Task DonorNamesCountsTheLimitAfterRemovingDuplicates()
    {
        var ids = Enumerable.Range(0, 100).Select(i => $"stu_{i:x32}").ToList();
        ids.Add(ids[0]);
        using var response = await Names(string.Join(',', ids));
        await Status(response, 200);
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"items\":[]}");
    }

    [Fact]
    public async Task DonorNamesSkipsEndedLinkWithoutItsOwnFrozenCopy()
    {
        var id = await NamedStudent("NamesMissingCopy225");
        await CreatedLink(id);
        using (var end = await Unlink(id)) await Status(end, 200);
        var latestLink = await CreatedLink(id);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SponsorshipDbContext>();
            var link = await db.Links.SingleAsync(l => l.Id == latestLink);
            link.EndType = LinkEndTypes.Ended;
            link.EndDate = DateOnly.Parse(Date(), System.Globalization.CultureInfo.InvariantCulture);
            link.EndedAt = DateTimeOffset.MaxValue;
            await db.SaveChangesAsync();
        }
        using var response = await Names(id);
        await Status(response, 200);
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"items\":[]}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("stu_bad")]
    [InlineData("stu_ABCDEF0123456789abcdef0123456789")]
    [InlineData("stu_00000000000000000000000000000000,")]
    [InlineData("too-many")]
    public async Task DonorNamesRejectsInvalidIds(string? ids)
    {
        if (ids == "too-many") ids = string.Join(',', Enumerable.Range(0, 101).Select(i => $"stu_{i:x32}"));
        using var response = await Names(ids);
        await Error(response, 400, "VALIDATION_ERROR");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("errors").GetProperty("ids").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DonorNamesAcceptsOneHundredDistinctIds()
    {
        using var response = await Names(string.Join(',', Enumerable.Range(0, 100).Select(i => $"stu_{i:x32}")));
        await Status(response, 200);
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"items\":[]}");
    }

    [Theory]
    [InlineData("missing", 401)]
    [InlineData("malformed", 401)]
    [InlineData("expired", 401)]
    [InlineData("staff", 403)]
    public async Task DonorNamesRequiresValidDonorTokenBeforeFinance(string scenario, int status)
    {
        var token = scenario switch
        {
            "missing" => null,
            "malformed" => "malformed-token",
            "expired" => TestTokens.Issue([], subject: "usr_donor_x", lifetime: TimeSpan.FromMinutes(-5), extraClaims: [new Claim(KjClaims.Role, KjRoles.Donor)]),
            _ => Staff
        };
        using var response = await Send(HttpMethod.Get, $"{Mine}/names?ids=stu_00000000000000000000000000000000", token);
        await Status(response, status);
        _factory.Finance.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("disabled", 403, "ACCOUNT_DISABLED")]
    [InlineData("inactive", 403, "DONOR_INACTIVE")]
    [InlineData("missing", 404, "NOT_A_DONOR")]
    [InlineData("down", 503, "FINANCE_UNAVAILABLE")]
    public async Task DonorNamesPreservesFinanceRefusals(string scenario, int status, string code)
    {
        if (scenario == "disabled") _factory.Finance.Donors[DonorId] = new(DonorId, "usr_donor_x", AccountStatus: "disabled");
        if (scenario == "inactive") _factory.Finance.Donors[DonorId] = new(DonorId, "usr_donor_x", Status: "inactive");
        if (scenario == "missing") _factory.Finance.Donors.TryRemove(DonorId, out _);
        _factory.Finance.Down = scenario == "down";
        using var response = await Names("stu_00000000000000000000000000000000");
        await Error(response, status, code);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task DonorNamesWritesRemainMethodNotAllowed(string method)
    {
        foreach (var token in new[] { null, Donor, Staff })
        {
            using var response = await Send(new HttpMethod(method), $"{Mine}/names", token);
            await Error(response, 405, "METHOD_NOT_ALLOWED");
            response.Content.Headers.Allow.Should().Contain("GET");
        }
    }

    private Task<HttpResponseMessage> Names(string? ids) => Send(HttpMethod.Get, $"{Mine}/names" + (ids is null ? "" : "?ids=" + Uri.EscapeDataString(ids)), Donor);
    private static string NamesBody(string id, string name) => JsonSerializer.Serialize(new { items = new[] { new { id, preferredName = name } } });

    private async Task<string> NamedStudent(string name)
    {
        var id = await Create();
        await Edit(id, new { preferredName = name });
        return id;
    }
}
