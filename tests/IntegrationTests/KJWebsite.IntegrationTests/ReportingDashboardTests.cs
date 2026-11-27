using System.Diagnostics;
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

public sealed class ReportingDashboardTests : IDisposable
{
    private const string Route = "/api/v1/admin/reporting/dashboards";
    private readonly ReportingApiFactory _factory = new();
    private readonly HttpClient _client;

    public ReportingDashboardTests()
    {
        _client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReportingDbContext>();
        var now = DateTime.UtcNow;
        var thisMonth = new DateOnly(now.Year, now.Month, 1);
        // Three years of history across every metric the dashboards read, with several dimensions each.
        string[] dimensions = ["a", "b", "c", "d", "e"];
        (string Source, string Metric, string Currency)[] metrics =
        [
            ("finance", "donations.confirmed.amount", "BDT"), ("finance", "donations.confirmed.count", "BDT"), ("finance", "expenses.ledger.amount", "BDT"), ("finance", "budget.planned.amount", "BDT"),
            ("membership", "applications.received", ""), ("membership", "members.joined", ""), ("membership", "volunteer.minutes.approved", ""), ("membership", "sessions.delivered", ""),
            ("content", "events.registrations", ""), ("content", "events.attended", ""), ("content", "content.published", ""), ("content", "content.pipeline.snapshot", ""),
            ("cta", "inbox.received", ""), ("cta", "inbox.first_responded", ""), ("cta", "inbox.closed", ""), ("cta", "inbox.open.snapshot", "")
        ];
        for (var m = 0; m < 36; m++)
        {
            var period = thisMonth.AddMonths(-m);
            foreach (var (source, metric, currency) in metrics)
            {
                foreach (var dimension in dimensions)
                {
                    db.AggregatePoints.Add(new AggregatePointEntity { Source = source, Metric = metric, Period = period, Dimension = dimension, Currency = currency, Value = 10 + m });
                }
            }
        }

        db.AggregateSources.AddRange(AggregateSourceCatalogNames().Select(s => new AggregateSourceEntity { Source = s, AsOf = DateTimeOffset.UtcNow, RunId = "run_seed" }));
        db.SaveChanges();
    }

    private static IEnumerable<string> AggregateSourceCatalogNames() => ["finance", "membership", "cta", "content"];

    private static HttpRequestMessage Req(string url, params string[] permissions)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(permissions));
        return request;
    }

    private async Task<(JsonElement Json, string Raw)> Get(string url, params string[] permissions)
    {
        var response = await _client.SendAsync(Req(url, permissions));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync();
        return (JsonDocument.Parse(raw).RootElement, raw);
    }

    [Fact]
    public async Task AContentEditorsDashboardCarriesNoFinancialFiguresAtAll()
    {
        var (json, raw) = await Get(Route, Permissions.DashboardView, Permissions.ContentView);
        json.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("id").GetString()).Should().Equal("content");
        raw.Should().NotContain("donation", "money must not be in the response for the UI to hide").And.NotContain("expenses").And.NotContain("budget");
    }

    [Fact]
    public async Task EachRoleGetsExactlyItsSectionsAndTheFinanceOfficerSeesBothMoneySections()
    {
        var (finance, _) = await Get(Route, Permissions.DashboardView, Permissions.FinanceView);
        finance.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("id").GetString()).Should().Equal("donations", "expenses");

        var (all, _) = await Get(Route, Permissions.DashboardView, Permissions.FinanceView, Permissions.MembershipManage, Permissions.EventsManage, Permissions.ContentView, Permissions.CtaView, Permissions.ProgrammeManage);
        all.GetProperty("sections").GetArrayLength().Should().Be(7);

        var (none, _) = await Get(Route, Permissions.DashboardView);
        none.GetProperty("sections").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task AFigureNamesTheMetricAndTheCallThatReturnsItsRows()
    {
        var (json, _) = await Get($"{Route}/donations", Permissions.DashboardView, Permissions.FinanceView);
        var section = json.GetProperty("sections")[0];
        var series = section.GetProperty("series")[0];
        series.GetProperty("points").GetArrayLength().Should().Be(12, "a year of months with no gaps");
        series.GetProperty("trace").GetProperty("metric").GetString().Should().Be("donations.confirmed.amount");
        series.GetProperty("trace").GetProperty("drilldown").GetString().Should().Contain("/api/v1/admin/reporting/aggregates?metric=donations.confirmed.amount");
        section.GetProperty("figures").EnumerateArray().Should().OnlyContain(f => f.GetProperty("trace").GetProperty("metric").GetString()!.StartsWith("donations."));
        json.GetProperty("stale").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ASectionNeedsItsOwnPermissionAndAnUnknownOneIsNotFound()
    {
        (await _client.SendAsync(Req($"{Route}/donations", Permissions.DashboardView, Permissions.ContentView))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.SendAsync(Req($"{Route}/nothing", Permissions.DashboardView))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.SendAsync(Req(Route, Permissions.ContentView))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.SendAsync(Req($"{Route}?months=99", Permissions.DashboardView))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheFullExecutiveDashboardLoadsInUnderTwoSecondsOverThreeYears()
    {
        string[] everything = [Permissions.DashboardView, Permissions.FinanceView, Permissions.MembershipManage, Permissions.EventsManage, Permissions.ContentView, Permissions.CtaView, Permissions.ProgrammeManage];
        (await _client.SendAsync(Req($"{Route}?months=36", everything))).EnsureSuccessStatusCode(); // warm up the host
        var watch = Stopwatch.StartNew();
        var response = await _client.SendAsync(Req($"{Route}?months=36", everything));
        watch.Stop();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ABudgetBurnIsPlannedAgainstSpentForTheFiscalYear()
    {
        var (json, _) = await Get($"{Route}/expenses", Permissions.DashboardView, Permissions.FinanceView);
        var figures = json.GetProperty("sections")[0].GetProperty("figures").EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!, f => f.GetProperty("value").GetString()!);
        figures.Should().ContainKeys("planned.BDT", "spent.BDT", "burn.BDT");
        decimal.Parse(figures["burn.BDT"], System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterThan(0);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
