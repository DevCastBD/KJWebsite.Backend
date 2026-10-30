using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ContentService;
using ContentService.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace KJWebsite.IntegrationTests;

/// <summary>A content service with explicit translation settings.</summary>
public sealed class TranslationApiFactory(Dictionary<string, string?> settings) : SqliteWebApplicationFactory<ContentServiceTestEntryPoint, ContentDbContext>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary) });
            configuration.AddInMemoryCollection(settings);
        });
    }
}

public sealed class TranslationTests : IDisposable
{
    private const string Admin = "/api/v1/admin/content";
    private const string Public = "/api/v1/content";
    private readonly List<IDisposable> _disposables = [];

    [Fact]
    public async Task CompletenessIsReportedPerLanguageForAnItem()
    {
        var api = Api();
        await api.Post("pages", new { slug = "half", status = "draft", translations = new object[] { new { lang = "en", title = "Half", body = "text" }, new { lang = "bn", title = "অর্ধেক" } } });
        await api.Post("testimonials", new { slug = "solo", status = "draft", translations = new[] { new { lang = "en", title = "Solo", body = "quote" } } });

        using var half = await api.Json($"{Admin}/items/page/half/completeness");
        half.RootElement.GetProperty("complete").GetBoolean().Should().BeFalse();
        var languages = half.RootElement.GetProperty("languages").EnumerateArray().ToDictionary(l => l.GetProperty("lang").GetString()!);
        languages["en"].GetProperty("complete").GetBoolean().Should().BeTrue();
        languages["en"].GetProperty("percent").GetInt32().Should().Be(100);
        languages["bn"].GetProperty("present").GetBoolean().Should().BeTrue();
        languages["bn"].GetProperty("percent").GetInt32().Should().Be(50);
        languages["bn"].GetProperty("missing").EnumerateArray().Select(m => m.GetString()).Should().Equal("body");

        using var solo = await api.Json($"{Admin}/testimonials/solo");
        var bn = solo.RootElement.GetProperty("completeness").GetProperty("languages").EnumerateArray().First(l => l.GetProperty("lang").GetString() == "bn");
        bn.GetProperty("present").GetBoolean().Should().BeFalse();
        bn.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).Should().Equal("title", "body");

        using var seeded = await api.Json($"{Admin}/projects/pathshala");
        seeded.RootElement.GetProperty("completeness").GetProperty("complete").GetBoolean().Should().BeTrue("the imported projects are bilingual");
        (await api.Get($"{Admin}/items/page/ghost/completeness")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheReportListsGapsAcrossAllContentTypesWithTotals()
    {
        var api = Api();
        await api.Post("pages", new { slug = "no-bn", status = "published", translations = new[] { new { lang = "en", title = "A", body = "b" } } });
        await api.Post("faqs", new { slug = "full", status = "published", translations = new object[] { new { lang = "en", title = "Q", body = "A" }, new { lang = "bn", title = "প্র", body = "উ" } } });
        await api.Post("partners", new { slug = "bn-only", status = "draft", translations = new[] { new { lang = "bn", title = "অংশীদার" } } });
        await api.Post("news", new { slug = "thin", status = "published", translations = new object[] { new { lang = "en", title = "T", summary = "s", content = new[] { "p" } }, new { lang = "bn", title = "শি" } } });

        using var all = await api.Json($"{Admin}/translations/report");
        var items = all.RootElement.GetProperty("items").EnumerateArray().ToList();
        string[] slugs = [.. items.Select(i => i.GetProperty("slug").GetString()!)];
        slugs.Should().Contain(["no-bn", "bn-only", "thin"]).And.NotContain("full").And.NotContain("pathshala");
        var noBn = items.Single(i => i.GetProperty("slug").GetString() == "no-bn");
        noBn.GetProperty("gaps").EnumerateArray().Single().GetProperty("state").GetString().Should().Be("missing");
        var thin = items.Single(i => i.GetProperty("slug").GetString() == "thin");
        thin.GetProperty("gaps")[0].GetProperty("state").GetString().Should().Be("incomplete");
        thin.GetProperty("gaps")[0].GetProperty("fields").EnumerateArray().Select(f => f.GetString()).Should().Equal("summary", "body");
        all.RootElement.GetProperty("byType").GetProperty("page").GetProperty("withGaps").GetInt32().Should().Be(1);
        all.RootElement.GetProperty("byLanguage").GetProperty("bn").GetProperty("missing").GetInt32().Should().Be(1);
        all.RootElement.GetProperty("byLanguage").GetProperty("en").GetProperty("missing").GetInt32().Should().Be(1);

        using var onlyBn = await api.Json($"{Admin}/translations/report?lang=bn");
        onlyBn.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("slug").GetString()).Should().BeEquivalentTo(["no-bn", "thin"]);
        using var drafts = await api.Json($"{Admin}/translations/report?type=partner&status=draft");
        drafts.RootElement.GetProperty("total").GetInt32().Should().Be(1);
        using var everything = await api.Json($"{Admin}/translations/report?includeComplete=true&limit=100");
        everything.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("slug").GetString()).Should().Contain("full");
        using var paged = await api.Json($"{Admin}/translations/report?limit=1&offset=1");
        paged.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
        paged.RootElement.GetProperty("total").GetInt32().Should().Be(3);

        foreach (var bad in new[] { "type=widget", "lang=fr", "status=nope", "limit=0" })
        {
            (await api.Get($"{Admin}/translations/report?{bad}")).StatusCode.Should().Be(HttpStatusCode.BadRequest, bad);
        }

        (await Api().Anonymous($"{Admin}/translations/report")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ByDefaultBengaliReadersGetAPendingPlaceholderAndEnglishReadersGetBengaliFlagged()
    {
        var api = Api();
        await api.Post("pages", new { slug = "english-only", status = "published", translations = new[] { new { lang = "en", title = "Only English", summary = "sum", body = "body" } } });
        await api.Post("pages", new { slug = "bengali-only", status = "published", translations = new[] { new { lang = "bn", title = "শুধু বাংলা", body = "বিস্তারিত" } } });

        using var placeholder = await api.Json($"{Public}/pages/english-only?lang=bn", anonymous: true);
        placeholder.RootElement.GetProperty("translationPending").GetBoolean().Should().BeTrue();
        placeholder.RootElement.GetProperty("requestedLang").GetString().Should().Be("bn");
        placeholder.RootElement.GetProperty("lang").GetString().Should().Be("en", "the text shown is English, and says so");
        placeholder.RootElement.GetProperty("title").GetString().Should().Be("Only English");
        placeholder.RootElement.GetProperty("body").GetString().Should().BeEmpty("a Bengali reader is told it is pending, not given the English text");
        placeholder.RootElement.GetProperty("summary").GetString().Should().BeEmpty();

        using var fallback = await api.Json($"{Public}/pages/bengali-only?lang=en", anonymous: true);
        fallback.RootElement.GetProperty("translationPending").GetBoolean().Should().BeTrue();
        fallback.RootElement.GetProperty("lang").GetString().Should().Be("bn");
        fallback.RootElement.GetProperty("body").GetString().Should().Be("বিস্তারিত");

        using var translated = await api.Json($"{Public}/pages/english-only?lang=en", anonymous: true);
        translated.RootElement.GetProperty("translationPending").GetBoolean().Should().BeFalse();
        translated.RootElement.GetProperty("requestedLang").ValueKind.Should().Be(JsonValueKind.Null);

        using var list = await api.Json($"{Public}/pages?lang=bn", anonymous: true);
        list.RootElement.GetProperty("items").EnumerateArray().Should().Contain(i => i.GetProperty("slug").GetString() == "english-only" && i.GetProperty("translationPending").GetBoolean());
    }

    [Fact]
    public async Task ProjectsAndNewsFollowTheSameRules()
    {
        var api = Api();
        await api.Post("projects", new { slug = "en-project", status = "active", translations = new[] { new { lang = "en", title = "English project", shortDescription = "short", fullDescription = "full" } } });
        await api.Post("news", new { slug = "en-news", status = "published", translations = new[] { new { lang = "en", title = "English news", summary = "sum", content = new[] { "p1" } } } });

        using var project = await api.Json($"{Public}/projects/en-project?lang=bn", anonymous: true);
        project.RootElement.GetProperty("translationPending").GetBoolean().Should().BeTrue();
        project.RootElement.GetProperty("title").GetString().Should().Be("English project");
        project.RootElement.GetProperty("fullDescription").GetString().Should().BeEmpty();
        using var news = await api.Json($"{Public}/news/en-news?lang=bn", anonymous: true);
        news.RootElement.GetProperty("translationPending").GetBoolean().Should().BeTrue();
        news.RootElement.GetProperty("content").GetArrayLength().Should().Be(0);

        using var seeded = await api.Json($"{Public}/projects/pathshala?lang=bn", anonymous: true);
        seeded.RootElement.GetProperty("translationPending").GetBoolean().Should().BeFalse();
        using var bootstrap = await api.Json($"{Public}/bootstrap?lang=bn", anonymous: true);
        bootstrap.RootElement.GetProperty("projects").GetProperty("items").EnumerateArray().Should().Contain(p => p.GetProperty("slug").GetString() == "en-project");
    }

    [Fact]
    public async Task TheRulesCanBeConfiguredPerLanguage()
    {
        var hide = Api(new() { ["Content:Translations:Fallback:bn"] = "hide", ["Content:Translations:Fallback:en"] = "hide" });
        await hide.Post("pages", new { slug = "en-only", status = "published", translations = new[] { new { lang = "en", title = "E", body = "b" } } });
        (await hide.Anonymous($"{Public}/pages/en-only?lang=bn")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var list = await hide.Json($"{Public}/pages?lang=bn", anonymous: true))
        {
            list.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        }

        var both = Api(new() { ["Content:Translations:Fallback:bn"] = "fallback" });
        await both.Post("pages", new { slug = "en-only", status = "published", translations = new[] { new { lang = "en", title = "E", body = "full body" } } });
        using var served = await both.Json($"{Public}/pages/en-only?lang=bn", anonymous: true);
        served.RootElement.GetProperty("body").GetString().Should().Be("full body");
        served.RootElement.GetProperty("translationPending").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void ABadFallbackSettingStopsTheServiceAtStartup()
    {
        var factory = new TranslationApiFactory(new() { ["Content:Translations:Fallback:bn"] = "english-please" });
        _disposables.Add(factory);

        var error = Record.Exception(() => factory.CreateClient());

        error.Should().NotBeNull();
        error!.ToString().Should().Contain("must be hide, fallback or placeholder");
    }

    [Fact]
    public async Task RelatedItemsFollowTheReadersFallbackRule()
    {
        var api = Api();
        await api.Post("pages", new { slug = "english-friend", status = "published", translations = new[] { new { lang = "en", title = "English friend", body = "b" } } });
        await api.Post("pages", new { slug = "host", status = "published", related = new[] { new { type = "page", slug = "english-friend" } }, translations = new[] { new { lang = "bn", title = "হোস্ট", body = "ব" } } });

        using var detail = await api.Json($"{Public}/pages/host?lang=bn", anonymous: true);
        detail.RootElement.GetProperty("related").EnumerateArray().Select(r => r.GetProperty("title").GetString()).Should().Equal("English friend");
        var hidden = Api(new() { ["Content:Translations:Fallback:bn"] = "hide" });
        await hidden.Post("pages", new { slug = "english-friend", status = "published", translations = new[] { new { lang = "en", title = "English friend", body = "b" } } });
        await hidden.Post("pages", new { slug = "host", status = "published", related = new[] { new { type = "page", slug = "english-friend" } }, translations = new[] { new { lang = "bn", title = "হোস্ট", body = "ব" } } });
        using var none = await hidden.Json($"{Public}/pages/host?lang=bn", anonymous: true);
        none.RootElement.GetProperty("related").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task PublishingAnIncompleteItemWarnsAndCanBeMadeStrict()
    {
        var api = Api();
        await api.Post("pages", new { slug = "incomplete", status = "draft", translations = new[] { new { lang = "en", title = "E", body = "b" } } });
        await api.Post("pages", new { slug = "complete", status = "draft", translations = new object[] { new { lang = "en", title = "E", body = "b" }, new { lang = "bn", title = "ব", body = "ব" } } });

        using var warned = await api.Send(HttpMethod.Post, $"{Admin}/items/page/incomplete/workflow", new { to = "published" });
        warned.StatusCode.Should().Be(HttpStatusCode.OK, "the default is a warning, not a block");
        (await warned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Equal("bn translation is missing");
        using var clean = await api.Send(HttpMethod.Post, $"{Admin}/items/page/complete/workflow", new { to = "published" });
        (await clean.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("warnings").GetArrayLength().Should().Be(0);

        var strict = Api(new() { ["Content:Translations:RequireAllLanguages"] = "true" });
        await strict.Post("pages", new { slug = "incomplete", status = "draft", translations = new[] { new { lang = "en", title = "E", body = "b" } } });
        using var refused = await strict.Send(HttpMethod.Post, $"{Admin}/items/page/incomplete/workflow", new { to = "published" });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("bn translation is missing");
        (await strict.Send(HttpMethod.Post, $"{Admin}/items/page/incomplete/workflow", new { to = "in_review" })).StatusCode.Should().Be(HttpStatusCode.OK, "only going live is checked");
    }

    [Theory]
    [InlineData(FallbackMode.Hide, false)]
    [InlineData(FallbackMode.Fallback, true)]
    [InlineData(FallbackMode.Placeholder, true)]
    public void ThePolicyOnlyServesAMissingLanguageWhenConfiguredTo(FallbackMode mode, bool served)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Content:Translations:Fallback:bn"] = mode.ToString() }).Build();
        var policy = new TranslationPolicy(configuration);

        policy.Choose(["en"], "bn").Should().Match<TranslationChoice?>(c => served ? c != null && c.ContentLang == "en" && c.Pending && c.Placeholder == (mode == FallbackMode.Placeholder) : c == null);
        policy.Choose(["bn", "en"], "bn").Should().Be(new TranslationChoice("bn", false, false));
        policy.Choose([], "bn").Should().BeNull();
    }

    private TranslationClient Api(Dictionary<string, string?>? settings = null)
    {
        var factory = new TranslationApiFactory(settings ?? []);
        var client = factory.CreateClient();
        _disposables.Add(factory);
        _disposables.Add(client);
        return new TranslationClient(client);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private sealed class TranslationClient(HttpClient client)
    {
        public Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body)
        {
            var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ContentAdmin());
            return client.SendAsync(request);
        }

        public async Task Post(string segment, object body) =>
            (await Send(HttpMethod.Post, $"{Admin}/{segment}", body)).StatusCode.Should().Be(HttpStatusCode.Created);

        public Task<HttpResponseMessage> Get(string path) => Send(HttpMethod.Get, path, null);

        public Task<HttpResponseMessage> Anonymous(string path) => client.GetAsync(path);

        public async Task<JsonDocument> Json(string path, bool anonymous = false)
        {
            using var response = anonymous ? await Anonymous(path) : await Get(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
    }
}
