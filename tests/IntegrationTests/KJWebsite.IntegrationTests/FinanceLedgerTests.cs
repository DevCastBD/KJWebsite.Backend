using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Ledger;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Finance;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class FinanceLedgerTests : IDisposable
{
    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;
    private static readonly string Viewer = TestTokens.Issue([Permissions.FinanceView]);
    private static readonly string Adjuster = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceLedgerAdjust]);
    private static readonly string Closer = TestTokens.Issue([Permissions.FinanceView, Permissions.FinancePeriodClose]);
    private static readonly string Today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public FinanceLedgerTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider);
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

    private async Task<JsonElement> PostOkAsync(string path, string token, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        using var response = await SendAsync(HttpMethod.Post, path, token, body);
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static object Adjustment(string debitAccount = "1010", string creditAccount = "4900", string amount = "100.00", string currency = "BDT", string? postedOn = null, string reason = "Opening balance") => new
    {
        postedOn,
        memo = "Test adjustment",
        reason,
        lines = new[]
        {
            new { account = debitAccount, debit = (string?)amount, credit = (string?)null, currency },
            new { account = creditAccount, debit = (string?)null, credit = (string?)amount, currency }
        }
    };

    private async Task<string> NewConfirmedDonationAsync(string amount = "1000.00", bool coverFees = false, string method = "bkash")
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/donations", new
        {
            amount,
            currency = "BDT",
            frequency = "once",
            designation = new { type = "general" },
            donor = new { name = "A Donor", email = "d@example.org" },
            anonymous = false,
            coverFees,
            language = "en"
        });
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        (await InScopeAsync(s => s.GetRequiredService<DonationConfirmation>().ConfirmAsync(id, method, "test"))).IsSuccess.Should().BeTrue();
        return id;
    }

    [Fact]
    public async Task TheChartIsSeededOnStartupAndSeedingAgainChangesNothing()
    {
        var chart = await GetAsync("/api/v1/admin/finance/ledger/accounts");
        chart.GetProperty("items").GetArrayLength().Should().Be(LedgerAccounts.Seed.Count);

        await InScopeAsync(async s =>
        {
            await LedgerSeeder.EnsureChartAsync(s.GetRequiredService<FinanceDbContext>(), TimeProvider.System);
            return 0;
        });

        (await InScopeAsync(s => s.GetRequiredService<FinanceDbContext>().Accounts.CountAsync())).Should().Be(LedgerAccounts.Seed.Count);
    }

    [Fact]
    public async Task AManualAdjustmentIsPostedBalancedWithItsFiscalYear()
    {
        var posting = await PostOkAsync("/api/v1/admin/finance/ledger/postings", Adjuster, Adjustment(amount: "250.50"));

        posting.GetProperty("memo").GetString().Should().Be("Test adjustment");
        posting.GetProperty("sourceType").GetString().Should().Be("adjustment");
        posting.GetProperty("periodKey").GetString().Should().Be(DateTime.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
        posting.GetProperty("fiscalYear").GetString().Should().MatchRegex(@"^\d{4}-\d{2}$");
        posting.GetProperty("createdBy").GetString().Should().Be("usr_test");
        var lines = posting.GetProperty("lines").EnumerateArray().ToList();
        lines.Select(l => (l.GetProperty("accountCode").GetString(), l.TryGetProperty("debit", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null)).Should().Contain(("1010", "250.50"));
    }

    [Theory]
    [InlineData("unbalanced")]
    [InlineData("one-line")]
    [InlineData("both-sides")]
    [InlineData("neither-side")]
    [InlineData("third-decimal")]
    [InlineData("negative")]
    [InlineData("zero")]
    [InlineData("unknown-account")]
    [InlineData("bad-currency")]
    [InlineData("no-reason")]
    [InlineData("future-date")]
    [InlineData("bad-date")]
    [InlineData("no-memo")]
    public async Task InvalidAdjustmentsAreRefusedAndNothingIsPosted(string problem)
    {
        object body = problem switch
        {
            "unbalanced" => new { memo = "m", reason = "reason", lines = new[] { new { account = "1010", debit = (string?)"100.00", credit = (string?)null, currency = "BDT" }, new { account = "4900", debit = (string?)null, credit = (string?)"99.99", currency = "BDT" } } },
            "one-line" => new { memo = "m", reason = "reason", lines = new[] { new { account = "1010", debit = (string?)"100.00", credit = (string?)null, currency = "BDT" } } },
            "both-sides" => new { memo = "m", reason = "reason", lines = new[] { new { account = "1010", debit = (string?)"1.00", credit = (string?)"1.00", currency = "BDT" }, new { account = "4900", debit = (string?)null, credit = (string?)"1.00", currency = "BDT" } } },
            "neither-side" => new { memo = "m", reason = "reason", lines = new[] { new { account = "1010", debit = (string?)null, credit = (string?)null, currency = "BDT" }, new { account = "4900", debit = (string?)null, credit = (string?)"1.00", currency = "BDT" } } },
            "third-decimal" => Adjustment(amount: "10.005"),
            "negative" => Adjustment(amount: "-5.00"),
            "zero" => Adjustment(amount: "0.00"),
            "unknown-account" => Adjustment(debitAccount: "9999"),
            "bad-currency" => Adjustment(currency: "XYZ"),
            "no-reason" => Adjustment(reason: ""),
            "future-date" => Adjustment(postedOn: DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            "bad-date" => Adjustment(postedOn: "31/12/2026"),
            _ => new { memo = "", reason = "reason", lines = new[] { new { account = "1010", debit = (string?)"1.00", credit = (string?)null, currency = "BDT" }, new { account = "4900", debit = (string?)null, credit = (string?)"1.00", currency = "BDT" } } }
        };

        using var response = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", Adjuster, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await InScopeAsync(s => s.GetRequiredService<FinanceDbContext>().Postings.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task WritesNeedTheirOwnPermissions()
    {
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/ledger", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/ledger", TestTokens.Issue([Permissions.ContentView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", Viewer, Adjustment())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", Closer, Adjustment())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings/1/reversal", Viewer, new { reason = "reason" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/accounts", Viewer, new { code = "7000", name = "x", type = "expense" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/periods/{DateTime.UtcNow:yyyy-MM}/close", Adjuster)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/periods/{DateTime.UtcNow:yyyy-MM}/close", Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TheLedgerListUsesTheFrontendShapeAndLinksReversals()
    {
        var first = await PostOkAsync("/api/v1/admin/finance/ledger/postings", Adjuster, Adjustment(amount: "75.00"));
        var firstId = first.GetProperty("id").GetString()!;
        var reversal = await PostOkAsync($"/api/v1/admin/finance/ledger/postings/{firstId}/reversal", Adjuster, new { reason = "Entered twice" });

        var list = await GetAsync("/api/v1/admin/finance/ledger");

        list.GetProperty("total").GetInt32().Should().Be(4);
        var rows = list.GetProperty("items").EnumerateArray().ToList();
        var original = rows.First(r => r.GetProperty("postingId").GetString() == firstId && r.TryGetProperty("debit", out var d) && d.ValueKind == JsonValueKind.String);
        (original.GetProperty("debit").GetString(), original.GetProperty("currency").GetString(), original.GetProperty("account").GetString(), original.GetProperty("memo").GetString())
            .Should().Be(("75.00", "BDT", "1010 Cash on hand", "Test adjustment"));
        original.GetProperty("reversedBy").GetString().Should().Be(reversal.GetProperty("id").GetString());
        original.TryGetProperty("credit", out var credit).Should().BeTrue();
        credit.ValueKind.Should().Be(JsonValueKind.Null);
        rows.Where(r => r.GetProperty("postingId").GetString() == reversal.GetProperty("id").GetString()).Should().OnlyContain(r => r.GetProperty("reversalOf").GetString() == firstId);

        (await GetAsync("/api/v1/admin/finance/ledger?account=4900")).GetProperty("total").GetInt32().Should().Be(2);
        (await GetAsync($"/api/v1/admin/finance/ledger?from={Today}&to={Today}")).GetProperty("total").GetInt32().Should().Be(4);
        (await GetAsync("/api/v1/admin/finance/ledger?from=2999-01-01")).GetProperty("total").GetInt32().Should().Be(0);
        var page = await GetAsync("/api/v1/admin/finance/ledger?limit=3&offset=3");
        (page.GetProperty("items").GetArrayLength(), page.GetProperty("limit").GetInt32()).Should().Be((1, 3));
    }

    [Fact]
    public async Task APostingCanBeReadWithAllItsLines()
    {
        var posting = await PostOkAsync("/api/v1/admin/finance/ledger/postings", Adjuster, Adjustment());

        var read = await GetAsync($"/api/v1/admin/finance/ledger/postings/{posting.GetProperty("id").GetString()}");

        read.GetProperty("lines").GetArrayLength().Should().Be(2);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/ledger/postings/999999", Viewer)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AReversalMirrorsTheOriginalAndOnlyOnce()
    {
        var original = await PostOkAsync("/api/v1/admin/finance/ledger/postings", Adjuster, Adjustment(amount: "40.00"));
        var id = original.GetProperty("id").GetString()!;

        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/postings/{id}/reversal", Adjuster, new { reason = "x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var reversal = await PostOkAsync($"/api/v1/admin/finance/ledger/postings/{id}/reversal", Adjuster, new { reason = "Wrong account" });

        reversal.GetProperty("reversesPostingId").GetString().Should().Be(id);
        reversal.GetProperty("sourceType").GetString().Should().Be("reversal");
        var mirrored = reversal.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("accountCode").GetString() == "1010");
        (mirrored.TryGetProperty("debit", out var debit) && debit.ValueKind == JsonValueKind.String).Should().BeFalse();
        mirrored.GetProperty("credit").GetString().Should().Be("40.00");
        (await GetAsync($"/api/v1/admin/finance/ledger/postings/{id}")).GetProperty("reversedByPostingId").GetString().Should().Be(reversal.GetProperty("id").GetString());

        using var again = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/postings/{id}/reversal", Adjuster, new { reason = "Again" });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync()).Should().Contain("ALREADY_REVERSED");
        using var ofReversal = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/postings/{reversal.GetProperty("id").GetString()}/reversal", Adjuster, new { reason = "Undo the undo" });
        ofReversal.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ofReversal.Content.ReadAsStringAsync()).Should().Contain("NOT_REVERSIBLE");
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings/999999/reversal", Adjuster, new { reason = "Missing" })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var balance = await GetAsync("/api/v1/admin/finance/ledger/trial-balance");
        balance.GetProperty("currencies")[0].GetProperty("accounts").EnumerateArray().Should().OnlyContain(a => a.GetProperty("balance").GetString() == "0.00");
    }

    [Fact]
    public async Task TheTrialBalanceBalancesInEachCurrencyAndSeparatesThem()
    {
        await PostOkAsync("/api/v1/admin/finance/ledger/postings", Adjuster, Adjustment(amount: "1000.00", currency: "BDT"));
        await PostOkAsync("/api/v1/admin/finance/ledger/postings", Adjuster, Adjustment(amount: "25.00", currency: "USD"));
        await PostOkAsync("/api/v1/admin/finance/ledger/postings", Adjuster, Adjustment(debitAccount: "5030", creditAccount: "1010", amount: "300.00", currency: "BDT"));

        var balance = await GetAsync("/api/v1/admin/finance/ledger/trial-balance");

        var sections = balance.GetProperty("currencies").EnumerateArray().ToList();
        sections.Select(s => s.GetProperty("currency").GetString()).Should().Equal("BDT", "USD");
        sections.Should().OnlyContain(s => s.GetProperty("balanced").GetBoolean());
        var bdt = sections[0];
        (bdt.GetProperty("totalDebit").GetString(), bdt.GetProperty("totalCredit").GetString()).Should().Be(("1300.00", "1300.00"));
        var cash = bdt.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("code").GetString() == "1010");
        (cash.GetProperty("debit").GetString(), cash.GetProperty("credit").GetString(), cash.GetProperty("balance").GetString()).Should().Be(("1000.00", "300.00", "700.00"));
        (await GetAsync("/api/v1/admin/finance/ledger/trial-balance?currency=USD")).GetProperty("currencies").GetArrayLength().Should().Be(1);
        (await GetAsync("/api/v1/admin/finance/ledger/trial-balance?asOf=2000-01-01")).GetProperty("currencies").GetArrayLength().Should().Be(0);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/ledger/trial-balance?asOf=yesterday", Viewer)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/ledger/trial-balance?currency=XYZ", Viewer)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RandomPostingsAlwaysLeaveEveryCurrencyBalancedAndMatchAnIndependentTally()
    {
        var random = new Random(53);
        var codes = LedgerAccounts.Seed.Select(a => a.Code).ToArray();
        var expected = new Dictionary<(string Currency, string Code), (decimal Debit, decimal Credit)>();
        await InScopeAsync(async s =>
        {
            var ledger = s.GetRequiredService<LedgerService>();
            for (var i = 0; i < 150; i++)
            {
                var currency = Currencies.Supported[random.Next(Currencies.Supported.Count)];
                var amount = random.NextInt64(1, 10_000_000L) / 100m;
                var debit = codes[random.Next(codes.Length)];
                var credit = codes[random.Next(codes.Length)];
                var posted = await ledger.PostAsync(new PostingDraft(ledger.Today, $"random {i}", PostingSources.Adjustment, null,
                    [LedgerLine.Debit(debit, Money.Of(amount, currency)), LedgerLine.Credit(credit, Money.Of(amount, currency))], "property test"), "tester");
                posted.IsSuccess.Should().BeTrue();
                expected[(currency, debit)] = (expected.GetValueOrDefault((currency, debit)).Debit + amount, expected.GetValueOrDefault((currency, debit)).Credit);
                expected[(currency, credit)] = (expected.GetValueOrDefault((currency, credit)).Debit, expected.GetValueOrDefault((currency, credit)).Credit + amount);
            }

            return 0;
        });

        var balance = await InScopeAsync(s => s.GetRequiredService<LedgerService>().TrialBalanceAsync(DateOnly.FromDateTime(DateTime.UtcNow)));

        balance.Should().OnlyContain(section => section.Balanced);
        foreach (var section in balance)
        {
            foreach (var line in section.Lines)
            {
                expected[(section.Currency, line.Code)].Should().Be((line.Debit, line.Credit));
            }

            section.Lines.Count.Should().Be(expected.Keys.Count(k => k.Currency == section.Currency));
        }
    }

    [Fact]
    public async Task AClosedPeriodTakesNoPostingsAndOpenMonthsStillDo()
    {
        var lastMonth = DateTime.UtcNow.AddMonths(-1);
        var key = lastMonth.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var date = new DateTime(lastMonth.Year, lastMonth.Month, 15).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await PostOkAsync("/api/v1/admin/finance/ledger/postings", Adjuster, Adjustment(postedOn: date));

        var closed = await PostOkAsync($"/api/v1/admin/finance/ledger/periods/{key}/close", Closer, new { }, HttpStatusCode.OK);
        closed.GetProperty("closed").GetBoolean().Should().BeTrue();

        using var refused = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", Adjuster, Adjustment(postedOn: date));
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("PERIOD_CLOSED");
        await PostOkAsync("/api/v1/admin/finance/ledger/postings", Adjuster, Adjustment());
        using var again = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/periods/{key}/close", Closer);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var periods = (await GetAsync("/api/v1/admin/finance/ledger/periods")).GetProperty("items").EnumerateArray().ToList();
        var period = periods.Single(p => p.GetProperty("periodKey").GetString() == key);
        (period.GetProperty("closed").GetBoolean(), period.GetProperty("postings").GetInt32(), period.GetProperty("closedBy").GetString()).Should().Be((true, 1, "usr_test"));
        periods.Single(p => p.GetProperty("periodKey").GetString() == DateTime.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)).GetProperty("closed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task AReversalIntoAClosedCurrentMonthIsRefusedAndTheOriginalSurvives()
    {
        var original = await PostOkAsync("/api/v1/admin/finance/ledger/postings", Adjuster, Adjustment());
        await PostOkAsync($"/api/v1/admin/finance/ledger/periods/{DateTime.UtcNow:yyyy-MM}/close", Closer, new { }, HttpStatusCode.OK);

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/postings/{original.GetProperty("id").GetString()}/reversal", Adjuster, new { reason = "Too late" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("PERIOD_CLOSED");
        (await InScopeAsync(s => s.GetRequiredService<FinanceDbContext>().Postings.CountAsync())).Should().Be(1);
    }

    [Theory]
    [InlineData("2999-01", HttpStatusCode.BadRequest)]
    [InlineData("2026-13", HttpStatusCode.BadRequest)]
    [InlineData("nonsense", HttpStatusCode.BadRequest)]
    public async Task OnlyRealPastOrCurrentMonthsCanBeClosed(string key, HttpStatusCode expected)
    {
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/periods/{key}/close", Closer)).StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task AccountsCanBeAddedAndArchivedButNotDeletedOrRetyped()
    {
        var created = await PostOkAsync("/api/v1/admin/finance/ledger/accounts", Adjuster, new { code = "5910", name = "Travel", type = "expense" });
        created.GetProperty("isSystem").GetBoolean().Should().BeFalse();
        await PostOkAsync("/api/v1/admin/finance/ledger/postings", Adjuster, Adjustment(debitAccount: "5910", creditAccount: "1010", amount: "20.00"));

        using var duplicate = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/accounts", Adjuster, new { code = "5910", name = "Other", type = "expense" });
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        foreach (var bad in new object[] { new { code = "", name = "x", type = "expense" }, new { code = "has space", name = "x", type = "expense" }, new { code = "7001", name = "", type = "expense" }, new { code = "7002", name = "x", type = "magic" } })
        {
            (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/accounts", Adjuster, bad)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        var archived = await PostOkAsync($"/api/v1/admin/finance/ledger/accounts/{created.GetProperty("id").GetString()}/archive", Adjuster, new { }, HttpStatusCode.OK);
        archived.GetProperty("archived").GetBoolean().Should().BeTrue();
        using var refused = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", Adjuster, Adjustment(debitAccount: "5910", creditAccount: "1010"));
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("POSTING_ACCOUNT_ARCHIVED");

        var system = (await GetAsync("/api/v1/admin/finance/ledger/accounts")).GetProperty("items").EnumerateArray().First(a => a.GetProperty("isSystem").GetBoolean());
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/accounts/{system.GetProperty("id").GetString()}/archive", Adjuster, new { })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/accounts/acc_missing/archive", Adjuster, new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnArchivedAccountStillAllowsReversingItsOldPostings()
    {
        var account = await PostOkAsync("/api/v1/admin/finance/ledger/accounts", Adjuster, new { code = "5920", name = "Printing", type = "expense" });
        var posting = await PostOkAsync("/api/v1/admin/finance/ledger/postings", Adjuster, Adjustment(debitAccount: "5920", creditAccount: "1010", amount: "9.00"));
        await PostOkAsync($"/api/v1/admin/finance/ledger/accounts/{account.GetProperty("id").GetString()}/archive", Adjuster, new { }, HttpStatusCode.OK);

        await PostOkAsync($"/api/v1/admin/finance/ledger/postings/{posting.GetProperty("id").GetString()}/reversal", Adjuster, new { reason = "Correction" });
    }

    [Fact]
    public async Task EveryLedgerWriteIsAuditedWithTheCaller()
    {
        var posting = await PostOkAsync("/api/v1/admin/finance/ledger/postings", Adjuster, Adjustment());
        await PostOkAsync($"/api/v1/admin/finance/ledger/periods/{DateTime.UtcNow.AddMonths(-2):yyyy-MM}/close", Closer, new { }, HttpStatusCode.OK);

        var audit = await GetAsync("/api/v1/admin/finance/audit?limit=100", TestTokens.Issue([Permissions.AuditView]));

        var events = audit.GetProperty("items").EnumerateArray().ToList();
        events.Select(e => e.GetProperty("entity").GetString()).Should().Contain(["Posting", "LedgerEntry", "PeriodClose"]);
        events.Should().OnlyContain(e => e.GetProperty("actorId").GetString() == "usr_test");
        events.First(e => e.GetProperty("entity").GetString() == "Posting").GetProperty("entityId").GetString().Should().NotBeNullOrEmpty();
        posting.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ConfirmingADonationPostsItOnceWithTheFeeSplitOut()
    {
        var id = await NewConfirmedDonationAsync("1000.00", coverFees: true, method: "sslcommerz");

        var lines = (await GetAsync("/api/v1/admin/finance/ledger")).GetProperty("items").EnumerateArray().ToList();

        lines.Should().HaveCount(3);
        string? Side(string code, string side) => lines.Single(l => l.GetProperty("accountCode").GetString() == code).GetProperty(side).GetString();
        Side("1110", "debit").Should().Be("1025.65");
        Side("4010", "credit").Should().Be("1000.00");
        Side("4090", "credit").Should().Be("25.65");
        (await InScopeAsync(s => s.GetRequiredService<FinanceDbContext>().Postings.SingleAsync())).Should().Match<PostingEntity>(p => p.SourceType == "donation" && p.SourceId == id);
        (await GetAsync("/api/v1/admin/finance/ledger/trial-balance")).GetProperty("currencies")[0].GetProperty("balanced").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ReplayingAConfirmationTenTimesLeavesOneEventAndOnePosting()
    {
        var id = await NewConfirmedDonationAsync();

        for (var i = 0; i < 10; i++)
        {
            var again = await InScopeAsync(s => s.GetRequiredService<DonationConfirmation>().ConfirmAsync(id, "bkash", "replay"));
            again.IsSuccess.Should().BeTrue();
            again.Value.Should().BeFalse("the donation was already confirmed");
        }

        (await InScopeAsync(s => s.GetRequiredService<FinanceDbContext>().Postings.CountAsync())).Should().Be(1);
        (await InScopeAsync(s => s.GetRequiredService<FinanceDbContext>().DonationEvents.CountAsync(e => e.Status == "confirmed"))).Should().Be(1);
        (await InScopeAsync(s => s.GetRequiredService<FinanceDbContext>().LedgerEntries.CountAsync())).Should().Be(2);
    }

    [Fact]
    public async Task ConfirmationRefusesWhatCannotBeConfirmed()
    {
        var confirmation = await InScopeAsync(s => Task.FromResult(s.GetRequiredService<DonationConfirmation>()));
        (await InScopeAsync(s => s.GetRequiredService<DonationConfirmation>().ConfirmAsync("don_missing", "bkash", null))).Error!.Code.Should().Be("NOT_FOUND");
        confirmation.Should().NotBeNull();

        var id = await NewConfirmedDonationAsync();
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{id}/reversal", TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationReverse]), new { reason = "Refunded" });
        (await InScopeAsync(s => s.GetRequiredService<DonationConfirmation>().ConfirmAsync(id, "bkash", null))).Error!.Code.Should().Be("NOT_CONFIRMABLE");
    }

    [Fact]
    public async Task ADonationThatFailedCanStillBeConfirmedLater()
    {
        await TestCampaigns.SeedAsync(_factory.Services, "flood");
        using var created = await _client.PostAsJsonAsync("/api/v1/donations", new
        {
            amount = "300.00",
            currency = "BDT",
            frequency = "once",
            designation = new { type = "campaign", id = "flood" },
            donor = new { name = "A", email = "a@example.org" },
            anonymous = false,
            coverFees = false,
            language = "en"
        });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await InScopeAsync(async s =>
        {
            var db = s.GetRequiredService<FinanceDbContext>();
            db.DonationEvents.Add(new DonationEventEntity { DonationId = id, Status = "failed", At = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });

        (await InScopeAsync(s => s.GetRequiredService<DonationConfirmation>().ConfirmAsync(id, "nagad", "late webhook"))).Value.Should().BeTrue();

        var lines = (await GetAsync("/api/v1/admin/finance/ledger")).GetProperty("items").EnumerateArray().ToList();
        lines.Select(l => l.GetProperty("accountCode").GetString()).Should().BeEquivalentTo(["1130", "4020"]);
    }

    [Fact]
    public async Task ReversingAConfirmedDonationReversesItsPostingInTheSameCommit()
    {
        var id = await NewConfirmedDonationAsync("500.00");
        var reverser = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationReverse]);

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{id}/reversal", reverser, new { reason = "Chargeback" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var postings = await InScopeAsync(s => s.GetRequiredService<FinanceDbContext>().Postings.AsNoTracking().OrderBy(p => p.Id).ToListAsync());
        postings.Should().HaveCount(2);
        (postings[1].SourceType, postings[1].ReversesPostingId, postings[1].Reason).Should().Be(("reversal", postings[0].Id, "Chargeback"));
        (await GetAsync("/api/v1/admin/finance/ledger/trial-balance")).GetProperty("currencies")[0].GetProperty("accounts").EnumerateArray()
            .Should().OnlyContain(a => a.GetProperty("balance").GetString() == "0.00");
    }

    [Fact]
    public async Task ADonationWithoutAPostingCannotBeReversedSilently()
    {
        using var created = await _client.PostAsJsonAsync("/api/v1/donations", new
        {
            amount = "300.00",
            currency = "BDT",
            frequency = "once",
            designation = new { type = "general" },
            donor = new { name = "A", email = "a@example.org" },
            anonymous = false,
            coverFees = false,
            language = "en"
        });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await InScopeAsync(async s =>
        {
            var db = s.GetRequiredService<FinanceDbContext>();
            db.DonationEvents.Add(new DonationEventEntity { DonationId = id, Status = "confirmed", At = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{id}/reversal", TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationReverse]), new { reason = "Chargeback" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("NOT_POSTED");
        (await InScopeAsync(s => s.GetRequiredService<FinanceDbContext>().Donations.CountAsync())).Should().Be(1, "no reversing donation was written either");
    }

    [Fact]
    public async Task ADonationReversalIntoAClosedMonthChangesNothing()
    {
        var id = await NewConfirmedDonationAsync();
        await PostOkAsync($"/api/v1/admin/finance/ledger/periods/{DateTime.UtcNow:yyyy-MM}/close", Closer, new { }, HttpStatusCode.OK);

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{id}/reversal", TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationReverse]), new { reason = "Chargeback" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("PERIOD_CLOSED");
        (await InScopeAsync(s => s.GetRequiredService<FinanceDbContext>().Donations.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task TheSameSourceCannotBePostedTwiceEvenFromTwoContexts()
    {
        var draft = new PostingDraft(DateOnly.FromDateTime(DateTime.UtcNow), "once", PostingSources.Expense, "exp_1",
            [LedgerLine.Debit("5010", Money.Of(5m, "BDT")), LedgerLine.Credit("1010", Money.Of(5m, "BDT"))]);

        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();
        var staged = await scopeA.ServiceProvider.GetRequiredService<LedgerService>().StageAsync(draft, "a");
        var winner = await scopeB.ServiceProvider.GetRequiredService<LedgerService>().PostAsync(draft, "b");

        winner.Value!.Created.Should().BeTrue();
        staged.Value!.Created.Should().BeTrue("A staged before B saved");
        await ((Func<Task>)(() => scopeA.ServiceProvider.GetRequiredService<FinanceDbContext>().SaveChangesAsync())).Should().ThrowAsync<DbUpdateException>();
        var repeat = await scopeB.ServiceProvider.GetRequiredService<LedgerService>().PostAsync(draft, "b");
        (repeat.Value!.Created, repeat.Value.Posting.Id).Should().Be((false, winner.Value.Posting.Id));
        (await InScopeAsync(s => s.GetRequiredService<FinanceDbContext>().Postings.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task NoCodePathCanChangeOrDeleteTheLedger()
    {
        var posting = await PostOkAsync("/api/v1/admin/finance/ledger/postings", Adjuster, Adjustment());
        var id = long.Parse(posting.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        await PostOkAsync($"/api/v1/admin/finance/ledger/periods/{DateTime.UtcNow.AddMonths(-3):yyyy-MM}/close", Closer, new { }, HttpStatusCode.OK);

        await InScopeAsync(async s =>
        {
            var db = s.GetRequiredService<FinanceDbContext>();
            async Task Refused(Action change, string because)
            {
                change();
                await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<InvalidOperationException>(because);
                db.ChangeTracker.Clear();
            }

            await Refused(() => db.Postings.First(p => p.Id == id).Memo = "edited", "update a posting");
            await Refused(() => db.Postings.Remove(db.Postings.First(p => p.Id == id)), "delete a posting");
            await Refused(() => db.LedgerEntries.First(e => e.PostingId == id).Debit = 1m, "update a line");
            await Refused(() => db.LedgerEntries.Remove(db.LedgerEntries.First(e => e.PostingId == id)), "delete a line");
            await Refused(() => db.PeriodCloses.First().ClosedBy = "someone", "update a period close");
            await Refused(() => db.PeriodCloses.Remove(db.PeriodCloses.First()), "reopen a period");
            await Refused(() => db.Accounts.First(a => a.Code == "1010").Code = "9999", "recode an account");
            await Refused(() => db.Accounts.First(a => a.Code == "1010").Type = AccountTypes.Expense, "retype an account");
            await Refused(() => db.Accounts.Remove(db.Accounts.First(a => a.Code == "1010")), "delete an account");
            await Refused(() => db.LedgerEntries.Add(new LedgerEntryEntity { PostingId = id, AccountId = db.Accounts.First().Id, Debit = 1m, Currency = "BDT" }), "add a line to an existing posting");
            return 0;
        });

        (await InScopeAsync(s => s.GetRequiredService<FinanceDbContext>().Accounts.Where(a => a.Code == "1010").Select(a => a.Name).SingleAsync())).Should().Be("Cash on hand");
        await InScopeAsync(async s =>
        {
            var db = s.GetRequiredService<FinanceDbContext>();
            var account = await db.Accounts.FirstAsync(a => a.Code == "1010");
            account.Name = "Cash in hand";
            await db.SaveChangesAsync();
            return 0;
        });
    }

    [Theory]
    [InlineData("unbalanced")]
    [InlineData("one-line")]
    [InlineData("both-sides")]
    [InlineData("no-side")]
    [InlineData("no-lines")]
    [InlineData("cross-currency")]
    public async Task APostingThatBreaksTheRulesCannotBeSavedEvenByDirectContextUse(string problem)
    {
        await InScopeAsync(async s =>
        {
            var db = s.GetRequiredService<FinanceDbContext>();
            var cash = (await db.Accounts.FirstAsync(a => a.Code == "1010")).Id;
            var income = (await db.Accounts.FirstAsync(a => a.Code == "4900")).Id;
            LedgerEntryEntity Line(string account, decimal debit, decimal credit, string currency = "BDT") => new() { AccountId = account, Debit = debit, Credit = credit, Currency = currency };
            var lines = problem switch
            {
                "unbalanced" => new List<LedgerEntryEntity> { Line(cash, 10m, 0m), Line(income, 0m, 9m) },
                "one-line" => [Line(cash, 10m, 0m)],
                "both-sides" => [Line(cash, 10m, 10m), Line(income, 0m, 0m)],
                "no-side" => [Line(cash, 0m, 0m), Line(income, 0m, 0m)],
                "no-lines" => [],
                _ => [Line(cash, 10m, 0m, "BDT"), Line(income, 0m, 10m, "USD")]
            };
            db.Postings.Add(new PostingEntity { PostedOn = DateOnly.FromDateTime(DateTime.UtcNow), PeriodKey = "2026-10", FiscalYear = "2026-27", Memo = "bad", SourceType = "adjustment", CreatedAt = DateTimeOffset.UtcNow, Entries = lines });

            await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<InvalidOperationException>();
            return 0;
        });

        (await InScopeAsync(s => s.GetRequiredService<FinanceDbContext>().Postings.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task ThePostingAndItsLinesAreSavedTogetherOrNotAtAll()
    {
        await InScopeAsync(async s =>
        {
            var ledger = s.GetRequiredService<LedgerService>();
            var db = s.GetRequiredService<FinanceDbContext>();
            var staged = await ledger.StageAsync(new PostingDraft(ledger.Today, "staged", PostingSources.Adjustment, null,
                [LedgerLine.Debit("1010", Money.Of(3m, "BDT")), LedgerLine.Credit("4900", Money.Of(3m, "BDT"))]), "x");
            staged.Value!.Posting.Entries.Should().HaveCount(2);
            db.ChangeTracker.Clear();
            return 0;
        });

        (await InScopeAsync(s => s.GetRequiredService<FinanceDbContext>().Postings.CountAsync())).Should().Be(0, "staging alone saves nothing");
    }

    [Fact]
    public async Task TheLedgerServiceRefusesFutureDatesAndBadPeriods()
    {
        await InScopeAsync(async s =>
        {
            var ledger = s.GetRequiredService<LedgerService>();
            var future = await ledger.PostAsync(new PostingDraft(ledger.Today.AddDays(1), "future", PostingSources.Adjustment, null,
                [LedgerLine.Debit("1010", Money.Of(3m, "BDT")), LedgerLine.Credit("4900", Money.Of(3m, "BDT"))]), "x");
            future.Error!.Code.Should().Be("POSTING_DATE_FUTURE");
            (await ledger.ClosePeriodAsync("2999-01", "x")).Error!.Code.Should().Be("PERIOD_IN_FUTURE");
            (await ledger.ClosePeriodAsync("garbage", "x")).Error!.Code.Should().Be("PERIOD_INVALID");
            return 0;
        });
    }
}
