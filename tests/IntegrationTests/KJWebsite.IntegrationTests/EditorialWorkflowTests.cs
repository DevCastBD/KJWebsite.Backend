using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ContentService;
using ContentService.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KJWebsite.IntegrationTests;

public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Throws from SaveChanges while switched on, to prove a failed scheduled publish is retried.</summary>
public sealed class FailingSaveInterceptor : SaveChangesInterceptor
{
    public bool Fail { get; set; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
        Fail ? throw new InvalidOperationException("simulated outage") : base.SavingChangesAsync(eventData, result, cancellationToken);
}

/// <summary>A content service with a controllable clock and no background poller; tests call the publisher themselves.</summary>
public sealed class WorkflowApiFactory : SqliteWebApplicationFactory<ContentServiceTestEntryPoint, ContentDbContext>
{
    public ManualTimeProvider Clock { get; } = new();

    public FailingSaveInterceptor Outage { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary),
            ["Publishing:Enabled"] = "false",
            ["Preview:SigningKey"] = "test-signing-key-for-previews"
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    protected override void ConfigureDbContext(DbContextOptionsBuilder options, IServiceProvider provider) => options.AddInterceptors(Outage);
}

/// <summary>The real poller on the real clock, polling fast.</summary>
public sealed class PollingApiFactory : SqliteWebApplicationFactory<ContentServiceTestEntryPoint, ContentDbContext>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary),
            ["Publishing:PollSeconds"] = "0.1"
        }));
    }
}

public sealed class EditorialWorkflowTests : IDisposable
{
    private const string Admin = "/api/v1/admin/content";
    private const string Public = "/api/v1/content";
    private readonly WorkflowApiFactory _factory = new();
    private readonly HttpClient _client;

    public EditorialWorkflowTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task EditorsSubmitForReviewAndOnlyPublishersPublish()
    {
        var editor = TestTokens.ContentEditor();
        (await Send(HttpMethod.Post, $"{Admin}/pages", Page("about", "draft", "About"), editor)).StatusCode.Should().Be(HttpStatusCode.Created);

        (await Workflow("page", "about", "in_review", token: editor)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Workflow("page", "about", "published", token: editor)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.GetAsync($"{Public}/pages/about")).StatusCode.Should().Be(HttpStatusCode.NotFound, "in review is not public");

        (await Workflow("page", "about", "published")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync($"{Public}/pages/about")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Workflow("page", "about", "draft", token: editor)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "unpublishing needs content.publish");
        (await Workflow("page", "about", "archived")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync($"{Public}/pages/about")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Workflow("page", "about", "published")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "archived items go back to draft first");
        (await Workflow("page", "about", "bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Workflow("page", "ghost", "published")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Workflow("widget", "about", "published")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreatingOrChangingAStatusDirectlyCannotBypassThePublishPermission()
    {
        var editor = TestTokens.ContentEditor();

        (await Send(HttpMethod.Post, $"{Admin}/pages", Page("sneaky", "published", "Sneaky"), editor)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Post, $"{Admin}/projects", new { slug = "sneaky", status = "active", translations = new[] { new { lang = "en", title = "x" } } }, editor)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Post, $"{Admin}/news", new { slug = "sneaky", status = "published", translations = new[] { new { lang = "en", title = "x" } } }, editor)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Post, $"{Admin}/pages", Page("later", "scheduled", "Later"))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "scheduling needs a time and has its own endpoint");

        await Send(HttpMethod.Post, $"{Admin}/pages", Page("live", "published", "Live"));
        (await Send(HttpMethod.Put, $"{Admin}/pages/live", Page("live", "published", "Edited"), editor)).StatusCode.Should().Be(HttpStatusCode.OK, "editing a live page does not change its status");
        (await Send(HttpMethod.Put, $"{Admin}/pages/live", Page("live", "draft", "Edited"), editor)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Put, $"{Admin}/pages/live", Page("live", "draft", "Edited"))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ProjectsKeepTheirActiveStatusWhenPublished()
    {
        await Send(HttpMethod.Post, $"{Admin}/projects", new { slug = "wf-project", status = "draft", translations = new[] { new { lang = "en", title = "WF" } } });

        using var published = await Workflow("project", "wf-project", "published");

        (await published.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("active");
        (await _client.GetAsync($"{Public}/projects/wf-project")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SchedulingValidatesTheTimeAndCanBeChangedOrCancelled()
    {
        await Send(HttpMethod.Post, $"{Admin}/pages", Page("soon", "draft", "Soon"));
        var future = _factory.Clock.GetUtcNow().AddHours(2);

        (await Workflow("page", "soon", "scheduled")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Workflow("page", "soon", "scheduled", _factory.Clock.GetUtcNow().AddMinutes(-1))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Workflow("page", "soon", "in_review", future)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Workflow("page", "soon", "scheduled", future, token: TestTokens.ContentEditor())).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await Workflow("page", "soon", "scheduled", future)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync($"{Public}/pages/soon")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var admin = await Send(HttpMethod.Get, $"{Admin}/pages/soon", null))
        {
            var view = await admin.Content.ReadFromJsonAsync<JsonElement>();
            view.GetProperty("status").GetString().Should().Be("scheduled");
            view.GetProperty("scheduledPublishAt").GetDateTimeOffset().Should().BeCloseTo(future, TimeSpan.FromSeconds(1));
        }

        (await Workflow("page", "soon", "scheduled", future.AddDays(1))).StatusCode.Should().Be(HttpStatusCode.OK, "rescheduling");
        (await Workflow("page", "soon", "draft")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var cleared = await Send(HttpMethod.Get, $"{Admin}/pages/soon", null);
        (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scheduledPublishAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task AScheduledItemGoesLiveWhenItsTimeComesAndIsAudited()
    {
        await Send(HttpMethod.Post, $"{Admin}/pages", Page("launch", "draft", "Launch"));
        var at = _factory.Clock.GetUtcNow().AddHours(1);
        await Workflow("page", "launch", "scheduled", at);
        var publisher = _factory.Services.GetRequiredService<ScheduledPublisher>();

        (await publisher.PublishDueAsync()).Should().Be(0, "not due yet");
        (await _client.GetAsync($"{Public}/pages/launch")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        _factory.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        (await publisher.PublishDueAsync()).Should().Be(1);
        (await publisher.PublishDueAsync()).Should().Be(0, "publishing is not repeated");

        using var live = await JsonAsync($"{Public}/pages/launch");
        live.RootElement.GetProperty("publishedAt").GetDateTimeOffset().Should().BeCloseTo(at, TimeSpan.FromSeconds(1));
        using var revisions = await JsonAsync($"{Admin}/items/page/launch/revisions", TestTokens.ContentAdmin());
        revisions.RootElement.GetProperty("items")[0].GetProperty("reason").GetString().Should().Be("scheduled-publish");
        revisions.RootElement.GetProperty("items")[0].GetProperty("actorId").GetString().Should().Be("system:scheduler");
        using var audit = await JsonAsync($"{Admin}/audit?actor=system:scheduler", TestTokens.Issue([KJWebsite.BuildingBlocks.Security.Permissions.AuditView]));
        audit.RootElement.GetProperty("items").EnumerateArray().Should().Contain(e => e.GetProperty("entity").GetString() == "ContentItem" && e.GetProperty("after").GetProperty("status").GetString() == "published");
    }

    [Fact]
    public async Task AFailedScheduledPublishIsRetriedNotDropped()
    {
        await Send(HttpMethod.Post, $"{Admin}/pages", Page("retry", "draft", "Retry"));
        await Workflow("page", "retry", "scheduled", _factory.Clock.GetUtcNow().AddMinutes(5));
        _factory.Clock.Advance(TimeSpan.FromMinutes(6));
        var publisher = _factory.Services.GetRequiredService<ScheduledPublisher>();

        _factory.Outage.Fail = true;
        (await publisher.PublishDueAsync()).Should().Be(0);
        (await _client.GetAsync($"{Public}/pages/retry")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        _factory.Outage.Fail = false;
        (await publisher.PublishDueAsync()).Should().Be(1, "the item stayed scheduled and is published late");
        (await _client.GetAsync($"{Public}/pages/retry")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AMissedScheduleIsPublishedAndTheCacheIsEvicted()
    {
        await Send(HttpMethod.Post, $"{Admin}/pages", Page("cached", "draft", "Cached"));
        (await _client.GetAsync($"{Public}/pages?limit=100")).StatusCode.Should().Be(HttpStatusCode.OK);
        await Workflow("page", "cached", "scheduled", _factory.Clock.GetUtcNow().AddMinutes(1));
        _factory.Clock.Advance(TimeSpan.FromDays(3));

        await _factory.Services.GetRequiredService<ScheduledPublisher>().PublishDueAsync();

        using var list = await JsonAsync($"{Public}/pages?limit=100");
        list.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("slug").GetString()).Should().Contain("cached");
    }

    [Fact]
    public async Task RevisionsShowWhichLanguageChangedAndRestoreBilingualContent()
    {
        await Send(HttpMethod.Post, $"{Admin}/pages", new { slug = "story", status = "published", translations = new object[] { new { lang = "en", title = "Story", body = "one" }, new { lang = "bn", title = "গল্প", body = "এক" } } });
        await Send(HttpMethod.Put, $"{Admin}/pages/story", new { slug = "story", status = "published", translations = new object[] { new { lang = "en", title = "Story", body = "one" }, new { lang = "bn", title = "কাহিনী", body = "এক" } } });
        await Send(HttpMethod.Put, $"{Admin}/pages/story", new { slug = "story", status = "published", translations = new object[] { new { lang = "en", title = "Story", body = "one" }, new { lang = "bn", title = "কাহিনী", body = "এক" } } });

        using var list = await JsonAsync($"{Admin}/items/page/story/revisions");
        list.RootElement.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("number").GetInt32()).Should().Equal(2, 1);

        using var diff = await JsonAsync($"{Admin}/items/page/story/revisions/diff");
        var changes = diff.RootElement.GetProperty("changes").EnumerateArray().ToList();
        changes.Should().ContainSingle(c => c.GetProperty("path").GetString() == "translations.bn.title");
        changes.Single(c => c.GetProperty("path").GetString() == "translations.bn.title").GetProperty("before").GetString().Should().Be("গল্প");
        changes.Select(c => c.GetProperty("path").GetString()).Should().NotContain("translations.en.title");

        using var restored = await Send(HttpMethod.Post, $"{Admin}/items/page/story/revisions/1/restore", null);
        restored.StatusCode.Should().Be(HttpStatusCode.OK);
        using var bn = await JsonAsync($"{Public}/pages/story?lang=bn");
        bn.RootElement.GetProperty("title").GetString().Should().Be("গল্প");
        using var after = await JsonAsync($"{Admin}/items/page/story/revisions");
        after.RootElement.GetProperty("items")[0].GetProperty("reason").GetString().Should().Be("restore:1");
        using var same = await JsonAsync($"{Admin}/items/page/story/revisions/diff?from=1&to=3");
        same.RootElement.GetProperty("changes").GetArrayLength().Should().Be(0);
        (await _client.GetAsync($"{Public}/pages/story")).StatusCode.Should().Be(HttpStatusCode.OK, "a restore keeps the status");
        (await Send(HttpMethod.Post, $"{Admin}/items/page/story/revisions/99/restore", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Get, $"{Admin}/items/page/story/revisions/99", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ProjectAndNewsHistoryIsKeptAndRestorable()
    {
        await Send(HttpMethod.Post, $"{Admin}/projects", new { slug = "hist", status = "active", icon = "flaticon-a", imageUrl = "/a.jpg", translations = new[] { new { lang = "en", title = "Hist" } } });
        await Send(HttpMethod.Put, $"{Admin}/projects/hist", new { slug = "hist", status = "active", icon = "flaticon-b", imageUrl = "/b.jpg", translations = new[] { new { lang = "en", title = "Hist 2" } } });

        using var diff = await JsonAsync($"{Admin}/items/project/hist/revisions/diff");
        diff.RootElement.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("path").GetString()).Should().Contain(["project.icon", "project.imageUrl", "translations.en.title"]);
        (await Send(HttpMethod.Post, $"{Admin}/items/project/hist/revisions/1/restore", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var back = await JsonAsync($"{Public}/projects/hist");
        back.RootElement.GetProperty("icon").GetString().Should().Be("flaticon-a");
        back.RootElement.GetProperty("title").GetString().Should().Be("Hist");
    }

    [Fact]
    public async Task SoftDeletedContentCanBeListedAndRestoredAsADraft()
    {
        await Send(HttpMethod.Post, $"{Admin}/pages", Page("oops", "published", "Oops"));
        await Send(HttpMethod.Delete, $"{Admin}/pages/oops", null);

        using var trash = await JsonAsync($"{Admin}/trash?type=page");
        trash.RootElement.GetProperty("items").EnumerateArray().Should().ContainSingle(i => i.GetProperty("slug").GetString() == "oops" && i.GetProperty("title").GetString() == "Oops");
        (await Send(HttpMethod.Get, $"{Admin}/trash?type=widget", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await Send(HttpMethod.Post, $"{Admin}/items/page/oops/restore", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync($"{Public}/pages/oops")).StatusCode.Should().Be(HttpStatusCode.NotFound, "restored as a draft, not republished");
        using var admin = await JsonAsync($"{Admin}/pages/oops");
        admin.RootElement.GetProperty("status").GetString().Should().Be("draft");
        (await Send(HttpMethod.Post, $"{Admin}/items/page/oops/restore", null)).StatusCode.Should().Be(HttpStatusCode.NotFound, "it is no longer in the trash");
        (await JsonAsync($"{Admin}/trash")).RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task APreviewLinkShowsOneUnpublishedItemUntilItExpires()
    {
        await Send(HttpMethod.Post, $"{Admin}/pages", Page("secret", "draft", "Secret draft"));
        await Send(HttpMethod.Post, $"{Admin}/pages", Page("other", "draft", "Other draft"));
        using var created = await Send(HttpMethod.Post, $"{Admin}/items/page/secret/preview-token", new { ttlMinutes = 30 });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        using var preview = await _client.GetAsync($"{Public}/preview/{token}");
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        preview.Headers.CacheControl!.NoStore.Should().BeTrue();
        preview.Headers.GetValues("X-Robots-Tag").Should().Contain("noindex, nofollow");
        (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString().Should().Be("Secret draft");
        (await _client.GetAsync($"{Public}/pages/secret")).StatusCode.Should().Be(HttpStatusCode.NotFound, "the draft stays unpublished");

        _factory.Clock.Advance(TimeSpan.FromMinutes(31));
        (await _client.GetAsync($"{Public}/preview/{token}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task APreviewTokenIsScopedToItsItemAndCannotBeForged()
    {
        await Send(HttpMethod.Post, $"{Admin}/pages", Page("one", "draft", "One"));
        await Send(HttpMethod.Post, $"{Admin}/pages", Page("two", "draft", "Two"));
        var one = await PreviewToken("one");
        var two = await PreviewToken("two");

        // The payload of one with the signature of the other, and the reverse.
        (await _client.GetAsync($"{Public}/preview/{one.Split('.')[0]}.{two.Split('.')[1]}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync($"{Public}/preview/{one.Split('.')[0]}.")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        foreach (var junk in new[] { "garbage", "a.b", "....", new string('x', 400) + "." + new string('y', 50) })
        {
            (await _client.GetAsync($"{Public}/preview/{junk}")).StatusCode.Should().Be(HttpStatusCode.NotFound, junk);
        }

        using var one_ = await JsonAsync($"{Public}/preview/{one}");
        one_.RootElement.GetProperty("slug").GetString().Should().Be("one");
        (await Send(HttpMethod.Delete, $"{Admin}/pages/one", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.GetAsync($"{Public}/preview/{one}")).StatusCode.Should().Be(HttpStatusCode.NotFound, "a deleted item cannot be previewed");
    }

    [Fact]
    public async Task PreviewTokenCreationIsValidatedAndPermissioned()
    {
        await Send(HttpMethod.Post, $"{Admin}/pages", Page("p", "draft", "P"));

        (await Send(HttpMethod.Post, $"{Admin}/items/page/p/preview-token", new { ttlMinutes = 0 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Admin}/items/page/p/preview-token", new { ttlMinutes = 60 * 24 * 8 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Admin}/items/page/ghost/preview-token", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Post, $"{Admin}/items/page/p/preview-token", null, TestTokens.Issue([KJWebsite.BuildingBlocks.Security.Permissions.ContentView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Post, $"{Admin}/items/page/p/preview-token", null)).StatusCode.Should().Be(HttpStatusCode.OK, "default lifetime");
    }

    [Fact]
    public void PreviewTokensNeedAKeyOutsideDevelopment()
    {
        var clock = new ManualTimeProvider();
        var noKey = new PreviewTokens(new ConfigurationBuilder().Build(), new TestEnvironment("Production"), clock);
        var withKey = new PreviewTokens(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Preview:SigningKey"] = "a-long-enough-signing-key" }).Build(), new TestEnvironment("Production"), clock);

        noKey.IsConfigured.Should().BeFalse();
        noKey.Validate(withKey.Create("cnt_1", TimeSpan.FromHours(1)).Token).Should().BeNull("a service without the key accepts nothing");
        var (token, _) = withKey.Create("cnt_1", TimeSpan.FromHours(1));
        withKey.Validate(token).Should().Be("cnt_1");
        clock.Advance(TimeSpan.FromHours(2));
        withKey.Validate(token).Should().BeNull();
    }

    private async Task<string> PreviewToken(string slug)
    {
        using var response = await Send(HttpMethod.Post, $"{Admin}/items/page/{slug}/preview-token", null);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private static object Page(string slug, string status, string title) => new { slug, status, translations = new[] { new { lang = "en", title } } };

    private Task<HttpResponseMessage> Workflow(string type, string slug, string to, DateTimeOffset? publishAt = null, string? token = null) =>
        Send(HttpMethod.Post, $"{Admin}/items/{type}/{slug}/workflow", new { to, publishAt }, token);

    private async Task<JsonDocument> JsonAsync(string path, string? token = null)
    {
        using var response = await Send(HttpMethod.Get, path, null, token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, string? token = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.ContentAdmin());
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

public sealed class SchedulerPollingTests
{
    [Fact]
    public async Task ScheduledContentPublishesWithoutAnyoneRunningAnything()
    {
        using var factory = new PollingApiFactory();
        using var client = factory.CreateClient();
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body)
        {
            using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ContentAdmin());
            return await client.SendAsync(request);
        }

        await Send(HttpMethod.Post, "/api/v1/admin/content/pages", new { slug = "auto", status = "draft", translations = new[] { new { lang = "en", title = "Auto" } } });
        (await Send(HttpMethod.Post, "/api/v1/admin/content/items/page/auto/workflow", new { to = "scheduled", publishAt = DateTimeOffset.UtcNow.AddSeconds(1.5) })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/content/pages/auto")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        HttpStatusCode status;
        do
        {
            await Task.Delay(200);
            status = (await client.GetAsync("/api/v1/content/pages/auto")).StatusCode;
        }
        while (status != HttpStatusCode.OK && DateTimeOffset.UtcNow < deadline);

        status.Should().Be(HttpStatusCode.OK);
    }
}

internal sealed class TestEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;

    public string ApplicationName { get; set; } = "tests";

    public string ContentRootPath { get; set; } = ".";

    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
}
