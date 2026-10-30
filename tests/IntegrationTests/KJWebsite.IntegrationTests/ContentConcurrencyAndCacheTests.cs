using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ContentService.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class ContentConcurrencyAndCacheTests : IDisposable
{
    private const string AdminProjects = "/api/v1/admin/content/projects";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;

    public ContentConcurrencyAndCacheTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task AStaleIfMatchIsRejectedAndNothingIsOverwritten()
    {
        await Create("edit-me", "Original");
        var tag = await AdminETag("edit-me");

        using var firstEditor = await Put("edit-me", "Editor one", tag);
        using var secondEditor = await Put("edit-me", "Editor two", tag);

        firstEditor.StatusCode.Should().Be(HttpStatusCode.OK);
        firstEditor.Headers.ETag!.Tag.Should().NotBe(tag, "a save changes the version");
        secondEditor.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await secondEditor.Content.ReadAsStringAsync()).Should().Contain("CONFLICT");
        (await AdminTitle("edit-me")).Should().Be("Editor one");
    }

    [Fact]
    public async Task TheETagFromAWriteIsTheVersionOfTheNextRead()
    {
        await Create("chain", "One");
        using var update = await Put("chain", "Two", await AdminETag("chain"));

        (await AdminETag("chain")).Should().Be(update.Headers.ETag!.Tag);
        using var next = await Put("chain", "Three", update.Headers.ETag!.Tag);
        next.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WithoutIfMatchTheLastWriteWinsAndWildcardAlwaysMatches()
    {
        await Create("loose", "One");

        (await Put("loose", "Two", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Put("loose", "Three", "*")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AdminTitle("loose")).Should().Be("Three");
    }

    [Fact]
    public async Task AWriteRacingAnotherEditorLosesWithoutOverwriting()
    {
        await Create("race", "Original");
        var tag = await AdminETag("race");

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Put("race", $"Editor {i}", tag)));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1, "only one editor can save against a given version");
        results.Where(r => r.StatusCode != HttpStatusCode.OK).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PublicReadsAreCachedPerLanguageAndEvictedByAnAdminWrite()
    {
        await Create("cached", "Hello", "হ্যালো");

        (await PublicTitle("cached", "en")).Should().Be("Hello");
        (await PublicTitle("cached", "bn")).Should().Be("হ্যালো");

        // Change the row behind the API's back: only a cache hit can still answer with the old title.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
            await db.Translations.Where(t => t.Lang == "en" && t.Title == "Hello").ExecuteUpdateAsync(s => s.SetProperty(t => t.Title, "Changed"));
        }

        (await PublicTitle("cached", "en")).Should().Be("Hello", "served from the output cache");
        (await PublicTitle("cached", "bn")).Should().Be("হ্যালো", "bn and en are cached separately");

        await Create("other", "Other");

        (await PublicTitle("cached", "en")).Should().Be("Changed", "an admin write evicts the cache");
        (await PublicTitle("cached", "bn")).Should().Be("হ্যালো");
    }

    [Fact]
    public async Task AcceptLanguageIsPartOfTheCacheKey()
    {
        await Create("negotiated", "Hello", "হ্যালো");

        using var en = await PublicGet("/api/v1/content/projects/negotiated", "en");
        using var bn = await PublicGet("/api/v1/content/projects/negotiated", "bn");

        en.RootElement.GetProperty("title").GetString().Should().Be("Hello");
        bn.RootElement.GetProperty("title").GetString().Should().Be("হ্যালো");
    }

    private async Task Create(string slug, string en, string? bn = null)
    {
        var translations = new List<object> { new { lang = "en", title = en } };
        if (bn is not null)
        {
            translations.Add(new { lang = "bn", title = bn });
        }

        using var response = await Send(HttpMethod.Post, AdminProjects, new { slug, status = "active", translations }, null);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private Task<HttpResponseMessage> Put(string slug, string title, string? ifMatch) =>
        Send(HttpMethod.Put, $"{AdminProjects}/{slug}", new { slug, status = "active", translations = new[] { new { lang = "en", title } } }, ifMatch);

    private async Task<string> AdminETag(string slug)
    {
        using var response = await Send(HttpMethod.Get, $"{AdminProjects}/{slug}", null, null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return response.Headers.ETag!.Tag;
    }

    private async Task<string?> AdminTitle(string slug)
    {
        using var response = await Send(HttpMethod.Get, $"{AdminProjects}/{slug}", null, null);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("translations")[0].GetProperty("title").GetString();
    }

    private async Task<string?> PublicTitle(string slug, string lang)
    {
        using var doc = await PublicGet($"/api/v1/content/projects/{slug}?lang={lang}", null);
        return doc.RootElement.GetProperty("title").GetString();
    }

    private async Task<JsonDocument> PublicGet(string path, string? acceptLanguage)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (acceptLanguage is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);
        }

        using var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, string? ifMatch)
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
