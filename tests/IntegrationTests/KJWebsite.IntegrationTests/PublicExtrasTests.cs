using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using ContentService;
using ContentService.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace KJWebsite.IntegrationTests;

public sealed class SiteApiFactory(string baseUrl) : SqliteWebApplicationFactory<ContentServiceTestEntryPoint, ContentDbContext>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary),
            ["Site:BaseUrl"] = baseUrl,
            ["Site:Name"] = "Test Site"
        }));
    }
}

public sealed class PublicExtrasTests : IDisposable
{
    private const string Admin = "/api/v1/admin/content";
    private readonly SiteApiFactory _factory = new("https://example.org/");
    private readonly HttpClient _client;

    public PublicExtrasTests() => _client = _factory.CreateClient();

    [Theory]
    [InlineData("ক্ষমতা", "ক্ষমতায়ন")]
    [InlineData("বিশ্ববিদ্যালয়", "বিশ্ববিদ্যালয়ে")]
    public void ConjunctsStayOneTerm(string word, string longer)
    {
        SearchTokenizer.Tokens(word).Should().Equal(word);
        SearchTokenizer.Tokens($"{longer}, {word}!").Should().Equal(longer, word);
    }

    [Fact]
    public void TheTokenizerNormalisesEncodingsDigitsAndMixedText()
    {
        SearchTokenizer.Tokens("য়").Should().Equal(SearchTokenizer.Tokens("\u09AF\u09BC"), "precomposed and decomposed য় are the same term");
        SearchTokenizer.Tokens("২০২৬ Flood-Relief বন্যা").Should().Equal("2026", "flood", "relief", "বন্যা");
        SearchTokenizer.Tokens("ক\u200Dষ").Should().Equal(SearchTokenizer.Tokens("কষ"));
        SearchTokenizer.Tokens("  ,.; ").Should().BeEmpty();
        SearchTokenizer.Tokens(new string('x', 200)).Single().Length.Should().Be(SearchTokenizer.MaxTermLength);
    }

    [Fact]
    public async Task BengaliQueriesIncludingConjunctsAndMixedLanguageWork()
    {
        await Create("empower", "published", ("bn", "নারীর ক্ষমতায়ন", "প্রশিক্ষণ কর্মসূচি", "গ্রামের নারীদের ক্ষমতায়ন ও স্বাবলম্বী করা"), ("en", "Women empowerment", "Training", "Empowering rural women"));
        await Create("exam", "published", ("bn", "বিশ্ববিদ্যালয় ভর্তি পরীক্ষা", "", "ভর্তি তথ্য"), ("en", "University admission", "", ""));
        await Create("mixed", "published", ("bn", "কোভিড-19 টিকা ক্যাম্প", "Vaccine camp ঢাকা", ""), ("en", "Vaccine camp", "", ""));
        await Create("draft-one", "draft", ("bn", "ক্ষমতায়ন খসড়া", "", ""));

        (await Slugs("ক্ষমতা", "bn")).Should().Equal(["empower"], "prefix of a conjunct word matches; the draft is excluded");
        (await Slugs("ক্ষমতায়ন নারী", "bn")).Should().Equal("empower");
        (await Slugs("বিশ্ববিদ্যালয়", "bn")).Should().Equal("exam");
        (await Slugs("বিশ্ব", "bn")).Should().Equal("exam");
        (await Slugs("টিকা vaccine", "bn")).Should().Equal(["mixed"], "mixed bn/en query");
        (await Slugs("ঢাকা", "bn")).Should().Equal("mixed");
        (await Slugs("কোভিড 19", "bn")).Should().Equal("mixed");
        (await Slugs("empower", "en")).Should().Equal("empower");
        (await Slugs("empower", "bn")).Should().BeEmpty("the English words are not in the Bengali translation");
        (await Slugs("ক্ষমতা কম্পিউটার", "bn")).Should().BeEmpty("every term must match");
        (await Slugs("ক", "bn")).Should().Contain("empower");
    }

    [Fact]
    public async Task SearchRanksTitleMatchesAboveBodyMatchesAndFollowsContentChanges()
    {
        await Create("in-body", "published", ("en", "Annual report", "", "Details about the scholarship programme"));
        await Create("in-title", "published", ("en", "Scholarship programme", "", ""));

        (await Slugs("scholarship", "en")).Should().Equal("in-title", "in-body");

        await Send(HttpMethod.Put, $"{Admin}/pages/in-body", Page("in-body", "published", ("en", "Annual report", "", "Nothing relevant")));
        (await Slugs("scholarship", "en")).Should().Equal("in-title");
        await Send(HttpMethod.Post, $"{Admin}/items/page/in-title/workflow", new { to = "archived" });
        (await Slugs("scholarship", "en")).Should().BeEmpty("archived content is not searchable");
        await Send(HttpMethod.Post, $"{Admin}/items/page/in-title/workflow", new { to = "draft" });
        await Send(HttpMethod.Post, $"{Admin}/items/page/in-title/workflow", new { to = "published" });
        await Send(HttpMethod.Delete, $"{Admin}/pages/in-title", null);
        (await Slugs("scholarship", "en")).Should().BeEmpty("deleted content is not searchable");
    }

    [Fact]
    public async Task SearchValidatesInputPagesAndFiltersByType()
    {
        for (var i = 0; i < 3; i++)
        {
            await Create($"page-{i}", "published", ("en", $"Common word {i}", "", ""));
        }

        using var page = await Json("/api/v1/content/search?q=common&lang=en&limit=2&offset=1");
        page.RootElement.GetProperty("total").GetInt32().Should().Be(3);
        page.RootElement.GetProperty("items").GetArrayLength().Should().Be(2);
        page.RootElement.GetProperty("items")[0].GetProperty("path").GetString().Should().StartWith("/pages/page-");
        (await Slugs("common", "en", "news")).Should().BeEmpty();
        foreach (var bad in new[] { "q=", "q=%20", "q=ok&type=widget", "q=ok&limit=0" })
        {
            (await _client.GetAsync($"/api/v1/content/search?{bad}")).StatusCode.Should().Be(HttpStatusCode.BadRequest, bad);
        }

        using var punctuation = await Json("/api/v1/content/search?q=%21%21");
        punctuation.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        using var seeded = await Json("/api/v1/content/search?q=" + Uri.EscapeDataString("পাঠশালা") + "&lang=bn");
        seeded.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("slug").GetString()).Should().Contain("pathshala", "the imported content is indexed at startup");
    }

    [Fact]
    public async Task TheSitemapListsPublishedContentWithAlternatesAndNoDrafts()
    {
        await Create("live", "published", ("en", "Live", "s", "b"), ("bn", "লাইভ", "স", "ব"));
        await Create("hidden", "draft", ("en", "Hidden", "", ""));
        await Create("review", "in_review", ("en", "Review", "", ""));
        await Send(HttpMethod.Post, $"{Admin}/faqs", Page("faq-live", "published", ("en", "Q", "", "A")));

        var xml = XDocument.Parse(await _client.GetStringAsync("/sitemap.xml"));
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        var locs = xml.Descendants(ns + "loc").Select(l => l.Value).ToList();
        locs.Should().Contain(["https://example.org/en/pages/live", "https://example.org/bn/pages/live", "https://example.org/en/projects/pathshala"]);
        locs.Should().NotContain(l => l.Contains("hidden", StringComparison.Ordinal) || l.Contains("review", StringComparison.Ordinal) || l.Contains("faq-live", StringComparison.Ordinal));
        var live = xml.Descendants(ns + "url").First(u => u.Element(ns + "loc")!.Value == "https://example.org/en/pages/live");
        live.Elements(xhtml + "link").Select(a => (a.Attribute("hreflang")!.Value, a.Attribute("href")!.Value)).Should().Equal(("bn", "https://example.org/bn/pages/live"), ("en", "https://example.org/en/pages/live"));
        live.Element(ns + "lastmod")!.Value.Should().MatchRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$");
        using var response = await _client.GetAsync("/sitemap.xml");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/xml");
    }

    [Fact]
    public async Task FeedsAreValidAndPerLanguage()
    {
        await Send(HttpMethod.Post, $"{Admin}/news", new { slug = "both", status = "published", translations = new object[] { new { lang = "en", title = "Both & <more>", summary = "Sum \u0001 here", content = new[] { "p" } }, new { lang = "bn", title = "উভয়", summary = "সারাংশ", content = new[] { "প" } } } });
        await Send(HttpMethod.Post, $"{Admin}/news", new { slug = "english-only", status = "published", translations = new[] { new { lang = "en", title = "English only", summary = "s", content = new[] { "p" } } } });
        await Send(HttpMethod.Post, $"{Admin}/news", new { slug = "draft-news", status = "draft", translations = new[] { new { lang = "en", title = "Draft news" } } });

        var rss = XDocument.Parse(await _client.GetStringAsync("/feed.rss?lang=en"));
        rss.Root!.Name.LocalName.Should().Be("rss");
        rss.Root.Attribute("version")!.Value.Should().Be("2.0");
        var channel = rss.Root.Element("channel")!;
        channel.Element("language")!.Value.Should().Be("en");
        var items = channel.Elements("item").ToList();
        items.Select(i => i.Element("title")!.Value).Should().Contain(["Both & <more>", "English only"]).And.NotContain("Draft news");
        items.Select(i => DateTimeOffset.TryParse(i.Element("pubDate")!.Value, out var parsed) ? parsed : (DateTimeOffset?)null).Should().OnlyContain(d => d != null);
        items.First(i => i.Element("title")!.Value.StartsWith("Both", StringComparison.Ordinal)).Element("guid")!.Value.Should().Be("https://example.org/en/news/both");

        var bnRss = XDocument.Parse(await _client.GetStringAsync("/feed.rss?lang=bn"));
        bnRss.Root!.Element("channel")!.Elements("item").Select(i => i.Element("title")!.Value).Should().Contain("উভয়").And.NotContain("English only");

        XNamespace atom = "http://www.w3.org/2005/Atom";
        var feed = XDocument.Parse(await _client.GetStringAsync("/feed.atom?lang=bn"));
        feed.Root!.Name.Should().Be(atom + "feed");
        feed.Root.Attribute(XNamespace.Xml + "lang")!.Value.Should().Be("bn");
        feed.Root.Element(atom + "id")!.Value.Should().Contain("lang=bn");
        var entries = feed.Root.Elements(atom + "entry").ToList();
        entries.Select(e => e.Element(atom + "title")!.Value).Should().Contain("উভয়").And.NotContain("English only");
        entries.Should().OnlyContain(e => e.Element(atom + "updated") != null && e.Element(atom + "id") != null);
        using var response = await _client.GetAsync("/feed.atom");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/atom+xml");
        (await _client.GetAsync("/feed.rss")).Content.Headers.ContentType!.MediaType.Should().Be("application/rss+xml");
    }

    [Fact]
    public async Task OpenGraphMetadataIsAvailablePerItemAndLanguage()
    {
        await Send(HttpMethod.Post, $"{Admin}/news", new { slug = "story", status = "published", imageUrl = "/img/story.jpg", translations = new object[] { new { lang = "en", title = "A story", summary = "Short summary", content = new[] { "p" } }, new { lang = "bn", title = "একটি গল্প", summary = "সংক্ষিপ্ত", content = new[] { "প" } } } });
        await Send(HttpMethod.Post, $"{Admin}/tags", new { slug = "hope", names = new { en = "Hope", bn = "আশা" } });
        await Send(HttpMethod.Put, $"{Admin}/items/news/story/tags", new { tags = new[] { "hope" } });

        using var bn = await Json("/api/v1/content/og/news/story?lang=bn");
        var og = bn.RootElement;
        og.GetProperty("title").GetString().Should().Be("একটি গল্প");
        og.GetProperty("description").GetString().Should().Be("সংক্ষিপ্ত");
        og.GetProperty("url").GetString().Should().Be("https://example.org/bn/news/story");
        og.GetProperty("image").GetString().Should().Be("https://example.org/img/story.jpg", "relative images are made absolute");
        og.GetProperty("type").GetString().Should().Be("article");
        og.GetProperty("locale").GetString().Should().Be("bn_BD");
        og.GetProperty("alternateLocales").EnumerateArray().Select(l => l.GetString()).Should().Equal("en_US");
        og.GetProperty("siteName").GetString().Should().Be("Test Site");
        og.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal("আশা");

        (await _client.GetAsync("/api/v1/content/og/news/missing")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/api/v1/content/og/widget/x")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await Send(HttpMethod.Post, $"{Admin}/pages", Page("draft-page", "draft", ("en", "Draft", "", "")));
        (await _client.GetAsync("/api/v1/content/og/page/draft-page")).StatusCode.Should().Be(HttpStatusCode.NotFound, "no metadata for unpublished content");
    }

    [Fact]
    public async Task WithoutASiteUrlTheAbsoluteLinkEndpointsSayWhy()
    {
        using var factory = new SiteApiFactory(string.Empty);
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/sitemap.xml", "/feed.rss", "/feed.atom", "/api/v1/content/og/news/anything" })
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, path);
            (await response.Content.ReadAsStringAsync()).Should().Contain("SITE_URL_NOT_CONFIGURED");
        }

        (await client.GetAsync("/api/v1/content/search?q=test")).StatusCode.Should().Be(HttpStatusCode.OK, "search needs no site url");
    }

    // Pages only: the imported projects and news are also indexed and would match some of these words.
    private async Task<List<string?>> Slugs(string q, string lang, string type = "page")
    {
        using var doc = await Json($"/api/v1/content/search?q={Uri.EscapeDataString(q)}&lang={lang}&limit=100&type={type}");
        return [.. doc.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("slug").GetString())];
    }

    private async Task Create(string slug, string status, params (string Lang, string Title, string Summary, string Body)[] translations)
    {
        using var response = await Send(HttpMethod.Post, $"{Admin}/pages", Page(slug, status, translations));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static object Page(string slug, string status, params (string Lang, string Title, string Summary, string Body)[] translations) =>
        new { slug, status, translations = translations.Select(t => new { lang = t.Lang, title = t.Title, summary = t.Summary, body = t.Body }) };

    private async Task<JsonDocument> Json(string path)
    {
        using var response = await _client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ContentAdmin());
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
