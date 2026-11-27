using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Aggregates;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReportingService.Aggregates;
using ReportingService.Data;

namespace KJWebsite.IntegrationTests;

/// <summary>Stands in for the four services' feeds, keyed by the port each runs on in development.</summary>
public sealed class FeedHandler : HttpMessageHandler
{
    public Dictionary<int, Func<HttpResponseMessage>> Feeds { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.GetValues(AggregateFeedExtensions.KeyHeader).Should().ContainSingle().Which.Should().Be("feed-key");
        request.RequestUri!.AbsolutePath.Should().Be(AggregateFeedExtensions.Route);
        return Task.FromResult(Feeds[request.RequestUri.Port]());
    }

    public static HttpResponseMessage Ok(AggregateFeedResponse feed) => new(HttpStatusCode.OK) { Content = JsonContent.Create(feed) };
}

public sealed class ReportingApiFactory : SqliteWebApplicationFactory<ReportingService.ReportingServiceTestEntryPoint, ReportingDbContext>
{
    public FeedHandler Handler { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddHttpClient(AggregateRefresher.ClientName).ConfigurePrimaryHttpMessageHandler(() => Handler));
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary),
                ["Reporting:Aggregates:WorkerEnabled"] = "false",
                ["Reporting:Aggregates:Attempts"] = "1",
                ["Internal:Key"] = "feed-key"
            }));
    }
}

public sealed class ReportingAggregateTests : IDisposable
{
    private const string Route = "/api/v1/admin/reporting/aggregates";
    private static readonly DateOnly Jan = new(2026, 1, 1);
    private static readonly DateOnly Feb = new(2026, 2, 1);
    private readonly ReportingApiFactory _factory = new();
    private readonly HttpClient _client;

    public ReportingAggregateTests()
    {
        _client = _factory.CreateClient();
        Healthy(donations: 1500m, ledger: 1500m);
    }

    private void Healthy(decimal donations, decimal ledger)
    {
        var finance = new AggregateFeedResponse("finance", DateTimeOffset.UtcNow,
            [new("donations.confirmed.amount", Jan, "general", donations - 500m, "BDT"), new("donations.confirmed.amount", Feb, "project", 500m, "BDT"), new("donations.confirmed.count", Jan, "general", 3, "BDT")],
            [new("ledger.donations.net", ledger, "BDT")]);
        _factory.Handler.Feeds[7005] = () => FeedHandler.Ok(finance);
        _factory.Handler.Feeds[7004] = () => FeedHandler.Ok(new("membership", DateTimeOffset.UtcNow, [new("volunteer.minutes.approved", Jan, "school-visits", 120m)], []));
        _factory.Handler.Feeds[7002] = () => FeedHandler.Ok(new("cta", DateTimeOffset.UtcNow, [new("inbox.received", Jan, "media", 4m)], []));
        _factory.Handler.Feeds[7001] = () => FeedHandler.Ok(new("content", DateTimeOffset.UtcNow, [new("content.published", Jan, "news", 2m)], []));
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, string[] permissions)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(permissions));
        return request;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task ARefreshStoresEveryServicesFiguresWithAnAsOfDateAndChecksThemAgainstTheLedger()
    {
        var refresh = await _client.SendAsync(Req(HttpMethod.Post, $"{Route}/refresh", [Permissions.ReportingManage]));
        refresh.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await Json(refresh);
        run.GetProperty("status").GetString().Should().Be("succeeded");
        run.GetProperty("reconciliation")[0].GetProperty("agrees").GetBoolean().Should().BeTrue();

        var series = await Json(await _client.SendAsync(Req(HttpMethod.Get, $"{Route}?metric=donations.confirmed.amount", [Permissions.ReportingView])));
        series.GetProperty("stale").GetBoolean().Should().BeFalse();
        series.GetProperty("asOf").ValueKind.Should().Be(JsonValueKind.String);
        series.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("period").GetString(), i.GetProperty("dimension").GetString(), i.GetProperty("value").GetString(), i.GetProperty("currency").GetString()))
            .Should().Equal(("2026-01-01", "general", "1000.00", "BDT"), ("2026-02-01", "project", "500.00", "BDT"));

        var narrowed = await Json(await _client.SendAsync(Req(HttpMethod.Get, $"{Route}?metric=donations.confirmed.amount&from=2026-02-01&dimension=project", [Permissions.ReportingView])));
        narrowed.GetProperty("items").GetArrayLength().Should().Be(1);

        var status = await Json(await _client.SendAsync(Req(HttpMethod.Get, $"{Route}/status", [Permissions.ReportingView])));
        status.GetProperty("alert").GetBoolean().Should().BeFalse();
        status.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("source").GetString()).Should().BeEquivalentTo("finance", "membership", "cta", "content");
    }

    [Fact]
    public async Task AFigureThatDisagreesWithTheLedgerIsNeverPublishedAndTheOldFiguresAreReportedStale()
    {
        (await _client.SendAsync(Req(HttpMethod.Post, $"{Route}/refresh", [Permissions.ReportingManage]))).StatusCode.Should().Be(HttpStatusCode.OK);

        Healthy(donations: 9000m, ledger: 1500m);
        var failed = await _client.SendAsync(Req(HttpMethod.Post, $"{Route}/refresh", [Permissions.ReportingManage]));
        failed.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var run = await Json(failed);
        run.GetProperty("status").GetString().Should().Be("failed");
        run.GetProperty("error").GetString().Should().Contain("do not agree with the ledger");

        var series = await Json(await _client.SendAsync(Req(HttpMethod.Get, $"{Route}?metric=donations.confirmed.amount", [Permissions.ReportingView])));
        series.GetProperty("stale").GetBoolean().Should().BeTrue("yesterday's numbers must not pass as today's");
        series.GetProperty("items").EnumerateArray().Sum(i => decimal.Parse(i.GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).Should().Be(1500m, "the last good figures stay");

        var status = await Json(await _client.SendAsync(Req(HttpMethod.Get, $"{Route}/status", [Permissions.ReportingView])));
        status.GetProperty("alert").GetBoolean().Should().BeTrue();
        status.GetProperty("lastSuccess").GetProperty("status").GetString().Should().Be("succeeded");

        Healthy(donations: 1500m, ledger: 1500m);
        (await _client.SendAsync(Req(HttpMethod.Post, $"{Route}/refresh", [Permissions.ReportingManage]))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(await _client.SendAsync(Req(HttpMethod.Get, $"{Route}/status", [Permissions.ReportingView])))).GetProperty("alert").GetBoolean().Should().BeFalse("the next good run clears the alert");
    }

    [Fact]
    public async Task AnUnreachableServiceFailsTheWholeRunInsteadOfMixingFreshAndOldFigures()
    {
        _factory.Handler.Feeds[7004] = () => throw new HttpRequestException("connection refused");
        var failed = await _client.SendAsync(Req(HttpMethod.Post, $"{Route}/refresh", [Permissions.ReportingManage]));
        failed.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await Json(failed)).GetProperty("error").GetString().Should().Contain("membership could not be reached");

        var series = await Json(await _client.SendAsync(Req(HttpMethod.Get, $"{Route}?metric=donations.confirmed.amount", [Permissions.ReportingView])));
        series.GetProperty("items").GetArrayLength().Should().Be(0, "nothing was replaced");
        series.GetProperty("stale").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ReadingNeedsViewRefreshingNeedsManageAndTheMetricIsRequired()
    {
        (await _client.GetAsync($"{Route}/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _client.SendAsync(Req(HttpMethod.Post, $"{Route}/refresh", [Permissions.ReportingView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.SendAsync(Req(HttpMethod.Get, Route, [Permissions.FinanceView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.SendAsync(Req(HttpMethod.Get, Route, [Permissions.ReportingView]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.SendAsync(Req(HttpMethod.Get, $"{Route}?metric=x&from=yesterday", [Permissions.ReportingView]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void ReconciliationComparesEachCurrencySeparatelyAndToleratesRoundingOnly()
    {
        var feed = new AggregateFeedResponse("finance", DateTimeOffset.UtcNow,
            [new("donations.confirmed.amount", Jan, "general", 100.004m, "BDT"), new("donations.confirmed.amount", Jan, "general", 50m, "USD")],
            [new("ledger.donations.net", 100m, "BDT"), new("ledger.donations.net", 49m, "USD")]);
        var lines = AggregateRefresher.Reconcile([feed]);
        lines.Single(l => l.Currency == "BDT").Agrees.Should().BeTrue();
        lines.Single(l => l.Currency == "USD").Agrees.Should().BeFalse();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}

public sealed class AggregateFeedTests : IDisposable
{
    private readonly FinanceApiFactory _factory = new();

    public AggregateFeedTests() => _factory.Settings["Internal:Key"] = "feed-key";

    [Fact]
    public async Task TheFeedIsClosedWithoutTheSharedKeyAndNeverListedInTheContract()
    {
        using var client = _factory.CreateClient();
        (await client.GetAsync(AggregateFeedExtensions.Route)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var wrong = new HttpRequestMessage(HttpMethod.Get, AggregateFeedExtensions.Route);
        wrong.Headers.Add(AggregateFeedExtensions.KeyHeader, "not-the-key");
        (await client.SendAsync(wrong)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var good = new HttpRequestMessage(HttpMethod.Get, AggregateFeedExtensions.Route);
        good.Headers.Add(AggregateFeedExtensions.KeyHeader, "feed-key");
        var response = await client.SendAsync(good);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var feed = await response.Content.ReadFromJsonAsync<AggregateFeedResponse>();
        feed!.Source.Should().Be("finance");

        (await client.GetStringAsync("/openapi/v1.json")).Should().NotContain("/internal/v1/aggregates");
    }

    public void Dispose() => _factory.Dispose();
}
