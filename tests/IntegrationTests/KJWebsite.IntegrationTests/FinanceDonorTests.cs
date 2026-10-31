using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donors;
using FinanceService.Ledger;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class DonorSegmentTests
{
    private static readonly DonorOptions Options = new();
    private static readonly DateOnly AsOf = new(2026, 10, 31);

    private static GiftFact Gift(DateOnly on, decimal amount = 100m, string currency = "BDT", bool recurring = false, bool inKind = false) => new(on, currency, amount, inKind, recurring);

    [Fact]
    public void ADonorWithNoGiftsByTheDateIsInNoSegment()
    {
        DonorSegments.Of([], AsOf, Options).Should().BeEmpty();
        DonorSegments.Of([Gift(AsOf.AddDays(1))], AsOf, Options).Should().BeEmpty("gifts after the date do not exist yet");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(89, true)]
    [InlineData(90, true)]
    [InlineData(91, false)]
    public void NewMeansTheFirstGiftIsWithinNinetyDays(int daysAgo, bool expected)
    {
        DonorSegments.Of([Gift(AsOf.AddDays(-daysAgo))], AsOf, Options).Contains(Segments.New).Should().Be(expected);
    }

    [Fact]
    public void NewIsAboutTheFirstGiftNotTheLatest()
    {
        DonorSegments.Of([Gift(AsOf.AddDays(-400)), Gift(AsOf.AddDays(-5))], AsOf, Options).Should().NotContain(Segments.New);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(364, false)]
    [InlineData(365, false)]
    [InlineData(366, true)]
    [InlineData(900, true)]
    public void LapsedMeansTheLastGiftIsOlderThanTwelveMonths(int daysAgo, bool expected)
    {
        DonorSegments.Of([Gift(AsOf.AddDays(-daysAgo))], AsOf, Options).Contains(Segments.Lapsed).Should().Be(expected);
    }

    [Fact]
    public void LapsedUsesCalendarMonthsExactly()
    {
        DonorSegments.Of([Gift(AsOf.AddMonths(-12))], AsOf, Options).Should().NotContain(Segments.Lapsed);
        DonorSegments.Of([Gift(AsOf.AddMonths(-12).AddDays(-1))], AsOf, Options).Should().Contain(Segments.Lapsed);
    }

    [Fact]
    public void MajorNeedsOneCurrencyToReachItsOwnThresholdAndCurrenciesAreNeverAdded()
    {
        DonorSegments.Of([Gift(AsOf, 99_999.99m)], AsOf, Options).Should().NotContain(Segments.Major);
        DonorSegments.Of([Gift(AsOf, 60_000m), Gift(AsOf, 40_000m)], AsOf, Options).Should().Contain(Segments.Major);
        DonorSegments.Of([Gift(AsOf, 99_000m), Gift(AsOf, 999m, "USD")], AsOf, Options).Should().NotContain(Segments.Major, "BDT and USD are not added together");
        DonorSegments.Of([Gift(AsOf, 1_000m, "USD")], AsOf, Options).Should().Contain(Segments.Major);
        DonorSegments.Of([Gift(AsOf, 5_000_000m, "JPY")], AsOf, Options).Should().NotContain(Segments.Major, "no threshold for an unknown currency");
    }

    [Fact]
    public void RecurringMeansAnyGiftCameFromARecurringPlan()
    {
        DonorSegments.Of([Gift(AsOf, recurring: true)], AsOf, Options).Should().Contain(Segments.Recurring);
        DonorSegments.Of([Gift(AsOf)], AsOf, Options).Should().NotContain(Segments.Recurring);
    }

    [Fact]
    public void ASegmentCanBeReRunForAPastDateAndGivesTheSameAnswerEveryTime()
    {
        GiftFact[] gifts = [Gift(new DateOnly(2024, 1, 10), 70_000m), Gift(new DateOnly(2025, 8, 1), 40_000m)];

        DonorSegments.Of(gifts, new DateOnly(2024, 2, 1), Options).Should().Equal(Segments.New);
        DonorSegments.Of(gifts, new DateOnly(2024, 6, 1), Options).Should().BeEmpty();
        DonorSegments.Of(gifts, new DateOnly(2025, 8, 5), Options).Should().Equal(Segments.Major);
        DonorSegments.Of(gifts, new DateOnly(2026, 12, 1), Options).Should().Contain(Segments.Lapsed).And.Contain(Segments.Major);
        for (var i = 0; i < 5; i++)
        {
            DonorSegments.Of(gifts, AsOf, Options).Should().Equal(DonorSegments.Of(gifts, AsOf, Options));
        }
    }

    [Fact]
    public void RandomGiftHistoriesNeverBreakTheRules()
    {
        var random = new Random(61);
        for (var i = 0; i < 2_000; i++)
        {
            var gifts = Enumerable.Range(0, random.Next(0, 8)).Select(_ => Gift(AsOf.AddDays(-random.Next(-30, 1_500)), random.Next(1, 200_000), random.Next(3) == 0 ? "USD" : "BDT", random.Next(5) == 0)).ToList();
            var segments = DonorSegments.Of(gifts, AsOf, Options);
            var counted = gifts.Where(g => g.ReceivedOn <= AsOf).ToList();

            segments.Should().OnlyHaveUniqueItems();
            if (counted.Count == 0)
            {
                segments.Should().BeEmpty();
                continue;
            }

            segments.Contains(Segments.Lapsed).Should().Be(counted.Max(g => g.ReceivedOn) < AsOf.AddMonths(-12));
            segments.Contains(Segments.New).Should().Be(counted.Min(g => g.ReceivedOn) >= AsOf.AddDays(-90));
            segments.Contains(Segments.Recurring).Should().Be(counted.Any(g => g.Recurring));
            (segments.Contains(Segments.Lapsed) && segments.Contains(Segments.New)).Should().BeFalse("a donor cannot be both new and lapsed");
        }
    }

    [Fact]
    public void TotalsKeepMoneyAndInKindApartPerCurrency()
    {
        var totals = DonorSegments.Totals([Gift(AsOf, 100m), Gift(AsOf, 50.50m), Gift(AsOf, 1000m, inKind: true), Gift(AsOf, 5m, "USD")]);

        totals.Select(t => (t.Currency, t.Cash.ToDecimalString(), t.InKind.ToDecimalString())).Should().Equal(("BDT", "150.50", "1000.00"), ("USD", "5.00", "0.00"));
    }
}

public sealed class FinanceDonorTests : IDisposable
{
    private static readonly string Viewer = TestTokens.Issue([Permissions.FinanceView], subject: "usr_viewer");
    private static readonly string Manager = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonorManage], subject: "usr_manager");
    private static readonly string Exporter = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonorExport], subject: "usr_exporter");
    private static readonly string Reverser = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationReverse], subject: "usr_reverser");
    private static readonly string Maker = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationRecord], subject: "usr_maker");
    private static readonly string Checker = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationApprove], subject: "usr_checker");

    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _proofs = Path.Combine(Path.GetTempPath(), $"kj-test-proofs-{Guid.NewGuid():N}");

    public FinanceDonorTests()
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

    private async Task<(string Id, string Reference)> ConfirmedAsync(string email = "donor@example.org", string amount = "1000.00", string currency = "BDT", string name = "Rahim Uddin",
        bool anonymous = false, bool? consent = null, string frequency = "once")
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/donations", new
        {
            amount,
            currency,
            frequency,
            designation = new { type = "general" },
            donor = new { name, email },
            anonymous,
            coverFees = false,
            language = "en",
            communicationConsent = consent ?? false
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = json.GetProperty("id").GetString()!;
        (await ScopeAsync(s => s.GetRequiredService<DonationConfirmation>().ConfirmAsync(id, "bkash", null))).IsSuccess.Should().BeTrue();
        return (id, json.GetProperty("reference").GetString()!);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private async Task<JsonElement> GetAsync(string path, string? token = null)
    {
        using var response = await SendAsync(HttpMethod.Get, path, token ?? Viewer);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task OnlyNonAnonymousGiftsWithAnEmailCreateADonor()
    {
        await ConfirmedAsync("anon@example.org", anonymous: true);
        using var walkIn = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/donations", Maker, new { donorName = "Walk-in", amount = "100.00", currency = "BDT", method = "cash" });
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{(await walkIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()}/approve", Checker, new { });
        using var pending = await _client.PostAsJsonAsync("/api/v1/donations", new { amount = "100.00", currency = "BDT", frequency = "once", designation = new { type = "general" }, donor = new { name = "P", email = "pending@example.org" }, anonymous = false, coverFees = false, language = "en" });
        pending.StatusCode.Should().Be(HttpStatusCode.Created);

        (await DbAsync(db => db.Donors.CountAsync())).Should().Be(0, "an anonymous gift, a walk-in with no email and an unconfirmed gift leave no donor record");
        (await GetAsync("/api/v1/admin/finance/donors")).GetProperty("total").GetInt32().Should().Be(0);

        await ConfirmedAsync("named@example.org", name: "Named Donor");
        (await DbAsync(db => db.Donors.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task AnAnonymousGiftFromAKnownDonorStaysOutOfTheirHistoryAndTotals()
    {
        await ConfirmedAsync("known@example.org", "500.00", name: "Known Donor");
        await ConfirmedAsync("known@example.org", "9999.00", name: "Known Donor", anonymous: true);

        var donor = (await GetAsync("/api/v1/admin/finance/donors")).GetProperty("items")[0];

        (donor.GetProperty("count").GetInt32(), donor.GetProperty("total").GetString()).Should().Be((1, "500.00"));
        var detail = await GetAsync($"/api/v1/admin/finance/donors/{donor.GetProperty("id").GetString()}");
        detail.GetProperty("gifts").GetArrayLength().Should().Be(1);
        (await GetAsync("/api/v1/admin/finance/donors/reconciliation")).GetProperty("reconciled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task OneDonorByEmailRegardlessOfCaseWithTheLatestNameAndTheFrontendShape()
    {
        await ConfirmedAsync("Same@Example.org", "1000.00", name: "Old Name");
        await ConfirmedAsync("same@example.org", "250.50", name: "New Name");
        await ConfirmedAsync("other@example.org", "70.00", name: "Other Person");

        var list = await GetAsync("/api/v1/admin/finance/donors");

        list.GetProperty("total").GetInt32().Should().Be(2);
        var same = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "New Name");
        (same.GetProperty("total").GetString(), same.GetProperty("currency").GetString(), same.GetProperty("count").GetInt32(), same.GetProperty("lastGiftOn").GetString(), same.GetProperty("email").GetString())
            .Should().Be(("1250.50", "BDT", 2, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "same@example.org"));
        (await GetAsync("/api/v1/admin/finance/donors?q=other")).GetProperty("total").GetInt32().Should().Be(1);
        (await GetAsync("/api/v1/admin/finance/donors?limit=1&offset=1")).GetProperty("items").GetArrayLength().Should().Be(1);

        var gifts = (await GetAsync($"/api/v1/admin/finance/donors/{same.GetProperty("id").GetString()}/donations")).GetProperty("items").EnumerateArray().ToList();
        gifts.Should().HaveCount(2);
        gifts[0].GetProperty("status").GetString().Should().Be("confirmed");
        gifts[0].GetProperty("donorId").GetString().Should().Be(same.GetProperty("id").GetString());
        gifts.Select(g => g.GetProperty("amount").GetString()).Should().BeEquivalentTo(["1000.00", "250.50"]);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/donors/dnr_missing", Viewer)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/donors/dnr_missing/donations", Viewer)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LifetimeValueAndHistoryReconcileExactlyWithTheLedger()
    {
        await ConfirmedAsync("a@example.org", "100.00", name: "A");
        await ConfirmedAsync("a@example.org", "250.50", name: "A");
        await ConfirmedAsync("a@example.org", "10.00", "USD", name: "A");
        var (reversedId, _) = await ConfirmedAsync("a@example.org", "777.00", name: "A");
        await ConfirmedAsync("b@example.org", "60.00", name: "B");
        using var kind = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/donations", Maker, new
        {
            donorName = "A",
            email = "a@example.org",
            amount = "1000.00",
            currency = "BDT",
            method = "in_kind",
            valuation = new { basis = "appraisal", itemDescription = "Desks" }
        });
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{(await kind.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()}/approve", Checker, new { });
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{reversedId}/reversal", Reverser, new { reason = "Chargeback" });

        var reconciliation = await GetAsync("/api/v1/admin/finance/donors/reconciliation");
        var donors = (await GetAsync("/api/v1/admin/finance/donors")).GetProperty("items").EnumerateArray().ToList();

        reconciliation.GetProperty("reconciled").GetBoolean().Should().BeTrue(reconciliation.GetRawText());
        var a = donors.Single(d => d.GetProperty("name").GetString() == "A");
        a.GetProperty("count").GetInt32().Should().Be(4, "the reversed gift is gone from history");
        var totals = a.GetProperty("totals").EnumerateArray().ToDictionary(t => t.GetProperty("currency").GetString()!, t => (t.GetProperty("cash").GetString(), t.GetProperty("inKind").GetString()));
        (totals["BDT"], totals["USD"]).Should().Be((("350.50", "1000.00"), ("10.00", "0.00")));

        // The books say the same: income in the ledger for A's gifts, net of the reversal.
        var income = await DbAsync(async db => (await (from e in db.LedgerEntries.AsNoTracking() join acc in db.Accounts.AsNoTracking() on e.AccountId equals acc.Id where acc.Code == "4010" || acc.Code == "4020" select new { e.Currency, e.Debit, e.Credit }).ToListAsync()).GroupBy(x => x.Currency).ToDictionary(g => g.Key, g => g.Sum(x => x.Credit - x.Debit)));
        income["BDT"].Should().Be(100m + 250.50m + 60m + 1000m);
        income["USD"].Should().Be(10m);
    }

    [Fact]
    public async Task AnyDriftBetweenGivingAndTheLedgerIsReportedWithTheDifference()
    {
        await ConfirmedAsync("a@example.org", "400.00", name: "A");
        await DbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE ledger_entries SET Credit = 390.00 WHERE Credit = 400.00");
            return 0;
        });

        var reconciliation = await GetAsync("/api/v1/admin/finance/donors/reconciliation");

        reconciliation.GetProperty("reconciled").GetBoolean().Should().BeFalse();
        var line = reconciliation.GetProperty("mismatches")[0];
        (line.GetProperty("currency").GetString(), line.GetProperty("receipts").GetString(), line.GetProperty("ledger").GetString(), line.GetProperty("difference").GetString()).Should().Be(("BDT", "400.00", "390.00", "10.00"));
    }

    [Fact]
    public async Task SegmentsRunOverTheApiAndCanBeReRunForAnotherDate()
    {
        await ConfirmedAsync("new@example.org", "500.00", name: "Newcomer");
        await ConfirmedAsync("major@example.org", "150000.00", name: "Major Donor");
        await ConfirmedAsync("monthly@example.org", "300.00", name: "Monthly Donor", frequency: "monthly");
        string Names(JsonElement page) => string.Join(",", page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).OrderBy(n => n, StringComparer.Ordinal));

        Names(await GetAsync("/api/v1/admin/finance/donors?segment=major")).Should().Be("Major Donor");
        Names(await GetAsync("/api/v1/admin/finance/donors?segment=recurring")).Should().Be("Monthly Donor");
        Names(await GetAsync("/api/v1/admin/finance/donors?segment=new")).Should().Be("Major Donor,Monthly Donor,Newcomer");
        Names(await GetAsync("/api/v1/admin/finance/donors?segment=lapsed")).Should().BeEmpty();

        var later = DateTime.UtcNow.AddYears(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Names(await GetAsync($"/api/v1/admin/finance/donors?segment=lapsed&asOf={later}")).Should().Be("Major Donor,Monthly Donor,Newcomer");
        Names(await GetAsync($"/api/v1/admin/finance/donors?segment=new&asOf={later}")).Should().BeEmpty();
        Names(await GetAsync("/api/v1/admin/finance/donors?asOf=2000-01-01")).Should().BeEmpty("nobody had given by then");
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/donors?segment=vip", Viewer)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/donors?asOf=yesterday", Viewer)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheFormsConsentGrantsThanksAndUpdatesButNeverAppealsAndAbsenceIsNotConsent()
    {
        await ConfirmedAsync("yes@example.org", name: "Consenting", consent: true);
        await ConfirmedAsync("no@example.org", name: "Silent", consent: false);

        var list = (await GetAsync("/api/v1/admin/finance/donors")).GetProperty("items").EnumerateArray().ToList();
        var yes = await GetAsync($"/api/v1/admin/finance/donors/{list.Single(d => d.GetProperty("name").GetString() == "Consenting").GetProperty("id").GetString()}");
        var no = await GetAsync($"/api/v1/admin/finance/donors/{list.Single(d => d.GetProperty("name").GetString() == "Silent").GetProperty("id").GetString()}");

        var consent = yes.GetProperty("consent");
        (consent.GetProperty("thanks").GetProperty("granted").GetBoolean(), consent.GetProperty("updates").GetProperty("granted").GetBoolean(), consent.GetProperty("appeals").GetProperty("granted").GetBoolean()).Should().Be((true, true, false));
        consent.GetProperty("thanks").GetProperty("source").GetString().Should().Be("donation_form");
        no.GetProperty("consent").EnumerateObject().Should().OnlyContain(p => !p.Value.GetProperty("granted").GetBoolean());
        no.GetProperty("consentHistory").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task AThankYouIsQueuedOnlyForADonorWhoConsentedAndOtherwiseTheSuppressionIsRecorded()
    {
        await ConfirmedAsync("yes@example.org", name: "Consenting", consent: true);
        await ConfirmedAsync("yes@example.org", name: "Consenting", consent: false);
        await ConfirmedAsync("no@example.org", name: "Silent", consent: false);

        var thankYous = await DbAsync(db => db.Notifications.AsNoTracking().Where(n => n.Template.StartsWith("donor.")).OrderBy(n => n.Id).Select(n => n.Template).ToListAsync());
        var suppressed = await DbAsync(db => db.DonorSuppressions.AsNoTracking().ToListAsync());

        thankYous.Should().Equal("donor.welcome_thanks", "donor.thank_you");
        suppressed.Should().ContainSingle().Which.Should().Match<DonorSuppressionEntity>(s => s.Template == "donor.welcome_thanks" && s.Purpose == "thanks");
        (await DbAsync(db => db.Notifications.CountAsync(n => n.Template == "donation.receipt"))).Should().Be(3, "the receipt is transactional and does not need consent");
    }

    [Fact]
    public async Task WithdrawingConsentStopsTheNextThankYouAndTheHistoryKeepsBoth()
    {
        await ConfirmedAsync("d@example.org", name: "Donor", consent: true);
        var id = (await GetAsync("/api/v1/admin/finance/donors")).GetProperty("items")[0].GetProperty("id").GetString()!;

        using var withdraw = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donors/{id}/consent", Manager, new { purpose = "thanks", granted = false, source = "donor_request" });
        withdraw.StatusCode.Should().Be(HttpStatusCode.OK, await withdraw.Content.ReadAsStringAsync());
        await ConfirmedAsync("d@example.org", name: "Donor", consent: false);

        (await DbAsync(db => db.Notifications.CountAsync(n => n.Template == "donor.thank_you"))).Should().Be(0);
        (await DbAsync(db => db.DonorSuppressions.CountAsync())).Should().Be(1);
        var detail = await GetAsync($"/api/v1/admin/finance/donors/{id}");
        detail.GetProperty("consent").GetProperty("thanks").GetProperty("granted").GetBoolean().Should().BeFalse();
        detail.GetProperty("consent").GetProperty("updates").GetProperty("granted").GetBoolean().Should().BeTrue("only thanks was withdrawn");
        var history = detail.GetProperty("consentHistory").EnumerateArray().Select(h => (h.GetProperty("purpose").GetString(), h.GetProperty("granted").GetBoolean(), h.GetProperty("source").GetString())).ToList();
        history.Should().Equal(("thanks", true, "donation_form"), ("updates", true, "donation_form"), ("thanks", false, "donor_request"));

        // A later ticked box grants it again, as a new row.
        await ConfirmedAsync("d@example.org", name: "Donor", consent: true);
        (await GetAsync($"/api/v1/admin/finance/donors/{id}")).GetProperty("consent").GetProperty("thanks").GetProperty("granted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ConsentChangesAreValidatedPermissionedAndAudited()
    {
        await ConfirmedAsync("d@example.org", name: "Donor");
        var id = (await GetAsync("/api/v1/admin/finance/donors")).GetProperty("items")[0].GetProperty("id").GetString()!;

        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donors/{id}/consent", Viewer, new { purpose = "appeals", granted = true })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donors/{id}/consent", Manager, new { purpose = "spam", granted = true })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donors/{id}/consent", Manager, new { purpose = "appeals", granted = true, source = "donation_form" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donors/{id}/consent", Manager, new { purpose = "appeals", granted = true, source = "telepathy" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/donors/dnr_missing/consent", Manager, new { purpose = "appeals", granted = true })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donors/{id}/consent", Manager, new { purpose = "appeals", granted = true })).StatusCode.Should().Be(HttpStatusCode.OK);

        var audit = await GetAsync("/api/v1/admin/finance/audit?limit=100", TestTokens.Issue([Permissions.AuditView]));
        audit.GetProperty("items").EnumerateArray().Select(e => (e.GetProperty("entity").GetString(), e.GetProperty("actorId").GetString())).Should().Contain(("DonorConsent", "usr_manager"));
    }

    [Fact]
    public async Task AppealsAndUpdatesGoOnlyToDonorsWhoConsentedForThatPurpose()
    {
        await ConfirmedAsync("a@example.org", name: "Appeals Ok");
        await ConfirmedAsync("b@example.org", name: "Updates Only", consent: true);
        await ConfirmedAsync("c@example.org", name: "Nothing");
        var donors = (await GetAsync("/api/v1/admin/finance/donors")).GetProperty("items").EnumerateArray().ToDictionary(d => d.GetProperty("name").GetString()!, d => d.GetProperty("id").GetString()!);
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donors/{donors["Appeals Ok"]}/consent", Manager, new { purpose = "appeals", granted = true });

        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/donors/messages", Viewer, new { template = "donor.appeal" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/donors/messages", Manager, new { template = "donor.thank_you" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/donors/messages", Manager, new { template = "donor.appeal", segment = "vip" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var appeal = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/donors/messages", Manager, new { template = "donor.appeal", segment = "new" });
        using var update = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/donors/messages", Manager, new { template = "donor.update" });

        var appealResult = await appeal.Content.ReadFromJsonAsync<JsonElement>();
        (appealResult.GetProperty("purpose").GetString(), appealResult.GetProperty("queued").GetInt32(), appealResult.GetProperty("suppressed").GetInt32()).Should().Be(("appeals", 1, 2));
        var updateResult = await update.Content.ReadFromJsonAsync<JsonElement>();
        (updateResult.GetProperty("queued").GetInt32(), updateResult.GetProperty("suppressed").GetInt32()).Should().Be((1, 2));
        (await DbAsync(db => db.Notifications.CountAsync(n => n.Template == "donor.appeal"))).Should().Be(1);
        (await DbAsync(db => db.DonorSuppressions.CountAsync(s => s.Purpose == "appeals"))).Should().Be(2);
        (await DbAsync(db => db.Notifications.CountAsync(n => n.Template == "donor.update"))).Should().Be(1);
    }

    [Fact]
    public async Task TheExportIsReproducibleFilterableInjectionSafeAndLogged()
    {
        await ConfirmedAsync("a@example.org", "500.00", name: "=HYPERLINK(\"http://evil\")", consent: true);
        await ConfirmedAsync("b@example.org", "150000.00", name: "Big, \"Quoted\" Donor");
        await ConfirmedAsync("anon@example.org", "60.00", name: "Anonymous One", anonymous: true);
        var id = (await GetAsync("/api/v1/admin/finance/donors?segment=major")).GetProperty("items")[0].GetProperty("id").GetString()!;
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donors/{id}/consent", Manager, new { purpose = "appeals", granted = true });

        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/donors/export", Manager)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/donors/export", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/donors/export?consent=spam", Exporter)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/donors/export?segment=vip", Exporter)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var first = await SendAsync(HttpMethod.Get, $"/api/v1/admin/finance/donors/export?asOf={today}", Exporter);
        using var again = await SendAsync(HttpMethod.Get, $"/api/v1/admin/finance/donors/export?asOf={today}", Exporter);
        var csv = await first.Content.ReadAsStringAsync();

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (first.Content.Headers.ContentType!.MediaType, first.Content.Headers.ContentDisposition!.DispositionType).Should().Be(("text/csv", "attachment"));
        csv.Should().Be(await again.Content.ReadAsStringAsync(), "the same date and rules give the same export");
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("id,name,email,gifts,totals,first_gift_on,last_gift_on,segments,consent_thanks,consent_updates,consent_appeals");
        lines.Should().HaveCount(3, "two donors; the anonymous gift is not there");
        csv.Should().NotContain("anon@example.org").And.NotContain("Anonymous One");
        csv.Should().Contain("\"'=HYPERLINK(\"\"http://evil\"\")\"", "a leading = is neutralised and quotes are doubled").And.Contain("\"Big, \"\"Quoted\"\" Donor\"");
        lines.Single(l => l.Contains("a@example.org", StringComparison.Ordinal)).Should().EndWith("\"yes\",\"yes\",\"no\"");

        var major = await (await SendAsync(HttpMethod.Get, $"/api/v1/admin/finance/donors/export?segment=major&asOf={today}", Exporter)).Content.ReadAsStringAsync();
        major.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2).And.Subject.Last().Should().Contain("b@example.org");
        var consented = await (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/donors/export?consent=appeals", Exporter)).Content.ReadAsStringAsync();
        consented.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2).And.Subject.Last().Should().Contain("b@example.org");

        var logs = await DbAsync(db => db.DonorExports.AsNoTracking().OrderBy(e => e.Id).ToListAsync());
        logs.Select(l => (l.Segment, l.ConsentFilter, l.Rows, l.ExportedBy)).Should().Equal(("all", null, 2, "usr_exporter"), ("all", null, 2, "usr_exporter"), ("major", null, 1, "usr_exporter"), ("all", "appeals", 1, "usr_exporter"));
    }

    [Fact]
    public async Task ConsentHistorySuppressionsAndExportLogsAreAppendOnlyButDonorContactCanBeRefreshed()
    {
        await ConfirmedAsync("d@example.org", name: "Donor", consent: true);
        await ConfirmedAsync("n@example.org", name: "Silent");
        await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/donors/export", Exporter);

        await DbAsync(async db =>
        {
            async Task Refused(Action change)
            {
                change();
                await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<InvalidOperationException>();
                db.ChangeTracker.Clear();
            }

            await Refused(() => db.DonorConsents.First().Granted = false);
            await Refused(() => db.DonorConsents.Remove(db.DonorConsents.First()));
            await Refused(() => db.DonorSuppressions.First().Purpose = "x");
            await Refused(() => db.DonorExports.First().Rows = 99);
            db.Donors.First().Language = "bn";
            await db.SaveChangesAsync();
            return 0;
        });
    }

    [Fact]
    public async Task ViewingDonorsNeedsFinanceViewAndTheSeededRolesSplitTheDuties()
    {
        (await _client.GetAsync("/api/v1/admin/finance/donors")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/donors", TestTokens.Issue([Permissions.ContentView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/donors/reconciliation", TestTokens.Issue([Permissions.ContentView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        KjRoles.Seed[KjRoles.FinanceOfficer].Should().Contain(Permissions.FinanceDonorManage).And.NotContain(Permissions.FinanceDonorExport);
        KjRoles.Seed[KjRoles.Admin].Should().Contain([Permissions.FinanceDonorManage, Permissions.FinanceDonorExport]);
    }
}
