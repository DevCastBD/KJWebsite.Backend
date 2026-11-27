using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReportingService.Analytics;
using ReportingService.Data;

namespace KJWebsite.IntegrationTests;

public sealed class ReportingAnalyticsTests : IDisposable
{
    private const string Collector = "/api/v1/analytics/events";
    private const string Funnels = "/api/v1/admin/reporting/funnels";
    private readonly ReportingApiFactory _factory = new();
    private readonly HttpClient _client;

    public ReportingAnalyticsTests() => _client = _factory.CreateClient();

    /// <summary>Posts the way the frontend does: text/plain so the browser sends no preflight, no credentials.</summary>
    private Task<HttpResponseMessage> Post(object body, Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Collector) { Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "text/plain") };
        configure?.Invoke(request);
        return _client.SendAsync(request);
    }

    private async Task Event(string name, string? step = null, string? run = null, string? field = null)
    {
        var props = new Dictionary<string, string>();
        if (step is not null) props["step"] = step;
        if (run is not null) props["run"] = run;
        if (field is not null) props["field"] = field;
        (await Post(new { name, props, path = "/join" })).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    private async Task<JsonElement> Funnel(string name, string query = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{Funnels}/{name}{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue([Permissions.ReportingView]));
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task TheApplicationFunnelReportsPerStepEntryCompletionAndAbandonment()
    {
        await Event("application_page_view");
        await Event("application_started");
        // visit a: completes everything
        foreach (var step in new[] { "personal", "education", "address", "motivation", "review" })
        {
            await Event("application_step_view", step, "visit-a");
            await Event("application_step_complete", step, "visit-a");
        }

        await Event("registration_submit_success");
        // visit b: leaves at education
        await Event("application_step_view", "personal", "visit-b");
        await Event("application_step_complete", "personal", "visit-b");
        await Event("application_step_view", "education", "visit-b");
        await Event("application_abandon", "education", "visit-b");
        // visit c: still on personal
        await Event("application_step_view", "personal", "visit-c");

        var funnel = await Funnel("application");
        funnel.GetProperty("visits").GetInt32().Should().Be(3);
        funnel.GetProperty("converted").GetInt32().Should().Be(1);
        funnel.GetProperty("abandoned").GetInt32().Should().Be(1);
        funnel.GetProperty("inProgress").GetInt32().Should().Be(1);
        funnel.GetProperty("conversionPercent").GetString().Should().Be("33.3");
        var steps = funnel.GetProperty("steps").EnumerateArray().ToDictionary(s => s.GetProperty("step").GetString()!);
        steps["personal"].GetProperty("entered").GetInt32().Should().Be(3);
        steps["personal"].GetProperty("completed").GetInt32().Should().Be(2);
        steps["personal"].GetProperty("dropOffPercent").GetString().Should().Be("33.3");
        steps["education"].GetProperty("entered").GetInt32().Should().Be(2);
        steps["education"].GetProperty("abandoned").GetInt32().Should().Be(1);
        steps["attachments"].GetProperty("entered").GetInt32().Should().Be(0);
        funnel.GetProperty("events").GetProperty("application_started").GetInt32().Should().Be(1);
        funnel.GetProperty("events").GetProperty("registration_submit_success").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task TheDonationFunnelIsSeparateAndCountsCopies()
    {
        await Event("donate_view");
        await Event("donate_copy", field: "bank-account");
        await Event("donation_step_view", "amount", "d1");
        await Event("donation_step_complete", "amount", "d1");
        await Event("donation_step_view", "details", "d1");
        await Event("donation_abandon", "details", "d1");
        await Event("application_step_view", "personal", "visit-a");

        var funnel = await Funnel("donation");
        funnel.GetProperty("visits").GetInt32().Should().Be(1, "application events are not in the donation funnel");
        funnel.GetProperty("abandoned").GetInt32().Should().Be(1);
        funnel.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("step").GetString()).Should().Equal("amount", "details", "review");
        funnel.GetProperty("events").GetProperty("donate_copy").GetInt32().Should().Be(1);
        funnel.GetProperty("events").TryGetProperty("application_step_view", out _).Should().BeFalse();
    }

    [Fact]
    public async Task NoPersonalDataIsStoredAndTheIpIsOnlyEverHashed()
    {
        const string ip = "203.0.113.77";
        (await Post(new { name = "donate_view", props = new { }, path = "/donate" }, r => r.Headers.Add("X-Forwarded-For", ip))).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Post("""{"name":"donation_step_view","props":{"step":"amount","run":"abc"},"path":"/donate"}""", r => r.Headers.UserAgent.ParseAdd("Mozilla/5.0 test"))).StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReportingDbContext>();
        var rows = await db.AnalyticsEvents.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        var dump = JsonSerializer.Serialize(rows);
        dump.Should().NotContain(ip).And.NotContain("127.0.0.1").And.NotContain("Mozilla").And.NotContain("@");
        rows.Should().OnlyContain(r => r.Visitor.Length == 32);

        var response = await Post(new { name = "donate_view", path = "/donate" });
        response.Headers.Contains("Set-Cookie").Should().BeFalse("no cookie, so no third-party cookie either");
    }

    [Theory]
    [InlineData("""{"name":"made_up_event"}""")]
    [InlineData("""{"name":"application_step_view","props":{"step":"personal","email":"a@b.org"}}""")]
    [InlineData("""{"name":"application_step_view","props":{"step":"a@b.org","run":"x"}}""")]
    [InlineData("""{"name":"donate_copy","props":{"field":"Jane Doe"}}""")]
    [InlineData("""{"name":"application_step_view","props":{"step":"amount","run":"x"}}""")]
    [InlineData("""{"name":"application_step_view","props":{"run":"x"}}""")]
    [InlineData("""{"name":"donate_view","props":{"step":"amount"}}""")]
    [InlineData("""["not","an","object"]""")]
    [InlineData("not json")]
    public async Task AnythingThatCouldCarryPersonalDataOrBreakTheContractIsRefused(string body)
    {
        (await Post(body)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ReportingDbContext>().AnalyticsEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DoNotTrackAndGlobalPrivacyControlAreHonouredAndOversizedBodiesRefused()
    {
        (await Post(new { name = "donate_view" }, r => r.Headers.Add("DNT", "1"))).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Post(new { name = "donate_view" }, r => r.Headers.Add("Sec-GPC", "1"))).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Post(new { name = "donate_view", path = new string('a', 5000) })).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ReportingDbContext>().AnalyticsEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task OldEventsArePurgedByTheRetentionWindow()
    {
        await Event("donate_view");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReportingDbContext>();
        db.AnalyticsEvents.Add(new ReportingService.Data.Entities.AnalyticsEventEntity { At = DateTime.UtcNow.AddDays(-800), Name = "donate_view", Visitor = new string('a', 32) });
        await db.SaveChangesAsync();

        (await scope.ServiceProvider.GetRequiredService<AnalyticsStore>().PurgeAsync(CancellationToken.None)).Should().Be(1);
        (await db.AnalyticsEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task FunnelReadingNeedsViewAndValidWindows()
    {
        (await _client.GetAsync($"{Funnels}/application")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var denied = new HttpRequestMessage(HttpMethod.Get, $"{Funnels}/application");
        denied.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue([Permissions.FinanceView]));
        (await _client.SendAsync(denied)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        foreach (var (query, status) in new[] { ("/nothing", HttpStatusCode.NotFound), ("/application?from=soon", HttpStatusCode.BadRequest), ("/application?from=2026-05-02&to=2026-05-01", HttpStatusCode.BadRequest), ("/application?from=2020-01-01&to=2026-01-01", HttpStatusCode.BadRequest) })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Funnels + query);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue([Permissions.ReportingView]));
            (await _client.SendAsync(request)).StatusCode.Should().Be(status, query);
        }
    }

    [Fact]
    public async Task OnlyTheDocumentedEventNamesAreAccepted()
    {
        AnalyticsEvents.All.Should().BeEquivalentTo("application_page_view", "application_started", "registration_submit_success", "application_step_view", "application_step_complete", "application_abandon",
            "donate_view", "donate_copy", "donation_step_view", "donation_step_complete", "donation_abandon");
        foreach (var name in AnalyticsEvents.All.Where(n => !n.Contains("step", StringComparison.Ordinal) && !n.Contains("abandon", StringComparison.Ordinal)))
        {
            await Event(name);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
