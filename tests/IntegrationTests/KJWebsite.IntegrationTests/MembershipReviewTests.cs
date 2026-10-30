using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using MembershipService.Review;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class MembershipReviewTests : IDisposable
{
    private const string Admin = "/api/v1/admin/membership/applications";
    private readonly MembershipApiFactory _factory = new();
    private readonly HttpClient _client;

    public MembershipReviewTests() => _client = _factory.CreateClient();

    [Theory]
    [InlineData("awaiting", "under_review", null)]
    [InlineData("awaiting", "rejected", null)]
    [InlineData("awaiting", "approved", "invalid_transition")]
    [InlineData("awaiting", "shortlisted", "invalid_transition")]
    [InlineData("awaiting", "awaiting", "invalid_transition")]
    [InlineData("under_review", "shortlisted", null)]
    [InlineData("under_review", "awaiting", "invalid_transition")]
    [InlineData("shortlisted", "interview_scheduled", "interview_date_required")]
    [InlineData("interview_scheduled", "approved", null)]
    [InlineData("shortlisted", "approved", "invalid_transition")]
    [InlineData("postponed", "under_review", null)]
    [InlineData("postponed", "shortlisted", "invalid_transition")]
    [InlineData("approved", "rejected", "final")]
    [InlineData("rejected", "under_review", "final")]
    [InlineData("withdrawn", "under_review", "final")]
    [InlineData("awaiting", "nonsense", "unknown_status")]
    public void StateMachineAllowsOnlyDefinedMoves(string from, string to, string? expected) =>
        StatusTransitions.Check(from, to, null).Should().Be(expected);

    [Fact]
    public void EveryOpenStateCanBeRejectedPostponedOrWithdrawnExceptPostponedItself()
    {
        foreach (var from in new[] { "awaiting", "under_review", "shortlisted", "interview_scheduled" })
        {
            foreach (var to in new[] { "rejected", "postponed", "withdrawn" })
            {
                StatusTransitions.Check(from, to, null).Should().BeNull($"{from} -> {to}");
            }
        }
    }

    [Fact]
    public async Task TwentyApplicationsMoveInBulkWithEveryChangeRecordedAndQueued()
    {
        var ids = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            ids.Add(await Apply($"person{i}@example.org", $"0171000{i:D4}", i % 2 == 0 ? "bn" : "en"));
        }

        using var response = await Send(HttpMethod.Post, $"{Admin}/transition", body: new { ids, to = "under_review" });
        var results = (await Json(response)).GetProperty("results");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        results.GetArrayLength().Should().Be(20);
        results.EnumerateArray().Should().OnlyContain(r => r.GetProperty("ok").GetBoolean() && r.GetProperty("notified").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        (await db.Applications.CountAsync(a => a.Status == "under_review")).Should().Be(20);
        (await db.StatusEvents.CountAsync(e => e.Status == "under_review" && e.By == "usr_test")).Should().Be(20);
        var outbox = await db.NotificationOutbox.ToListAsync();
        outbox.Should().HaveCount(20).And.OnlyContain(o => o.Template == "application.status.under_review" && o.SentAt == null);
        outbox.Count(o => o.Language == "bn").Should().Be(10);
        var audits = await db.Set<AuditEvent>().Where(a => a.Entity == "Application" && a.Action == "update").ToListAsync();
        audits.Should().HaveCount(20);
    }

    [Fact]
    public async Task BadItemsFailIndividuallyAndChangeNothing()
    {
        var good = await Apply("a@example.org", "01710000001");
        var rejected = await Apply("b@example.org", "01710000002");
        await Transition([rejected], "rejected");

        using var response = await Send(HttpMethod.Post, $"{Admin}/transition", body: new { ids = new[] { good, rejected, "app_missing" }, to = "under_review" });
        var results = (await Json(response)).GetProperty("results").EnumerateArray().ToList();

        results.Select(r => r.GetProperty("ok").GetBoolean()).Should().Equal(true, false, false);
        results[1].GetProperty("error").GetString().Should().Be("final");
        results[2].GetProperty("error").GetString().Should().Be("not_found");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        (await db.Applications.SingleAsync(a => a.Id == rejected)).Status.Should().Be("rejected");
        (await db.NotificationOutbox.CountAsync(o => o.ApplicationId == rejected)).Should().Be(1, "only the rejection was queued");
    }

    [Fact]
    public async Task InterviewNeedsADateAndKeepsIt()
    {
        var id = await Apply("c@example.org", "01710000003");
        await Transition([id], "under_review");
        await Transition([id], "shortlisted");

        var without = await Transition([id], "interview_scheduled");
        var at = DateTimeOffset.UtcNow.AddDays(3);
        var with = await Transition([id], "interview_scheduled", at);

        without[0].GetProperty("error").GetString().Should().Be("interview_date_required");
        with[0].GetProperty("ok").GetBoolean().Should().BeTrue();
        var detail = await Json(await Send(HttpMethod.Get, $"{Admin}/{id}"));
        detail.GetProperty("status").GetString().Should().Be("interview_scheduled");
        detail.GetProperty("interviewAt").GetDateTimeOffset().Should().BeCloseTo(at, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task HistoryIsCompleteOrderedAndCannotBeRewritten()
    {
        var id = await Apply("d@example.org", "01710000004");
        await Transition([id], "under_review");
        await Transition([id], "postponed");

        var detail = await Json(await Send(HttpMethod.Get, $"{Admin}/{id}"));
        detail.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("status").GetString())
            .Should().Equal("awaiting", "under_review", "postponed");
        detail.GetProperty("nextStatuses").EnumerateArray().Select(s => s.GetString()).Should().Contain("under_review");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        (await db.StatusEvents.FirstAsync()).Status = "approved";
        await db.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
    }

    [Fact]
    public async Task ReviewerScoreAndNotesAreInternalOnly()
    {
        var id = await Apply("e@example.org", "01710000005");
        using var patched = await Send(HttpMethod.Patch, $"{Admin}/{id}", body: new { score = 82, reviewerId = "usr_9" });
        using var note = await Send(HttpMethod.Post, $"{Admin}/{id}/notes", body: new { text = "Strong referee, SECRETNOTE" });
        await Transition([id], "under_review");

        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        note.StatusCode.Should().Be(HttpStatusCode.Created);
        var detail = await Json(await Send(HttpMethod.Get, $"{Admin}/{id}"));
        detail.GetProperty("score").GetInt32().Should().Be(82);
        detail.GetProperty("reviewerId").GetString().Should().Be("usr_9");
        detail.GetProperty("notes")[0].GetProperty("text").GetString().Should().Contain("SECRETNOTE");

        var reference = detail.GetProperty("reference").GetString();
        using var lookup = await _client.PostAsJsonAsync("/api/v1/membership/applications/status", new { reference, email = "e@example.org" });
        var text = await lookup.Content.ReadAsStringAsync();
        lookup.StatusCode.Should().Be(HttpStatusCode.OK);
        text.Should().Contain("under_review").And.NotContain("SECRETNOTE").And.NotContain("82").And.NotContain("usr_9");

        using var cleared = await Send(HttpMethod.Patch, $"{Admin}/{id}", body: new { score = (int?)null });
        (await Json(cleared)).GetProperty("score").ValueKind.Should().Be(JsonValueKind.Null);
        (await Json(cleared)).GetProperty("reviewerId").GetString().Should().Be("usr_9", "absent fields are left alone");
    }

    [Fact]
    public async Task InvalidReviewInputIsRejected()
    {
        var id = await Apply("f@example.org", "01710000006");

        (await Send(HttpMethod.Patch, $"{Admin}/{id}", body: new { score = 101 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Admin}/{id}/notes", body: new { text = "  " })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Admin}/transition", body: new { ids = Array.Empty<string>(), to = "approved" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Admin}/transition", body: new { ids = new[] { id }, to = "bogus" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Get, $"{Admin}/app_missing")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListFiltersByStatusAndPages()
    {
        var a = await Apply("g1@example.org", "01710000007");
        await Apply("g2@example.org", "01710000008");
        await Transition([a], "under_review");

        var filtered = await Json(await Send(HttpMethod.Get, $"{Admin}?status=under_review"));
        var paged = await Json(await Send(HttpMethod.Get, $"{Admin}?limit=1"));

        filtered.GetProperty("total").GetInt32().Should().Be(1);
        filtered.GetProperty("items")[0].GetProperty("email").GetString().Should().Be("g1@example.org");
        paged.GetProperty("items").GetArrayLength().Should().Be(1);
        paged.GetProperty("total").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ReviewEndpointsNeedThePermission()
    {
        (await _client.GetAsync(Admin)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Get, Admin, TestTokens.Issue([Permissions.ContentView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Post, $"{Admin}/transition", TestTokens.Issue([Permissions.CtaView]), new { ids = new[] { "x" }, to = "approved" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AuditRowsCarryNoPersonalData()
    {
        var id = await Apply("private.person@example.org", "01710009999");
        await Transition([id], "under_review");

        using var scope = _factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<MembershipDbContext>().Set<AuditEvent>().ToListAsync();
        var all = string.Join('\n', rows.Select(r => r.Before + r.After));

        rows.Should().NotBeEmpty();
        all.Should().NotContain("Tahmid").And.NotContain("Rahman").And.NotContain("private.person").And.NotContain("01710009999");
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<string> Apply(string email, string phone, string language = "en")
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/membership/applications", new
        {
            firstName = "Tahmid",
            lastName = "Rahman",
            email,
            phone,
            bloodGroup = "O+",
            currentAddress = "Dhaka",
            academicInstitute = "DU",
            occupation = "Student",
            language
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private async Task<List<JsonElement>> Transition(IEnumerable<string> ids, string to, DateTimeOffset? interviewAt = null)
    {
        using var response = await Send(HttpMethod.Post, $"{Admin}/transition", body: new { ids, to, interviewAt });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return [.. (await Json(response)).GetProperty("results").EnumerateArray()];
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token = null, object? body = null)
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
