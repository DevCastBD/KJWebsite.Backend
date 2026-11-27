using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;

namespace KJWebsite.IntegrationTests;

public sealed class EventRegistrationTests : IDisposable
{
    private const string Admin = "/api/v1/admin/content";
    private const string Public = "/api/v1/content";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _staff = TestTokens.Issue([Permissions.ContentView, Permissions.ContentCreate, Permissions.ContentUpdate, Permissions.ContentPublish, Permissions.EventsManage]);

    public EventRegistrationTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task GuestRegistersWithoutAnAccountAndGetsAnUnguessableTicket()
    {
        await CreateEvent("open-day", capacity: 10);

        using var created = await Register("open-day", "Rahim", "rahim@example.org", expected: HttpStatusCode.Created);
        var code = created.RootElement.GetProperty("ticketCode").GetString()!;
        created.RootElement.GetProperty("status").GetString().Should().Be("confirmed");
        code.Should().HaveLength(26).And.MatchRegex("^[0-9A-HJKMNP-TV-Z]+$");

        using var again = await Register("open-day", "Rahim", "RAHIM@example.org ", expected: HttpStatusCode.OK);
        again.RootElement.GetProperty("ticketCode").GetString().Should().Be(code, "the same person registering twice gets the same ticket");

        using var other = await Register("open-day", "Karim", "karim@example.org", expected: HttpStatusCode.Created);
        other.RootElement.GetProperty("ticketCode").GetString().Should().NotBe(code);
        (await _client.GetAsync($"{Public}/event-tickets/AAAAAAAAAAAAAAAAAAAAAAAAAA")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task FullEventWaitlistsAndAFreedSeatIsOfferedWithADeadline()
    {
        await CreateEvent("small", capacity: 1);
        using var first = await Register("small", "One", "one@example.org", expected: HttpStatusCode.Created);
        using var second = await Register("small", "Two", "two@example.org", expected: HttpStatusCode.Created);
        second.RootElement.GetProperty("status").GetString().Should().Be("waitlisted");
        second.RootElement.GetProperty("waitlistPosition").GetInt32().Should().Be(1);
        var secondCode = second.RootElement.GetProperty("ticketCode").GetString()!;

        (await _client.PostAsync($"{Public}/event-tickets/{first.RootElement.GetProperty("ticketCode").GetString()}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var offered = await Get($"{Public}/event-tickets/{secondCode}");
        offered.RootElement.GetProperty("status").GetString().Should().Be("offered");
        offered.RootElement.GetProperty("deadlineAt").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow.AddHours(20));

        using var accepted = await Post($"{Public}/event-tickets/{secondCode}/accept");
        accepted.RootElement.GetProperty("status").GetString().Should().Be("confirmed");
    }

    [Fact]
    public async Task ConcurrentRegistrationsNeverOverbook()
    {
        await CreateEvent("race", capacity: 3);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            using var response = await _client.PostAsJsonAsync($"{Public}/events/race/registrations", new { name = $"Person {i}", email = $"p{i}@example.org" });
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("status").GetString();
        }));

        results.Count(s => s == "confirmed").Should().Be(3);
        results.Count(s => s == "waitlisted").Should().Be(9);
        using var list = await Send(HttpMethod.Get, $"{Admin}/events/race/registrations");
        var summary = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.GetProperty("summary");
        summary.GetProperty("seatsHeld").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task ApprovalRequiredEventHoldsNoSeatUntilApproved()
    {
        await CreateEvent("vetted", capacity: 5, requiresApproval: true);
        using var pending = await Register("vetted", "Nadia", "nadia@example.org", expected: HttpStatusCode.Created);
        pending.RootElement.GetProperty("status").GetString().Should().Be("pending_approval");

        var id = await RegistrationId("vetted", "nadia@example.org");
        (await Send(HttpMethod.Post, $"{Admin}/events/vetted/registrations/{id}/reject", new { reason = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var approved = await Send(HttpMethod.Post, $"{Admin}/events/vetted/registrations/{id}/approve");
        approved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await approved.Content.ReadAsStringAsync()).Should().Contain("\"confirmed\"");

        using var second = await Register("vetted", "Omar", "omar@example.org", expected: HttpStatusCode.Created);
        var secondId = await RegistrationId("vetted", "omar@example.org");
        using var rejected = await Send(HttpMethod.Post, $"{Admin}/events/vetted/registrations/{secondId}/reject", new { reason = "Out of area" });
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("Out of area");
    }

    [Fact]
    public async Task PaidRegistrationStaysPendingUntilPaymentIsRecorded()
    {
        await CreateEvent("workshop", capacity: 5, price: 50000);
        using var ticket = await Register("workshop", "Sara", "sara@example.org", expected: HttpStatusCode.Created);
        ticket.RootElement.GetProperty("status").GetString().Should().Be("pending_payment");
        ticket.RootElement.GetProperty("priceMinor").GetInt64().Should().Be(50000);
        ticket.RootElement.GetProperty("deadlineAt").ValueKind.Should().Be(JsonValueKind.String);
        var code = ticket.RootElement.GetProperty("ticketCode").GetString()!;

        (await _client.PostAsync($"{Public}/event-tickets/{code}/accept", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var id = await RegistrationId("workshop", "sara@example.org");
        (await Send(HttpMethod.Post, $"{Admin}/events/workshop/registrations/{id}/payment", new { reference = " " })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Admin}/events/workshop/registrations/{id}/payment", new { reference = "PAY-1" })).StatusCode.Should().Be(HttpStatusCode.OK);
        using var after = await Get($"{Public}/event-tickets/{code}");
        after.RootElement.GetProperty("status").GetString().Should().Be("confirmed");
    }

    [Fact]
    public async Task ClosedDraftAndInvalidRegistrationsAreRefused()
    {
        await CreateEvent("closed", capacity: 5, registrationOpen: false);
        await CreateEvent("hidden", capacity: 5, status: "draft");

        (await _client.PostAsJsonAsync($"{Public}/events/closed/registrations", new { name = "A", email = "a@example.org" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.PostAsJsonAsync($"{Public}/events/hidden/registrations", new { name = "A", email = "a@example.org" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.PostAsJsonAsync($"{Public}/events/closed/registrations", new { name = "", email = "a@example.org" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsJsonAsync($"{Public}/events/closed/registrations", new { name = "A", email = "not-an-email" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RegistrantDataIsOnlyForOrganisers()
    {
        await CreateEvent("private-list", capacity: 5);
        await Register("private-list", "Hidden Name", "hidden@example.org", expected: HttpStatusCode.Created);

        (await _client.GetAsync($"{Admin}/events/private-list/registrations")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var editor = new HttpRequestMessage(HttpMethod.Get, $"{Admin}/events/private-list/registrations");
        editor.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ContentAdmin());
        (await _client.SendAsync(editor)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "content permissions do not include events.manage");

        using var list = await Send(HttpMethod.Get, $"{Admin}/events/private-list/registrations");
        (await list.Content.ReadAsStringAsync()).Should().Contain("hidden@example.org");
    }

    private async Task<string> RegistrationId(string slug, string email)
    {
        using var list = await Send(HttpMethod.Get, $"{Admin}/events/{slug}/registrations");
        var items = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray();
        return items.First(i => i.GetProperty("email").GetString() == email).GetProperty("id").GetString()!;
    }

    private async Task CreateEvent(string slug, int? capacity, bool requiresApproval = false, long? price = null, bool registrationOpen = true, string status = "published")
    {
        (await Send(HttpMethod.Post, $"{Admin}/events", new
        {
            slug,
            status,
            translations = new object[]
            {
                new { lang = "en", title = "Event", summary = "S", body = "B" },
                new { lang = "bn", title = "ইভেন্ট", summary = "সা", body = "বি" }
            }
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        var start = DateTimeOffset.UtcNow.AddDays(30);
        (await Send(HttpMethod.Put, $"{Admin}/events/{slug}/schedule", new
        {
            start,
            end = start.AddHours(3),
            timeZoneId = "Asia/Dhaka",
            capacity,
            registrationOpen,
            requiresApproval,
            priceMinor = price,
            currency = price is null ? null : "BDT"
        })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<JsonDocument> Register(string slug, string name, string email, HttpStatusCode expected)
    {
        using var response = await _client.PostAsJsonAsync($"{Public}/events/{slug}/registrations", new { name, email });
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonDocument> Get(string path)
    {
        using var response = await _client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonDocument> Post(string path)
    {
        using var response = await _client.PostAsync(path, null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
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
