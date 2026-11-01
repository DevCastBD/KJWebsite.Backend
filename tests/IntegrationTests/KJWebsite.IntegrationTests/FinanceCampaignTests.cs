using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceService.Campaigns;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Ledger;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KJWebsite.IntegrationTests;

public sealed class FinanceCampaignTests : IDisposable
{
    private static readonly string Viewer = TestTokens.Issue([Permissions.FinanceView], subject: "usr_viewer");
    private static readonly string Manager = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceCampaignManage], subject: "usr_manager");
    private static readonly string Reverser = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationReverse], subject: "usr_reverser");
    private static readonly string Maker = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationRecord], subject: "usr_maker");
    private static readonly string Checker = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationApprove], subject: "usr_checker");
    private static string Ymd(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _proofs = Path.Combine(Path.GetTempPath(), $"kj-test-proofs-{Guid.NewGuid():N}");

    public FinanceCampaignTests()
    {
        _factory.Settings["Finance:Proofs:Path"] = _proofs;
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_proofs))
        {
            Directory.Delete(_proofs, recursive: true);
        }
    }

    private async Task<T> ScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    private Task<T> DbAsync<T>(Func<FinanceDbContext, Task<T>> action) => ScopeAsync(s => action(s.GetRequiredService<FinanceDbContext>()));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, object? body = null, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await TestControls.SendAsync(_client, request);
    }

    private async Task<JsonElement> GetAsync(string path, string? token = null)
    {
        using var response = await SendAsync(HttpMethod.Get, path, token ?? Viewer);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> CreateAsync(object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/campaigns", Manager, body);
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<(string Id, string Reference)> GiveAsync(string slug, string amount = "1000.00", string currency = "BDT", bool anonymous = false, bool confirm = true, string designationType = "campaign")
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/donations", new
        {
            amount,
            currency,
            frequency = "once",
            designation = new { type = designationType, id = slug },
            donor = new { name = "A Donor", email = "d@example.org" },
            anonymous,
            coverFees = false,
            language = "en"
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = json.GetProperty("id").GetString()!;
        if (confirm)
        {
            (await ScopeAsync(s => s.GetRequiredService<DonationConfirmation>().ConfirmAsync(id, "bkash", null))).IsSuccess.Should().BeTrue();
        }

        return (id, json.GetProperty("reference").GetString()!);
    }

    private void Fresh(string slug) => _factory.Services.GetRequiredService<CampaignProgressCache>().Invalidate(slug);

    private async Task<JsonElement> ProgressAsync(string slug)
    {
        Fresh(slug);
        using var response = await _client.GetAsync($"/api/v1/campaigns/{slug}/progress");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task ACampaignIsCreatedWithTheFrontendBodyAndStartsAsADraftWithNothingRaised()
    {
        var created = await CreateAsync(new { title = "Flood Relief 2026", goal = "500000.00", currency = "BDT", status = "draft", endsOn = (string?)null });

        (created.GetProperty("slug").GetString(), created.GetProperty("title").GetString(), created.GetProperty("goal").GetString(), created.GetProperty("currency").GetString(), created.GetProperty("raised").GetString(), created.GetProperty("status").GetString())
            .Should().Be(("flood-relief-2026", "Flood Relief 2026", "500000.00", "BDT", "0.00", "draft"));
        created.GetProperty("endsOn").ValueKind.Should().Be(JsonValueKind.Null);
        (await CreateAsync(new { title = "Flood Relief 2026", goal = "1.00", currency = "BDT" })).GetProperty("slug").GetString().Should().Be("flood-relief-2026-2");
        (await CreateAsync(new { title = "বন্যা ত্রাণ", titleBn = "বন্যা ত্রাণ", goal = "1000.00", currency = "BDT" })).GetProperty("slug").GetString().Should().StartWith("campaign-");
        (await CreateAsync(new { title = "Named", slug = "my-slug", goal = "5.00", currency = "USD", status = "active" })).GetProperty("slug").GetString().Should().Be("my-slug");
        (await CreateAsync(new { title = "Other", slug = "my-slug", goal = "5.00", currency = "USD" }, HttpStatusCode.Conflict)).GetProperty("error").GetProperty("code").GetString().Should().Be("SLUG_EXISTS");
        (await GetAsync("/api/v1/admin/finance/campaigns")).GetProperty("total").GetInt32().Should().Be(4);
        (await GetAsync("/api/v1/admin/finance/campaigns?status=active")).GetProperty("total").GetInt32().Should().Be(1);
    }

    [Theory]
    [InlineData("no-title")]
    [InlineData("long-title")]
    [InlineData("zero-goal")]
    [InlineData("negative-goal")]
    [InlineData("third-decimal")]
    [InlineData("bad-currency")]
    [InlineData("bad-status")]
    [InlineData("bad-start")]
    [InlineData("bad-end")]
    [InlineData("end-before-start")]
    [InlineData("bad-slug")]
    public async Task InvalidCampaignsAreRefused(string problem)
    {
        object body = problem switch
        {
            "no-title" => new { title = " ", goal = "10.00", currency = "BDT" },
            "long-title" => new { title = new string('t', 201), goal = "10.00", currency = "BDT" },
            "zero-goal" => new { title = "T", goal = "0.00", currency = "BDT" },
            "negative-goal" => new { title = "T", goal = "-5.00", currency = "BDT" },
            "third-decimal" => new { title = "T", goal = "10.005", currency = "BDT" },
            "bad-currency" => new { title = "T", goal = "10.00", currency = "XYZ" },
            "bad-status" => new { title = "T", goal = "10.00", currency = "BDT", status = "paused" },
            "bad-start" => new { title = "T", goal = "10.00", currency = "BDT", startsOn = "tomorrow" },
            "bad-end" => new { title = "T", goal = "10.00", currency = "BDT", endsOn = "31/12/2026" },
            "end-before-start" => new { title = "T", goal = "10.00", currency = "BDT", startsOn = "2026-12-01", endsOn = "2026-11-01" },
            _ => new { title = "T", goal = "10.00", currency = "BDT", slug = "Not A Slug" }
        };

        using var response = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/campaigns", Manager, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await DbAsync(db => db.Campaigns.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task EditingChangesTheFieldsButNeverTheSlugAndTheCurrencyLocksOnceGiftsExist()
    {
        var created = await CreateAsync(new { title = "Winter", goal = "100.00", currency = "BDT", status = "active" });
        var id = created.GetProperty("id").GetString()!;

        using var edited = await SendAsync(HttpMethod.Put, $"/api/v1/admin/finance/campaigns/{id}", Manager, new { title = "Winter Appeal", titleBn = "শীত", goal = "250.00", currency = "USD", status = "ended", endsOn = "2030-01-31" });
        edited.StatusCode.Should().Be(HttpStatusCode.OK, await edited.Content.ReadAsStringAsync());
        var row = await edited.Content.ReadFromJsonAsync<JsonElement>();
        (row.GetProperty("title").GetString(), row.GetProperty("goal").GetString(), row.GetProperty("currency").GetString(), row.GetProperty("status").GetString(), row.GetProperty("endsOn").GetString(), row.GetProperty("slug").GetString())
            .Should().Be(("Winter Appeal", "250.00", "USD", "ended", "2030-01-31", "winter"), "the currency may change while nothing is designated to the campaign");

        (await SendAsync(HttpMethod.Put, $"/api/v1/admin/finance/campaigns/{id}", Manager, new { title = "x", slug = "renamed", goal = "1.00", currency = "USD" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await SendAsync(HttpMethod.Put, $"/api/v1/admin/finance/campaigns/{id}", Manager, new { title = "Winter Appeal", goal = "250.00", currency = "USD", status = "active" });
        await GiveAsync("winter", "10.00", "USD");
        using var locked = await SendAsync(HttpMethod.Put, $"/api/v1/admin/finance/campaigns/{id}", Manager, new { title = "Winter Appeal", goal = "250.00", currency = "BDT" });
        locked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await locked.Content.ReadAsStringAsync()).Should().Contain("CURRENCY_LOCKED");
        (await SendAsync(HttpMethod.Put, $"/api/v1/admin/finance/campaigns/{id}", Manager, new { title = "", goal = "1.00", currency = "USD" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Put, "/api/v1/admin/finance/campaigns/cmp_missing", Manager, new { title = "x", goal = "1.00", currency = "USD" })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var audit = await GetAsync("/api/v1/admin/finance/audit?limit=100", TestTokens.Issue([Permissions.AuditView]));
        audit.GetProperty("items").EnumerateArray().Select(e => (e.GetProperty("entity").GetString(), e.GetProperty("actorId").GetString())).Should().Contain(("Campaign", "usr_manager"));
    }

    [Fact]
    public async Task OnlyManagersWriteAndFinanceStaffRead()
    {
        var body = new { title = "T", goal = "10.00", currency = "BDT" };
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/campaigns", null, body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/campaigns", Viewer, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/campaigns", TestTokens.Issue([Permissions.ContentView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/campaigns", Viewer)).StatusCode.Should().Be(HttpStatusCode.OK);
        KjRoles.Seed[KjRoles.FinanceOfficer].Should().Contain(Permissions.FinanceCampaignManage);
        KjRoles.Seed[KjRoles.Admin].Should().Contain(Permissions.FinanceCampaignManage);
    }

    [Fact]
    public async Task TheProgressEndpointHasTheFrontendShapeAndIsPublicOnlyForNonDraftCampaigns()
    {
        await CreateAsync(new { title = "Draft One", goal = "1000.00", currency = "BDT" });
        await CreateAsync(new { title = "Live One", goal = "500000.00", currency = "BDT", status = "active" });

        (await _client.GetAsync("/api/v1/campaigns/draft-one/progress")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/api/v1/campaigns/nope/progress")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/api/v1/campaigns/Not%20A%20Slug/progress")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var response = await _client.GetAsync("/api/v1/campaigns/live-one/progress");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        (body.GetProperty("raised").GetString(), body.GetProperty("goal").GetString(), body.GetProperty("currency").GetString(), body.GetProperty("cacheSeconds").GetInt32()).Should().Be(("0.00", "500000.00", "BDT", 30));
        DateTimeOffset.TryParse(body.GetProperty("asOf").GetString(), CultureInfo.InvariantCulture, out _).Should().BeTrue();
        response.Headers.CacheControl!.Public.Should().BeTrue();
        response.Headers.CacheControl.MaxAge!.Value.TotalSeconds.Should().BeInRange(1, 30);
        response.Headers.ETag.Should().NotBeNull();
        // A draft that is later published becomes visible; one taken back to draft disappears.
        (await SendAsync(HttpMethod.Put, "/api/v1/admin/finance/campaigns/" + (await GetAsync("/api/v1/admin/finance/campaigns")).GetProperty("items").EnumerateArray().First(i => i.GetProperty("slug").GetString() == "draft-one").GetProperty("id").GetString(), Manager, new { title = "Draft One", goal = "1000.00", currency = "BDT", status = "active" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync("/api/v1/campaigns/draft-one/progress")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ConditionalRequestsGetA304WhileTheFiguresStandStill()
    {
        await CreateAsync(new { title = "Live", goal = "1000.00", currency = "BDT", status = "active" });
        using var first = await _client.GetAsync("/api/v1/campaigns/live/progress");
        var etag = first.Headers.ETag!.Tag;

        using var same = await SendAsync(HttpMethod.Get, "/api/v1/campaigns/live/progress", null, ifNoneMatch: etag);
        same.StatusCode.Should().Be(HttpStatusCode.NotModified);

        await GiveAsync("live", "100.00");
        Fresh("live");
        using var changed = await SendAsync(HttpMethod.Get, "/api/v1/campaigns/live/progress", null, ifNoneMatch: etag);
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        changed.Headers.ETag!.Tag.Should().NotBe(etag);
    }

    [Fact]
    public async Task ProgressCountsOnlyConfirmedUnreversedGiftsInTheGoalCurrencyAndNeverConvertsTheRest()
    {
        await CreateAsync(new { title = "Relief", goal = "500000.00", currency = "BDT", status = "active" });
        await CreateAsync(new { title = "Elsewhere", goal = "500000.00", currency = "BDT", status = "active" });
        await GiveAsync("relief", "1000.00");
        await GiveAsync("relief", "500.50", anonymous: true);
        await GiveAsync("relief", "300.00", confirm: false);
        var (reversed, _) = await GiveAsync("relief", "777.00");
        await GiveAsync("relief", "25.00", "USD");
        await GiveAsync("relief", "10.00", "GBP");
        await GiveAsync("elsewhere", "9999.00");
        await GiveAsync("relief", "400.00", designationType: "project");
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{reversed}/reversal", Reverser, new { reason = "Chargeback" });
        using var kind = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/donations", Maker, new
        {
            donorName = "Desks",
            amount = "5000.00",
            currency = "BDT",
            method = "in_kind",
            designation = "campaign:relief",
            valuation = new { basis = "appraisal", itemDescription = "Desks" }
        });
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{(await kind.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()}/approve", Checker, new { });

        var progress = await ProgressAsync("relief");

        progress.GetProperty("raised").GetString().Should().Be("1500.50", "confirmed, un-reversed BDT money only: pending, reversed, in-kind and project gifts are out; anonymous counts");
        progress.GetProperty("gifts").GetInt32().Should().Be(4, "1000, 500.50, 25 USD, 10 GBP");
        progress.GetProperty("otherCurrencies").EnumerateArray().Select(o => (o.GetProperty("currency").GetString(), o.GetProperty("raised").GetString())).Should().Equal(("GBP", "10.00"), ("USD", "25.00"));
        var row = (await GetAsync("/api/v1/admin/finance/campaigns")).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("slug").GetString() == "relief");
        (row.GetProperty("raised").GetString(), row.GetProperty("inKind").GetString()).Should().Be(("1500.50", "5000.00"));
        (await ProgressAsync("elsewhere")).GetProperty("raised").GetString().Should().Be("9999.00");
    }

    [Fact]
    public async Task ReconciliationAgreesWithTheLedgerAndShowsDriftWhenTheBooksAreTouched()
    {
        await CreateAsync(new { title = "Relief", goal = "500000.00", currency = "BDT", status = "active" });
        await GiveAsync("relief", "1000.00");
        var (reversed, _) = await GiveAsync("relief", "600.00");
        await GiveAsync("relief", "25.00", "USD");
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{reversed}/reversal", Reverser, new { reason = "Chargeback" });
        await ProgressAsync("relief");

        var ok = await GetAsync("/api/v1/admin/finance/campaigns/reconciliation");

        ok.GetProperty("reconciled").GetBoolean().Should().BeTrue(ok.GetRawText());
        var bdt = ok.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("currency").GetString() == "BDT");
        (bdt.GetProperty("receipts").GetString(), bdt.GetProperty("ledger").GetString(), bdt.GetProperty("cached").GetString()).Should().Be(("1000.00", "1000.00", "1000.00"));

        await DbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE ledger_entries SET Credit = 990.00 WHERE Credit = 1000.00");
            return 0;
        });
        var bad = await GetAsync("/api/v1/admin/finance/campaigns/reconciliation");
        bad.GetProperty("reconciled").GetBoolean().Should().BeFalse();
        bad.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("currency").GetString() == "BDT").GetProperty("difference").GetString().Should().Be("10.00");
    }

    [Fact]
    public async Task OnlyAnOpenCampaignTakesPublicGiftsAndARecordedGiftNeedsOneThatExistsAndIsNotADraft()
    {
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        await TestCampaigns.SeedAsync(_factory.Services, "open");
        await TestCampaigns.SeedAsync(_factory.Services, "draft", status: "draft");
        await TestCampaigns.SeedAsync(_factory.Services, "ended", status: "ended");
        await TestCampaigns.SeedAsync(_factory.Services, "expired", endsOn: yesterday);
        await TestCampaigns.SeedAsync(_factory.Services, "future", startsOn: tomorrow);
        await TestCampaigns.SeedAsync(_factory.Services, "last-day", endsOn: DateOnly.FromDateTime(DateTime.UtcNow));

        foreach (var slug in new[] { "draft", "ended", "expired", "future", "nope" })
        {
            using var refused = await _client.PostAsJsonAsync("/api/v1/donations", new
            {
                amount = "100.00",
                currency = "BDT",
                frequency = "once",
                designation = new { type = "campaign", id = slug },
                donor = new { name = "A", email = "a@example.org" },
                anonymous = false,
                coverFees = false,
                language = "en"
            });
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, slug);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("designation.id");
        }

        await GiveAsync("open", confirm: false);
        await GiveAsync("last-day", confirm: false);

        foreach (var (slug, expected) in new[] { ("ended", HttpStatusCode.Created), ("open", HttpStatusCode.Created), ("draft", HttpStatusCode.BadRequest), ("nope", HttpStatusCode.BadRequest) })
        {
            using var recorded = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/donations", Maker, new { donorName = "A", amount = "100.00", currency = "BDT", method = "cash", designation = $"campaign:{slug}" });
            recorded.StatusCode.Should().Be(expected, slug);
        }
    }

    [Fact]
    public async Task ProjectDesignationsAreStillOpaqueSlugsForTheContentService()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/donations", new
        {
            amount = "100.00",
            currency = "BDT",
            frequency = "once",
            designation = new { type = "project", id = "any-project" },
            donor = new { name = "A", email = "a@example.org" },
            anonymous = false,
            coverFees = false,
            language = "en"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task AGapOfTrafficCostsOneComputationPerIntervalAndSpikesShareIt()
    {
        await CreateAsync(new { title = "Live", goal = "1000.00", currency = "BDT", status = "active" });
        await GiveAsync("live", "100.00");
        var counting = new CountingScopeFactory(_factory.Services.GetRequiredService<IServiceScopeFactory>());
        var clock = new ManualClock();
        var cache = new CampaignProgressCache(counting, Options.Create(new CampaignOptions { ProgressCacheSeconds = 30 }), clock);

        var spike = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => cache.GetAsync("live", default)));

        counting.Scopes.Should().Be(1, "two hundred simultaneous requests share one computation");
        spike.Select(r => r.View!.Raised).Should().OnlyContain(r => r == "100.00");

        await GiveAsync("live", "50.00");
        clock.Advance(TimeSpan.FromSeconds(10));
        (await cache.GetAsync("live", default)).View!.Raised.Should().Be("100.00", "inside the interval the cached figure is served");
        counting.Scopes.Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(25));
        (await cache.GetAsync("live", default)).View!.Raised.Should().Be("150.00", "after the interval it is recomputed, once");
        counting.Scopes.Should().Be(2);
        cache.PeekRaised("live").Should().Be("150.00");
        cache.PeekRaised("unknown").Should().BeNull();
    }

    [Fact]
    public async Task IfARefreshFailsTheLastFigureIsServedForAWhileAndThenTheCallFailsRatherThanLie()
    {
        await CreateAsync(new { title = "Live", goal = "1000.00", currency = "BDT", status = "active" });
        await GiveAsync("live", "100.00");
        var scopes = new CountingScopeFactory(_factory.Services.GetRequiredService<IServiceScopeFactory>());
        var clock = new ManualClock();
        var cache = new CampaignProgressCache(scopes, Options.Create(new CampaignOptions { ProgressCacheSeconds = 30, StaleMultiple = 10 }), clock);
        (await cache.GetAsync("live", default)).View!.Raised.Should().Be("100.00");

        scopes.Fail = true;
        clock.Advance(TimeSpan.FromSeconds(60));
        (await cache.GetAsync("live", default)).View!.Raised.Should().Be("100.00", "stale but recent enough");
        clock.Advance(TimeSpan.FromSeconds(400));
        await ((Func<Task>)(() => cache.GetAsync("live", default))).Should().ThrowAsync<InvalidOperationException>();

        scopes.Fail = false;
        (await cache.GetAsync("live", default)).View!.Raised.Should().Be("100.00", "it recovers on the next successful refresh");
    }

    [Fact]
    public async Task WhenNoTrustworthyFigureExistsTheEndpointFailsFastWithA503()
    {
        await CreateAsync(new { title = "Live", goal = "1000.00", currency = "BDT", status = "active" });
        Fresh("live");
        await DbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE receipt_voids");
            return 0;
        });

        using var response = await _client.GetAsync("/api/v1/campaigns/live/progress");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.Should().NotBeNull();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private int _scopes;

        public int Scopes => _scopes;

        public bool Fail { get; set; }

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _scopes);
            return Fail ? throw new InvalidOperationException("The database is unavailable.") : inner.CreateScope();
        }
    }
}
