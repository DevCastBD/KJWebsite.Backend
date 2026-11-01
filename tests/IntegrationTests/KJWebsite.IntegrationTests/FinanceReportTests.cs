using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Ledger;
using FinanceService.Reports;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class ReportExportTests
{
    private static readonly ReportTable Sample = new("Donations", ["Name", "Amount", "Count"],
    [
        ["রহিম উদ্দিন", 1234.5m, 2],
        ["Quote \"q\", comma", -0.5m, null],
        ["=HYPERLINK(\"x\")", 0m, 1],
        ["line\nbreak", 10m, 3]
    ]);

    [Fact]
    public void CsvHasAByteOrderMarkCrlfQuotingAndNoFormulaInjection()
    {
        var bytes = ReportExport.Csv(Sample);
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var lines = text.Split("\r\n");
        lines[0].Should().Be("Name,Amount,Count");
        lines[1].Should().Be("রহিম উদ্দিন,1234.50,2");
        lines[2].Should().Be("\"Quote \"\"q\"\", comma\",-0.50,");
        lines[3].Should().Be("\"'=HYPERLINK(\"\"x\"\")\",0.00,1", "a text cell that starts with = must never reach a spreadsheet as a formula");
        text.Should().Contain("\"line\nbreak\",10.00,3");
        text.Should().EndWith("\r\n");
    }

    [Fact]
    public void BengaliTextSurvivesCsvAndXlsxByteForByte()
    {
        var csv = Encoding.UTF8.GetString(ReportExport.Csv(Sample)).TrimStart('﻿');
        csv.Should().Contain("রহিম উদ্দিন");

        using var workbook = new ZipArchive(new MemoryStream(ReportExport.Xlsx([Sample])));
        var sheet = XDocument.Load(workbook.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        sheet.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value).Should().Contain("রহিম উদ্দিন");
    }

    [Fact]
    public void AnXlsxIsAWellFormedWorkbookWithNumbersAsNumbersAndSafeSheetNames()
    {
        var tables = new[]
        {
            Sample,
            new ReportTable("A very long sheet name that goes past thirty one characters [x]", ["h"], [["bad\u0001char", 1m]]),
            new ReportTable("A very long sheet name that goes past thirty one characters [y]", ["h"], []),
            new ReportTable("Donations", ["h"], [])
        };
        var bytes = ReportExport.Xlsx(tables);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal) || e.FullName.EndsWith(".rels", StringComparison.Ordinal)))
        {
            var parse = () => XDocument.Load(entry.Open());
            parse.Should().NotThrow(entry.FullName);
        }

        var names = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open()).Descendants().Where(e => e.Name.LocalName == "sheet").Select(e => e.Attribute("name")!.Value).ToList();
        names.Should().HaveCount(4);
        names.Should().OnlyContain(n => n.Length <= 31 && !n.Contains('[', StringComparison.Ordinal));
        names.Select(n => n.ToLowerInvariant()).Should().OnlyHaveUniqueItems();

        var sheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var numeric = sheet.Descendants().Where(e => e.Name.LocalName == "c" && e.Attribute("t") is null).Select(e => e.Value).ToList();
        numeric.Should().Contain("1234.50").And.Contain("-0.50").And.Contain("2");
        XDocument.Load(zip.GetEntry("xl/worksheets/sheet2.xml")!.Open()).Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value).Should().Contain("badchar");

        ReportExport.Xlsx(tables).Should().Equal(bytes, "the workbook depends only on its content");
    }

    [Fact]
    public void AZipOfCsvsHasOneFilePerTable()
    {
        using var zip = new ZipArchive(new MemoryStream(ReportExport.CsvZip([Sample, new ReportTable("Trial balance", ["x"], [])])));
        zip.Entries.Select(e => e.FullName).Should().Equal("donations.csv", "trial-balance.csv");
    }
}

public sealed class FinanceReportTests : IDisposable
{
    private static string Token(string subject, params string[] permissions) => TestTokens.Issue([Permissions.FinanceView, .. permissions], subject: subject);
    private static readonly string Viewer = Token("usr_viewer");
    private static readonly string Generator = Token("usr_gen", Permissions.FinanceReportGenerate);
    private static readonly string Adjuster = Token("usr_adj", Permissions.FinanceLedgerAdjust);
    private static readonly string Submitter = Token("usr_sub", Permissions.FinanceExpenseSubmit);
    private static readonly string Reverser = Token("usr_rev", Permissions.FinanceDonationReverse);
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\n");
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly string ThisMonth = Today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    private static string Ymd(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _proofs = Path.Combine(Path.GetTempPath(), $"kj-test-reports-{Guid.NewGuid():N}");

    public FinanceReportTests()
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

        return await TestControls.SendAsync(_client, request);
    }

    private async Task<JsonElement> JsonAsync(HttpMethod method, string path, string token, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await SendAsync(method, path, token, body);
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<JsonElement> ReportAsync(string name, string query = "") => JsonAsync(HttpMethod.Get, $"/api/v1/admin/finance/reports/{name}{query}", Viewer);

    private async Task PostAsync(string debit, string credit, string amount, DateOnly? on = null, string currency = "BDT", string memo = "report test")
    {
        await JsonAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", Adjuster, new
        {
            postedOn = on is null ? null : Ymd(on.Value),
            memo,
            reason = "report test",
            lines = new[] { new { account = debit, debit = (string?)amount, credit = (string?)null, currency }, new { account = credit, debit = (string?)null, credit = (string?)amount, currency } }
        }, HttpStatusCode.Created);
    }

    private static IEnumerable<string?[]> Rows(JsonElement report, int table = 0) =>
        report.GetProperty("tables")[table].GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Null ? null : c.ToString()).ToArray());

    private static string Amount(JsonElement report, string name, string currency = "BDT", int table = 0) =>
        Rows(report, table).Single(r => r[0] == currency && r.Contains(name)).Last()!;

    private async Task<(string Id, string Reference)> DonationAsync(string method = "bkash", string email = "donor@example.org", string amount = "1000.00", string name = "রহিম উদ্দিন", object? designation = null, DateOnly? on = null)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/donations", new
        {
            amount,
            currency = "BDT",
            frequency = "once",
            designation = designation ?? new { type = "general" },
            donor = new { name, email },
            anonymous = false,
            coverFees = false,
            language = "en",
            communicationConsent = false
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = json.GetProperty("id").GetString()!;
        (await ScopeAsync(s => s.GetRequiredService<DonationConfirmation>().ConfirmAsync(id, method, null, on))).IsSuccess.Should().BeTrue();
        return (id, json.GetProperty("reference").GetString()!);
    }

    private async Task<string> ApprovedExpenseAsync(string amount, string category, string? project = null)
    {
        var submitted = await JsonAsync(HttpMethod.Post, "/api/v1/admin/finance/expenses", Submitter, new { payee = "P", description = "d", category, amount, currency = "BDT", project }, HttpStatusCode.Created);
        var id = submitted.GetProperty("id").GetString()!;
        using var upload = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/finance/expenses/{id}/receipts") { Content = new ByteArrayContent(Pdf) };
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Submitter);
        (await _client.SendAsync(upload)).StatusCode.Should().Be(HttpStatusCode.Created);
        await JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{id}/approve", Token("usr_appr", Permissions.FinanceExpenseApprove), new { });
        return id;
    }

    // ---- statements reconcile with the ledger --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheIncomeStatementIsTheLedgersIncomeAndExpensesAndAgreesWithTheTrialBalance()
    {
        await PostAsync("1010", "4900", "1000.00");
        await PostAsync("5010", "1010", "400.00");
        await PostAsync("1010", "4900", "75.50", currency: "USD");

        var report = await ReportAsync("income-statement");
        Amount(report, "Total income").Should().Be("1000.00");
        Amount(report, "Total expenses").Should().Be("400.00");
        Amount(report, "Surplus (deficit)").Should().Be("600.00");
        Amount(report, "Surplus (deficit)", "USD").Should().Be("75.50", "currencies are never mixed");

        var trial = await ReportAsync("trial-balance");
        trial.GetProperty("checks").EnumerateArray().Should().OnlyContain(c => c.GetProperty("ok").GetBoolean());
        var ledger = await JsonAsync(HttpMethod.Get, $"/api/v1/admin/finance/ledger/trial-balance?asOf={Ymd(Today)}", Viewer);
        ledger.GetProperty("currencies").EnumerateArray().Single(c => c.GetProperty("currency").GetString() == "BDT").GetProperty("totalDebit").GetString()
            .Should().Be(Rows(trial).Single(r => r[0] == "BDT" && r[2] == "Total")[4], "the report's totals are the ledger's");
    }

    [Fact]
    public async Task TheBalanceSheetBalancesWithAccumulatedSurplusAndEveryCheckIsGreen()
    {
        await PostAsync("1010", "3010", "5000.00");
        await PostAsync("1010", "4900", "1000.00");
        await PostAsync("5010", "1010", "300.00");
        await PostAsync("5030", "2100", "120.00");

        var report = await ReportAsync("balance-sheet");
        Amount(report, "Total assets").Should().Be("5700.00");
        Amount(report, "Total liabilities").Should().Be("120.00");
        Amount(report, "Accumulated surplus (deficit)").Should().Be("580.00", "1000 income less 300 and 120 expense, with no year-end closing entry");
        Amount(report, "Total liabilities and equity").Should().Be("5700.00");
        report.GetProperty("checks").EnumerateArray().Should().ContainSingle().Which.GetProperty("ok").GetBoolean().Should().BeTrue();

        var before = await ReportAsync("balance-sheet", $"?asOf={Ymd(Today.AddDays(-30))}");
        before.GetProperty("tables")[0].GetProperty("rows").GetArrayLength().Should().Be(0, "nothing was posted that early");
    }

    [Fact]
    public async Task TheCashFlowFollowsTheMoneyAndClosesOnTheLedgerBalance()
    {
        var firstOfMonth = new DateOnly(Today.Year, Today.Month, 1);
        await PostAsync("1010", "3010", "2000.00", firstOfMonth.AddDays(-5));
        await PostAsync("1010", "4010", "800.00", firstOfMonth);
        await PostAsync("5010", "1010", "150.00", firstOfMonth);
        await PostAsync("1020", "1010", "500.00", firstOfMonth);
        await PostAsync("1020", "1140", "300.00", firstOfMonth);

        var report = await ReportAsync("cash-flow", $"?from={Ymd(firstOfMonth)}&to={Ymd(Today)}");
        var rows = Rows(report).ToList();
        string[] Row(string line) => rows.Single(r => r[1] == line)!.Select(c => c ?? "").ToArray();
        rows.Single(r => r[1] == "Opening balance")[4].Should().Be("2000.00");
        Row("Donations received")[2..].Should().Equal("800.00", "0.00", "800.00");
        Row("Expenses paid")[2..].Should().Equal("0.00", "150.00", "-150.00");
        Row("Gateway settlements")[2..].Should().Equal("300.00", "0.00", "300.00");
        rows.Any(r => r[1] == "Other movements").Should().BeFalse("a transfer between two cash accounts is not a cash movement");
        rows.Single(r => r[1] == "Net movement")[4].Should().Be("950.00");
        rows.Single(r => r[1] == "Closing balance")[4].Should().Be("2950.00");
        rows.Single(r => (r[1] ?? "").StartsWith("Agrees", StringComparison.Ordinal))[4].Should().Be("2950.00");
        report.GetProperty("checks").EnumerateArray().Should().OnlyContain(c => c.GetProperty("ok").GetBoolean());
    }

    // ---- donation, project and donor reports -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DonationsAreSummarisedByChannelDesignationAndMonthAndReversalsComeOff()
    {
        await TestCampaigns.SeedAsync(_factory.Services, "flood-relief", currency: "BDT");
        await DonationAsync("bkash", amount: "1000.00");
        await DonationAsync("stripe", "b@example.org", amount: "500.00", designation: new { type = "campaign", id = "flood-relief" });
        var (reversedId, _) = await DonationAsync("bkash", "c@example.org", amount: "250.00");
        await JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{reversedId}/reversal", Reverser, new { reason = "Chargeback" }, HttpStatusCode.Created);

        var report = await ReportAsync("donations");
        var rows = Rows(report).ToList();
        string[] Line(string grouping, string key) => rows.Single(r => r[1] == grouping && r[2] == key).Select(c => c ?? "").ToArray();
        rows.Single(r => r[1] == "Total")[4].Should().Be("1500.00", "1000 + 500 + 250 - 250");
        rows.Single(r => r[1] == "Total")[5].Should().Be("2", "the reversed gift counts as no gift");
        Line("Channel", "1120")[4..].Should().Equal("1000.00", "1");
        Line("Channel", "1140")[4..].Should().Equal("500.00", "1");
        Line("Designation", "general")[4].Should().Be("1000.00");
        Line("Designation", "campaign:flood-relief")[4].Should().Be("500.00");
        Line("Month", ThisMonth)[4].Should().Be("1500.00");

        var income = await ReportAsync("income-statement");
        Amount(income, "Donations - general").Should().Be("1000.00", "the summary agrees with the ledger's income");
    }

    [Fact]
    public async Task ProjectCostsGroupApprovedSpendAndShowManualPostingsSoTheTotalEqualsTheStatement()
    {
        await ApprovedExpenseAsync("3000.00", "programme", "school");
        await ApprovedExpenseAsync("1000.00", "events");
        await PostAsync("5900", "1010", "200.00");

        var report = await ReportAsync("project-costs");
        var rows = Rows(report).ToList();
        rows.Single(r => r[1] == "school" && r[2] == "programme")[3].Should().Be("3000.00");
        rows.Single(r => r[1] == "(organisation)" && r[2] == "events")[3].Should().Be("1000.00");
        rows.Single(r => r[1] == "(manual postings)")[3].Should().Be("200.00");
        rows.Single(r => r[1] == "Total")[3].Should().Be("4200.00");
        Amount(await ReportAsync("income-statement"), "Total expenses").Should().Be("4200.00");
        report.GetProperty("checks").EnumerateArray().Should().ContainSingle().Which.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task TheDonorReportCountsNewAndReturningDonorsAndLeaksNoPersonalDetail()
    {
        var firstOfMonth = new DateOnly(Today.Year, Today.Month, 1);
        await DonationAsync("bkash", "old@example.org", "100.00", on: firstOfMonth.AddDays(-10));
        await DonationAsync("bkash", "old@example.org", "300.00", "Old Donor Name");
        await DonationAsync("bkash", "new@example.org", "600.00", "New Donor Name");
        await DonationAsync("bkash", "new@example.org", "100.00", "New Donor Name");

        var response = await ReportAsync("donors", $"?from={Ymd(firstOfMonth)}&to={Ymd(Today)}");
        var row = Rows(response).Single();
        row.Should().Equal("BDT", "2", "1", "1", "3", "1000.00", "333.33", "600.00", "0", "0.00");
        var text = response.GetRawText();
        text.Should().NotContain("example.org").And.NotContain("Donor Name").And.NotContain("রহিম");
    }

    [Fact]
    public async Task TheExpenseAndDonationRegistersAndTheJournalListTheLedgerLines()
    {
        await DonationAsync("bkash", amount: "700.00");
        var expense = await ApprovedExpenseAsync("900.00", "programme", "school");
        var register = Rows(await ReportAsync("expense-register")).Single(r => r[2] == expense);
        register[5].Should().Be("school");
        register[6].Should().Be("programme");
        register[7].Should().Be("900.00");
        Rows(await ReportAsync("donation-register")).Should().ContainSingle().Which[5].Should().Be("700.00");
        Rows(await ReportAsync("general-ledger")).Count().Should().BeGreaterThanOrEqualTo(4);
    }

    // ---- formats and request rules -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryReportExportsAsCsvAndXlsxAndTheAuditorPackAsAZipOrAWorkbook()
    {
        await PostAsync("1010", "4900", "10.00", memo: "দান — Bengali memo");
        foreach (var name in ReportNames.All.Where(n => n != ReportNames.AuditorPack))
        {
            using var csv = await SendAsync(HttpMethod.Get, $"/api/v1/admin/finance/reports/{name}?format=csv", Viewer);
            csv.StatusCode.Should().Be(HttpStatusCode.OK, name);
            csv.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
            (await csv.Content.ReadAsByteArrayAsync()).Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
            using var xlsx = await SendAsync(HttpMethod.Get, $"/api/v1/admin/finance/reports/{name}?format=xlsx", Viewer);
            xlsx.StatusCode.Should().Be(HttpStatusCode.OK, name);
            xlsx.Content.Headers.ContentType!.MediaType.Should().Be(ReportService.XlsxType);
        }

        using var zip = await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/reports/auditor-pack?format=csv", Viewer);
        zip.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        using var archive = new ZipArchive(new MemoryStream(await zip.Content.ReadAsByteArrayAsync()));
        archive.Entries.Should().HaveCount(11);
        Encoding.UTF8.GetString(await ReadAsync(archive.GetEntry("general-ledger.csv")!)).Should().Contain("দান — Bengali memo");

        using var book = await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/reports/auditor-pack?format=xlsx", Viewer);
        using var workbook = new ZipArchive(new MemoryStream(await book.Content.ReadAsByteArrayAsync()));
        XDocument.Load(workbook.GetEntry("xl/workbook.xml")!.Open()).Descendants().Count(e => e.Name.LocalName == "sheet").Should().Be(11);
    }

    private static async Task<byte[]> ReadAsync(ZipArchiveEntry entry)
    {
        using var buffer = new MemoryStream();
        await using var stream = entry.Open();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task BadRequestsAreRefusedAndOnlyViewersGetReports()
    {
        foreach (var query in new[] { "?from=2026-13-01", "?to=nope", "?asOf=x", "?format=pdf", "?currency=bdt", "?from=2026-03-02&to=2026-03-01", "?from=1990-01-01&to=2026-01-01" })
        {
            using var response = await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/reports/income-statement" + query, Viewer);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, query);
        }

        using var unknown = await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/reports/nonsense", Viewer);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var anonymous = await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/reports/income-statement", null);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var noView = await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/reports/income-statement", TestTokens.Issue([Permissions.ContentView]));
        noView.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- stored packs --------------------------------------------------------------------------------------------------------------------------------

    private async Task<JsonElement> GenerateAsync(string month) => await JsonAsync(HttpMethod.Post, "/api/v1/admin/finance/reports", Generator, new { month }, HttpStatusCode.Created);

    [Fact]
    public async Task AGeneratedPackIsStoredFrozenAndDownloadsWithItsSignedLinkOnly()
    {
        await PostAsync("1010", "4900", "1000.00", memo: "দান");
        await DonationAsync("bkash", amount: "250.00");
        var pack = await GenerateAsync(ThisMonth);
        pack.GetProperty("month").GetString().Should().Be(ThisMonth);
        pack.GetProperty("url").GetString().Should().StartWith("http").And.Contain("/api/v1/finance/report-downloads/" + pack.GetProperty("id").GetString() + "?token=");
        DateTimeOffset.Parse(pack.GetProperty("generatedAt").GetString()!, CultureInfo.InvariantCulture).Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));

        var url = new Uri(pack.GetProperty("url").GetString()!).PathAndQuery;
        using var first = await _client.GetAsync(url);
        first.StatusCode.Should().Be(HttpStatusCode.OK, "a browser link carries no bearer token");
        first.Content.Headers.ContentType!.MediaType.Should().Be(ReportService.XlsxType);
        var bytes = await first.Content.ReadAsByteArrayAsync();
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)).Should().Be(pack.GetProperty("sha256").GetString());
        bytes.Length.Should().Be(pack.GetProperty("sizeBytes").GetInt32());

        using var workbook = new ZipArchive(new MemoryStream(bytes));
        var sheets = XDocument.Load(workbook.GetEntry("xl/workbook.xml")!.Open()).Descendants().Where(e => e.Name.LocalName == "sheet").Select(e => e.Attribute("name")!.Value).ToList();
        sheets.Should().Equal("Summary", "Trial balance", "Income statement", "Balance sheet", "Cash flow", "Donations", "Project costs", "Donors", "Donation register", "Expense register", "General ledger");
        var summary = XDocument.Load(workbook.GetEntry("xl/worksheets/sheet1.xml")!.Open()).Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value).ToList();
        summary.Should().Contain("Kolpojontro").And.Contain("usr_gen").And.Contain("OK").And.NotContain("FAILED");
        var ledgerSheet = XDocument.Load(workbook.GetEntry("xl/worksheets/sheet11.xml")!.Open()).Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value).ToList();
        ledgerSheet.Should().Contain("দান");

        // A later posting changes the live figures but never the stored pack.
        await PostAsync("1010", "4900", "999.00");
        using var again = await _client.GetAsync(new Uri((await JsonAsync(HttpMethod.Get, "/api/v1/admin/finance/reports", Viewer)).GetProperty("items")[0].GetProperty("url").GetString()!).PathAndQuery);
        (await again.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
        Amount(await ReportAsync("income-statement"), "Total income").Should().Be("2249.00");

        var list = await JsonAsync(HttpMethod.Get, "/api/v1/admin/finance/reports", Viewer);
        list.GetProperty("items").GetArrayLength().Should().Be(1);
        list.GetProperty("items")[0].GetProperty("id").GetString().Should().Be(pack.GetProperty("id").GetString());
    }

    [Fact]
    public async Task ALinkThatIsTamperedWithMissingOrForAnotherReportIsRefused()
    {
        var first = await GenerateAsync(ThisMonth);
        var second = await GenerateAsync(ThisMonth);
        var firstId = first.GetProperty("id").GetString()!;
        var secondId = second.GetProperty("id").GetString()!;
        var token = Uri.UnescapeDataString(first.GetProperty("url").GetString()!.Split("token=")[1]);

        foreach (var path in new[]
                 {
                     $"/api/v1/finance/report-downloads/{firstId}",
                     $"/api/v1/finance/report-downloads/{firstId}?token=",
                     $"/api/v1/finance/report-downloads/{firstId}?token={Uri.EscapeDataString(token[..^3] + "abc")}",
                     $"/api/v1/finance/report-downloads/{secondId}?token={Uri.EscapeDataString(token)}",
                     $"/api/v1/finance/report-downloads/{firstId}?token=garbage"
                 })
        {
            using var response = await _client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
        }

        var missingToken = Uri.UnescapeDataString((await JsonAsync(HttpMethod.Get, "/api/v1/admin/finance/reports", Viewer)).GetProperty("items")[0].GetProperty("url").GetString()!.Split("token=")[1]);
        using var unknown = await _client.GetAsync($"/api/v1/finance/report-downloads/rpt_missing?token={Uri.EscapeDataString(missingToken)}");
        unknown.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a token is only good for the report it was made for");
        (await ScopeAsync(s => s.GetRequiredService<FinanceDbContext>().ReportPacks.CountAsync())).Should().Be(2);
    }

    [Fact]
    public async Task AStoredFileThatNoLongerMatchesItsHashIsNeverServed()
    {
        var pack = await GenerateAsync(ThisMonth);
        var file = Directory.EnumerateFiles(_proofs, "*", SearchOption.AllDirectories).Single(f => f.Contains(pack.GetProperty("id").GetString()![4..], StringComparison.Ordinal));
        await File.AppendAllTextAsync(file, "tampered");
        using var response = await _client.GetAsync(new Uri(pack.GetProperty("url").GetString()!).PathAndQuery);
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync()).Should().Contain("REPORT_CORRUPT");
    }

    [Fact]
    public async Task GeneratingNeedsThePermissionAValidMonthAndNotTheFuture()
    {
        using var viewer = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/reports", Viewer, new { month = ThisMonth });
        viewer.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        foreach (var month in new[] { "", "2026", "2026-13", "March", "1999-01" })
        {
            using var response = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/reports", Generator, new { month });
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, month);
        }

        var future = Today.AddMonths(2).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        using var tooLate = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/reports", Generator, new { month = future });
        tooLate.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await tooLate.Content.ReadAsStringAsync()).Should().Contain("MONTH_IN_FUTURE");
        using var anonymous = await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/reports", null);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ScopeAsync(s => s.GetRequiredService<FinanceDbContext>().ReportPacks.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task APastMonthReportCoversOnlyThatMonthAndAMonthWithNothingIsStillAValidPack()
    {
        var last = Today.AddMonths(-1);
        await PostAsync("1010", "4900", "40.00", new DateOnly(last.Year, last.Month, 1));
        await PostAsync("1010", "4900", "60.00");
        var income = await ReportAsync("income-statement", $"?from={Ymd(new DateOnly(last.Year, last.Month, 1))}&to={Ymd(new DateOnly(last.Year, last.Month, 1).AddMonths(1).AddDays(-1))}");
        Amount(income, "Total income").Should().Be("40.00");
        (await GenerateAsync(last.AddMonths(-6).ToString("yyyy-MM", CultureInfo.InvariantCulture))).GetProperty("id").GetString().Should().StartWith("rpt_");
    }

    [Fact]
    public async Task APackOverThousandsOfPostingsIsBuiltWellInsideTheLatencyBudget()
    {
        await ScopeAsync(async s =>
        {
            var db = s.GetRequiredService<FinanceDbContext>();
            var accounts = await db.Accounts.ToDictionaryAsync(a => a.Code, a => a.Id);
            var period = Today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            for (var i = 0; i < 3000; i++)
            {
                var amount = 10m + (i % 90);
                db.Postings.Add(new PostingEntity
                {
                    PostedOn = Today,
                    PeriodKey = period,
                    FiscalYear = FiscalCalendar.FiscalYear(Today, 7),
                    Memo = "বাল্ক " + i.ToString(CultureInfo.InvariantCulture),
                    SourceType = PostingSources.Adjustment,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Entries =
                    [
                        new LedgerEntryEntity { AccountId = accounts["1010"], Debit = amount, Currency = "BDT" },
                        new LedgerEntryEntity { AccountId = accounts[i % 2 == 0 ? "4010" : "4900"], Credit = amount, Currency = "BDT" }
                    ]
                });
            }

            await db.SaveChangesAsync();
            return 0;
        });

        await ReportAsync("income-statement"); // warm up the host and the query plan
        var watch = Stopwatch.StartNew();
        var pack = await GenerateAsync(ThisMonth);
        watch.Stop();
        watch.ElapsedMilliseconds.Should().BeLessThan(2000, "report generation has a 2 second budget");
        pack.GetProperty("sizeBytes").GetInt32().Should().BeGreaterThan(50_000);

        watch.Restart();
        var live = await ReportAsync("income-statement");
        watch.Stop();
        watch.ElapsedMilliseconds.Should().BeLessThan(2000);
        Amount(live, "Total income").Should().NotBe("0.00");
    }

    [Fact]
    public async Task ApplicationGuardRefusesEditingAStoredPackRecord()
    {
        await GenerateAsync(ThisMonth);
        await ScopeAsync(async s =>
        {
            var db = s.GetRequiredService<FinanceDbContext>();
            (await db.ReportPacks.FirstAsync()).Sha256 = "x";
            await db.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
            return 0;
        });
    }
}
