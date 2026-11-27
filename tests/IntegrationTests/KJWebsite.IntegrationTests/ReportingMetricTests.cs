using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.Extensions.DependencyInjection;
using ReportingService.Data;
using ReportingService.Data.Entities;

namespace KJWebsite.IntegrationTests;

public sealed class ReportingMetricTests : IDisposable
{
    private const string Route = "/api/v1/admin/reporting/metrics";
    private readonly ReportingApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly DateOnly _thisMonth = new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

    public ReportingMetricTests()
    {
        _client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReportingDbContext>();
        foreach (var (period, project, minutes, attendance) in new[]
        {
            (_thisMonth.AddMonths(-2), "school-visits", 120m, 30m), (_thisMonth.AddMonths(-2), "reading-club", 60m, 10m),
            (_thisMonth.AddMonths(-1), "school-visits", 240m, 45m), (_thisMonth, "school-visits", 180m, 20m)
        })
        {
            db.AggregatePoints.Add(new AggregatePointEntity { Source = "membership", Metric = "volunteer.minutes.approved", Period = period, Dimension = project, Value = minutes });
            db.AggregatePoints.Add(new AggregatePointEntity { Source = "membership", Metric = "sessions.attendance", Period = period, Dimension = project, Value = attendance });
        }

        db.AggregatePoints.Add(new AggregatePointEntity { Source = "membership", Metric = "students.enrolled.snapshot", Period = _thisMonth, Dimension = "school-visits", Value = 80 });
        db.AggregatePoints.Add(new AggregatePointEntity { Source = "membership", Metric = "students.enrolled.snapshot", Period = _thisMonth, Dimension = "reading-club", Value = 25 });
        db.SaveChanges();
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, string[] permissions, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(permissions));
        return request;
    }

    private async Task<JsonElement> Get(string url)
    {
        var response = await _client.SendAsync(Req(HttpMethod.Get, url, [Permissions.ReportingView]));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> Post(string url, object body) => _client.SendAsync(Req(HttpMethod.Post, url, [Permissions.ReportingManage], body));

    private static string Period(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task TheSeededDefinitionsCarryTheirMethodologyAndSourceMetric()
    {
        var list = await Get(Route);
        var items = list.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("key").GetString()!);
        items.Keys.Should().Contain(["students-reached", "student-attendances", "volunteer-hours", "sessions-delivered", "institutions-engaged"]);
        items["students-reached"].GetProperty("methodology").GetString().Should().Contain("counted once");
        items["student-attendances"].GetProperty("methodology").GetString().Should().Contain("counts ten times");
        items["volunteer-hours"].GetProperty("methodology").GetString().Should().Contain("approved");
        items["volunteer-hours"].GetProperty("metric").GetString().Should().Be("volunteer.minutes.approved");
    }

    [Fact]
    public async Task SeriesAreAvailableForTheWholeOrganisationAndPerProjectWithDefinitionAndTrace()
    {
        var all = await Get($"{Route}/volunteer-hours/series");
        all.GetProperty("points").EnumerateArray().Select(p => (p.GetProperty("period").GetString(), p.GetProperty("value").GetString())).Should()
            .Equal((Period(_thisMonth.AddMonths(-2)), "3.00"), (Period(_thisMonth.AddMonths(-1)), "4.00"), (Period(_thisMonth), "3.00"));
        all.GetProperty("total").GetString().Should().Be("10.00");
        all.GetProperty("definition").GetProperty("methodology").GetString().Should().NotBeNullOrWhiteSpace();
        all.GetProperty("trace").GetProperty("metric").GetString().Should().Be("volunteer.minutes.approved");
        all.GetProperty("asOf").ValueKind.Should().Be(JsonValueKind.Null, "nothing has been refreshed in this test, and the figure says so");
        all.GetProperty("stale").GetBoolean().Should().BeTrue();

        var project = await Get($"{Route}/volunteer-hours/series?project=reading-club");
        project.GetProperty("points").GetArrayLength().Should().Be(1);
        project.GetProperty("points")[0].GetProperty("value").GetString().Should().Be("1.00");

        var snapshot = await Get($"{Route}/students-reached/series");
        snapshot.GetProperty("points")[0].GetProperty("value").GetString().Should().Be("105.00");
        snapshot.TryGetProperty("total", out var total).Should().BeTrue();
        total.ValueKind.Should().Be(JsonValueKind.Null, "a snapshot is never summed over months");
    }

    [Fact]
    public async Task ChangingADefinitionAppliesFromNowAndLeavesPublishedMonthsUnchanged()
    {
        var before = await Get($"{Route}/volunteer-hours/series");

        var changed = await Post($"{Route}/volunteer-hours/versions", new { methodology = "Counts only hours from approved events.", metric = "sessions.attendance", unit = "attendances", scale = 1 });
        changed.StatusCode.Should().Be(HttpStatusCode.Created);

        var after = await Get($"{Route}/volunteer-hours/series");
        var older = after.GetProperty("points").EnumerateArray().Where(p => string.CompareOrdinal(p.GetProperty("period").GetString(), Period(_thisMonth)) < 0).ToList();
        older.Select(p => (p.GetProperty("value").GetString(), p.GetProperty("definitionVersion").GetInt32())).Should()
            .Equal(before.GetProperty("points").EnumerateArray().Take(2).Select(p => (p.GetProperty("value").GetString(), 1)), "earlier months keep version 1");
        var now = after.GetProperty("points").EnumerateArray().Single(p => p.GetProperty("period").GetString() == Period(_thisMonth));
        now.GetProperty("definitionVersion").GetInt32().Should().Be(2);
        now.GetProperty("value").GetString().Should().Be("20.00", "this month is counted under the new definition");
        after.GetProperty("versions").GetArrayLength().Should().Be(2);
        after.GetProperty("definition").GetProperty("version").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ANewVersionCannotBeBackdatedAndMustRestateItsMethodology()
    {
        var backdated = await Post($"{Route}/volunteer-hours/versions", new { methodology = "x", effectiveFrom = Period(_thisMonth.AddMonths(-3)) });
        backdated.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await backdated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString().Should().Be("HISTORY_LOCKED");

        (await Post($"{Route}/volunteer-hours/versions", new { unit = "hours" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Post($"{Route}/volunteer-hours/versions", new { methodology = "x", effectiveFrom = "2026-03-15" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Post($"{Route}/nothing-here/versions", new { methodology = "x" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Get($"{Route}/volunteer-hours")).GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ANewMetricIsValidatedAndRefusesADuplicateKey()
    {
        var body = new { key = "events-checked-in", name = "Event check-ins", methodology = "People checked in at an event.", unit = "people", metric = "events.attended", kind = "monthly", scale = 1 };
        (await Post(Route, body)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Post(Route, body)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Post(Route, new { key = "Bad Key", name = "x", methodology = "m", unit = "u", metric = "events.attended", kind = "monthly", scale = 1 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Post(Route, new { key = "unknown-metric", name = "x", methodology = "m", unit = "u", metric = "nope.nothing", kind = "monthly", scale = 1 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Post(Route, new { key = "bad-kind", name = "x", methodology = "m", unit = "u", metric = "events.attended", kind = "weekly", scale = 1 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Post(Route, new { key = "bad-scale", name = "x", methodology = "m", unit = "u", metric = "events.attended", kind = "monthly", scale = 0 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReadingNeedsViewAndWritingNeedsManage()
    {
        (await _client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _client.SendAsync(Req(HttpMethod.Get, Route, [Permissions.FinanceView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.SendAsync(Req(HttpMethod.Post, Route, [Permissions.ReportingView], new { key = "abc" }))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.SendAsync(Req(HttpMethod.Get, $"{Route}/volunteer-hours/series?from=soon", [Permissions.ReportingView]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
