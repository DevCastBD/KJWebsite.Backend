using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContentService.Data;
using ContentService.Data.Entities;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class NewsletterTests : IDisposable
{
    private const string Public = "/api/v1/newsletter";
    private const string Admin = "/api/v1/admin/newsletter";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;

    public NewsletterTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task NothingIsSentBeforeConfirmationAndConfirmationNeedsTheEmailedToken()
    {
        (await Subscribe("ana@example.org", "en")).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var messages = await Query(db => db.NewsletterMessages.AsNoTracking().ToListAsync());
        messages.Should().ContainSingle(m => m.Kind == "confirmation" && m.ToEmail == "ana@example.org");
        (await Query(db => db.NewsletterSubscribers.AsNoTracking().SingleAsync())).Status.Should().Be("pending");

        // the campaign gate refuses an unconfirmed address
        var id = (await Query(db => db.NewsletterSubscribers.AsNoTracking().SingleAsync())).Id;
        (await With(store => store.EnqueueCampaignAsync(id, "Hello", "Body"))).Should().BeFalse();

        using var forged = await _client.PostAsJsonAsync($"{Public}/confirm", new { token = id + ".0000" });
        forged.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var token = await With(store => Task.FromResult(store.Token("confirm", id)));
        messages[0].Body.Should().Contain(token);
        (await _client.PostAsJsonAsync($"{Public}/confirm", new { token })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.PostAsJsonAsync($"{Public}/confirm", new { token })).StatusCode.Should().Be(HttpStatusCode.NoContent, "confirming twice is harmless");

        (await With(store => store.EnqueueCampaignAsync(id, "Hello", "Body"))).Should().BeTrue();
        var campaign = (await Query(db => db.NewsletterMessages.AsNoTracking().Where(m => m.Kind == "campaign").SingleAsync()));
        campaign.ListUnsubscribeUrl.Should().Contain($"{Public}/unsubscribe/");
        (await Query(db => db.NewsletterEvents.AsNoTracking().Where(e => e.Type == "confirmed").CountAsync())).Should().Be(1, "consent is evidenced");
    }

    [Fact]
    public async Task SubscribeDoesNotRevealWhetherAnAddressIsKnownAndRejectsBadInput()
    {
        (await Subscribe("known@example.org")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await Confirm("known@example.org");
        (await Subscribe("known@example.org")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Query(db => db.NewsletterMessages.AsNoTracking().CountAsync())).Should().Be(1, "a confirmed address gets no second email");

        (await Subscribe("not-an-email")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Subscribe("x@example.org", "fr")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsJsonAsync($"{Public}/subscribe", new { email = "bot@example.org", website = "http://spam" })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Query(db => db.NewsletterSubscribers.AsNoTracking().CountAsync(s => s.Email == "bot@example.org"))).Should().Be(0, "the honeypot stores nothing");
    }

    [Fact]
    public async Task OneClickUnsubscribeNeedsNoLoginAndIsIdempotent()
    {
        await Subscribe("bob@example.org");
        var id = await Confirm("bob@example.org");
        var token = await With(store => Task.FromResult(store.Token("manage", id)));

        using var first = await _client.PostAsync($"{Public}/unsubscribe/{token}", new StringContent("List-Unsubscribe=One-Click", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.PostAsync($"{Public}/unsubscribe/{token}", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Query(db => db.NewsletterSubscribers.AsNoTracking().SingleAsync())).Status.Should().Be("unsubscribed");
        (await With(store => store.EnqueueCampaignAsync(id, "Hello", "Body"))).Should().BeFalse();
        (await _client.PostAsync($"{Public}/unsubscribe/{id}.bad", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // coming back goes through double opt-in again
        await Subscribe("bob@example.org");
        (await Query(db => db.NewsletterSubscribers.AsNoTracking().SingleAsync())).Status.Should().Be("pending");
    }

    [Fact]
    public async Task PreferencesChangeLanguageAndTopicsAndMaskTheAddress()
    {
        await Subscribe("carol@example.org", "bn");
        var id = await Confirm("carol@example.org");
        var token = await With(store => Task.FromResult(store.Token("manage", id)));

        var view = await _client.GetFromJsonAsync<JsonElement>($"{Public}/preferences/{token}");
        view.GetProperty("email").GetString().Should().Be("c****@example.org");
        view.GetProperty("lang").GetString().Should().Be("bn");

        using var put = await _client.PutAsJsonAsync($"{Public}/preferences/{token}", new { lang = "en", topics = new[] { "Events", "events", "news" } });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await Query(db => db.NewsletterSubscribers.AsNoTracking().SingleAsync());
        saved.Lang.Should().Be("en");
        saved.TopicsJson.Should().Be("[\"events\",\"news\"]");
        (await _client.PutAsJsonAsync($"{Public}/preferences/{token}", new { lang = "xx" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BouncesAndComplaintsSuppressAndSuppressionSurvivesResubscribing()
    {
        await Subscribe("hard@example.org");
        var hard = await Confirm("hard@example.org");
        await Subscribe("soft@example.org");
        var soft = await Confirm("soft@example.org");
        await Subscribe("angry@example.org");
        var angry = await Confirm("angry@example.org");

        using var noSecret = await _client.PostAsJsonAsync($"{Public}/provider-events", new[] { new { email = "hard@example.org", type = "bounce" } });
        noSecret.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        async Task<JsonElement> Send(object[] events)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Public}/provider-events") { Content = JsonContent.Create(events) };
            request.Headers.Add("X-Webhook-Secret", ContentApiFactory.NewsletterWebhookSecret);
            using var response = await _client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        (await Send([new { email = "hard@example.org", type = "bounce" }, new { email = "angry@example.org", type = "complaint" }])).GetProperty("applied").GetInt32().Should().Be(2);
        await Send([new { email = "soft@example.org", type = "soft-bounce" }, new { email = "soft@example.org", type = "soft-bounce" }]);
        (await Query(db => db.NewsletterSubscribers.AsNoTracking().SingleAsync(s => s.Id == soft))).Status.Should().Be("confirmed", "two soft bounces are tolerated");
        await Send([new { email = "soft@example.org", type = "soft-bounce" }]);

        foreach (var id in new[] { hard, soft, angry })
        {
            (await With(store => store.EnqueueCampaignAsync(id, "Hello", "Body"))).Should().BeFalse();
        }

        (await Query(db => db.NewsletterSubscribers.AsNoTracking().SingleAsync(s => s.Id == angry))).SuppressionReason.Should().Be("complaint");

        // a person who complained cannot simply sign up again
        await Subscribe("angry@example.org");
        (await Query(db => db.NewsletterSubscribers.AsNoTracking().SingleAsync(s => s.Id == angry))).Status.Should().Be("suppressed");
    }

    [Fact]
    public async Task SegmentsAreReproducibleAndOnlyHoldConfirmedConsentingAddresses()
    {
        foreach (var (email, lang, source) in new[] { ("a@example.org", "bn", "footer"), ("b@example.org", "en", "footer"), ("c@example.org", "bn", "event"), ("pending@example.org", "bn", "footer") })
        {
            await _client.PostAsJsonAsync($"{Public}/subscribe", new { email, lang, source });
            if (email != "pending@example.org")
            {
                await Confirm(email);
            }
        }

        await Subscribe("gone@example.org");
        var goneId = await Confirm("gone@example.org");
        var goneToken = await With(store => Task.FromResult(store.Token("manage", goneId)));
        await _client.PostAsync($"{Public}/unsubscribe/{goneToken}", null);

        var staff = TestTokens.Issue([Permissions.CommsManage]);
        using var create = new HttpRequestMessage(HttpMethod.Post, $"{Admin}/segments") { Content = JsonContent.Create(new { name = "Bengali footer", filter = new { langs = new[] { "bn" }, sources = new[] { "footer" } } }) };
        create.Headers.Authorization = new("Bearer", staff);
        using var created = await _client.SendAsync(create);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var segmentId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        async Task<string[]> Emails()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Admin}/segments/{segmentId}/subscribers");
            request.Headers.Authorization = new("Bearer", staff);
            using var response = await _client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("email").GetString()!)];
        }

        var first = await Emails();
        first.Should().Equal("a@example.org");
        (await Emails()).Should().Equal(first, "the same data resolves to the same list");

        using var denied = new HttpRequestMessage(HttpMethod.Get, $"{Admin}/subscribers");
        denied.Headers.Authorization = new("Bearer", TestTokens.Issue([Permissions.ContentView]));
        (await _client.SendAsync(denied)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- helpers

    private Task<HttpResponseMessage> Subscribe(string email, string? lang = null) => _client.PostAsJsonAsync($"{Public}/subscribe", new { email, lang });

    private async Task<string> Confirm(string email)
    {
        var id = (await Query(db => db.NewsletterSubscribers.AsNoTracking().SingleAsync(s => s.Email == email))).Id;
        var token = await With(store => Task.FromResult(store.Token("confirm", id)));
        (await _client.PostAsJsonAsync($"{Public}/confirm", new { token })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        return id;
    }

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
