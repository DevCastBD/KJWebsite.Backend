using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace KJWebsite.IntegrationTests;

public sealed class ContentContractTests : IDisposable
{
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;

    public ContentContractTests() => _client = _factory.CreateClient();

    [Theory]
    [InlineData("/api/v1/content/projects?lang=bn", "bn")]
    [InlineData("/api/v1/content/news?lang=en", "en")]
    public async Task PublicListsReturnPagingEnvelopeWithResolvedLanguage(string path, string lang)
    {
        using var doc = await GetJsonAsync(path);
        var root = doc.RootElement;

        root.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("total").GetInt32().Should().BeGreaterThan(0);
        root.GetProperty("limit").GetInt32().Should().Be(20);
        root.GetProperty("offset").GetInt32().Should().Be(0);
        root.GetProperty("lang").GetString().Should().Be(lang);
    }

    [Fact]
    public async Task LanguageFallsBackToAcceptLanguageAndIsEchoed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/content/projects");
        request.Headers.Add("Accept-Language", "bn");
        using var response = await _client.SendAsync(request);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        doc.RootElement.GetProperty("lang").GetString().Should().Be("bn");
    }

    [Theory]
    [InlineData("/api/v1/content/projects?limit=0")]
    [InlineData("/api/v1/content/projects?limit=101")]
    [InlineData("/api/v1/content/projects?offset=-1")]
    [InlineData("/api/v1/content/news?limit=101")]
    public async Task InvalidPagingIsRejected(string path)
    {
        using var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BootstrapReturnsProjectsNewsSettingsAndLang()
    {
        using var doc = await GetJsonAsync("/api/v1/content/bootstrap?lang=bn");
        var root = doc.RootElement;

        root.GetProperty("lang").GetString().Should().Be("bn");
        root.GetProperty("projects").GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("news").GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("settings").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task BootstrapHasETagAndReturnsNotModifiedOnRevalidation()
    {
        using var first = await _client.GetAsync("/api/v1/content/bootstrap");
        var etag = first.Headers.ETag;

        etag.Should().NotBeNull();
        first.Headers.CacheControl!.Public.Should().BeTrue();
        first.Headers.CacheControl.MaxAge.Should().BeGreaterThan(TimeSpan.Zero);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/content/bootstrap");
        request.Headers.IfNoneMatch.Add(etag!);
        using var second = await _client.SendAsync(request);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task DifferentLanguagesGetDifferentETags()
    {
        using var en = await _client.GetAsync("/api/v1/content/bootstrap?lang=en");
        using var bn = await _client.GetAsync("/api/v1/content/bootstrap?lang=bn");

        en.Headers.ETag.Should().NotBe(bn.Headers.ETag);
    }

    [Fact]
    public async Task AdminReadsRequireAuthorization()
    {
        using var response = await _client.GetAsync("/api/v1/admin/content/projects");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/api/v1/admin/content/projects/pathshala")]
    [InlineData("/api/v1/admin/content/news/bauniabadh-mou")]
    public async Task AdminGetByIdReturnsAllTranslations(string path)
    {
        using var doc = await GetAdminJsonAsync(path);
        var translations = doc.RootElement.GetProperty("translations");

        translations.GetArrayLength().Should().Be(2);
        translations.EnumerateArray().Select(t => t.GetProperty("lang").GetString())
            .Should().BeEquivalentTo("en", "bn");
    }

    [Theory]
    [InlineData("/api/v1/admin/content/projects")]
    [InlineData("/api/v1/admin/content/news")]
    public async Task AdminListGroupsTranslationsPerSlug(string path)
    {
        using var doc = await GetAdminJsonAsync(path);
        var items = doc.RootElement.GetProperty("items");

        items.GetArrayLength().Should().BeGreaterThan(0);
        foreach (var item in items.EnumerateArray())
        {
            item.GetProperty("translations").GetArrayLength().Should().Be(2);
        }

        doc.RootElement.GetProperty("total").GetInt32().Should().Be(items.GetArrayLength());
    }

    [Fact]
    public async Task AdminGetUnknownIdReturnsNotFound()
    {
        using var request = AdminRequest("/api/v1/admin/content/projects/nope");
        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonDocument> GetAdminJsonAsync(string path)
    {
        using var request = AdminRequest(path);
        using var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage AdminRequest(string path) => new(HttpMethod.Get, path)
    {
        Headers = { Authorization = new("Bearer", TestTokens.ContentAdmin()) }
    };

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
