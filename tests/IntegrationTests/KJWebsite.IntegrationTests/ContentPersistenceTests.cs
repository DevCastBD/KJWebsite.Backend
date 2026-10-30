using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KJWebsite.IntegrationTests;

public sealed class ContentPersistenceTests : IDisposable
{
    private const string AdminProjects = "/api/v1/admin/content/projects";
    private const string AdminNews = "/api/v1/admin/content/news";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;

    public ContentPersistenceTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task TheFrontendProjectDataIsImportedIntactInBothLanguages()
    {
        foreach (var lang in new[] { "en", "bn" })
        {
            var expected = SeedFile("projects", lang);
            using var doc = await GetAsync($"/api/v1/content/projects?lang={lang}&limit=100");
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();

            items.Select(i => i.GetProperty("id").GetString()).Should().Equal(expected.Select(e => e.GetProperty("id").GetString()));
            foreach (var (actual, source) in items.Zip(expected))
            {
                foreach (var (apiName, sourceName) in new[] { ("title", "title"), ("shortTitle", "shortTitle"), ("shortDescription", "shortDescription"), ("fullDescription", "fullDescription"), ("category", "category"), ("year", "year"), ("icon", "icon"), ("imageUrl", "image") })
                {
                    actual.GetProperty(apiName).GetString().Should().Be(source.GetProperty(sourceName).GetString(), $"{lang} {source.GetProperty("id").GetString()} {apiName}");
                }
            }
        }

        using var bengali = await GetAsync("/api/v1/content/projects/pathshala?lang=bn");
        bengali.RootElement.GetProperty("title").GetString().Should().Be("পাঠশালা");
        bengali.RootElement.GetProperty("year").GetString().Should().Be("২০১৬");
    }

    [Fact]
    public async Task TheFrontendNewsDataIsImportedIntactInBothLanguages()
    {
        foreach (var lang in new[] { "en", "bn" })
        {
            var expected = SeedFile("news", lang);
            using var doc = await GetAsync($"/api/v1/content/news?lang={lang}&limit=100");
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();

            items.Select(i => i.GetProperty("id").GetString()).Should().Equal(expected.Select(e => e.GetProperty("id").GetString()), "newest first, as in the snapshot");
            foreach (var (actual, source) in items.Zip(expected))
            {
                actual.GetProperty("title").GetString().Should().Be(source.GetProperty("title").GetString());
                actual.GetProperty("summary").GetString().Should().Be(source.GetProperty("summary").GetString());
                actual.GetProperty("date").GetString().Should().Be(source.GetProperty("date").GetString());
                actual.GetProperty("content").EnumerateArray().Select(c => c.GetString()).Should().Equal(source.GetProperty("content").EnumerateArray().Select(c => c.GetString()));
                actual.GetProperty("tags").EnumerateArray().Select(c => c.GetString()).Should().Equal(source.GetProperty("tags").EnumerateArray().Select(c => c.GetString()));
            }
        }
    }

    [Fact]
    public async Task CreateReadUpdateAndSoftDeleteAProject()
    {
        const string slug = "persist-me";
        (await SendAdmin(HttpMethod.Post, AdminProjects, new
        {
            slug,
            status = "active",
            icon = "flaticon-x",
            imageUrl = "/img/x.jpg",
            translations = new object[]
            {
                new { lang = "en", title = "Persist me", shortDescription = "short", fullDescription = "full", category = "Education", year = "2026" },
                new { lang = "bn", title = "আমাকে রাখো", shortDescription = "সংক্ষিপ্ত", fullDescription = "বিস্তারিত", category = "শিক্ষা", year = "২০২৬" }
            }
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        using (var bn = await GetAsync($"/api/v1/content/projects/{slug}?lang=bn"))
        {
            bn.RootElement.GetProperty("title").GetString().Should().Be("আমাকে রাখো");
            bn.RootElement.GetProperty("year").GetString().Should().Be("২০২৬");
        }

        (await SendAdmin(HttpMethod.Put, $"{AdminProjects}/{slug}", new { slug, status = "active", translations = new[] { new { lang = "en", title = "Renamed" } } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        using (var admin = await GetAdmin($"{AdminProjects}/{slug}"))
        {
            admin.RootElement.GetProperty("translations").GetArrayLength().Should().Be(1, "an update replaces the translation set");
            admin.RootElement.GetProperty("translations")[0].GetProperty("title").GetString().Should().Be("Renamed");
        }

        (await SendAdmin(HttpMethod.Delete, $"{AdminProjects}/{slug}", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.GetAsync($"/api/v1/content/projects/{slug}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAdmin(HttpMethod.Get, $"{AdminProjects}/{slug}", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAdmin(HttpMethod.Delete, $"{AdminProjects}/{slug}", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAdmin(HttpMethod.Put, $"{AdminProjects}/{slug}", new { slug, status = "active", translations = new[] { new { lang = "en", title = "Zombie" } } }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The slug stays reserved: creating it again restores the same item rather than a duplicate.
        (await SendAdmin(HttpMethod.Post, AdminProjects, new { slug, status = "active", translations = new[] { new { lang = "en", title = "Back again" } } }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        using var restored = await GetAsync($"/api/v1/content/projects/{slug}");
        restored.RootElement.GetProperty("title").GetString().Should().Be("Back again");
    }

    [Fact]
    public async Task CreateReadUpdateAndSoftDeleteANewsArticle()
    {
        const string slug = "news-persist";
        (await SendAdmin(HttpMethod.Post, AdminNews, new
        {
            slug,
            status = "published",
            publishedAt = "2026-10-01T00:00:00Z",
            translations = new object[]
            {
                new { lang = "en", title = "Hello", summary = "s", content = new[] { "one", "two" }, tags = new[] { "A" } },
                new { lang = "bn", title = "স্বাগতম", content = new[] { "এক", "দুই" } }
            }
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        using (var bn = await GetAsync($"/api/v1/content/news/{slug}?lang=bn"))
        {
            bn.RootElement.GetProperty("content").EnumerateArray().Select(c => c.GetString()).Should().Equal("এক", "দুই");
        }

        (await SendAdmin(HttpMethod.Put, $"{AdminNews}/{slug}", new { slug, status = "published", translations = new[] { new { lang = "en", title = "Hello 2", content = new[] { "only" } } } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        using (var en = await GetAsync($"/api/v1/content/news/{slug}"))
        {
            en.RootElement.GetProperty("title").GetString().Should().Be("Hello 2");
        }

        (await SendAdmin(HttpMethod.Delete, $"{AdminNews}/{slug}", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.GetAsync($"/api/v1/content/news/{slug}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DraftNewsIsNotPublic()
    {
        await SendAdmin(HttpMethod.Post, AdminNews, new { slug = "secret-draft", status = "draft", translations = new[] { new { lang = "en", title = "Draft" } } });

        (await _client.GetAsync("/api/v1/content/news/secret-draft")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var list = await GetAsync("/api/v1/content/news?limit=100");
        list.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Should().NotContain("secret-draft");
        using var admin = await GetAdmin($"{AdminNews}/secret-draft");
        admin.RootElement.GetProperty("status").GetString().Should().Be("draft");
    }

    [Fact]
    public async Task SlugsAreUniquePerTypeAndValidated()
    {
        (await SendAdmin(HttpMethod.Post, AdminProjects, new { slug = "shared", status = "active", translations = new[] { new { lang = "en", title = "P" } } })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await SendAdmin(HttpMethod.Post, AdminNews, new { slug = "shared", status = "published", translations = new[] { new { lang = "en", title = "N" } } })).StatusCode.Should().Be(HttpStatusCode.Created);

        using var again = await SendAdmin(HttpMethod.Post, AdminProjects, new { slug = "shared", status = "active", translations = new[] { new { lang = "en", title = "P2" } } });
        using var list = await GetAdmin($"{AdminProjects}?limit=100");
        list.RootElement.GetProperty("items").EnumerateArray().Count(i => i.GetProperty("slug").GetString() == "shared").Should().Be(1);

        foreach (var bad in new object[]
        {
            new { slug = "Not A Slug", status = "active", translations = new[] { new { lang = "en", title = "x" } } },
            new { slug = "ok-slug", status = "bogus", translations = new[] { new { lang = "en", title = "x" } } },
            new { slug = "ok-slug", status = "active", translations = new[] { new { lang = "fr", title = "x" } } },
            new { slug = "ok-slug", status = "active", translations = new[] { new { lang = "en", title = "x" }, new { lang = "en", title = "y" } } },
            new { slug = "ok-slug", status = "active", translations = Array.Empty<object>() }
        })
        {
            (await SendAdmin(HttpMethod.Post, AdminProjects, bad)).StatusCode.Should().Be(HttpStatusCode.BadRequest, JsonSerializer.Serialize(bad));
        }
    }

    [Fact]
    public async Task ContentSurvivesARestartAndSeedingDoesNotRunTwice()
    {
        (await SendAdmin(HttpMethod.Post, AdminProjects, new { slug = "survivor", status = "active", translations = new[] { new { lang = "bn", title = "টিকে থাকা" } } })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await SendAdmin(HttpMethod.Delete, $"{AdminProjects}/pathshala", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // A new host on the same database is both a restart and a second replica.
        using var restarted = _factory.WithWebHostBuilder(_ => { });
        using var other = restarted.CreateClient();
        using var response = await other.GetAsync("/api/v1/content/projects/survivor?lang=bn");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString().Should().Be("টিকে থাকা");

        using var list = JsonDocument.Parse(await other.GetStringAsync("/api/v1/content/projects?limit=100"));
        var ids = list.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        ids.Should().NotContain("pathshala", "the seeder must not resurrect an item an editor deleted");
        ids.Count(id => id == "engineers-talk").Should().Be(1, "the import must not run a second time");
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static List<JsonElement> SeedFile(string name, string lang)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KJWebsite.Backend.slnx")))
        {
            root = root.Parent;
        }

        var path = Path.Combine(root!.FullName, "src", "Services", "ContentService", "Data", "Seed", $"{name}.{lang}.json");
        return [.. JsonDocument.Parse(File.ReadAllText(path)).RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    private async Task<JsonDocument> GetAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonDocument> GetAdmin(string path)
    {
        using var response = await SendAdmin(HttpMethod.Get, path, null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> SendAdmin(HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ContentAdmin());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }
}
