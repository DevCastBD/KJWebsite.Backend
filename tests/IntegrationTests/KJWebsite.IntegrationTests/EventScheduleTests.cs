using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ContentService.Data;
using FluentAssertions;

namespace KJWebsite.IntegrationTests;

public sealed class EventScheduleTests : IDisposable
{
    private const string Admin = "/api/v1/admin/content";
    private const string Public = "/api/v1/content";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;

    public EventScheduleTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task EventIsPublishedBilinguallyWithAnOrderedAgendaAndExplicitZone()
    {
        await CreateEvent("orientation", "published");
        var put = await Send(HttpMethod.Put, $"{Admin}/events/orientation/schedule", Schedule(sessions:
        [
            new { startOffsetMinutes = 60, durationMinutes = 30, titles = new { en = "Tea", bn = "চা" }, speaker = "", room = "Hall" },
            new { startOffsetMinutes = 0, durationMinutes = 60, titles = new { en = "Welcome", bn = "স্বাগতম" }, speaker = "Chair", room = "Hall" }
        ]));
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        using var view = await Get($"{Public}/events/orientation/schedule");
        view.RootElement.GetProperty("timeZoneId").GetString().Should().Be("Asia/Dhaka");
        view.RootElement.GetProperty("startUtc").GetDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-12-05T04:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        view.RootElement.GetProperty("startLocal").GetString().Should().Be("2026-12-05T10:00:00+06:00");
        view.RootElement.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("titles").GetProperty("en").GetString()).Should().Equal("Welcome", "Tea");
        using var detail = await Get($"{Public}/events/orientation?lang=bn");
        detail.RootElement.GetProperty("title").GetString().Should().Be("অরিয়েন্টেশন");
        detail.RootElement.GetProperty("type").GetString().Should().Be("event");
    }

    [Fact]
    public async Task DraftEventScheduleIsNotPublic()
    {
        await CreateEvent("secret", "draft");
        (await Send(HttpMethod.Put, $"{Admin}/events/secret/schedule", Schedule())).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _client.GetAsync($"{Public}/events/secret/schedule")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Get, $"{Admin}/events/secret/schedule", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RecurringSeriesIsOneRecordAndKeepsLocalTimeAcrossDst()
    {
        await CreateEvent("weekly", "published");
        // 10:00 New York every Saturday from 2026-03-07 (EST); DST starts 2026-03-08, so later Saturdays are EDT.
        var put = await Send(HttpMethod.Put, $"{Admin}/events/weekly/schedule", Schedule(
            start: "2026-03-07T15:00:00Z", end: "2026-03-07T16:00:00Z", zone: "America/New_York", recurrence: "FREQ=WEEKLY;COUNT=3"));
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        using var view = await Get($"{Public}/events/weekly/schedule");
        var occurrences = view.RootElement.GetProperty("occurrences").EnumerateArray().ToList();
        occurrences.Select(o => o.GetProperty("startLocal").GetString()![..16]).Should().Equal("2026-03-07T10:00", "2026-03-14T10:00", "2026-03-21T10:00");
        occurrences[0].GetProperty("startUtc").GetDateTimeOffset().Hour.Should().Be(15);
        occurrences[1].GetProperty("startUtc").GetDateTimeOffset().Hour.Should().Be(14, "the wall-clock hour is kept after DST");

        // Editing the series is one write; dropping one date does not touch rows.
        (await Send(HttpMethod.Put, $"{Admin}/events/weekly/schedule", Schedule(
            start: "2026-03-07T15:00:00Z", end: "2026-03-07T16:00:00Z", zone: "America/New_York", recurrence: "FREQ=WEEKLY;COUNT=3", exceptions: ["2026-03-14T10:00"]))).StatusCode.Should().Be(HttpStatusCode.OK);
        using var edited = await Get($"{Public}/events/weekly/schedule");
        edited.RootElement.GetProperty("occurrences").GetArrayLength().Should().Be(2);

        using var list = await Get($"{Public}/event-occurrences?lang=en&from=2026-03-01T00:00:00Z&to=2026-04-01T00:00:00Z");
        list.RootElement.GetProperty("total").GetInt32().Should().Be(2);
    }

    [Theory]
    [InlineData("FREQ=WEEKLY")]
    [InlineData("FREQ=YEARLY;COUNT=2")]
    [InlineData("FREQ=DAILY;COUNT=0")]
    [InlineData("FREQ=DAILY;BYDAY=MO;COUNT=2")]
    public async Task InvalidRecurrenceIsRejected(string rule)
    {
        await CreateEvent("bad", "published");
        (await Send(HttpMethod.Put, $"{Admin}/events/bad/schedule", Schedule(recurrence: rule))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task InvalidScheduleFieldsAreRejected()
    {
        await CreateEvent("v", "published");
        (await Send(HttpMethod.Put, $"{Admin}/events/v/schedule", Schedule(zone: "Not/AZone"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Put, $"{Admin}/events/v/schedule", Schedule(end: "2026-12-05T03:00:00Z"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Put, $"{Admin}/events/v/schedule", Schedule(capacity: -1))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Put, $"{Admin}/events/v/schedule", Schedule(sessions: [new { startOffsetMinutes = 0, durationMinutes = 600, titles = new { en = "Too long" }, speaker = "", room = "" }]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Put, $"{Admin}/events/missing/schedule", Schedule())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ScheduleWritesHonourIfMatch()
    {
        await CreateEvent("etag", "published");
        await Send(HttpMethod.Put, $"{Admin}/events/etag/schedule", Schedule());
        using var read = await Send(HttpMethod.Get, $"{Admin}/events/etag/schedule", null);
        var tag = read.Headers.ETag!.Tag;

        (await Send(HttpMethod.Put, $"{Admin}/events/etag/schedule", Schedule(capacity: 10), ifMatch: tag)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Put, $"{Admin}/events/etag/schedule", Schedule(capacity: 20), ifMatch: tag)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ScheduleEndpointsRequireAPermissionForWrites()
    {
        (await _client.GetAsync($"{Admin}/events/x/schedule")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _client.PutAsJsonAsync($"{Admin}/events/x/schedule", Schedule())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("FREQ=MONTHLY;COUNT=4", "2026-01-31T10:00", "2026-01-31T10:00,2026-03-31T10:00,2026-05-31T10:00,2026-07-31T10:00")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE;COUNT=3", "2026-12-07T09:00", "2026-12-07T09:00,2026-12-09T09:00,2026-12-14T09:00")]
    [InlineData("FREQ=DAILY;INTERVAL=2;UNTIL=20261211", "2026-12-07T09:00", "2026-12-07T09:00,2026-12-09T09:00,2026-12-11T09:00")]
    public void RuleExpansionFollowsRfc5545(string text, string first, string expected)
    {
        var (rule, error) = RecurrenceRule.Parse(text);
        error.Should().BeNull();

        rule!.Expand(DateTime.Parse(first, System.Globalization.CultureInfo.InvariantCulture)).Select(d => d.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture)).Should().Equal(expected.Split(','));
    }

    private async Task CreateEvent(string slug, string status)
    {
        var response = await Send(HttpMethod.Post, $"{Admin}/events", new
        {
            slug,
            status,
            translations = new object[]
            {
                new { lang = "en", title = "Orientation", summary = "Welcome day", body = "Details", attributes = new { venueName = "Community Hall" } },
                new { lang = "bn", title = "অরিয়েন্টেশন", summary = "স্বাগত দিন", body = "বিস্তারিত", attributes = new { venueName = "কমিউনিটি হল" } }
            }
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static object Schedule(string start = "2026-12-05T04:00:00Z", string end = "2026-12-05T08:00:00Z", string zone = "Asia/Dhaka", string? recurrence = null, string[]? exceptions = null, int? capacity = 100, object[]? sessions = null) => new
    {
        start,
        end,
        timeZoneId = zone,
        onlineUrl = "https://meet.example.org/room",
        latitude = 23.8,
        longitude = 90.4,
        capacity,
        recurrence,
        exceptions,
        sessions
    };

    private async Task<JsonDocument> Get(string path)
    {
        using var response = await _client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, string? ifMatch = null)
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
