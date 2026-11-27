using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContentService.Data;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class SocialTests : IDisposable
{
    private const string Admin = "/api/v1/admin/content";
    private const string Public = "/api/v1/content";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _staff = TestTokens.Issue([Permissions.ContentView, Permissions.ContentCreate, Permissions.ContentUpdate, Permissions.ContentPublish]);

    public SocialTests() => _client = _factory.CreateClient();

    [Fact]
    public void BengaliTitlesRenderToARealPngOfTheRightSizeAndDifferFromEnglish()
    {
        var brand = new SocialOptions();
        var bn = SocialStore.Render("রক্তদান ক্যাম্প ২০২৬: ক্ষুদ্র প্রচেষ্টায় বড় পরিবর্তন", "স্বেচ্ছাসেবকদের সাথে একটি দিন", "bn", brand);
        var en = SocialStore.Render("Blood donation camp 2026", "A day with volunteers", "en", brand);

        foreach (var png in new[] { bn, en })
        {
            png.Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)).Should().Be(1200);
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)).Should().Be(630);
            png.Length.Should().BeGreaterThan(2_000);
        }

        bn.Should().NotEqual(en);
    }

    [Fact]
    public async Task ImagesAreDrawnInTheBackgroundNotOnRequestAndRedrawnOnlyWhenTheTextChanges()
    {
        await Publish("news", "launch", "লঞ্চ দিবস", "Launch day");
        await Publish("faqs", "not-og", "FAQ", "FAQ"); // a type with no social image
        (await Get($"{Public}/og-image/news/launch?lang=bn")).StatusCode.Should().Be(HttpStatusCode.NotFound, "publishing does not draw on the request path");
        (await Query(db => db.Set<ContentService.Data.Entities.OgImageEntity>().CountAsync())).Should().Be(0);

        (await Json(await Send(HttpMethod.Post, $"{Admin}/social/og/generate"))).GetProperty("drawn").GetInt32().Should().BeGreaterThanOrEqualTo(2, "one per language of the news item, plus the seeded published content");
        (await Json(await Send(HttpMethod.Post, $"{Admin}/social/og/generate"))).GetProperty("drawn").GetInt32().Should().Be(0, "nothing changed, nothing redrawn");

        using var bnImage = await Get($"{Public}/og-image/news/launch?lang=bn");
        bnImage.StatusCode.Should().Be(HttpStatusCode.OK);
        bnImage.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        bnImage.Headers.CacheControl!.Public.Should().BeTrue();
        var etag = bnImage.Headers.ETag!.ToString();

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"{Public}/og-image/news/launch?lang=bn");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        (await _client.SendAsync(conditional)).StatusCode.Should().Be(HttpStatusCode.NotModified);

        // an edit to the English title redraws only the English image
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
            var translation = await db.Translations.SingleAsync(t => t.Lang == "en" && t.Title == "Launch day");
            translation.Title = "Launch day, rescheduled";
            await db.SaveChangesAsync();
        }

        (await Json(await Send(HttpMethod.Post, $"{Admin}/social/og/generate"))).GetProperty("drawn").GetInt32().Should().Be(1);
        (await Get($"{Public}/og-image/news/launch?lang=bn")).Headers.ETag!.ToString().Should().Be(etag, "the Bengali image is untouched");

        (await Get($"{Public}/og-image/news/missing")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Post, $"{Admin}/social/og/generate", viewer: true)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SharesAreCountedPerChannelForPublishedItemsOnly()
    {
        await Publish("news", "story", "গল্প", "Story");
        await Send(HttpMethod.Post, $"{Admin}/news", new { slug = "draft-one", status = "draft", translations = new object[] { new { lang = "en", title = "D", summary = "S", body = "B" } } });

        foreach (var channel in new[] { "facebook", "facebook", "whatsapp" })
        {
            (await Post($"{Public}/shares/news/story", new { channel })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        (await Post($"{Public}/shares/news/story", new { channel = "myspace" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Post($"{Public}/shares/news/draft-one", new { channel = "x" })).StatusCode.Should().Be(HttpStatusCode.NotFound, "unpublished items cannot be shared");

        var counts = await Json(await Get($"{Public}/shares/news/story"));
        counts.GetProperty("total").GetInt64().Should().Be(3);
        counts.GetProperty("channels").GetProperty("facebook").GetInt64().Should().Be(2);

        var top = await Json(await Send(HttpMethod.Get, $"{Admin}/social/top-shared"));
        top[0].GetProperty("slug").GetString().Should().Be("story");
    }

    // ---- helpers

    private async Task Publish(string segment, string slug, string titleBn, string titleEn) =>
        (await Send(HttpMethod.Post, $"{Admin}/{segment}", new
        {
            slug,
            status = "published",
            translations = new object[]
            {
                new { lang = "bn", title = titleBn, summary = "সারাংশ", body = "বি" },
                new { lang = "en", title = titleEn, summary = "Summary", body = "B" }
            }
        })).StatusCode.Should().Be(HttpStatusCode.Created);

    private Task<HttpResponseMessage> Get(string url) => _client.GetAsync(url);

    private Task<HttpResponseMessage> Post(string url, object body) => _client.PostAsJsonAsync(url, body);

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null, bool viewer = false)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new("Bearer", viewer ? TestTokens.Issue([Permissions.ContentView]) : _staff);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private async Task<T> Query<T>(Func<ContentDbContext, Task<T>> query)
    {
        using var scope = _factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<ContentDbContext>());
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
