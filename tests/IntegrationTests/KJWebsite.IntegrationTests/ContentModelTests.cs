using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ContentService.Data;
using FluentAssertions;

namespace KJWebsite.IntegrationTests;

public sealed class ContentModelTests : IDisposable
{
    private const string Admin = "/api/v1/admin/content";
    private const string Public = "/api/v1/content";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;

    public ContentModelTests() => _client = _factory.CreateClient();

    [Theory]
    [InlineData("pages", "page")]
    [InlineData("faqs", "faq")]
    [InlineData("team-members", "team-member")]
    [InlineData("testimonials", "testimonial")]
    [InlineData("partners", "partner")]
    [InlineData("press", "press")]
    public async Task EveryGenericTypeSharesTheSameWorkflow(string segment, string type)
    {
        (await Send(HttpMethod.Post, $"{Admin}/{segment}", Item("hello", "published", "Hello", "হ্যালো"))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Send(HttpMethod.Post, $"{Admin}/{segment}", Item("secret", "draft", "Secret"))).StatusCode.Should().Be(HttpStatusCode.Created);

        using var list = await Public_Get($"{Public}/{segment}?lang=bn");
        list.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("slug").GetString()).Should().Equal("hello");
        list.RootElement.GetProperty("lang").GetString().Should().Be("bn");
        using var detail = await Public_Get($"{Public}/{segment}/hello?lang=bn");
        detail.RootElement.GetProperty("title").GetString().Should().Be("হ্যালো");
        detail.RootElement.GetProperty("type").GetString().Should().Be(type);
        (await _client.GetAsync($"{Public}/{segment}/secret")).StatusCode.Should().Be(HttpStatusCode.NotFound, "drafts are not public");
        (await _client.GetAsync($"{Public}/{segment}/hello?lang=en")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Send(HttpMethod.Put, $"{Admin}/{segment}/hello", Item("hello", "published", "Hello again"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Delete, $"{Admin}/{segment}/hello", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.GetAsync($"{Public}/{segment}/hello")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Post, $"{Admin}/{segment}", Item("hello", "published", "Restored"))).StatusCode.Should().Be(HttpStatusCode.Created);
        using var restored = await Public_Get($"{Public}/{segment}/hello");
        restored.RootElement.GetProperty("title").GetString().Should().Be("Restored");
    }

    [Fact]
    public async Task AdminEndpointsRequireAPermission()
    {
        (await _client.GetAsync($"{Admin}/pages")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _client.PostAsJsonAsync($"{Admin}/tags", new { slug = "x", names = new { en = "X" } })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _client.PostAsJsonAsync($"{Admin}/redirects", new { from = "/a", to = "/b" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GenericPutHonoursIfMatch()
    {
        await Send(HttpMethod.Post, $"{Admin}/pages", Item("about", "published", "About"));
        using var read = await Send(HttpMethod.Get, $"{Admin}/pages/about", null);
        var tag = read.Headers.ETag!.Tag;

        (await Send(HttpMethod.Put, $"{Admin}/pages/about", Item("about", "published", "One"), ifMatch: tag)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var stale = await Send(HttpMethod.Put, $"{Admin}/pages/about", Item("about", "published", "Two"), ifMatch: tag);

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("শিক্ষা", "শিক্ষা  ")]
    [InlineData("য়", "য়")]
    [InlineData("ক‌খ", "কখ")]
    [InlineData("২০২৬", "2026")]
    [InlineData("#Education", "education ")]
    public void TagNamesAreComparedAfterBengaliAwareNormalisation(string a, string b) =>
        TagNormalizer.Normalize(a).Should().Be(TagNormalizer.Normalize(b));

    [Fact]
    public async Task OneTagSpansBothLanguagesAndNamesCannotDiverge()
    {
        (await Send(HttpMethod.Post, $"{Admin}/tags", new { slug = "education", names = new { en = "Education", bn = "শিক্ষা" } })).StatusCode.Should().Be(HttpStatusCode.Created);

        using var clash = await Send(HttpMethod.Post, $"{Admin}/tags", new { slug = "learning", names = new { en = "Learning", bn = "শিক্ষা " } });
        clash.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await clash.Content.ReadAsStringAsync()).Should().Contain("education");

        await Send(HttpMethod.Post, $"{Admin}/pages", Item("school", "published", "School", "স্কুল"));
        await Send(HttpMethod.Post, $"{Admin}/pages", Item("plain", "published", "Plain"));
        // Tagged by the Bengali name; the slug and the English name reach the same tag.
        (await Send(HttpMethod.Put, $"{Admin}/items/page/school/tags", new { tags = new[] { "শিক্ষা", "Education", "education" } })).StatusCode.Should().Be(HttpStatusCode.OK);

        using var admin = await Send(HttpMethod.Get, $"{Admin}/pages/school", null);
        JsonDocument.Parse(await admin.Content.ReadAsStringAsync()).RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal("education");
        foreach (var filter in new[] { "education", "শিক্ষা", "Education" })
        {
            using var filtered = await Public_Get($"{Public}/pages?lang=en&tag={Uri.EscapeDataString(filter)}");
            filtered.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("slug").GetString()).Should().Equal("school");
        }

        using var bn = await Public_Get($"{Public}/pages/school?lang=bn");
        bn.RootElement.GetProperty("tags")[0].GetProperty("name").GetString().Should().Be("শিক্ষা");
        using var en = await Public_Get($"{Public}/pages/school?lang=en");
        en.RootElement.GetProperty("tags")[0].GetProperty("name").GetString().Should().Be("Education");
    }

    [Fact]
    public async Task UnknownTagsAreRejectedNotCreated()
    {
        await Send(HttpMethod.Post, $"{Admin}/pages", Item("p", "published", "P"));

        (await Send(HttpMethod.Put, $"{Admin}/items/page/p/tags", new { tags = new[] { "nope" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Admin}/pages", Item("q", "published", "Q", tags: ["nope"]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RelatedContentWorksAcrossTypes()
    {
        await Send(HttpMethod.Post, $"{Admin}/pages", Item("about", "published", "About", "সম্পর্কে"));
        await Send(HttpMethod.Post, $"{Admin}/faqs", Item("how", "published", "How"));
        await Send(HttpMethod.Post, $"{Admin}/partners", Item("hidden", "draft", "Hidden"));

        (await Send(HttpMethod.Put, $"{Admin}/items/page/about/related", new
        {
            related = new object[]
            {
                new { type = "project", slug = "pathshala" },
                new { type = "news", slug = (string?)null },
            }
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var seeded = await Public_Get($"{Public}/projects?limit=1");
        var news = (await Public_Get($"{Public}/news?limit=1")).RootElement.GetProperty("items")[0].GetProperty("slug").GetString();
        (await Send(HttpMethod.Put, $"{Admin}/items/page/about/related", new
        {
            related = new object[] { new { type = "faq", slug = "how" }, new { type = "news", slug = news }, new { type = "partner", slug = "hidden" } }
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        using var detail = await Public_Get($"{Public}/pages/about?lang=en");
        detail.RootElement.GetProperty("related").EnumerateArray().Select(r => r.GetProperty("type").GetString()).Should().Equal("faq", "news");
        (await Send(HttpMethod.Put, $"{Admin}/items/page/about/related", new { related = new[] { new { type = "page", slug = "about" } } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Put, $"{Admin}/items/page/about/related", new { related = new[] { new { type = "faq", slug = "ghost" } } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ProjectsAndNewsJoinTheSameTaxonomyThroughTheItemsEndpoints()
    {
        await Send(HttpMethod.Post, $"{Admin}/tags", new { slug = "community", names = new { en = "Community" } });

        (await Send(HttpMethod.Put, $"{Admin}/items/project/pathshala/tags", new { tags = new[] { "community" } })).StatusCode.Should().Be(HttpStatusCode.OK);

        using var project = await Public_Get($"{Public}/items/project/pathshala?lang=en");
        project.RootElement.GetProperty("tags")[0].GetProperty("slug").GetString().Should().Be("community");
        project.RootElement.GetProperty("title").GetString().Should().NotBeNullOrEmpty();
        (await _client.GetAsync($"{Public}/items/widget/x")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ChangingASlugLeavesARedirectAndTheOldPathStopsServing()
    {
        await Send(HttpMethod.Post, $"{Admin}/pages", Item("old-name", "published", "Page"));

        using var renamed = await Send(HttpMethod.Post, $"{Admin}/items/page/old-name/rename", new { newSlug = "new-name" });

        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync($"{Public}/pages/old-name")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync($"{Public}/pages/new-name")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var resolved = await Public_Get($"{Public}/redirects/resolve?path=/Pages/Old-Name/");
        resolved.RootElement.GetProperty("to").GetString().Should().Be("/pages/new-name");
        resolved.RootElement.GetProperty("statusCode").GetInt32().Should().Be(301);
    }

    [Fact]
    public async Task RenameChainsAreFlattenedAndRenamingBackDropsTheRedirect()
    {
        await Send(HttpMethod.Post, $"{Admin}/pages", Item("a", "published", "Page"));
        await Send(HttpMethod.Post, $"{Admin}/items/page/a/rename", new { newSlug = "b" });
        await Send(HttpMethod.Post, $"{Admin}/items/page/b/rename", new { newSlug = "c" });

        (await Resolve("/pages/a")).Should().Be("/pages/c", "the first redirect is retargeted, not chained");
        (await Resolve("/pages/b")).Should().Be("/pages/c");

        await Send(HttpMethod.Post, $"{Admin}/items/page/c/rename", new { newSlug = "a" });

        (await Resolve("/pages/a")).Should().BeNull("a live path must not redirect away");
        (await Resolve("/pages/b")).Should().Be("/pages/a");
        (await Resolve("/pages/c")).Should().Be("/pages/a");
    }

    [Fact]
    public async Task RenameRejectsATakenSlugAndAnInvalidOne()
    {
        await Send(HttpMethod.Post, $"{Admin}/pages", Item("one", "published", "One"));
        await Send(HttpMethod.Post, $"{Admin}/pages", Item("two", "published", "Two"));

        (await Send(HttpMethod.Post, $"{Admin}/items/page/one/rename", new { newSlug = "two" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Send(HttpMethod.Post, $"{Admin}/items/page/one/rename", new { newSlug = "Not A Slug" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Admin}/items/page/ghost/rename", new { newSlug = "x" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Post, $"{Admin}/items/widget/one/rename", new { newSlug = "x" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LegacyUrlsResolveThroughTheRedirectTable()
    {
        await Send(HttpMethod.Post, $"{Admin}/pages", Item("about", "published", "About"));
        await Send(HttpMethod.Post, $"{Admin}/pages", Item("contact", "published", "Contact"));
        (await Send(HttpMethod.Post, $"{Admin}/redirects", new { from = "/About.html", to = "/pages/about" })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Send(HttpMethod.Post, $"{Admin}/redirects", new { from = "/contact.php", to = "/pages/contact", statusCode = 302 })).StatusCode.Should().Be(HttpStatusCode.Created);

        (await Resolve("/about.html?utm=1")).Should().Be("/pages/about");
        using var php = await Public_Get($"{Public}/redirects/resolve?path=/CONTACT.php");
        php.RootElement.GetProperty("statusCode").GetInt32().Should().Be(302);
        (await _client.GetAsync($"{Public}/redirects/resolve?path=/missing.html")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync($"{Public}/redirects/resolve")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RedirectLoopsAndBadStatusCodesAreRejectedAndDeleteWorks()
    {
        await Send(HttpMethod.Post, $"{Admin}/redirects", new { from = "/old.html", to = "/new" });

        (await Send(HttpMethod.Post, $"{Admin}/redirects", new { from = "/new", to = "/old.html" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Admin}/redirects", new { from = "/same", to = "/same/" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Admin}/redirects", new { from = "/x", to = "/y", statusCode = 200 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var list = await Send(HttpMethod.Get, $"{Admin}/redirects", null);
        var id = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.GetProperty("items")[0].GetProperty("id").GetString();
        (await Send(HttpMethod.Delete, $"{Admin}/redirects/{id}", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Resolve("/old.html")).Should().BeNull();
    }

    [Fact]
    public async Task ValidationRejectsBadItems()
    {
        (await Send(HttpMethod.Post, $"{Admin}/pages", Item("Bad Slug", "published", "X"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Admin}/pages", Item("ok", "live", "X"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Admin}/pages", new { slug = "ok", status = "draft", translations = Array.Empty<object>() })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Put, $"{Admin}/pages/ghost", Item("ghost", "draft", "X"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<string?> Resolve(string path)
    {
        using var response = await _client.GetAsync($"{Public}/redirects/resolve?path={Uri.EscapeDataString(path)}");
        return response.StatusCode == HttpStatusCode.NotFound ? null : JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("to").GetString();
    }

    private static object Item(string slug, string status, string en, string? bn = null, string[]? tags = null)
    {
        var translations = new List<object> { new { lang = "en", title = en, summary = "s", body = "b", attributes = new { role = "x" } } };
        if (bn is not null)
        {
            translations.Add(new { lang = "bn", title = bn });
        }

        return new { slug, status, translations, tags };
    }

    private async Task<JsonDocument> Public_Get(string path)
    {
        using var response = await _client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ContentAdmin());
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

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
