using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;

namespace KJWebsite.IntegrationTests;

public sealed class PressCoverageTests : IDisposable
{
    private const string Admin = "/api/v1/admin/content";
    private const string Public = "/api/v1/content";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _staff = TestTokens.Issue([Permissions.ContentView, Permissions.ContentCreate, Permissions.ContentUpdate, Permissions.ContentPublish]);

    public PressCoverageTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task PressItemsUseTheEditorialWorkflowAndOnlyPublishedCoverageIsPublic()
    {
        await CreatePress("daily-star", "ডেইলি স্টার", status: "draft");
        await PutCoverage("daily-star", new { outlet = "The Daily Star", publishedOn = "2026-10-01", externalUrl = "https://example.com/story", language = "en" });

        (await Get($"{Public}/press/daily-star/coverage")).StatusCode.Should().Be(HttpStatusCode.NotFound, "a draft is not public");
        (await Json(await Get($"{Public}/press-coverage?lang=en"))).GetProperty("items").GetArrayLength().Should().Be(0);

        (await Send(HttpMethod.Post, $"{Admin}/items/press/daily-star/workflow", new { to = "published" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await Json(await Get($"{Public}/press/daily-star/coverage"));
        view.GetProperty("outlet").GetString().Should().Be("The Daily Star");
        view.GetProperty("permissionNote").ValueKind.Should().Be(JsonValueKind.Null, "internal notes are not public");
    }

    [Fact]
    public async Task AHostedClippingNeedsRecordedPermissionAndIsHiddenUnlessGranted()
    {
        await CreatePress("prothom-alo", "প্রথম আলো");
        var coverage = new { outlet = "Prothom Alo", publishedOn = "2026-09-20", externalUrl = "https://example.com/pa", language = "bn" };

        (await PutCoverage("prothom-alo", new { coverage.outlet, coverage.publishedOn, coverage.externalUrl, coverage.language, clippingUrl = "https://cdn.example.org/clip.pdf" }, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("message").GetString().Should().Contain("granted");
        await PutCoverage("prothom-alo", new { coverage.outlet, coverage.publishedOn, coverage.externalUrl, coverage.language, clippingUrl = "https://cdn.example.org/clip.pdf", reproductionPermission = "granted" }, HttpStatusCode.BadRequest);
        await PutCoverage("prothom-alo", new { coverage.outlet, coverage.publishedOn, coverage.externalUrl, coverage.language, reproductionPermission = "maybe" }, HttpStatusCode.BadRequest);
        await PutCoverage("prothom-alo", new { coverage.outlet, coverage.publishedOn, externalUrl = "http://insecure.example.com", coverage.language }, HttpStatusCode.BadRequest);

        await PutCoverage("prothom-alo", new { coverage.outlet, coverage.publishedOn, coverage.externalUrl, coverage.language, clippingUrl = "https://cdn.example.org/clip.pdf", reproductionPermission = "granted", permissionNote = "Editor agreed by email 2026-09-18" });
        (await Json(await Get($"{Public}/press/prothom-alo/coverage"))).GetProperty("clippingUrl").GetString().Should().Be("https://cdn.example.org/clip.pdf");

        // permission withdrawn: the clipping disappears from the public API immediately, while the link stays
        await PutCoverage("prothom-alo", new { coverage.outlet, coverage.publishedOn, coverage.externalUrl, coverage.language, reproductionPermission = "linked-only" });
        var after = await Json(await Get($"{Public}/press/prothom-alo/coverage"));
        after.GetProperty("clippingUrl").ValueKind.Should().Be(JsonValueKind.Null);
        after.GetProperty("externalUrl").GetString().Should().Be("https://example.com/pa");
    }

    [Fact]
    public async Task FeaturedOrderDrivesTheHomepageStripWithoutADeploymentAndLanguageFiltersWork()
    {
        foreach (var (slug, date, language) in new[] { ("a-story", "2026-08-01", "en"), ("b-story", "2026-09-01", "bn"), ("c-story", "2026-10-01", "en") })
        {
            await CreatePress(slug, slug);
            await PutCoverage(slug, new { outlet = "Outlet " + slug, publishedOn = date, externalUrl = "https://example.com/" + slug, language });
        }

        (await Slugs("press-coverage?lang=en")).Should().Equal("c-story", "b-story", "a-story");
        (await Slugs("press-coverage?lang=en&coverageLanguage=bn")).Should().Equal("b-story");
        (await Slugs("press-coverage?lang=en&featured=true")).Should().BeEmpty();

        (await Send(HttpMethod.Put, $"{Admin}/press-featured-order", new { slugs = new[] { "a-story", "c-story" } })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Slugs("press-coverage?lang=en&featured=true")).Should().Equal("a-story", "c-story");

        (await Send(HttpMethod.Put, $"{Admin}/press-featured-order", new { slugs = new[] { "c-story", "b-story" } })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Slugs("press-coverage?lang=en&featured=true")).Should().Equal("c-story", "b-story");
        (await Send(HttpMethod.Put, $"{Admin}/press-featured-order", new { slugs = new[] { "nope" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CoverageEditsUseOptimisticConcurrency()
    {
        await CreatePress("x", "X");
        using var first = await Send(HttpMethod.Get, $"{Admin}/press/x/coverage", null);
        first.StatusCode.Should().Be(HttpStatusCode.NotFound, "no details yet");
        await PutCoverage("x", new { outlet = "O", publishedOn = "2026-01-01", externalUrl = "https://example.com/x", language = "en" });
        using var read = await Send(HttpMethod.Get, $"{Admin}/press/x/coverage", null);
        var etag = read.Headers.ETag!.ToString();
        await PutCoverage("x", new { outlet = "O2", publishedOn = "2026-01-01", externalUrl = "https://example.com/x", language = "en" });

        using var stale = new HttpRequestMessage(HttpMethod.Put, $"{Admin}/press/x/coverage") { Content = JsonContent.Create(new { outlet = "O3", publishedOn = "2026-01-01", externalUrl = "https://example.com/x", language = "en" }) };
        stale.Headers.Authorization = new("Bearer", _staff);
        stale.Headers.TryAddWithoutValidation("If-Match", etag);
        (await _client.SendAsync(stale)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- helpers

    private async Task CreatePress(string slug, string title, string status = "published")
    {
        (await Send(HttpMethod.Post, $"{Admin}/press", new
        {
            slug,
            status,
            translations = new object[]
            {
                new { lang = "en", title, summary = "Excerpt", body = "B" },
                new { lang = "bn", title, summary = "উদ্ধৃতি", body = "বি" }
            }
        })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private async Task<JsonElement> PutCoverage(string slug, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await Send(HttpMethod.Put, $"{Admin}/press/{slug}/coverage", body);
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        return await Json(response);
    }

    private async Task<string[]> Slugs(string path) =>
        [.. (await Json(await Get($"{Public}/{path}"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("slug").GetString()!)];

    private Task<HttpResponseMessage> Get(string url) => _client.GetAsync(url);

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new("Bearer", _staff);
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
