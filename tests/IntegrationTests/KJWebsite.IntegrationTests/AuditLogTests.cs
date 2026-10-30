using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AuthIdentityService.Data;
using AuthIdentityService.Data.Entities;
using ContentService.Data;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class AuditLogTests : IDisposable
{
    private const string Pages = "/api/v1/admin/content/pages";
    private const string Audit = "/api/v1/admin/content/audit";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;

    public AuditLogTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task StartupSeedingIsNotAnAdminMutation()
    {
        using var _ = _client;
        (await Query(Audit)).RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task EveryAdminWriteIsAuditedWithActorRequestAndContext()
    {
        using var response = await Send(HttpMethod.Post, Pages, Page("about", "draft", "About"), forwardedFor: "203.0.113.7, 10.0.0.1");
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var events = Events(await Query($"{Audit}?entity=ContentItem"));
        var created = events.Should().ContainSingle().Subject;
        created.GetProperty("action").GetString().Should().Be("create");
        created.GetProperty("actorId").GetString().Should().Be("usr_test");
        created.GetProperty("operation").GetString().Should().Be($"POST {Pages}");
        created.GetProperty("ip").GetString().Should().Be("203.0.113.7");
        created.GetProperty("userAgent").GetString().Should().Be("audit-tests/1.0");
        created.GetProperty("before").ValueKind.Should().Be(JsonValueKind.Null);
        created.GetProperty("after").GetProperty("slug").GetString().Should().Be("about");
        created.GetProperty("after").GetProperty("status").GetString().Should().Be("draft");

        // One save touches several tables; the request id ties the rows together.
        var requestId = created.GetProperty("requestId").GetString()!;
        Events(await Query($"{Audit}?requestId={requestId}")).Select(e => e.GetProperty("entity").GetString()).Should().Contain(["ContentItem", "ContentTranslation"]);
    }

    [Fact]
    public async Task AnUpdateRecordsOnlyWhatChangedWithItsPreviousValue()
    {
        await Send(HttpMethod.Post, Pages, Page("about", "draft", "About"));
        await Send(HttpMethod.Put, $"{Pages}/about", Page("about", "published", "About"));

        var update = Events(await Query($"{Audit}?entity=ContentItem&action=update")).Should().ContainSingle().Subject;

        update.GetProperty("before").GetProperty("status").GetString().Should().Be("draft");
        update.GetProperty("after").GetProperty("status").GetString().Should().Be("published");
        update.GetProperty("after").TryGetProperty("slug", out _).Should().BeFalse("unchanged columns are not repeated");
        update.GetProperty("after").TryGetProperty("updatedAt", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ChangesToTranslationsTagsAndDeletesAreAudited()
    {
        await Send(HttpMethod.Post, "/api/v1/admin/content/tags", new { slug = "news", names = new { en = "News" } });
        await Send(HttpMethod.Post, Pages, Page("about", "published", "About"));
        await Send(HttpMethod.Put, $"{Pages}/about", Page("about", "published", "About us"));
        await Send(HttpMethod.Delete, $"{Pages}/about", null);
        await Send(HttpMethod.Put, "/api/v1/admin/content/items/page/about/tags", new { tags = new[] { "news" } });

        Events(await Query($"{Audit}?entity=Tag&action=create")).Should().ContainSingle();
        var translation = Events(await Query($"{Audit}?entity=ContentTranslation&action=update")).Should().ContainSingle().Subject;
        translation.GetProperty("before").GetProperty("title").GetString().Should().Be("About");
        translation.GetProperty("after").GetProperty("title").GetString().Should().Be("About us");
        var softDelete = Events(await Query($"{Audit}?entity=ContentItem&action=update")).First(e => e.GetProperty("after").TryGetProperty("deletedAt", out _));
        softDelete.GetProperty("before").GetProperty("deletedAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task RedirectsCreatedAndRemovedByARenameAreAudited()
    {
        await Send(HttpMethod.Post, Pages, Page("a", "published", "A"));
        await Send(HttpMethod.Post, $"/api/v1/admin/content/items/page/a/rename", new { newSlug = "b" });
        await Send(HttpMethod.Post, $"/api/v1/admin/content/items/page/b/rename", new { newSlug = "a" });

        var redirects = Events(await Query($"{Audit}?entity=Redirect"));

        redirects.Select(e => e.GetProperty("action").GetString()).Should().Contain(["create", "delete"], "removing the shadowing redirect is a tracked delete, not a silent bulk delete");
    }

    [Fact]
    public async Task TheLogCanBeFilteredByActorEntityAndDate()
    {
        await Send(HttpMethod.Post, Pages, Page("one", "draft", "One"));
        await Send(HttpMethod.Post, Pages, Page("two", "draft", "Two"));

        Events(await Query($"{Audit}?actor=usr_test&entity=ContentItem")).Should().HaveCount(2);
        Events(await Query($"{Audit}?actor=someone-else")).Should().BeEmpty();
        var created = Events(await Query($"{Audit}?entity=ContentItem")).First();
        Events(await Query($"{Audit}?entityId={created.GetProperty("entityId").GetString()}")).Should().HaveCount(1).And.Subject.Single().GetProperty("entity").GetString().Should().Be("ContentItem");
        Events(await Query($"{Audit}?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"))}&entity=ContentItem")).Should().HaveCount(2);
        Events(await Query($"{Audit}?to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"))}")).Should().BeEmpty();
        using var single = await Send(HttpMethod.Get, $"{Audit}/{created.GetProperty("id").GetInt64()}", null, token: TokenWithAudit());
        single.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Get, $"{Audit}/999999", null, token: TokenWithAudit())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ALargeLogIsPagedByCursorWithoutGapsOrDuplicates()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
            db.Set<AuditEvent>().AddRange(Enumerable.Range(0, 2500).Select(i => new AuditEvent
            {
                At = DateTime.UtcNow.AddSeconds(-i),
                Service = "content",
                ActorId = i % 2 == 0 ? "usr_a" : "usr_b",
                Action = "update",
                Entity = "ContentItem",
                EntityId = $"cnt_{i % 50}"
            }));
            await db.SaveChangesAsync();
        }

        var seen = new List<long>();
        string? cursor = null;
        do
        {
            using var page = await Query($"{Audit}?actor=usr_a&limit=100{(cursor is null ? string.Empty : $"&cursor={cursor}")}");
            seen.AddRange(Events(page).Select(e => e.GetProperty("id").GetInt64()));
            cursor = page.RootElement.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
        }
        while (cursor is not null);

        seen.Should().HaveCount(1250).And.OnlyHaveUniqueItems().And.BeInDescendingOrder();
        Events(await Query($"{Audit}?entityId=cnt_7&limit=100")).Should().HaveCount(50);
        (await Send(HttpMethod.Get, $"{Audit}?limit=0", null, token: TokenWithAudit())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Get, $"{Audit}?limit=101", null, token: TokenWithAudit())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReadingTheLogNeedsItsOwnPermission()
    {
        (await _client.GetAsync(Audit)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Get, Audit, null)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "content.* alone does not open the log");
        (await Send(HttpMethod.Get, Audit, null, token: TokenWithAudit())).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task NoApiPathCanWriteOrDeleteAnEntry(string method)
    {
        await Send(HttpMethod.Post, Pages, Page("a", "draft", "A"));
        var id = Events(await Query($"{Audit}?limit=1")).First().GetProperty("id").GetInt64();

        foreach (var path in new[] { Audit, $"{Audit}/{id}" })
        {
            using var response = await Send(new HttpMethod(method), path, new { }, token: TokenWithAudit());
            ((int)response.StatusCode).Should().BeOneOf([403, 404, 405], $"{method} {path}: no handler (a 405 falls through to the deny-by-default policy)");
        }

        Events(await Query(Audit)).Should().Contain(e => e.GetProperty("id").GetInt64() == id);
    }

    [Fact]
    public async Task TheDataLayerRefusesToChangeOrRemoveAnEntry()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
        db.Set<AuditEvent>().Add(new AuditEvent { At = DateTime.UtcNow, Service = "content", ActorId = "usr_x", Action = "create", Entity = "ContentItem", EntityId = "cnt_1" });
        await db.SaveChangesAsync();

        var row = await db.Set<AuditEvent>().FirstAsync();
        row.Action = "delete";
        await db.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
        db.ChangeTracker.Clear();

        db.Set<AuditEvent>().Remove((await db.Set<AuditEvent>().FirstAsync()));
        await db.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
    }

    [Fact]
    public async Task SecretsAreNeverWrittenAndUnauthenticatedWorkIsNotRecorded()
    {
        using var auth = new AuthApiFactory();
        using var scope = auth.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();

        var user = await db.Users.FirstAsync();
        user.PasswordHash = "HASH-THAT-MUST-NOT-LEAK";
        user.SecurityStamp = "STAMP-THAT-MUST-NOT-LEAK";
        user.Status = "disabled";
        accessor.HttpContext = null;
        await db.SaveChangesAsync();
        (await db.Set<AuditEvent>().CountAsync()).Should().Be(0, "no caller, no admin mutation");

        accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(KjClaims.Subject, "usr_admin"), new Claim(KjClaims.Email, "admin@site.org")], "test")) };
        user.PasswordHash = "SECOND-HASH-THAT-MUST-NOT-LEAK";
        user.Status = "active";
        await db.SaveChangesAsync();

        var row = await db.Set<AuditEvent>().SingleAsync();
        row.Entity.Should().Be("AuthUser");
        row.After.Should().Contain("active").And.NotContain("HASH").And.NotContain("STAMP");
        row.Before.Should().Contain("disabled").And.NotContain("HASH");
        row.ActorEmail.Should().Be("admin@site.org");
    }

    private static object Page(string slug, string status, string title) => new { slug, status, translations = new[] { new { lang = "en", title } } };

    private static string TokenWithAudit() => TestTokens.Issue([Permissions.ContentView, Permissions.AuditView]);

    private async Task<JsonDocument> Query(string path)
    {
        using var response = await Send(HttpMethod.Get, path, null, token: TokenWithAudit());
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static List<JsonElement> Events(JsonDocument document) => [.. document.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone())];

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, string? token = null, string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.ContentAdmin());
        request.Headers.UserAgent.ParseAdd("audit-tests/1.0");
        if (forwardedFor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
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
