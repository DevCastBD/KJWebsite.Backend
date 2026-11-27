using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContentService.Data;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class NewsletterCampaignTests : IDisposable
{
    private const string Public = "/api/v1/newsletter";
    private const string Admin = "/api/v1/admin/newsletter";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _staff = TestTokens.Issue([Permissions.CommsManage]);

    public NewsletterCampaignTests() => _client = _factory.CreateClient();

    private static readonly object Full = new { subjectBn = "বিষয় {{email}}", bodyBn = "# শিরোনাম\n\nহ্যালো **বন্ধু**, [দেখুন](https://example.org)।", subjectEn = "Subject", bodyEn = "# Title\n\nHello **friend**, <script>x</script> [see](https://example.org)." };

    [Fact]
    public async Task CampaignCannotSendWithAMissingLanguageVersionOrAWrongRecipientCount()
    {
        await Subscriber("a@example.org", "bn");
        await Subscriber("b@example.org", "en");

        var partial = await Create("Partial", new { subjectEn = "Only English", bodyEn = "Body" });
        (await Send(partial, 2)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the Bengali version is missing");
        (await Json(await Send(partial, 2))).GetProperty("error").GetProperty("code").GetString().Should().Be("MISSING_LANGUAGE");

        var id = await Create("Full", Full);
        using var wrong = await Send(id, 5);
        wrong.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(wrong)).GetProperty("error").GetProperty("message").GetString().Should().Contain("(2)");
        (await Send(id, null)).StatusCode.Should().Be(HttpStatusCode.Conflict, "no confirmation, no send");

        using var ok = await Send(id, 2);
        ok.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Send(id, 2)).StatusCode.Should().Be(HttpStatusCode.Conflict, "a campaign sends once");
        (await Put(id, new { subjectEn = "x" })).StatusCode.Should().Be(HttpStatusCode.Conflict, "a sent campaign is frozen");
    }

    [Fact]
    public async Task EachSubscriberGetsTheirLanguageAndDeliveryIsThrottledAndReported()
    {
        await Subscriber("bn@example.org", "bn");
        await Subscriber("en@example.org", "en");
        await Subscriber("gone@example.org", "en");
        var id = await Create("Launch", Full);
        (await Send(id, 3)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var queued = await Messages(id);
        queued.Single(m => m.ToEmail == "bn@example.org").Subject.Should().Be("বিষয় bn@example.org");
        queued.Single(m => m.ToEmail == "en@example.org").Subject.Should().Be("Subject");
        queued.Single(m => m.ToEmail == "en@example.org").BodyHtml.Should().Contain("<strong>friend</strong>").And.NotContain("<script>", "markup in a body is escaped");
        queued.Should().OnlyContain(m => m.ListUnsubscribeUrl!.Contains("/api/v1/newsletter/unsubscribe/"));
        queued.Select(m => m.NotBeforeUnix).Distinct().Should().HaveCount(2, "120 per minute spaces recipients 0.5s apart, so three land on two distinct seconds");
        (queued.Max(m => m.NotBeforeUnix) - queued.Min(m => m.NotBeforeUnix)).Should().BeLessThanOrEqualTo(1);

        // one recipient leaves before delivery: consent is re-checked at the last moment
        var goneId = (await Query(db => db.NewsletterSubscribers.AsNoTracking().SingleAsync(s => s.Email == "gone@example.org"))).Id;
        var token = await With(store => Task.FromResult(store.Token("manage", goneId)));
        (await _client.PostAsync($"{Public}/unsubscribe/{token}", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await Task.Delay(1100); // the last recipient is spaced one second after the first
        (await _factory.Services.GetRequiredService<NewsletterDispatcher>().DispatchDueAsync(100)).Should().Be(5, "three confirmation emails and the two campaign messages still owed");

        var report = await Report(id);
        report.GetProperty("recipients").GetInt32().Should().Be(3);
        report.GetProperty("sent").GetInt32().Should().Be(2);
        report.GetProperty("skipped").GetInt32().Should().Be(1);
        report.GetProperty("unsubscribed").GetInt32().Should().Be(1);
        report.GetProperty("complete").GetBoolean().Should().BeTrue();
        report.GetProperty("byLanguage").GetProperty("en").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task TestSendMatchesTheRealRenderAndAMissingVariableFailsBeforeAnySend()
    {
        await Subscriber("z@example.org", "en");
        var id = await Create("Draft", Full);

        var preview = await Json(await Get($"{Admin}/campaigns/{id}/preview?lang=en"));
        using var test = await Post($"{Admin}/campaigns/{id}/test", new { email = "editor@example.org", lang = "en" });
        test.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var queuedTest = (await Messages(id)).Single(m => m.Kind == "test");
        queuedTest.BodyHtml.Should().NotBeNull();
        queuedTest.Body.Should().Contain("Hello **friend**", "the same renderer output the real send uses");
        queuedTest.Subject.Should().Be("[TEST] " + preview.GetProperty("subject").GetString());
        (await Messages(id)).Should().OnlyContain(m => m.Kind == "test", "a test send reaches only the address given");

        using var bad = await Post($"{Admin}/campaigns", new { name = "Bad", versions = new { subjectBn = "x {{firstName}}", bodyBn = "b", subjectEn = "s", bodyEn = "b" } });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(bad)).GetProperty("error").GetProperty("message").GetString().Should().Contain("firstName");

        using var denied = new HttpRequestMessage(HttpMethod.Post, $"{Admin}/campaigns/{id}/send") { Content = JsonContent.Create(new { confirmRecipientCount = 1 }) };
        denied.Headers.Authorization = new("Bearer", TestTokens.Issue([Permissions.ContentPublish]));
        (await _client.SendAsync(denied)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CancelStopsUndeliveredMessagesAndAScheduledSendWaits()
    {
        await Subscriber("late@example.org", "en");
        var id = await Create("Later", Full);
        var when = DateTimeOffset.UtcNow.AddHours(2);
        (await Post($"{Admin}/campaigns/{id}/send", new { confirmRecipientCount = 1, scheduledAt = when })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Messages(id)).Single().NotBeforeUnix.Should().Be(when.ToUnixTimeSeconds());

        (await _factory.Services.GetRequiredService<NewsletterDispatcher>().DispatchDueAsync(100)).Should().Be(1, "only the confirmation email is due; the scheduled campaign message is not");

        (await Post($"{Admin}/campaigns/{id}/cancel", new { })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Report(id)).GetProperty("cancelled").GetInt32().Should().Be(1);
    }

    // ---- helpers

    private async Task Subscriber(string email, string lang)
    {
        await _client.PostAsJsonAsync($"{Public}/subscribe", new { email, lang });
        var id = (await Query(db => db.NewsletterSubscribers.AsNoTracking().SingleAsync(s => s.Email == email))).Id;
        var token = await With(store => Task.FromResult(store.Token("confirm", id)));
        (await _client.PostAsJsonAsync($"{Public}/confirm", new { token })).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private async Task<string> Create(string name, object versions)
    {
        using var response = await Post($"{Admin}/campaigns", new { name, versions });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private Task<HttpResponseMessage> Send(string id, int? count) => Post($"{Admin}/campaigns/{id}/send", new { confirmRecipientCount = count });

    private async Task<JsonElement> Report(string id) => await Json(await Get($"{Admin}/campaigns/{id}/report"));

    private Task<List<ContentService.Data.Entities.NewsletterMessageEntity>> Messages(string id) =>
        Query(db => db.NewsletterMessages.AsNoTracking().Where(m => m.CampaignId == id).OrderBy(m => m.ToEmail).ToListAsync());

    private Task<HttpResponseMessage> Post(string url, object body) => Send(HttpMethod.Post, url, body);

    private Task<HttpResponseMessage> Put(string id, object versions) => Send(HttpMethod.Put, $"{Admin}/campaigns/{id}", new { versions });

    private Task<HttpResponseMessage> Get(string url) => Send(HttpMethod.Get, url, null);

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

    private async Task<T> Query<T>(Func<ContentDbContext, Task<T>> query)
    {
        using var scope = _factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<ContentDbContext>());
    }

    private async Task<T> With<T>(Func<NewsletterStore, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<NewsletterStore>());
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
