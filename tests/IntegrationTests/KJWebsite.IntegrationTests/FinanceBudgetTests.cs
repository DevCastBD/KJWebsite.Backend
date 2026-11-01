using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FinanceService.Budgets;
using FinanceService.Data;
using FinanceService.Ledger;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class FiscalCalendarRangeTests
{
    [Theory]
    [InlineData("2026-27", 7, "2026-07-01", "2027-06-30")]
    [InlineData("2099-00", 7, "2099-07-01", "2100-06-30")]
    [InlineData("2026", 1, "2026-01-01", "2026-12-31")]
    [InlineData("2025-26", 4, "2025-04-01", "2026-03-31")]
    [InlineData("2026-27", 12, "2026-12-01", "2027-11-30")]
    public void ALabelGivesItsFirstAndLastDay(string label, int startMonth, string start, string end)
    {
        FiscalCalendar.TryRange(label, startMonth, out var s, out var e).Should().BeTrue();
        (s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Should().Be((start, end));
    }

    [Theory]
    [InlineData("2026-28", 7)]
    [InlineData("2026", 7)]
    [InlineData("2026-27", 1)]
    [InlineData("26-27", 7)]
    [InlineData("abcd-ef", 7)]
    [InlineData("1800-01", 7)]
    [InlineData("", 7)]
    [InlineData(null, 7)]
    public void ALabelThisCalendarWouldNeverProduceIsRefused(string? label, int startMonth)
    {
        FiscalCalendar.TryRange(label, startMonth, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void EveryDayFallsInsideTheRangeOfItsOwnLabelAndNextDoorsAreOutside()
    {
        for (var startMonth = 1; startMonth <= 12; startMonth++)
        {
            for (var day = new DateOnly(2021, 1, 1); day < new DateOnly(2031, 1, 1); day = day.AddDays(7))
            {
                var label = FiscalCalendar.FiscalYear(day, startMonth);
                FiscalCalendar.TryRange(label, startMonth, out var start, out var end).Should().BeTrue($"{label} / {startMonth}");
                (day >= start && day <= end).Should().BeTrue($"{day:yyyy-MM-dd} in {label}");
                FiscalCalendar.FiscalYear(start.AddDays(-1), startMonth).Should().NotBe(label);
                FiscalCalendar.FiscalYear(end.AddDays(1), startMonth).Should().NotBe(label);
            }
        }
    }

    [Fact]
    public void ThresholdsDefaultToEightyAndOneHundredAndAreCleaned()
    {
        new BudgetOptions().EffectiveThresholds.Should().Equal(80, 100);
        new BudgetOptions { AlertThresholdPercents = [90, 50, 50, 0, -5] }.EffectiveThresholds.Should().Equal(50, 90);
    }

    [Theory]
    [InlineData("1000", "0", 0.0)]
    [InlineData("1000", "333.33", 33.3)]
    [InlineData("1000", "1000", 100.0)]
    [InlineData("1000", "1234.56", 123.5)]
    [InlineData("0", "5", 0.0)]
    public void PercentUsedIsOneDecimalAndSafeForAZeroPlan(string planned, string actual, double expected)
    {
        BudgetMath.PercentUsed(decimal.Parse(planned, CultureInfo.InvariantCulture), decimal.Parse(actual, CultureInfo.InvariantCulture)).Should().Be((decimal)expected);
    }

    [Fact]
    public void StatusIsOkWarningOrOver()
    {
        int[] thresholds = [80, 100];
        BudgetMath.Status(1000m, 799.99m, thresholds).Should().Be("ok");
        BudgetMath.Status(1000m, 800m, thresholds).Should().Be("warning");
        BudgetMath.Status(1000m, 1000m, thresholds).Should().Be("warning", "exactly the plan is not yet over it");
        BudgetMath.Status(1000m, 1000.01m, thresholds).Should().Be("over");
        BudgetMath.Status(1000m, 5m, []).Should().Be("ok");
    }
}

public sealed class FinanceBudgetTests : IDisposable
{
    private static string Token(string subject, params string[] permissions) => TestTokens.Issue([Permissions.FinanceView, .. permissions], subject: subject);
    private static readonly string Viewer = Token("usr_viewer");
    private static readonly string Manager = Token("usr_budget", Permissions.FinanceBudgetManage);
    private static readonly string Closer = Token("usr_closer", Permissions.FinancePeriodClose);
    private static readonly string Adjuster = Token("usr_adj", Permissions.FinanceLedgerAdjust);
    private static readonly string Submitter = Token("usr_sub", Permissions.FinanceExpenseSubmit);
    private static readonly string Approver = Token("usr_appr", Permissions.FinanceExpenseApprove);
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\n");
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly string CurrentYear = FiscalCalendar.FiscalYear(Today, 7);
    private static readonly string PreviousYear = FiscalCalendar.FiscalYear(Start(CurrentYear).AddDays(-1), 7);
    private static DateOnly Start(string label) => FiscalCalendar.TryRange(label, 7, out var s, out _) ? s : default;
    private static string Ymd(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _proofs = Path.Combine(Path.GetTempPath(), $"kj-test-proofs-{Guid.NewGuid():N}");

    public FinanceBudgetTests()
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

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, object? body = null, HttpClient? client = null)
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

        return await TestControls.SendAsync(client ?? _client, request);
    }

    private async Task<JsonElement> GetAsync(string path, string? token = null)
    {
        using var response = await SendAsync(HttpMethod.Get, path, token ?? Viewer);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> BudgetAsync(string category = "programme", string planned = "10000.00", string currency = "BDT", string? project = null, string? year = null, HttpStatusCode expected = HttpStatusCode.Created)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/budgets", Manager, new { year, category, project, planned, currency });
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> ApprovedExpenseAsync(string amount, string category = "programme", string? project = null, string currency = "BDT", string? date = null)
    {
        using var submit = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/expenses", Submitter, new { payee = "P", description = "d", category, amount, currency, project, date });
        submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        var id = (await submit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        using var upload = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/finance/expenses/{id}/receipts") { Content = new ByteArrayContent(Pdf) };
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Submitter);
        (await _client.SendAsync(upload)).StatusCode.Should().Be(HttpStatusCode.Created);
        for (var i = 0; i < (decimal.Parse(amount, CultureInfo.InvariantCulture) >= 10000m && currency == "BDT" ? 2 : 1); i++)
        {
            (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{id}/approve", Token($"usr_appr{i}", Permissions.FinanceExpenseApprove), new { })).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        return id;
    }

    private async Task<HttpResponseMessage> AdjustAsync(string debit, string credit, string amount, DateOnly? on = null, string currency = "BDT") =>
        await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", Adjuster, new
        {
            postedOn = on is null ? null : Ymd(on.Value),
            memo = "adjustment",
            reason = "test",
            lines = new[] { new { account = debit, debit = (string?)amount, credit = (string?)null, currency }, new { account = credit, debit = (string?)null, credit = (string?)amount, currency } }
        });

    private async Task<JsonElement> RowAsync(string id, string? year = null) =>
        (await GetAsync($"/api/v1/admin/finance/budgets{(year is null ? string.Empty : "?year=" + year)}")).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id);

    [Fact]
    public async Task ABudgetLineIsCreatedForTheCurrentYearAndListedInTheFrontendShape()
    {
        var created = await BudgetAsync("Programme", "50000.00", project: "school");

        (created.GetProperty("fiscalYear").GetString(), created.GetProperty("category").GetString(), created.GetProperty("project").GetString(), created.GetProperty("planned").GetString(), created.GetProperty("actual").GetString(),
            created.GetProperty("currency").GetString(), created.GetProperty("remaining").GetString(), created.GetProperty("percentUsed").GetString(), created.GetProperty("status").GetString(), created.GetProperty("yearClosed").GetBoolean())
            .Should().Be((CurrentYear, "programme", "school", "50000.00", "0.00", "BDT", "50000.00", "0.0", "ok", false));
        var list = await GetAsync("/api/v1/admin/finance/budgets");
        list.GetProperty("total").GetInt32().Should().Be(1);
        (await GetAsync($"/api/v1/admin/finance/budgets?year={PreviousYear}")).GetProperty("total").GetInt32().Should().Be(0);
        (await GetAsync("/api/v1/admin/finance/budgets?project=school")).GetProperty("total").GetInt32().Should().Be(1);
        (await GetAsync("/api/v1/admin/finance/budgets?category=events")).GetProperty("total").GetInt32().Should().Be(0);
        (await GetAsync("/api/v1/admin/finance/budgets?currency=USD")).GetProperty("total").GetInt32().Should().Be(0);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/budgets?year=2026-99", Viewer)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("bad-year")]
    [InlineData("bad-category")]
    [InlineData("bad-project")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("third-decimal")]
    [InlineData("bad-currency")]
    public async Task InvalidBudgetLinesAreRefused(string problem)
    {
        object body = problem switch
        {
            "bad-year" => new { year = "2026-28", category = "programme", planned = "10.00", currency = "BDT" },
            "bad-category" => new { category = "yachts", planned = "10.00", currency = "BDT" },
            "bad-project" => new { category = "programme", project = "Not A Slug", planned = "10.00", currency = "BDT" },
            "zero" => new { category = "programme", planned = "0.00", currency = "BDT" },
            "negative" => new { category = "programme", planned = "-5.00", currency = "BDT" },
            "third-decimal" => new { category = "programme", planned = "10.005", currency = "BDT" },
            _ => new { category = "programme", planned = "10.00", currency = "XYZ" }
        };

        using var response = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/budgets", Manager, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await DbAsync(db => db.Budgets.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task OneLinePerYearCurrencyProjectAndCategoryAndItCanBeChangedOrRemoved()
    {
        var organisation = await BudgetAsync("events", "1000.00");
        await BudgetAsync("events", "1000.00", expected: HttpStatusCode.Conflict);
        var project = await BudgetAsync("events", "400.00", project: "camp");
        await BudgetAsync("events", "400.00", project: "camp", expected: HttpStatusCode.Conflict);
        await BudgetAsync("events", "20.00", "USD");
        (await DbAsync(db => db.Budgets.CountAsync())).Should().Be(3);

        using var changed = await SendAsync(HttpMethod.Put, $"/api/v1/admin/finance/budgets/{organisation.GetProperty("id").GetString()}", Manager, new { planned = "1500.50" });
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("planned").GetString().Should().Be("1500.50");
        (await SendAsync(HttpMethod.Put, $"/api/v1/admin/finance/budgets/{organisation.GetProperty("id").GetString()}", Manager, new { planned = "0.00" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Put, "/api/v1/admin/finance/budgets/bud_missing", Manager, new { planned = "5.00" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Delete, $"/api/v1/admin/finance/budgets/{project.GetProperty("id").GetString()}", Manager)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Delete, "/api/v1/admin/finance/budgets/bud_missing", Manager)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await DbAsync(db => db.Budgets.CountAsync())).Should().Be(2);
        await BudgetAsync("events", "400.00", project: "camp");
    }

    [Fact]
    public async Task BudgetWritesNeedTheirPermissionAndAreAudited()
    {
        var body = new { category = "programme", planned = "10.00", currency = "BDT" };
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/budgets", null, body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/budgets", Viewer, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/budgets", TestTokens.Issue([Permissions.ContentView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var line = await BudgetAsync();
        await SendAsync(HttpMethod.Put, $"/api/v1/admin/finance/budgets/{line.GetProperty("id").GetString()}", Manager, new { planned = "20.00" });
        await SendAsync(HttpMethod.Delete, $"/api/v1/admin/finance/budgets/{line.GetProperty("id").GetString()}", Manager);

        var audit = await GetAsync("/api/v1/admin/finance/audit?limit=100", TestTokens.Issue([Permissions.AuditView]));

        audit.GetProperty("items").EnumerateArray().Where(e => e.GetProperty("entity").GetString() == "Budget").Select(e => (e.GetProperty("action").GetString(), e.GetProperty("actorId").GetString()))
            .Should().BeEquivalentTo([("create", "usr_budget"), ("update", "usr_budget"), ("delete", "usr_budget")]);
        KjRoles.Seed[KjRoles.FinanceOfficer].Should().Contain(Permissions.FinanceBudgetManage);
        KjRoles.Seed[KjRoles.Admin].Should().Contain(Permissions.FinanceBudgetManage);
    }

    [Fact]
    public async Task ActualSpendComesFromTheLedgerPerCategoryProjectAndCurrency()
    {
        var organisation = await BudgetAsync("programme", "10000.00");
        var school = await BudgetAsync("programme", "6000.00", project: "school");
        var events = await BudgetAsync("events", "2000.00");
        var usd = await BudgetAsync("programme", "100.00", "USD");
        await ApprovedExpenseAsync("3000.00", "programme", "school");
        await ApprovedExpenseAsync("1000.00", "programme");
        await ApprovedExpenseAsync("500.00", "events");
        await ApprovedExpenseAsync("40.00", "programme", "school", "USD");
        (await AdjustAsync("5010", "1010", "200.00")).StatusCode.Should().Be(HttpStatusCode.Created);
        // Submitted but never approved: not spend yet.
        using var pending = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/expenses", Submitter, new { payee = "P", description = "d", category = "programme", amount = "9999.00", currency = "BDT", project = "school" });
        pending.StatusCode.Should().Be(HttpStatusCode.Created);

        var org = await RowAsync(organisation.GetProperty("id").GetString()!);
        (org.GetProperty("actual").GetString(), org.GetProperty("remaining").GetString(), org.GetProperty("percentUsed").GetString(), org.GetProperty("status").GetString()).Should().Be(("4200.00", "5800.00", "42.0", "ok"), "3000 + 1000 + the 200 manual adjustment, BDT only");
        var proj = await RowAsync(school.GetProperty("id").GetString()!);
        (proj.GetProperty("actual").GetString(), proj.GetProperty("percentUsed").GetString()).Should().Be(("3000.00", "50.0"), "only spend from expenses coded to the project");
        (await RowAsync(events.GetProperty("id").GetString()!)).GetProperty("actual").GetString().Should().Be("500.00");
        var dollars = await RowAsync(usd.GetProperty("id").GetString()!);
        (dollars.GetProperty("actual").GetString(), dollars.GetProperty("status").GetString()).Should().Be(("40.00", "ok"));
    }

    [Fact]
    public async Task AReversalTakesTheSpendBackOutAndOtherYearsAreNotCounted()
    {
        var organisation = await BudgetAsync("programme", "10000.00");
        var school = await BudgetAsync("programme", "6000.00", project: "school");
        var previous = await BudgetAsync("programme", "900.00", year: PreviousYear);
        var id = await ApprovedExpenseAsync("3000.00", "programme", "school");
        (await AdjustAsync("5010", "1010", "777.00", Start(CurrentYear).AddDays(-1))).StatusCode.Should().Be(HttpStatusCode.Created);

        var postingId = (await DbAsync(db => db.Postings.Where(p => p.SourceId == id).Select(p => p.Id).SingleAsync())).ToString(CultureInfo.InvariantCulture);
        (await RowAsync(organisation.GetProperty("id").GetString()!)).GetProperty("actual").GetString().Should().Be("3000.00", "the 777 adjustment is dated in the previous fiscal year");
        (await RowAsync(previous.GetProperty("id").GetString()!, PreviousYear)).GetProperty("actual").GetString().Should().Be("777.00");

        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/postings/{postingId}/reversal", Adjuster, new { reason = "Entered in error" })).StatusCode.Should().Be(HttpStatusCode.Created);

        (await RowAsync(organisation.GetProperty("id").GetString()!)).GetProperty("actual").GetString().Should().Be("0.00");
        (await RowAsync(school.GetProperty("id").GetString()!)).GetProperty("actual").GetString().Should().Be("0.00", "a reversal follows the posting it reverses, project included");
    }

    [Fact]
    public async Task ThePercentUsedAndStatusFollowTheSpend()
    {
        var line = await BudgetAsync("events", "1000.00");
        var id = line.GetProperty("id").GetString()!;
        (await AdjustAsync("5050", "1010", "799.99")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await RowAsync(id)).GetProperty("status").GetString().Should().Be("ok");
        (await AdjustAsync("5050", "1010", "0.01")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await RowAsync(id)).GetProperty("status").GetString().Should().Be("warning");
        (await AdjustAsync("5050", "1010", "200.00")).StatusCode.Should().Be(HttpStatusCode.Created);
        var full = await RowAsync(id);
        (full.GetProperty("status").GetString(), full.GetProperty("percentUsed").GetString(), full.GetProperty("remaining").GetString()).Should().Be(("warning", "100.0", "0.00"));
        (await AdjustAsync("5050", "1010", "50.00")).StatusCode.Should().Be(HttpStatusCode.Created);
        var over = await RowAsync(id);
        (over.GetProperty("status").GetString(), over.GetProperty("remaining").GetString()).Should().Be(("over", "-50.00"));
    }

    [Fact]
    public async Task AlertsFireOnceAtEachThresholdAndAreRecordedAndLogged()
    {
        var line = await BudgetAsync("events", "1000.00");
        async Task<JsonElement> Run()
        {
            using var response = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/budgets/alerts/run", Manager, new { });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        (await AdjustAsync("5050", "1010", "700.00")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Run()).GetProperty("fired").GetInt32().Should().Be(0);
        (await AdjustAsync("5050", "1010", "150.00")).StatusCode.Should().Be(HttpStatusCode.Created);
        var first = await Run();
        (first.GetProperty("checked").GetInt32(), first.GetProperty("fired").GetInt32()).Should().Be((1, 1));
        (await Run()).GetProperty("fired").GetInt32().Should().Be(0, "the 80% alert fires once");
        (await AdjustAsync("5050", "1010", "150.00")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Run()).GetProperty("fired").GetInt32().Should().Be(1, "now the 100% alert");
        (await AdjustAsync("5050", "1010", "500.00")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Run()).GetProperty("fired").GetInt32().Should().Be(0, "no further thresholds");

        var alerts = (await GetAsync("/api/v1/admin/finance/budgets/alerts")).GetProperty("items").EnumerateArray().ToList();
        alerts.Select(a => (a.GetProperty("thresholdPercent").GetInt32(), a.GetProperty("actual").GetString(), a.GetProperty("category").GetString(), a.GetProperty("budgetId").GetString())).Should().Equal((100, "1000.00", "events", line.GetProperty("id").GetString()), (80, "850.00", "events", line.GetProperty("id").GetString()));
        _factory.Logs.Lines.Should().Contain(l => l.Contains("Budget alert", StringComparison.Ordinal) && l.Contains("80%", StringComparison.Ordinal)).And.Contain(l => l.Contains("100%", StringComparison.Ordinal));
        (await DbAsync(db => db.Notifications.CountAsync(n => n.Template == "budget.alert"))).Should().Be(0, "no addresses configured");
    }

    [Fact]
    public async Task ConfiguredThresholdsAndAddressesDriveTheAlertsAndTheOutbox()
    {
        using var factory = new FinanceApiFactory();
        factory.Settings["Finance:Budgets:AlertThresholdPercents:0"] = "50";
        factory.Settings["Finance:Budgets:AlertEmails:0"] = "treasurer@example.org";
        factory.Settings["Finance:Budgets:AlertEmails:1"] = "chair@example.org";
        using var client = factory.CreateClient();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/budgets", Manager, new { category = "events", planned = "1000.00", currency = "BDT" }, client);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", Adjuster, new
        {
            memo = "m",
            reason = "test",
            lines = new[] { new { account = "5050", debit = (string?)"500.00", credit = (string?)null, currency = "BDT" }, new { account = "1010", debit = (string?)null, credit = (string?)"500.00", currency = "BDT" } }
        }, client)).StatusCode.Should().Be(HttpStatusCode.Created);

        using var run = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/budgets/alerts/run", Manager, new { }, client);

        (await run.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fired").GetInt32().Should().Be(1, "50% is the only threshold and it was just reached");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<FinanceService.Donations.IDonorProtector>();
        var rows = await db.Notifications.AsNoTracking().Where(n => n.Template == "budget.alert").ToListAsync();
        rows.Select(r => protector.Unprotect(r.RecipientProtected)).Should().BeEquivalentTo(["treasurer@example.org", "chair@example.org"]);
        protector.Unprotect(rows[0].PayloadProtected).Should().Contain("\"threshold\":50").And.Contain("\"actual\":\"500.00\"");
        rows.Should().OnlyContain(r => r.RecipientProtected.Contains("example", StringComparison.Ordinal) == false);
    }

    [Fact]
    public async Task AnAlertRunNeedsTheBudgetPermission()
    {
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/budgets/alerts/run", Viewer, new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/budgets/alerts/run", null, new { })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/budgets/alerts", TestTokens.Issue([Permissions.ContentView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task FiscalYearsAreListedOpenWithTheirDatesAndCountsAndCloseOnlyAfterTheyEnd()
    {
        await BudgetAsync();
        (await AdjustAsync("5010", "1010", "5.00", Start(CurrentYear).AddDays(-3))).StatusCode.Should().Be(HttpStatusCode.Created);

        var years = (await GetAsync("/api/v1/admin/finance/fiscal-years")).GetProperty("items").EnumerateArray().ToList();
        years.Select(y => y.GetProperty("label").GetString()).Should().Equal(CurrentYear, PreviousYear);
        var current = years[0];
        (current.GetProperty("status").GetString(), current.GetProperty("start").GetString(), current.GetProperty("budgets").GetInt32()).Should().Be(("open", Ymd(Start(CurrentYear)), 1));
        years[1].GetProperty("postings").GetInt32().Should().Be(1);

        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/fiscal-years/{CurrentYear}/close", Closer, new { })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/fiscal-years/2999-00/close", Closer, new { })).StatusCode.Should().Be(HttpStatusCode.Conflict, "a year far in the future has not ended");
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/fiscal-years/2026-99/close", Closer, new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/fiscal-years/{PreviousYear}/close", Viewer, new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/fiscal-years/{PreviousYear}/close", null, new { })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var closed = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/fiscal-years/{PreviousYear}/close", Closer, new { });
        closed.StatusCode.Should().Be(HttpStatusCode.OK, await closed.Content.ReadAsStringAsync());
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/fiscal-years/{PreviousYear}/close", Closer, new { })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var after = (await GetAsync("/api/v1/admin/finance/fiscal-years")).GetProperty("items").EnumerateArray().Single(y => y.GetProperty("label").GetString() == PreviousYear);
        (after.GetProperty("status").GetString(), after.GetProperty("closedBy").GetString()).Should().Be(("closed", "usr_closer"));
        (await DbAsync(async db =>
        {
            db.FiscalYearCloses.Remove(await db.FiscalYearCloses.FirstAsync());
            await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<InvalidOperationException>();
            return 0;
        })).Should().Be(0);
    }

    [Fact]
    public async Task AClosedFiscalYearRejectsEveryPostingDatedInItAndFreezesItsBudgets()
    {
        var old = Start(CurrentYear).AddDays(-30);
        var budget = await BudgetAsync("programme", "1000.00", year: PreviousYear);
        var posted = await AdjustAsync("5010", "1010", "10.00", old);
        posted.StatusCode.Should().Be(HttpStatusCode.Created);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/fiscal-years/{PreviousYear}/close", Closer, new { })).StatusCode.Should().Be(HttpStatusCode.OK);

        using var refused = await AdjustAsync("5010", "1010", "10.00", old);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("FISCAL_YEAR_CLOSED");
        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(1);

        // The same date through the ledger service directly, and a closed month inside the year is irrelevant: the year is what is closed.
        var direct = await ScopeAsync(s => s.GetRequiredService<LedgerService>().PostAsync(new PostingDraft(old, "direct", FinanceService.Data.Entities.PostingSources.Adjustment, null,
            [LedgerLine.Debit("5010", KJWebsite.BuildingBlocks.Finance.Money.Of(1m, "BDT")), LedgerLine.Credit("1010", KJWebsite.BuildingBlocks.Finance.Money.Of(1m, "BDT"))]), "x"));
        direct.Error!.Code.Should().Be("FISCAL_YEAR_CLOSED");

        var id = budget.GetProperty("id").GetString()!;
        foreach (var attempt in new Func<Task<HttpResponseMessage>>[]
        {
            () => SendAsync(HttpMethod.Put, $"/api/v1/admin/finance/budgets/{id}", Manager, new { planned = "2000.00" }),
            () => SendAsync(HttpMethod.Delete, $"/api/v1/admin/finance/budgets/{id}", Manager),
            () => SendAsync(HttpMethod.Post, "/api/v1/admin/finance/budgets", Manager, new { year = PreviousYear, category = "events", planned = "5.00", currency = "BDT" })
        })
        {
            using var frozen = await attempt();
            frozen.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await frozen.Content.ReadAsStringAsync()).Should().Contain("FISCAL_YEAR_CLOSED");
        }

        var row = await RowAsync(id, PreviousYear);
        (row.GetProperty("planned").GetString(), row.GetProperty("actual").GetString(), row.GetProperty("yearClosed").GetBoolean()).Should().Be(("1000.00", "10.00", true));
    }

    [Fact]
    public async Task AnExpenseOrOfflineGiftDatedInAClosedYearIsPostedTodayInsteadOfFailing()
    {
        var old = Start(CurrentYear).AddDays(-30);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/fiscal-years/{PreviousYear}/close", Closer, new { })).StatusCode.Should().Be(HttpStatusCode.OK);

        var expense = await ApprovedExpenseAsync("500.00", "programme", date: Ymd(old));

        var posting = await DbAsync(db => db.Postings.AsNoTracking().SingleAsync(p => p.SourceId == expense));
        posting.PostedOn.Should().Be(Today);
        posting.FiscalYear.Should().Be(CurrentYear);
    }

    [Fact]
    public async Task TheFiscalYearCloseIsAuditedWithTheCaller()
    {
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/fiscal-years/{PreviousYear}/close", Closer, new { })).StatusCode.Should().Be(HttpStatusCode.OK);

        var audit = await GetAsync("/api/v1/admin/finance/audit?limit=100", TestTokens.Issue([Permissions.AuditView]));

        audit.GetProperty("items").EnumerateArray().Select(e => (e.GetProperty("entity").GetString(), e.GetProperty("actorId").GetString())).Should().Contain(("FiscalYearClose", "usr_closer"));
    }

    [Fact]
    public async Task AClosedYearIsNotCheckedForAlerts()
    {
        var old = Start(CurrentYear).AddDays(-30);
        await BudgetAsync("events", "100.00", year: PreviousYear);
        (await AdjustAsync("5050", "1010", "95.00", old)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/fiscal-years/{PreviousYear}/close", Closer, new { })).StatusCode.Should().Be(HttpStatusCode.OK);

        using var run = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/budgets/alerts/run", Manager, new { });

        var result = await run.Content.ReadFromJsonAsync<JsonElement>();
        (result.GetProperty("checked").GetInt32(), result.GetProperty("fired").GetInt32()).Should().Be((0, 0));
    }
}
