using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CtaSubmissionService.Data;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class EngagementInboxTests : IDisposable
{
    private const string Admin = "/api/v1/admin/cta";
    private readonly CtaApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _manager = TestTokens.Issue([Permissions.CtaView, Permissions.CtaManage], subject: "usr_manager");
    private readonly string _viewer = TestTokens.Issue([Permissions.CtaView], subject: "usr_viewer");

    public EngagementInboxTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task RulesRouteByCategoryAndKeywordInOrderAndMediaIsAlwaysPrioritised()
    {
        (await Put($"{Admin}/routing-rules", new
        {
            rules = new object[]
            {
                new { name = "Press to comms lead", matchKeyword = "journalist", assignTo = "usr_comms", priority = "urgent" },
                new { name = "Partnerships to chair", matchCategory = "partnership", assignTo = "usr_chair" },
                new { name = "Bengali general", matchLanguage = "bn", matchCategory = "general", assignTo = "usr_bn" }
            }
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var media = await Submit("media", "en", "I am a journalist at The Daily News, deadline today");
        var partnership = await Submit("partnership", "en", "We would like to sponsor");
        var bn = await Submit("contact", "bn", "প্রশ্ন");
        var plainMedia = await Submit("media", "en", "Interview request");
        var join = await Submit("join", "en", "I want to join");

        (await Item(media)).Should().Match<JsonElement>(i => i.GetProperty("assignedTo").GetString() == "usr_comms" && i.GetProperty("priority").GetString() == "urgent" && i.GetProperty("category").GetString() == "media",
            "the first matching rule wins even though the media category exists");
        (await Item(partnership)).GetProperty("assignedTo").GetString().Should().Be("usr_chair");
        (await Item(bn)).GetProperty("assignedTo").GetString().Should().Be("usr_bn");
        var unrouted = await Item(plainMedia);
        unrouted.GetProperty("priority").GetString().Should().Be("high", "media enquiries are never below high");
        unrouted.GetProperty("assignedTo").ValueKind.Should().Be(JsonValueKind.Null);
        (await Item(join)).GetProperty("category").GetString().Should().Be("membership");

        // the inbox puts urgent and high ahead of normal
        var all = await Json(await Get($"{Admin}/inbox"));
        all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("priority").GetString()).Should().BeInAscendingOrder(Comparer<string?>.Create((a, b) => Rank(a).CompareTo(Rank(b))));
        all.GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(5, "the five submitted here plus the seeded sample enquiry");

        (await Json(await Get($"{Admin}/inbox?category=media&assignee=none"))).GetProperty("total").GetInt32().Should().Be(1);
        (await Put($"{Admin}/routing-rules", new { rules = new object[] { new { name = "Catch everything", assignTo = "x" } } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var history = await Json(await Get($"{Admin}/inbox/{media}"));
        history.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("type").GetString()).Should().Equal("received", "routed");
    }

    [Fact]
    public async Task SlaClocksRunOnlyWithAgreedTargetsAndBreachesShowBeforeEscalation()
    {
        var none = await Submit("media", "en", "Question");
        (await Item(none)).GetProperty("slaState").GetString().Should().Be("none", "no target has been agreed, so there is no clock");

        (await Put($"{Admin}/sla-targets", new { targets = new { media = 240, general = 1440 } })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Put($"{Admin}/sla-targets", new { targets = new { fax = 5 } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var fresh = await Submit("media", "en", "Fresh");
        var risky = await Submit("media", "en", "At risk");
        var late = await Submit("media", "en", "Late");
        var generalLate = await Submit("contact", "en", "Late general");
        await Age(risky, TimeSpan.FromMinutes(200)); // 40 of 240 minutes left: under a quarter
        await Age(late, TimeSpan.FromMinutes(300));
        await Age(generalLate, TimeSpan.FromMinutes(100)); // general target is 1440

        (await Item(fresh)).GetProperty("slaState").GetString().Should().Be("on_track");
        (await Item(risky)).GetProperty("slaState").GetString().Should().Be("at_risk");
        (await Item(late)).GetProperty("slaState").GetString().Should().Be("breached");
        (await Item(generalLate)).GetProperty("slaState").GetString().Should().Be("on_track");

        (await Json(await Get($"{Admin}/inbox?slaState=at_risk"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Should().Equal(risky);
        (await Json(await Get($"{Admin}/inbox?status=open&slaState=breached"))).GetProperty("total").GetInt32().Should().Be(1);

        // answering stops the clock: late stays met_late forever, and the report shows it
        (await Post($"{Admin}/inbox/{late}/respond", new { note = "Replied by phone" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Item(late)).GetProperty("slaState").GetString().Should().Be("met_late");
        (await Post($"{Admin}/inbox/{fresh}/respond", new { note = "Replied" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Item(fresh)).GetProperty("slaState").GetString().Should().Be("met");

        var report = await Json(await Get($"{Admin}/sla-report"));
        var mediaRow = report.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("category").GetString() == "media");
        mediaRow.GetProperty("received").GetInt32().Should().Be(4);
        mediaRow.GetProperty("responded").GetInt32().Should().Be(2);
        mediaRow.GetProperty("respondedLate").GetInt32().Should().Be(1);
        mediaRow.GetProperty("openAtRisk").GetInt32().Should().Be(1);
        mediaRow.GetProperty("targetMinutes").GetInt32().Should().Be(240);
        mediaRow.GetProperty("medianResponseMinutes").GetDouble().Should().BeGreaterThan(0);

        // removing a target removes the clock
        (await Put($"{Admin}/sla-targets", new { targets = new Dictionary<string, int?> { ["media"] = null } })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Item(risky)).GetProperty("slaState").GetString().Should().Be("none");
    }

    [Fact]
    public async Task EnquiriesCanBeAssignedAnsweredAndClosedAndPermissionsAreEnforced()
    {
        var id = await Submit("contact", "en", "Hello");
        (await Post($"{Admin}/inbox/{id}/assign", new { assignee = "me" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var item = await Item(id);
        item.GetProperty("assignedTo").GetString().Should().Be("usr_manager");
        item.GetProperty("status").GetString().Should().Be("in_progress");
        (await Json(await Get($"{Admin}/inbox?assignee=me"))).GetProperty("total").GetInt32().Should().Be(1);

        (await Post($"{Admin}/inbox/{id}/close", new { note = "Spam" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Post($"{Admin}/inbox/{id}/assign", new { assignee = "usr_other" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Item(id)).GetProperty("slaState").GetString().Should().Be("none");
        (await Post($"{Admin}/inbox/nope/close", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var viewerWrite = new HttpRequestMessage(HttpMethod.Post, $"{Admin}/inbox/{id}/respond") { Content = JsonContent.Create(new { }) };
        viewerWrite.Headers.Authorization = new("Bearer", _viewer);
        (await _client.SendAsync(viewerWrite)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var anonymous = await _client.GetAsync($"{Admin}/inbox");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- helpers

    private async Task<string> Submit(string ctaType, string language, string text)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/cta/submissions", new
        {
            formName = ctaType == "join" ? "registration" : "contact",
            ctaType,
            language,
            sourcePath = "/contact",
            values = new Dictionary<string, object?> { ["message"] = text, ["email"] = "sender@example.org" },
            submittedAt = DateTimeOffset.UtcNow
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task Age(string id, TimeSpan by)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CtaDbContext>();
        var row = await db.Submissions.SingleAsync(s => s.Id == id);
        row.CreatedAt -= by;
        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> Item(string id) => (await Json(await Get($"{Admin}/inbox/{id}"))).GetProperty("item");

    private static int Rank(string? priority) => priority switch { "urgent" => 0, "high" => 1, _ => 2 };

    private Task<HttpResponseMessage> Get(string url) => Send(HttpMethod.Get, url, null);

    private Task<HttpResponseMessage> Post(string url, object body) => Send(HttpMethod.Post, url, body);

    private Task<HttpResponseMessage> Put(string url, object body) => Send(HttpMethod.Put, url, body);

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new("Bearer", _manager);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
