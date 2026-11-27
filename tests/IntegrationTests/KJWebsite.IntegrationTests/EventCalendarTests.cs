using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ContentService.Data;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;

namespace KJWebsite.IntegrationTests;

public sealed class EventCalendarTests : IDisposable
{
    private const string Admin = "/api/v1/admin/content";
    private const string Public = "/api/v1/content";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _staff = TestTokens.Issue([Permissions.ContentView, Permissions.ContentCreate, Permissions.ContentUpdate, Permissions.ContentPublish, Permissions.EventsManage]);

    public EventCalendarTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task PublicFeedHasValidStructureUtcTimesAndEscapedBengaliText()
    {
        var start = DateTimeOffset.UtcNow.AddDays(10);
        await CreateEvent("big-day", "বাংলা, ইংরেজি; মিশ্র\nদিন " + new string('ক', 60), start, "Asia/Dhaka", "FREQ=WEEKLY;COUNT=2");
        await CreateEvent("hidden-day", "Hidden", start, "Asia/Dhaka", status: "draft");

        using var response = await _client.GetAsync($"{Public}/event-feed.ics?lang=bn");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/calendar");
        var text = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());

        text.Should().StartWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n").And.EndWith("END:VCALENDAR\r\n");
        text.Split("\r\n").Count(l => l == "BEGIN:VEVENT").Should().Be(2, "a weekly series of two occurrences");
        text.Should().NotContain("Hidden", "drafts are not in the feed");
        foreach (var physical in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            Encoding.UTF8.GetByteCount(physical).Should().BeLessThanOrEqualTo(75, "lines are folded at 75 octets");
        }

        var unfolded = text.Replace("\r\n ", string.Empty, StringComparison.Ordinal);
        unfolded.Should().Contain("SUMMARY:বাংলা\\, ইংরেজি\\; মিশ্র\\nদিন ", "commas, semicolons and newlines are escaped and multi-byte text survives folding");
        unfolded.Should().Contain($"DTSTART:{IcsWriter.Utc(start)}");
        unfolded.Should().MatchRegex(@"DTSTART:\d{8}T\d{6}Z");
    }

    [Fact]
    public async Task TimesStayCorrectAcrossDaylightSavingBoundaries()
    {
        // 10:00 in New York every Saturday from 2026-03-07 (EST, UTC-5); DST begins 2026-03-08, so later Saturdays are EDT (UTC-4).
        await CreateEvent("ny-class", "NY class", DateTimeOffset.Parse("2026-03-07T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "America/New_York", "FREQ=WEEKLY;COUNT=3");

        using var response = await _client.GetAsync($"{Public}/events/ny-class/calendar.ics");
        var text = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
        text.Split("\r\n").Where(l => l.StartsWith("DTSTART:", StringComparison.Ordinal)).Should().Equal("DTSTART:20260307T150000Z", "DTSTART:20260314T140000Z", "DTSTART:20260321T140000Z");
        (await _client.GetAsync($"{Public}/events/nope/calendar.ics")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RegistrationProducesAnInviteForConfirmedTicketsOnly()
    {
        await CreateEvent("workshop", "Workshop", DateTimeOffset.UtcNow.AddDays(14), "Asia/Dhaka", capacity: 1);
        var confirmed = await Register("workshop", "Ana", "ana@example.org");
        var waiting = await Register("workshop", "Ben", "ben@example.org");

        using var invite = await _client.GetAsync($"{Public}/event-tickets/{confirmed}/calendar.ics");
        invite.StatusCode.Should().Be(HttpStatusCode.OK);
        invite.Headers.CacheControl!.NoStore.Should().BeTrue();
        var text = Encoding.UTF8.GetString(await invite.Content.ReadAsByteArrayAsync());
        text.Split("\r\n").Count(l => l == "BEGIN:VEVENT").Should().Be(1);
        text.Should().Contain("SUMMARY:Workshop").And.Contain("LOCATION:Community Hall\\, Dhaka");
        (await _client.GetAsync($"{Public}/event-tickets/{waiting}/calendar.ics")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.GetAsync($"{Public}/event-tickets/NOSUCHTICKET/calendar.ics")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MemberFeedTokenIsUnguessableRevocableAndOnlyShowsThatMembersEvents()
    {
        await CreateEvent("mine", "Mine", DateTimeOffset.UtcNow.AddDays(5), "Asia/Dhaka");
        await CreateEvent("theirs", "Theirs", DateTimeOffset.UtcNow.AddDays(6), "Asia/Dhaka");
        await Register("mine", "Rina", "rina@example.org", member: "usr_rina");
        await Register("theirs", "Sami", "sami@example.org", member: "usr_sami");

        (await _client.PostAsync($"{Public}/calendar-feed/token", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var created = await Member("usr_rina", HttpMethod.Post, $"{Public}/calendar-feed/token");
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var token = body.GetProperty("token").GetString()!;
        token.Should().HaveLength(43).And.MatchRegex("^[A-Za-z0-9_-]+$");
        token.Should().NotContain("rina");
        var path = body.GetProperty("path").GetString()!;

        using var feed = await _client.GetAsync(path);
        feed.StatusCode.Should().Be(HttpStatusCode.OK);
        feed.Headers.CacheControl!.NoStore.Should().BeTrue();
        var text = Encoding.UTF8.GetString(await feed.Content.ReadAsByteArrayAsync());
        text.Should().Contain("SUMMARY:Mine").And.NotContain("Theirs");

        (await _client.GetAsync($"{Public}/calendar-feed/{new string('x', 43)}.ics")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Rotating revokes the old URL at once; revoking stops the new one.
        using var rotated = await Member("usr_rina", HttpMethod.Post, $"{Public}/calendar-feed/token");
        var next = JsonDocument.Parse(await rotated.Content.ReadAsStringAsync()).RootElement.GetProperty("path").GetString()!;
        (await _client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync(next)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Member("usr_rina", HttpMethod.Delete, $"{Public}/calendar-feed/token")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.GetAsync(next)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Member("usr_rina", HttpMethod.Delete, $"{Public}/calendar-feed/token")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void WriterEscapesAndFoldsWithoutSplittingMultiByteCharacters()
    {
        IcsWriter.Escape("a\\b;c,d\ne").Should().Be("a\\\\b\\;c\\,d\\ne");
        var writer = new IcsWriter("Cal", "-//x//EN");
        writer.Event("uid", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), new string('ক', 100), null, null, null, 1);

        var text = Encoding.UTF8.GetString(writer.ToBytes());

        foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            Encoding.UTF8.GetByteCount(line).Should().BeLessThanOrEqualTo(75);
        }

        text.Replace("\r\n ", string.Empty, StringComparison.Ordinal).Should().Contain("SUMMARY:" + new string('ক', 100));
    }

    private async Task CreateEvent(string slug, string title, DateTimeOffset start, string zone, string? recurrence = null, string status = "published", int? capacity = null)
    {
        (await Send(_staff, HttpMethod.Post, $"{Admin}/events", new
        {
            slug,
            status,
            translations = new object[]
            {
                new { lang = "en", title, summary = "A summary", body = "B", attributes = new { venueName = "Community Hall", venueAddress = "Dhaka" } },
                new { lang = "bn", title, summary = "সারাংশ", body = "বি", attributes = new { venueName = "কমিউনিটি হল" } }
            }
        })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Send(_staff, HttpMethod.Put, $"{Admin}/events/{slug}/schedule", new { start, end = start.AddHours(2), timeZoneId = zone, recurrence, capacity })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<string> Register(string slug, string name, string email, string? member = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Public}/events/{slug}/registrations") { Content = JsonContent.Create(new { name, email }) };
        if (member is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue([Permissions.MemberProfileEdit], subject: member));
        }

        using var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("ticketCode").GetString()!;
    }

    private Task<HttpResponseMessage> Member(string subject, HttpMethod method, string path) =>
        Send(TestTokens.Issue([Permissions.MemberProfileEdit], subject: subject), method, path, null);

    private async Task<HttpResponseMessage> Send(string token, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
