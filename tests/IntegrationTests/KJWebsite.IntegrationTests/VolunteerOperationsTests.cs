using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using MembershipService.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class VolunteerOperationsTests : IDisposable
{
    private const string Me = "/api/v1/members/me/volunteer";
    private const string Admin = "/api/v1/admin/membership/volunteer";
    private readonly MembershipApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _staff = TestTokens.Issue([Permissions.EventsManage], subject: "usr_staff");

    public VolunteerOperationsTests()
    {
        _client = _factory.CreateClient();
        Seed(("rina", "Robotics, Teaching", true), ("sami", "Cooking, Teaching", false), ("tara", null, false));
    }

    [Fact]
    public async Task SubmittedHoursCountNowhereUntilApprovedAndRejectionsCarryAReason()
    {
        var id = await Submit("rina", "school-lab", 90, "Ran the robotics club");
        (await Rollup("project")).GetProperty("total").GetProperty("approvedMinutes").GetInt32().Should().Be(0, "unapproved hours count nowhere");

        using var mine = await Member("rina", HttpMethod.Get, $"{Me}/hours");
        var summary = JsonDocument.Parse(await mine.Content.ReadAsStringAsync()).RootElement.GetProperty("summary");
        summary.GetProperty("approvedMinutes").GetInt32().Should().Be(0);
        summary.GetProperty("pendingEntries").GetInt32().Should().Be(1);

        (await Staff(HttpMethod.Post, $"{Admin}/hours/{id}/reject", new { reason = " " })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Staff(HttpMethod.Post, $"{Admin}/hours/{id}/reject", new { reason = "Wrong date" })).StatusCode.Should().Be(HttpStatusCode.OK);
        using var afterReject = await Member("rina", HttpMethod.Get, $"{Me}/hours");
        (await afterReject.Content.ReadAsStringAsync()).Should().Contain("Wrong date");

        // The volunteer corrects it and it returns to review, then an organiser approves.
        using var fixedEntry = await Member("rina", HttpMethod.Put, $"{Me}/hours/{id}", Hours("school-lab", 120, "Ran the robotics club (corrected)"));
        fixedEntry.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Staff(HttpMethod.Post, $"{Admin}/hours/{id}/approve")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Member("rina", HttpMethod.Put, $"{Me}/hours/{id}", Hours("school-lab", 5, "x"))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Member("rina", HttpMethod.Delete, $"{Me}/hours/{id}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RollupsAgreeWithTheApprovedEntriesAndCarryAFingerprint()
    {
        var a = await Submit("rina", "school-lab", 60, "One");
        var b = await Submit("rina", "school-lab", 30, "Two");
        var c = await Submit("sami", "school-lab", 45, "Three");
        var d = await Submit("sami", "food-drive", 120, "Four");
        await Submit("sami", "food-drive", 500, "never approved");
        foreach (var id in new[] { a, b, c, d })
        {
            (await Staff(HttpMethod.Post, $"{Admin}/hours/{id}/approve")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var byProject = (await Rollup("project")).GetProperty("items").EnumerateArray().ToDictionary(r => r.GetProperty("key").GetString()!);
        byProject["school-lab"].GetProperty("approvedMinutes").GetInt32().Should().Be(135);
        byProject["school-lab"].GetProperty("approvedEntries").GetInt32().Should().Be(3);
        byProject["food-drive"].GetProperty("approvedMinutes").GetInt32().Should().Be(120);
        byProject["food-drive"].GetProperty("pendingEntries").GetInt32().Should().Be(1);
        byProject["school-lab"].GetProperty("fingerprint").GetString().Should().HaveLength(16);

        var byMember = (await Rollup("member")).GetProperty("items").EnumerateArray().Sum(r => r.GetProperty("approvedMinutes").GetInt32());
        byMember.Should().Be(255, "per-member and per-project rollups agree");
        (await Rollup("project")).GetProperty("total").GetProperty("approvedHours").GetDouble().Should().Be(4.25);
    }

    [Fact]
    public async Task OrganisersCannotReviewTheirOwnHoursAndEntriesAreValidated()
    {
        var id = await Submit("tara", "school-lab", 30, "Mine");
        using var own = new HttpRequestMessage(HttpMethod.Post, $"{Admin}/hours/{id}/approve");
        own.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue([Permissions.EventsManage, Permissions.MemberProfileEdit], subject: "usr_tara"));
        (await _client.SendAsync(own)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await Member("rina", HttpMethod.Post, $"{Me}/hours", Hours("school-lab", 0, "x"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Member("rina", HttpMethod.Post, $"{Me}/hours", Hours("School Lab", 10, "x"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Member("rina", HttpMethod.Post, $"{Me}/hours", new { projectSlug = "school-lab", date = "2999-01-01", minutes = 10, description = "x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.GetAsync($"{Admin}/hours")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SkillMatchingRespectsExpertiseConsent()
    {
        var opportunity = await CreateOpportunity("teach-saturday", ["teaching", "robotics"], slots: 2);

        using var board = await Member("sami", HttpMethod.Get, $"{Me}/opportunities");
        var item = JsonDocument.Parse(await board.Content.ReadAsStringAsync()).RootElement.GetProperty("items")[0];
        item.GetProperty("matchScore").GetInt32().Should().Be(1, "a member always sees a match against their own expertise");

        using var matches = await Staff(HttpMethod.Get, $"{Admin}/opportunities/{opportunity}/matches");
        var text = await matches.Content.ReadAsStringAsync();
        text.Should().Contain("KJ-2026-rina").And.NotContain("KJ-2026-sami", "sami did not consent to publish expertise");
    }

    [Fact]
    public async Task TheLastShiftSlotGoesToOnePersonEvenWhenTakenTogether()
    {
        var opportunity = await CreateOpportunity("one-slot", [], slots: 1);
        var shift = await FirstShift(opportunity);

        var codes = await Task.WhenAll(new[] { "rina", "sami", "tara" }.Select(async who =>
        {
            using var response = await Member(who, HttpMethod.Post, $"{Me}/shifts/{shift}/signup");
            return response.StatusCode;
        }));

        codes.Count(c => c == HttpStatusCode.OK).Should().Be(1);
        codes.Count(c => c == HttpStatusCode.Conflict).Should().Be(2);
    }

    [Fact]
    public async Task SigningUpTwiceIsIdempotentAndCancelFreesTheSlot()
    {
        var opportunity = await CreateOpportunity("flex", [], slots: 1);
        var shift = await FirstShift(opportunity);

        (await Member("rina", HttpMethod.Post, $"{Me}/shifts/{shift}/signup")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Member("rina", HttpMethod.Post, $"{Me}/shifts/{shift}/signup")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var full = await Member("sami", HttpMethod.Post, $"{Me}/shifts/{shift}/signup");
        full.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await full.Content.ReadAsStringAsync()).Should().Contain("SHIFT_FULL");

        (await Member("rina", HttpMethod.Delete, $"{Me}/shifts/{shift}/signup")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Member("sami", HttpMethod.Post, $"{Me}/shifts/{shift}/signup")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<string> CreateOpportunity(string slug, string[] skills, int slots)
    {
        using var response = await Staff(HttpMethod.Post, $"{Admin}/opportunities", new
        {
            slug,
            titleEn = "Saturday class",
            titleBn = "শনিবারের ক্লাস",
            descriptionEn = "Help teach",
            descriptionBn = "পড়াতে সাহায্য করুন",
            projectSlug = "school-lab",
            skills,
            shifts = new[] { new { startsAt = DateTimeOffset.UtcNow.AddDays(7), endsAt = DateTimeOffset.UtcNow.AddDays(7).AddHours(3), slots } }
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private async Task<string> FirstShift(string opportunityId)
    {
        using var list = await Staff(HttpMethod.Get, $"{Admin}/opportunities");
        return JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray()
            .First(o => o.GetProperty("id").GetString() == opportunityId).GetProperty("shifts")[0].GetProperty("id").GetString()!;
    }

    private async Task<string> Submit(string who, string project, int minutes, string description)
    {
        using var response = await Member(who, HttpMethod.Post, $"{Me}/hours", Hours(project, minutes, description));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> Rollup(string by)
    {
        using var response = await Staff(HttpMethod.Get, $"{Admin}/rollups?by={by}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static object Hours(string project, int minutes, string description) =>
        new { projectSlug = project, date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), minutes, description };

    private void Seed(params (string Key, string? Expertise, bool Consent)[] members)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        foreach (var (key, expertise, consent) in members)
        {
            db.Members.Add(new MemberEntity
            {
                Id = $"mem_{key}",
                UserId = $"usr_{key}",
                MemberCode = $"KJ-2026-{key}",
                ApplicationId = $"app_{key}",
                FirstName = key,
                LastName = "Tester",
                Status = "active",
                JoinedAt = DateTimeOffset.UtcNow,
                Expertise = expertise,
                ConsentExpertise = consent
            });
        }

        db.SaveChanges();
    }

    private Task<HttpResponseMessage> Member(string who, HttpMethod method, string path, object? body = null) =>
        Send(TestTokens.Issue([Permissions.MemberProfileEdit], subject: $"usr_{who}"), method, path, body);

    private Task<HttpResponseMessage> Staff(HttpMethod method, string path, object? body = null) => Send(_staff, method, path, body);

    private async Task<HttpResponseMessage> Send(string token, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
