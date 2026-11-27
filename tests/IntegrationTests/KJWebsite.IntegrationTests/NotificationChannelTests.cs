using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContentService.Data;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class NotificationChannelTests : IDisposable
{
    private const string Member = "/api/v1/notifications";
    private const string Admin = "/api/v1/admin/notifications";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _staff = TestTokens.Issue([Permissions.CommsManage]);
    private readonly string _ana = TestTokens.Issue([Permissions.MemberProfileEdit], subject: "usr_ana");
    private readonly string _ben = TestTokens.Issue([Permissions.MemberProfileEdit], subject: "usr_ben");

    public NotificationChannelTests() => _client = _factory.CreateClient();

    [Theory]
    [InlineData("01712345678", "+8801712345678")]
    [InlineData("+880 1712-345678", "+8801712345678")]
    [InlineData("8801912345678", "+8801912345678")]
    [InlineData("01212345678", null)]
    [InlineData("0171234567", null)]
    [InlineData("+14155550123", null)]
    public void BangladeshiNumbersAreNormalisedAndOthersRejected(string raw, string? expected) =>
        NotificationChannelStore.NormalizePhone(raw).Should().Be(expected);

    [Fact]
    public void BengaliTextSwitchesToUnicodeAndCostsMoreSegments()
    {
        NotificationChannelStore.Measure(new string('a', 160)).Should().Be(new SmsLength("gsm7", 1));
        NotificationChannelStore.Measure(new string('a', 161)).Should().Be(new SmsLength("gsm7", 2));
        NotificationChannelStore.Measure(new string('ক', 70)).Should().Be(new SmsLength("ucs2", 1));
        NotificationChannelStore.Measure(new string('ক', 71)).Should().Be(new SmsLength("ucs2", 2));
        NotificationChannelStore.Measure("Hello ক").Encoding.Should().Be("ucs2", "one Bengali letter makes the whole message Unicode");
        NotificationChannelStore.Measure("{x}").Segments.Should().Be(1);
    }

    [Fact]
    public async Task SmsNeedsEveryGateAndBengaliRendersAsUnicode()
    {
        var values = new { name = "রহিম", eventTitle = "রক্তদান ক্যাম্প", startsAt = "শনিবার ১০টা", ticketUrl = "https://example.org/t/1" };

        // not opted in, even though the sender allows SMS
        (await Notify("event.reminder", new { subject = "usr_ana", lang = "bn", phone = "01712345678", allowSms = true, values })).Single(c => c.Channel == "sms").Reason.Should().Be("RECIPIENT_NOT_OPTED_IN");

        (await Put(_ana, new { sms = true })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "SMS needs a number");
        (await Put(_ana, new { sms = true, phone = "01712345678" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Put(_ana, new { phone = "12345" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // the sender did not decide to use SMS for this send
        (await Notify("event.reminder", new { subject = "usr_ana", lang = "bn", values })).Single(c => c.Channel == "sms").Reason.Should().Be("SMS_NOT_ALLOWED_FOR_THIS_SEND");
        // a template that is not on the SMS allow-list
        (await Notify("donation.receipt", new { subject = "usr_ana", lang = "bn", allowSms = true, values = new { name = "রহিম", amount = "৳৫০০", receiptNumber = "R1", receiptUrl = "https://example.org/r" } })).Single(c => c.Channel == "sms").Reason.Should().Be("TEMPLATE_NOT_APPROVED_FOR_SMS");

        var sent = await Notify("event.reminder", new { subject = "usr_ana", lang = "bn", allowSms = true, values });
        sent.Single(c => c.Channel == "sms").Status.Should().Be("queued");

        var sms = (await Json(await Get(_staff, $"{Admin}/sms"))).EnumerateArray().Single();
        sms.GetProperty("toPhone").GetString().Should().Be("+8801712345678");
        sms.GetProperty("encoding").GetString().Should().Be("ucs2");
        sms.GetProperty("body").GetString().Should().Contain("রক্তদান ক্যাম্প").And.NotContain("**").And.Contain("https://example.org/t/1");

        // per-number limit (3 a day)
        (await Notify("event.reminder", new { subject = "usr_ana", lang = "bn", allowSms = true, values })).Single(c => c.Channel == "sms").Status.Should().Be("queued");
        (await Notify("event.reminder", new { subject = "usr_ana", lang = "bn", allowSms = true, values })).Single(c => c.Channel == "sms").Status.Should().Be("queued");
        (await Notify("event.reminder", new { subject = "usr_ana", lang = "bn", allowSms = true, values })).Single(c => c.Channel == "sms").Reason.Should().Be("RECIPIENT_DAILY_LIMIT");

        (await _factory.Services.GetRequiredService<SmsDispatcher>().DispatchDueAsync(50)).Should().Be(3);
    }

    [Fact]
    public async Task ChannelPreferencesAreHonouredAndInAppNotificationsPersistWithSharedReadState()
    {
        var values = new { name = "Ben", reference = "A-1" };
        (await Notify("application.received", new { subject = "usr_ben", email = "ben@example.org", lang = "en", values })).Select(c => (c.Channel, c.Status)).Should().Contain([("email", "queued"), ("inApp", "queued"), ("sms", "skipped")]);

        (await Put(_ben, new { email = false, inApp = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        var muted = await Notify("application.received", new { subject = "usr_ben", email = "ben@example.org", lang = "en", values });
        muted.Single(c => c.Channel == "email").Reason.Should().Be("RECIPIENT_OPTED_OUT");
        muted.Single(c => c.Channel == "inApp").Reason.Should().Be("RECIPIENT_OPTED_OUT");

        await Put(_ben, new { inApp = true });
        await Notify("application.received", new { subject = "usr_ben", lang = "en", values });

        var inbox = await Json(await Get(_ben, $"{Member}/inbox"));
        inbox.GetProperty("unread").GetInt32().Should().Be(2);
        var firstId = inbox.GetProperty("items")[1].GetProperty("id").GetInt64();
        (await Post(_ben, $"{Member}/inbox/{firstId}/read", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Post(_ana, $"{Member}/inbox/{firstId}/read", null)).StatusCode.Should().Be(HttpStatusCode.NotFound, "another member cannot touch it");

        // "another device": a fresh request sees the same read state
        (await Json(await Get(_ben, $"{Member}/inbox"))).GetProperty("unread").GetInt32().Should().Be(1);
        (await Json(await Get(_ben, $"{Member}/inbox?unreadOnly=true"))).GetProperty("items").GetArrayLength().Should().Be(1);
        (await Json(await Post(_ben, $"{Member}/inbox/read-all", null))).GetProperty("marked").GetInt32().Should().Be(1);
        (await Json(await Get(_ben, $"{Member}/inbox"))).GetProperty("unread").GetInt32().Should().Be(0);

        (await Json(await Get(_ana, $"{Member}/inbox"))).GetProperty("total").GetInt32().Should().Be(0, "inboxes are private");
    }

    [Fact]
    public async Task WebPushNeedsExplicitConsentAndIsOffByDefault()
    {
        (await Json(await Get(_ana, $"{Member}/preferences"))).GetProperty("push").GetBoolean().Should().BeFalse();
        var subscription = new { endpoint = "https://push.example.org/abc", p256dh = "key", auth = "secret" };

        (await Post(_ana, $"{Member}/push-subscriptions", subscription)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "no consent flag");
        (await Post(_ana, $"{Member}/push-subscriptions", new { subscription.endpoint, subscription.p256dh, subscription.auth, consent = true })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Json(await Get(_ana, $"{Member}/preferences"))).GetProperty("push").GetBoolean().Should().BeTrue();

        (await Put(_ana, new { push = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        using var remove = new HttpRequestMessage(HttpMethod.Delete, $"{Member}/push-subscriptions?endpoint={Uri.EscapeDataString(subscription.endpoint)}");
        remove.Headers.Authorization = new("Bearer", _ana);
        (await _client.SendAsync(remove)).StatusCode.Should().Be(HttpStatusCode.NotFound, "turning push off already removed the subscription");
    }

    // ---- helpers

    private async Task<List<ChannelOutcome>> Notify(string key, object body)
    {
        using var response = await Post(_staff, $"{Admin}/templates/{key}/notify", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return [.. (await Json(response)).GetProperty("channels").EnumerateArray().Select(c => new ChannelOutcome(c.GetProperty("channel").GetString()!, c.GetProperty("status").GetString()!, c.TryGetProperty("reason", out var r) ? r.GetString() : null))];
    }

    private Task<HttpResponseMessage> Put(string token, object body) => Send(HttpMethod.Put, token, $"{Member}/preferences", body);

    private Task<HttpResponseMessage> Get(string token, string url) => Send(HttpMethod.Get, token, url, null);

    private Task<HttpResponseMessage> Post(string token, string url, object? body) => Send(HttpMethod.Post, token, url, body);

    private async Task<HttpResponseMessage> Send(HttpMethod method, string token, string url, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
