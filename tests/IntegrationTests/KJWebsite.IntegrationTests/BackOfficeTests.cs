using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ContentService;
using ContentService.Data;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace KJWebsite.IntegrationTests;

public sealed class UncachedDashboardFactory : SqliteWebApplicationFactory<ContentServiceTestEntryPoint, ContentDbContext>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary),
            ["BackOffice:DashboardCacheSeconds"] = "0"
        }));
    }
}

public sealed class BackOfficeTests : IDisposable
{
    private const string Admin = "/api/v1/admin/content";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;

    private static readonly string[] AllPermissions =
    [
        Permissions.ContentView, Permissions.ContentCreate, Permissions.ContentUpdate, Permissions.ContentDelete, Permissions.ContentPublish,
        Permissions.DashboardView, Permissions.AuditView, Permissions.SettingsView, Permissions.SettingsManage
    ];

    public BackOfficeTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task TheDashboardSummarisesWhatNeedsAttention()
    {
        using var fresh = new UncachedDashboardFactory();
        using var client = fresh.CreateClient();
        var token = TestTokens.Issue(AllPermissions);
        await Post(client, "pages", new { slug = "review-me", status = "draft", translations = new[] { new { lang = "en", title = "Review me" } } }, token);
        await Send(client, HttpMethod.Post, $"{Admin}/items/page/review-me/workflow", new { to = "in_review" }, token);
        await Post(client, "pages", new { slug = "later", status = "draft", translations = new[] { new { lang = "en", title = "Later" } } }, token);
        await Send(client, HttpMethod.Post, $"{Admin}/items/page/later/workflow", new { to = "scheduled", publishAt = DateTimeOffset.UtcNow.AddDays(1) }, token);
        await Post(client, "pages", new { slug = "gone", status = "draft", translations = new[] { new { lang = "en", title = "Gone" } } }, token);
        await Send(client, HttpMethod.Delete, $"{Admin}/pages/gone", null, token);

        using var response = await Send(client, HttpMethod.Get, $"{Admin}/dashboard", null, token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("statusByType").GetProperty("page").GetProperty("in_review").GetInt32().Should().Be(1);
        doc.GetProperty("statusByType").GetProperty("project").GetProperty("active").GetInt32().Should().BeGreaterThan(0);
        doc.GetProperty("awaitingReview").EnumerateArray().Single().GetProperty("slug").GetString().Should().Be("review-me");
        doc.GetProperty("upcomingScheduled").EnumerateArray().Single().GetProperty("slug").GetString().Should().Be("later");
        doc.GetProperty("trashCount").GetInt32().Should().Be(1);
        doc.GetProperty("translationGaps").GetProperty("byType").GetProperty("page").GetProperty("withGaps").GetInt32().Should().Be(2);
        doc.GetProperty("recentActivity").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TheSummaryIsCachedAndActivityNeverLeaksToCallersWithoutAuditView()
    {
        var full = TestTokens.Issue(AllPermissions);
        var limited = TestTokens.Issue([Permissions.DashboardView]);
        await Post(_client, "pages", new { slug = "a", status = "draft", translations = new[] { new { lang = "en", title = "A" } } }, full);
        using var first = await Json(_client, $"{Admin}/dashboard", full);
        first.RootElement.GetProperty("recentActivity").GetArrayLength().Should().BeGreaterThan(0);

        using var limitedView = await Json(_client, $"{Admin}/dashboard", limited);
        limitedView.RootElement.TryGetProperty("recentActivity", out var activity).Should().BeTrue();
        activity.ValueKind.Should().Be(JsonValueKind.Null, "a cached copy with activity must not be served to someone without audit.view");

        await Post(_client, "pages", new { slug = "b", status = "draft", translations = new[] { new { lang = "en", title = "B" } } }, full);
        using var cached = await Json(_client, $"{Admin}/dashboard", full);
        cached.RootElement.GetProperty("generatedAt").GetString().Should().Be(first.RootElement.GetProperty("generatedAt").GetString());
        cached.RootElement.GetProperty("statusByType").GetProperty("page").GetProperty("draft").GetInt32().Should().Be(1, "served from the 30 s cache");

        (await Send(_client, HttpMethod.Get, $"{Admin}/dashboard", null, TestTokens.Issue([Permissions.ContentView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.GetAsync($"{Admin}/dashboard")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SearchFindsItemsTagsRedirectsAndSettingsInAnyLanguage()
    {
        var token = TestTokens.Issue(AllPermissions);
        await Post(_client, "pages", new { slug = "flood-relief", status = "published", translations = new object[] { new { lang = "en", title = "Flood relief" }, new { lang = "bn", title = "বন্যা ত্রাণ" } } }, token);
        await Send(_client, HttpMethod.Post, $"{Admin}/tags", new { slug = "flood", names = new { en = "Flood", bn = "বন্যা" } }, token);
        await Send(_client, HttpMethod.Post, $"{Admin}/redirects", new { from = "/flood.html", to = "/pages/flood-relief" }, token);
        await Send(_client, HttpMethod.Put, $"{Admin}/settings/custom.flood-banner", new { value = new { bn = "ক", en = "A" } }, token);

        using var en = await Json(_client, $"{Admin}/search?q=FLOOD", token);
        var kinds = en.RootElement.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("kind").GetString()).ToList();
        kinds.Should().Contain(["item", "tag", "redirect", "setting"]);
        en.RootElement.GetProperty("hits")[0].GetProperty("title").GetString().Should().NotBeNull();
        using var bn = await Json(_client, $"{Admin}/search?q=বন্যা", token);
        bn.RootElement.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("kind").GetString()).Should().Contain(["item", "tag"]);
        using var none = await Json(_client, $"{Admin}/search?q=zzzzzz", token);
        none.RootElement.GetProperty("hits").GetArrayLength().Should().Be(0);

        foreach (var bad in new[] { "q=a", "q=", "q=ab&limit=0", "q=ab&limit=51" })
        {
            (await Send(_client, HttpMethod.Get, $"{Admin}/search?{bad}", null, token)).StatusCode.Should().Be(HttpStatusCode.BadRequest, bad);
        }
    }

    [Fact]
    public async Task SearchOnlyCoversWhatTheCallerMayRead()
    {
        var admin = TestTokens.Issue(AllPermissions);
        await Post(_client, "pages", new { slug = "budget-secret", status = "draft", translations = new[] { new { lang = "en", title = "Budget plans" } } }, admin);
        await Send(_client, HttpMethod.Put, $"{Admin}/settings/custom.budget-key", new { value = new { bn = "ক", en = "A" }, isSecret = true }, admin);

        using var dashboardOnly = await Json(_client, $"{Admin}/search?q=budget", TestTokens.Issue([Permissions.DashboardView]));
        dashboardOnly.RootElement.GetProperty("hits").GetArrayLength().Should().Be(0, "no content.view, no settings.view");
        dashboardOnly.RootElement.GetProperty("searched").GetArrayLength().Should().Be(0);

        using var content = await Json(_client, $"{Admin}/search?q=budget", TestTokens.Issue([Permissions.DashboardView, Permissions.ContentView]));
        content.RootElement.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("kind").GetString()).Should().Equal("item");
        content.RootElement.GetProperty("searched").EnumerateArray().Select(h => h.GetString()).Should().NotContain("setting");

        using var settings = await Json(_client, $"{Admin}/search?q=budget", TestTokens.Issue([Permissions.DashboardView, Permissions.SettingsView]));
        settings.RootElement.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("kind").GetString()).Should().Equal("setting");
    }

    [Fact]
    public async Task BulkActionsReportEachItemAndPartialSuccessIsHonest()
    {
        var token = TestTokens.Issue(AllPermissions);
        foreach (var slug in new[] { "one", "two", "three" })
        {
            await Post(_client, "pages", new { slug, status = "draft", translations = new[] { new { lang = "en", title = slug } } }, token);
        }

        using var response = await Send(_client, HttpMethod.Post, $"{Admin}/bulk", new
        {
            action = "workflow:published",
            items = new object[] { new { type = "page", slug = "one" }, new { type = "page", slug = "ghost" }, new { type = "page", slug = "two" }, new { type = "widget", slug = "x" }, new { type = "page", slug = "one" } }
        }, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>());
        body.GetProperty("total").GetInt32().Should().Be(4, "a repeated item is processed once");
        body.GetProperty("succeeded").GetInt32().Should().Be(2);
        body.GetProperty("failed").GetInt32().Should().Be(2);
        var results = body.GetProperty("results").EnumerateArray().ToDictionary(r => r.GetProperty("slug").GetString()!);
        results["one"].GetProperty("status").GetString().Should().Be("ok");
        results["ghost"].GetProperty("code").GetString().Should().Be("NOT_FOUND");
        results["x"].GetProperty("code").GetString().Should().Be("INVALID");
        (await _client.GetAsync("/api/v1/content/pages/one")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync("/api/v1/content/pages/three")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var again = await Send(_client, HttpMethod.Post, $"{Admin}/bulk", new { action = "workflow:published", items = new[] { new { type = "page", slug = "one" } } }, token);
        (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0].GetProperty("code").GetString().Should().Be("INVALID", "published to published is not a move");
    }

    [Fact]
    public async Task BulkDeleteRestoreAndTagging()
    {
        var token = TestTokens.Issue(AllPermissions);
        await Send(_client, HttpMethod.Post, $"{Admin}/tags", new { slug = "urgent", names = new { en = "Urgent", bn = "জরুরি" } }, token);
        foreach (var slug in new[] { "a", "b" })
        {
            await Post(_client, "pages", new { slug, status = "draft", translations = new[] { new { lang = "en", title = slug } } }, token);
        }

        var items = new[] { new { type = "page", slug = "a" }, new { type = "page", slug = "b" } };
        (await Bulk("tag-add", items, token, tag: "জরুরি")).GetProperty("succeeded").GetInt32().Should().Be(2);
        using (var tagged = await Json(_client, $"{Admin}/pages/a", token))
        {
            tagged.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal("urgent");
        }

        (await Bulk("tag-remove", items, token, tag: "urgent")).GetProperty("succeeded").GetInt32().Should().Be(2);
        (await Bulk("tag-add", items, token, tag: "unknown")).GetProperty("results")[0].GetProperty("code").GetString().Should().Be("INVALID");
        (await Bulk("tag-add", items, token, tag: null)).GetProperty("failed").GetInt32().Should().Be(2);
        (await Bulk("delete", items, token)).GetProperty("succeeded").GetInt32().Should().Be(2);
        (await Bulk("delete", items, token)).GetProperty("failed").GetInt32().Should().Be(2, "already deleted");
        (await Bulk("restore", items, token)).GetProperty("succeeded").GetInt32().Should().Be(2);
        (await Bulk("explode", items, token)).GetProperty("results")[0].GetProperty("code").GetString().Should().Be("INVALID");
    }

    [Fact]
    public async Task BulkChecksEveryItemAgainstTheCallersPermissions()
    {
        var admin = TestTokens.Issue(AllPermissions);
        await Post(_client, "pages", new { slug = "p", status = "draft", translations = new[] { new { lang = "en", title = "P" } } }, admin);
        var editor = TestTokens.Issue([Permissions.ContentView, Permissions.ContentUpdate]);
        var items = new[] { new { type = "page", slug = "p" } };

        (await Bulk("delete", items, editor)).GetProperty("results")[0].GetProperty("code").GetString().Should().Be("FORBIDDEN");
        (await Bulk("workflow:published", items, editor)).GetProperty("results")[0].GetProperty("code").GetString().Should().Be("FORBIDDEN", "publishing needs content.publish");
        (await Bulk("workflow:in_review", items, editor)).GetProperty("succeeded").GetInt32().Should().Be(1);
        (await Send(_client, HttpMethod.Post, $"{Admin}/bulk", new { action = "delete", items = Array.Empty<object>() }, admin)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(_client, HttpMethod.Post, $"{Admin}/bulk", new { action = "delete", items = Enumerable.Range(0, 101).Select(i => new { type = "page", slug = $"s{i}" }) }, admin)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(_client, HttpMethod.Post, $"{Admin}/bulk", new { action = "delete", items = items }, TestTokens.Issue([]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BulkChangesAreAuditedAndEvictThePublicCache()
    {
        var token = TestTokens.Issue(AllPermissions);
        await Post(_client, "pages", new { slug = "cached", status = "draft", translations = new[] { new { lang = "en", title = "Cached" } } }, token);
        (await _client.GetAsync("/api/v1/content/pages?limit=100")).StatusCode.Should().Be(HttpStatusCode.OK);

        await Bulk("workflow:published", new[] { new { type = "page", slug = "cached" } }, token);

        using var list = await Json(_client, "/api/v1/content/pages?limit=100", null);
        list.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("slug").GetString()).Should().Contain("cached");
        using var audit = await Json(_client, $"{Admin}/audit?entity=ContentItem&action=update", token);
        audit.RootElement.GetProperty("items").EnumerateArray().Should().Contain(e => e.GetProperty("operation").GetString() == $"POST {Admin}/bulk");
    }

    private async Task<JsonElement> Bulk(string action, object items, string token, string? tag = null)
    {
        using var response = await Send(_client, HttpMethod.Post, $"{Admin}/bulk", new { action, items, tag }, token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task Post(HttpClient client, string segment, object body, string token) =>
        (await Send(client, HttpMethod.Post, $"{Admin}/{segment}", body, token)).StatusCode.Should().Be(HttpStatusCode.Created);

    private static async Task<JsonDocument> Json(HttpClient client, string path, string? token)
    {
        using var response = token is null ? await client.GetAsync(path) : await Send(client, HttpMethod.Get, path, null, token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? body, string token)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
