using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;

namespace KJWebsite.IntegrationTests;

public sealed class EventAttendanceTests : IDisposable
{
    private const string Admin = "/api/v1/admin/content";
    private const string Public = "/api/v1/content";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _staff = TestTokens.Issue([Permissions.ContentView, Permissions.ContentCreate, Permissions.ContentUpdate, Permissions.ContentPublish, Permissions.EventsManage]);

    public EventAttendanceTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task CheckInIsIdempotentAndKeepsTheEarliestTime()
    {
        await CreateEvent("gate", capacity: 1);
        await CreateEvent("other-gate", capacity: 5);
        var arrived = await Register("gate", "Ayesha", "ayesha@example.org");
        var waiting = await Register("gate", "Bilal", "bilal@example.org");
        var elsewhere = await Register("other-gate", "Chitra", "chitra@example.org");
        waiting.Status.Should().Be("waitlisted");
        var early = DateTimeOffset.UtcNow.AddMinutes(-3);

        // A late, out-of-order replay of the offline queue: the later scan comes first, the earlier one second, a repeat third.
        var first = await CheckIn("gate", new { ticketCode = arrived.Code, checkedInAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        first[0].GetProperty("result").GetString().Should().Be("checked_in");
        var second = await CheckIn("gate", new { ticketCode = arrived.Code, checkedInAt = early }, new { ticketCode = arrived.Code }, new { ticketCode = waiting.Code }, new { ticketCode = elsewhere.Code }, new { ticketCode = "NOPE" });
        second.Select(r => r.GetProperty("result").GetString()).Should().Equal("already_checked_in", "already_checked_in", "not_eligible", "wrong_event", "not_found");
        second[0].GetProperty("checkedInAt").GetDateTimeOffset().Should().BeCloseTo(early, TimeSpan.FromSeconds(1), "the earliest report wins");

        var id = await RegistrationId("gate", "ayesha@example.org");
        (await CheckIn("gate", new { registrationId = id }))[0].GetProperty("result").GetString().Should().Be("already_checked_in");
    }

    [Fact]
    public async Task NoShowsAreDistinctFromCancellationsAndALateScanRescuesOne()
    {
        var start = DateTimeOffset.UtcNow.AddSeconds(2);
        await CreateEvent("short", capacity: 10, start: start, end: start.AddSeconds(2));
        var showed = await Register("short", "Came", "came@example.org");
        var absent = await Register("short", "Absent", "absent@example.org");
        var cancelled = await Register("short", "Cancelled", "cancelled@example.org");
        (await _client.PostAsync($"{Public}/event-tickets/{cancelled.Code}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        await CheckIn("short", new { ticketCode = showed.Code });

        using var tooEarly = await Send(HttpMethod.Post, $"{Admin}/events/short/attendance/close", new { });
        tooEarly.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Conflict);

        await Task.Delay(TimeSpan.FromSeconds(5));
        using var closed = await Send(HttpMethod.Post, $"{Admin}/events/short/attendance/close", new { });
        closed.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await closed.Content.ReadAsStringAsync()).RootElement.GetProperty("noShows").GetInt32().Should().Be(1);

        (await Lookup(absent.Code)).Status.Should().Be("no_show");
        (await Lookup(cancelled.Code)).Status.Should().Be("cancelled");
        (await Lookup(showed.Code)).Status.Should().Be("confirmed");

        (await CheckIn("short", new { ticketCode = absent.Code }))[0].GetProperty("result").GetString().Should().Be("checked_in");
        (await Lookup(absent.Code)).Status.Should().Be("confirmed", "a late offline scan rescues a no-show");
    }

    [Fact]
    public async Task CertificateRendersABengaliNameAndAllowsACorrectionBeforeIssuing()
    {
        await CreateEvent("training", capacity: 10);
        var attendee = await Register("training", "Nusrat Jahan", "nusrat@example.org");
        var absent = await Register("training", "Not Here", "nothere@example.org");
        var id = await RegistrationId("training", "nusrat@example.org");
        var absentId = await RegistrationId("training", "nothere@example.org");

        (await Send(HttpMethod.Post, $"{Admin}/events/training/registrations/{id}/certificate", new { })).StatusCode.Should().Be(HttpStatusCode.Conflict, "not checked in yet");
        await CheckIn("training", new { ticketCode = attendee.Code });
        (await Send(HttpMethod.Post, $"{Admin}/events/training/registrations/{absentId}/certificate", new { })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var issued = await Send(HttpMethod.Post, $"{Admin}/events/training/registrations/{id}/certificate", new { name = "নুসরাত জাহান" });
        issued.StatusCode.Should().Be(HttpStatusCode.Created);
        JsonDocument.Parse(await issued.Content.ReadAsStringAsync()).RootElement.GetProperty("name").GetString().Should().Be("নুসরাত জাহান");
        (await Send(HttpMethod.Post, $"{Admin}/events/training/registrations/{id}/certificate", new { name = "Again" })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var pdf = await _client.GetAsync($"{Public}/event-tickets/{attendee.Code}/certificate");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
        Encoding.Latin1.GetString(bytes).Should().Contain("NotoSansBengali", "the Bengali font is embedded so conjuncts shape correctly");
        (await _client.GetAsync($"{Public}/event-tickets/{absent.Code}/certificate")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task FeedbackIsForAttendeesOnlyAggregatesAndIsAccessControlled()
    {
        await CreateEvent("survey", capacity: 10);
        var one = await Register("survey", "One", "one@example.org");
        var two = await Register("survey", "Two", "two@example.org");
        var noShow = await Register("survey", "Three", "three@example.org");
        await CheckIn("survey", new { ticketCode = one.Code }, new { ticketCode = two.Code });

        (await Send(HttpMethod.Put, $"{Admin}/events/survey/feedback-form", new { questions = new[] { new { id = "bad", type = "choice", prompt = new { en = "x" }, required = false, options = new[] { "only" } } } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Put, $"{Admin}/events/survey/feedback-form", new
        {
            isOpen = true,
            questions = new object[]
            {
                new { id = "overall", type = "rating", prompt = new { en = "Overall?", bn = "সামগ্রিক?" }, required = true },
                new { id = "again", type = "choice", prompt = new { en = "Come again?" }, required = false, options = new[] { "yes", "no" } },
                new { id = "words", type = "text", prompt = new { en = "Anything else?" }, required = false }
            }
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _client.GetAsync($"{Public}/event-tickets/{noShow.Code}/feedback-form")).StatusCode.Should().Be(HttpStatusCode.NotFound, "feedback is for people who attended");
        (await _client.PostAsJsonAsync($"{Public}/event-tickets/{one.Code}/feedback", new { answers = new { again = "yes" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsJsonAsync($"{Public}/event-tickets/{one.Code}/feedback", new { answers = new { overall = 6 } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsJsonAsync($"{Public}/event-tickets/{one.Code}/feedback", new { answers = new { overall = 5, again = "yes", words = "Loved it" } })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await _client.PostAsJsonAsync($"{Public}/event-tickets/{two.Code}/feedback", new { answers = new { overall = 3, again = "no" } })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await _client.PostAsJsonAsync($"{Public}/event-tickets/{two.Code}/feedback", new { answers = new { overall = 1 } })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var results = await Send(HttpMethod.Get, $"{Admin}/events/survey/feedback");
        var root = JsonDocument.Parse(await results.Content.ReadAsStringAsync()).RootElement;
        root.GetProperty("responses").GetInt32().Should().Be(2);
        root.GetProperty("attended").GetInt32().Should().Be(2);
        var overall = root.GetProperty("questions").EnumerateArray().First(q => q.GetProperty("id").GetString() == "overall");
        overall.GetProperty("average").GetDouble().Should().Be(4);
        root.GetProperty("questions").EnumerateArray().First(q => q.GetProperty("id").GetString() == "words").GetProperty("texts")[0].GetString().Should().Be("Loved it");
        (await results.Content.ReadAsStringAsync()).Should().NotContain("one@example.org").And.NotContain(one.Code);

        (await _client.GetAsync($"{Admin}/events/survey/feedback")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var editorOnly = new HttpRequestMessage(HttpMethod.Get, $"{Admin}/events/survey/feedback");
        editorOnly.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ContentAdmin());
        (await _client.SendAsync(editorOnly)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private sealed record Ticket(string Code, string Status);

    private async Task<Ticket> Register(string slug, string name, string email)
    {
        using var response = await _client.PostAsJsonAsync($"{Public}/events/{slug}/registrations", new { name, email });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return new Ticket(root.GetProperty("ticketCode").GetString()!, root.GetProperty("status").GetString()!);
    }

    private async Task<Ticket> Lookup(string code)
    {
        using var response = await _client.GetAsync($"{Public}/event-tickets/{code}");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return new Ticket(code, root.GetProperty("status").GetString()!);
    }

    private async Task<List<JsonElement>> CheckIn(string slug, params object[] items)
    {
        using var response = await Send(HttpMethod.Post, $"{Admin}/events/{slug}/check-ins", new { items });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return [.. JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    private async Task<string> RegistrationId(string slug, string email)
    {
        using var list = await Send(HttpMethod.Get, $"{Admin}/events/{slug}/registrations");
        return JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("email").GetString() == email).GetProperty("id").GetString()!;
    }

    private async Task CreateEvent(string slug, int? capacity, DateTimeOffset? start = null, DateTimeOffset? end = null)
    {
        (await Send(HttpMethod.Post, $"{Admin}/events", new
        {
            slug,
            status = "published",
            translations = new object[]
            {
                new { lang = "en", title = "Training day", summary = "S", body = "B" },
                new { lang = "bn", title = "প্রশিক্ষণ দিবস", summary = "সা", body = "বি" }
            }
        })).StatusCode.Should().Be(HttpStatusCode.Created);
        var from = start ?? DateTimeOffset.UtcNow.AddDays(30);
        (await Send(HttpMethod.Put, $"{Admin}/events/{slug}/schedule", new { start = from, end = end ?? from.AddHours(3), timeZoneId = "Asia/Dhaka", capacity })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _staff);
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
