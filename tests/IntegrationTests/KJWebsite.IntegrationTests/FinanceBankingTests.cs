using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FinanceService.Banking;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class StatementParserTests
{
    [Theory]
    [InlineData("1,234.50", "1234.50")]
    [InlineData("(500.00)", "-500.00")]
    [InlineData("250.00 Dr", "-250.00")]
    [InlineData("250 Cr", "250")]
    [InlineData("-12", "-12")]
    [InlineData("+7.5", "7.5")]
    [InlineData("৳1,000", "1000")]
    [InlineData("BDT 2,500.00", "2500.00")]
    [InlineData("১২৩.৫০", "123.50")]
    [InlineData("1,00,000.50", "100000.50")]
    [InlineData("12,34,567", "1234567")]
    [InlineData("1 234.50", "1234.50")]
    [InlineData(".5", "0.5")]
    public void AmountsAreReadTheWayStatementsPrintThem(string text, string expected)
    {
        StatementParsers.TryAmount(text, out var amount, out var error).Should().BeTrue(error);
        amount.Should().Be(decimal.Parse(expected, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    [InlineData("12.345")]
    [InlineData("1e5")]
    [InlineData("--5")]
    [InlineData("1,50")]
    [InlineData("1.000,00")]
    [InlineData("1,2345")]
    [InlineData("1,234,5")]
    [InlineData("99999999999999999")]
    public void AnAmountThatIsNotExactlyReadableIsRefusedNeverRounded(string text)
    {
        StatementParsers.TryAmount(text, out _, out var error).Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("2026-03-09", null)]
    [InlineData("09/03/2026", null)]
    [InlineData("9-3-2026", null)]
    [InlineData("09-Mar-2026", null)]
    [InlineData("9 Mar 2026", null)]
    [InlineData("09.03.2026", null)]
    [InlineData("20260309", null)]
    [InlineData("৯/০৩/২০২৬", null)]
    [InlineData("03/09/2026", "MM/dd/yyyy")]
    public void DatesInCommonFormatsAreRead(string text, string? format)
    {
        StatementParsers.TryDate(text, format, out var date).Should().BeTrue();
        date.Should().Be(new DateOnly(2026, 3, 9));
    }

    [Theory]
    [InlineData("31/02/2026", null)]
    [InlineData("13/13/2026", null)]
    [InlineData("tomorrow", null)]
    [InlineData("", null)]
    [InlineData("1850-01-01", null)]
    [InlineData("13/03/2026", "MM/dd/yyyy")]
    public void ADateThatIsNotRealIsRefused(string text, string? format)
    {
        StatementParsers.TryDate(text, format, out _).Should().BeFalse();
    }

    [Fact]
    public void DayFirstWinsWhenAnUnmappedDateIsAmbiguous()
    {
        StatementParsers.TryDate("03/04/2026", null, out var date).Should().BeTrue();
        date.Should().Be(new DateOnly(2026, 4, 3));
    }

    [Fact]
    public void DebitAndCreditColumnsAreFoundByTheirHeaderAndQuotedFieldsSurvive()
    {
        const string csv = "﻿Date,Narration,Chq No,Withdrawal,Deposit,Balance\r\n09/03/2026,\"Rahim, Md. \"\"Zakat\"\"\",CHQ-1,,\"5,000.00\",\"15,000.00\"\r\n10/03/2026,\"Bank\nCharge\",,150.00,,\"14,850.00\"\r\n\r\n";
        var result = StatementParsers.ParseCsv(csv, null);
        result.Errors.Should().BeEmpty();
        result.Lines.Should().HaveCount(2);
        result.Lines[0].Should().BeEquivalentTo(new ParsedLine(2, new DateOnly(2026, 3, 9), "Rahim, Md. \"Zakat\"", "CHQ-1", 5000.00m, 15000.00m));
        result.Lines[1].Amount.Should().Be(-150.00m);
        result.Lines[1].Description.Should().Be("Bank\nCharge");
        result.Lines[1].Row.Should().Be(3);
    }

    [Fact]
    public void ASemicolonSeparatedSignedAmountFileIsDetected()
    {
        var result = StatementParsers.ParseCsv("Transaction Date;Details;Amount\n2026-03-09;Deposit;1.000,00\n", null);
        result.Errors.Should().ContainSingle().Which.Row.Should().Be(2);

        var fine = StatementParsers.ParseCsv("Transaction Date;Details;Amount\n2026-03-09;Deposit;1000.00\n2026-03-10;Fee;-5\n", null);
        fine.Errors.Should().BeEmpty();
        fine.Lines.Select(l => l.Amount).Should().Equal(1000m, -5m);
    }

    [Fact]
    public void AMappingByPositionOrNameOverridesDetection()
    {
        var mapping = new CsvMapping("1", "Memo Text", null, "4", null, null, null, "dd/MM/yyyy", null);
        var result = StatementParsers.ParseCsv("D,Memo Text,X,Amt\n09/03/2026,Hello,zz,12.00\n", mapping);
        result.Errors.Should().BeEmpty();
        result.Lines.Should().ContainSingle().Which.Amount.Should().Be(12m);
    }

    [Fact]
    public void EveryBadRowIsReportedWithItsRowNumber()
    {
        const string csv = "Date,Description,Debit,Credit\n" +
                           "09/03/2026,Good,,10.00\n" +
                           "nope,Bad date,,10.00\n" +
                           "09/03/2026,Both,5.00,5.00\n" +
                           "09/03/2026,Neither,,\n" +
                           "09/03/2026,Bad amount,,12.345\n" +
                           "09/03/2026,,,3.00\n" +
                           "09/03/2026,Zero,,0.00\n";
        var result = StatementParsers.ParseCsv(csv, null);
        result.Lines.Should().ContainSingle();
        result.Errors.Select(e => e.Row).Should().Equal(3, 4, 5, 6, 7, 8);
        result.Errors.Select(e => e.Reason).Should().OnlyContain(r => r.Length > 5);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("Date,Description,Amount\n", "no rows")]
    [InlineData("Foo,Bar\n1,2\n", "No date column")]
    [InlineData("Date,Description\n09/03/2026,x\n", "amount column")]
    public void AFileWithoutUsableStructureIsRefusedWithOneClearReason(string csv, string reason)
    {
        var result = StatementParsers.ParseCsv(csv, null);
        result.Lines.Should().BeEmpty();
        result.Errors.Should().NotBeEmpty();
        result.Errors[0].Reason.Should().Contain(reason);
    }

    [Fact]
    public void AnOfxFileInSgmlOrXmlFormIsRead()
    {
        const string sgml = "OFXHEADER:100\nDATA:OFXSGML\n\n<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>BDT\n<BANKTRANLIST>\n" +
                            "<STMTTRN>\n<TRNTYPE>CREDIT\n<DTPOSTED>20260309120000\n<TRNAMT>5000.00\n<FITID>A1\n<NAME>Rahim\n<MEMO>Zakat\n</STMTTRN>\n" +
                            "<STMTTRN>\n<TRNTYPE>DEBIT\n<DTPOSTED>20260310\n<TRNAMT>-150.00\n<FITID>A2\n<NAME>Bank charge\n</STMTTRN>\n</BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
        var viaSgml = StatementParsers.ParseOfx(sgml);
        viaSgml.Errors.Should().BeEmpty();
        viaSgml.Currency.Should().Be("BDT");
        viaSgml.Lines.Select(l => (l.Amount, l.Reference, l.Description)).Should().Equal((5000m, "A1", "Rahim - Zakat"), (-150m, "A2", "Bank charge"));

        const string xml = "<?xml version=\"1.0\"?><OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>BDT</CURDEF><BANKTRANLIST>" +
                           "<STMTTRN><TRNTYPE>CREDIT</TRNTYPE><DTPOSTED>20260309</DTPOSTED><TRNAMT>5000.00</TRNAMT><FITID>A1</FITID><NAME>Rahim &amp; Co</NAME></STMTTRN>" +
                           "</BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
        var viaXml = StatementParsers.ParseOfx(xml);
        viaXml.Errors.Should().BeEmpty();
        viaXml.Lines.Should().ContainSingle().Which.Description.Should().Be("Rahim & Co");
        StatementParsers.LooksLikeOfx(sgml).Should().BeTrue();
        StatementParsers.LooksLikeOfx(xml).Should().BeTrue();
        StatementParsers.LooksLikeOfx("Date,Description,Amount").Should().BeFalse();
    }

    [Fact]
    public void AnOfxTransactionMissingItsDateOrAmountIsReportedNotSkipped()
    {
        const string ofx = "<OFX><CURDEF>BDT<BANKTRANLIST><STMTTRN><TRNAMT>1.00<NAME>a</STMTTRN><STMTTRN><DTPOSTED>20260309<TRNAMT>x<NAME>b</STMTTRN><STMTTRN><DTPOSTED>20260309<TRNAMT>2.00<NAME>c</STMTTRN></BANKTRANLIST></OFX>";
        var result = StatementParsers.ParseOfx(ofx);
        result.Lines.Should().ContainSingle();
        result.Errors.Select(e => e.Row).Should().Equal(1, 2);
    }

    [Fact]
    public void AFingerprintIgnoresCaseAndSpacingButNotAmountOrDate()
    {
        var a = new ParsedLine(2, new DateOnly(2026, 3, 9), "Rahim  Zakat", "r1", 10m, 100m);
        var b = new ParsedLine(9, new DateOnly(2026, 3, 9), " RAHIM zakat ", "R1", 10.00m, 100.0m);
        StatementParsers.Fingerprint(a).Should().Be(StatementParsers.Fingerprint(b));
        StatementParsers.Fingerprint(a).Should().NotBe(StatementParsers.Fingerprint(a with { Amount = 10.01m }));
        StatementParsers.Fingerprint(a).Should().NotBe(StatementParsers.Fingerprint(a with { Date = new DateOnly(2026, 3, 10) }));
    }
}

public sealed class FinanceBankingTests : IDisposable
{
    private static string Token(string subject, params string[] permissions) => TestTokens.Issue([Permissions.FinanceView, .. permissions], subject: subject);
    private static readonly string Viewer = Token("usr_viewer");
    private static readonly string Manager = Token("usr_bank", Permissions.FinanceBankManage);
    private static readonly string Revealer = Token("usr_reveal", Permissions.FinanceBankReveal);
    private static readonly string Reconciler = Token("usr_rec", Permissions.FinanceReconcile);
    private static readonly string Adjuster = Token("usr_adj", Permissions.FinanceLedgerAdjust);
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static string Ymd(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Ago(int days) => Ymd(Today.AddDays(-days));

    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;

    public FinanceBankingTests()
    {
        _factory.Settings["Finance:Banking:MaxFileBytes"] = "4000";
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<T> DbAsync<T>(Func<FinanceDbContext, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<FinanceDbContext>());
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, object? body = null, string? raw = null, string? filename = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (raw is not null)
        {
            request.Content = new StringContent(raw, Encoding.UTF8, "text/plain");
            if (filename is not null)
            {
                request.Headers.Add("X-Filename", filename);
            }
        }
        else if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await TestControls.SendAsync(_client, request);
    }

    private async Task<JsonElement> OkAsync(HttpMethod method, string path, string token, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await SendAsync(method, path, token, body);
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private async Task<string> AccountAsync(string number = "0123456789012", string ledgerAccount = "1020", string currency = "BDT", object? csvMapping = null)
    {
        var body = await OkAsync(HttpMethod.Post, "/api/v1/admin/finance/bank-accounts", Manager,
            new { name = "Main account", bankName = "Sonali Bank", number, currency, ledgerAccount, csvMapping }, HttpStatusCode.Created);
        return body.GetProperty("id").GetString()!;
    }

    private async Task<long> PostAsync(string debit, string credit, string amount, string postedOn, string memo = "Test posting", string currency = "BDT")
    {
        var body = await OkAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", Adjuster, new
        {
            postedOn,
            memo,
            reason = "test",
            lines = new[] { new { account = debit, debit = (string?)amount, credit = (string?)null, currency }, new { account = credit, debit = (string?)null, credit = (string?)amount, currency } }
        }, HttpStatusCode.Created);
        return long.Parse(body.GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
    }

    private static string Csv(params string[] rows) => "Date,Description,Reference,Debit,Credit,Balance\n" + string.Join('\n', rows) + "\n";

    private async Task<HttpResponseMessage> ImportRawAsync(string accountId, string content, string? format = null, string? token = null) =>
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/bank-accounts/{accountId}/statements" + (format is null ? string.Empty : "?format=" + format), token ?? Reconciler, raw: content, filename: "march.csv");

    private async Task<JsonElement> ImportAsync(string accountId, string content, string? format = null)
    {
        using var response = await ImportRawAsync(accountId, content, format);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> ReconciliationAsync(string query = "") => await OkAsync(HttpMethod.Get, "/api/v1/admin/finance/reconciliation" + query, Viewer);

    private static JsonElement Line(JsonElement reconciliation, string description) =>
        reconciliation.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("description").GetString() == description);

    // ---- registry --------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAccountIsStoredEncryptedAndShownMaskedAndOnlyTheRevealPermissionReadsItInFull()
    {
        var id = await AccountAsync("0123 4567-89012");
        var list = await OkAsync(HttpMethod.Get, "/api/v1/admin/finance/bank-accounts", Viewer);
        list.GetRawText().Should().NotContain("0123456789012").And.NotContain("456789012");
        var row = list.GetProperty("items").EnumerateArray().Single();
        row.GetProperty("numberMasked").GetString().Should().Be("••••9012");
        row.GetProperty("ledgerAccount").GetString().Should().Be("1020");

        (await DbAsync(db => db.BankAccounts.Select(a => a.NumberProtected).SingleAsync())).Should().NotContain("0123456789012");

        using (var manager = await SendAsync(HttpMethod.Get, $"/api/v1/admin/finance/bank-accounts/{id}/number", Manager))
        {
            manager.StatusCode.Should().Be(HttpStatusCode.Forbidden, "managing accounts does not include reading the number");
        }

        (await DbAsync(db => db.BankReveals.CountAsync())).Should().Be(0);
        var revealed = await OkAsync(HttpMethod.Get, $"/api/v1/admin/finance/bank-accounts/{id}/number", Revealer);
        revealed.GetProperty("number").GetString().Should().Be("0123456789012");
        var log = await DbAsync(db => db.BankReveals.ToListAsync());
        log.Should().ContainSingle().Which.By.Should().Be("usr_reveal");

        using var missing = await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/bank-accounts/nope/number", Revealer);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await DbAsync(db => db.BankReveals.CountAsync())).Should().Be(1, "a read of nothing is not logged");
    }

    [Fact]
    public async Task RegisteringAnAccountIsValidatedAndPermissioned()
    {
        foreach (var bad in new object[]
                 {
                     new { name = "", bankName = "B", number = "123456", currency = "BDT" },
                     new { name = "N", bankName = "", number = "123456", currency = "BDT" },
                     new { name = "N", bankName = "B", number = "12", currency = "BDT" },
                     new { name = "N", bankName = "B", number = "12 34 5!", currency = "BDT" },
                     new { name = "N", bankName = "B", number = "123456", currency = "XXX" },
                     new { name = "N", bankName = "B", number = "123456", currency = "BDT", csvMapping = new { amount = "a", debit = "d", credit = "c" } },
                     new { name = "N", bankName = "B", number = "123456", currency = "BDT", csvMapping = new { debit = "d" } }
                 })
        {
            using var response = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/bank-accounts", Manager, bad);
            (await ErrorCodeAsync(response, HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
        }

        using var forbidden = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/bank-accounts", Viewer, new { name = "N", bankName = "B", number = "123456", currency = "BDT" });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var anonymous = await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/bank-accounts", null);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var noLedger = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/bank-accounts", Manager, new { name = "N", bankName = "B", number = "123456", currency = "BDT", ledgerAccount = "4010" });
        (await ErrorCodeAsync(noLedger, HttpStatusCode.UnprocessableEntity)).Should().Be("LEDGER_ACCOUNT_INVALID");
        using var unknownLedger = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/bank-accounts", Manager, new { name = "N", bankName = "B", number = "123456", currency = "BDT", ledgerAccount = "9999" });
        (await ErrorCodeAsync(unknownLedger, HttpStatusCode.UnprocessableEntity)).Should().Be("LEDGER_ACCOUNT_INVALID");
    }

    [Fact]
    public async Task TwoLiveAccountsCannotShareALedgerAccountButAnArchivedOneFreesIt()
    {
        var first = await AccountAsync();
        using var clash = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/bank-accounts", Manager, new { name = "Second", bankName = "B", number = "999999", currency = "BDT", ledgerAccount = "1020" });
        (await ErrorCodeAsync(clash, HttpStatusCode.Conflict)).Should().Be("BANK_LEDGER_ACCOUNT_TAKEN");

        var archived = await OkAsync(new HttpMethod("PATCH"), $"/api/v1/admin/finance/bank-accounts/{first}", Manager, new { archived = true });
        archived.GetProperty("archived").GetBoolean().Should().BeTrue();
        (await OkAsync(HttpMethod.Get, "/api/v1/admin/finance/bank-accounts", Viewer)).GetProperty("items").GetArrayLength().Should().Be(0);
        (await OkAsync(HttpMethod.Get, "/api/v1/admin/finance/bank-accounts?includeArchived=true", Viewer)).GetProperty("items").GetArrayLength().Should().Be(1);
        await AccountAsync("555555", "1020");

        using var refused = await ImportRawAsync(first, Csv("09/03/2026,Gift,,,100.00,100.00"));
        (await ErrorCodeAsync(refused, HttpStatusCode.Conflict)).Should().Be("BANK_ACCOUNT_ARCHIVED");
    }

    [Fact]
    public async Task RenamingRemappingAndLockingTheLedgerAccountOnceStatementsExist()
    {
        var id = await AccountAsync();
        var renamed = await OkAsync(new HttpMethod("PATCH"), $"/api/v1/admin/finance/bank-accounts/{id}", Manager,
            new { name = "Operating", csvMapping = new { date = "Value Date", description = "Narrative", amount = "Net", dateFormat = "dd-MM-yyyy" } });
        renamed.GetProperty("name").GetString().Should().Be("Operating");
        renamed.GetProperty("csvMapping").GetProperty("amount").GetString().Should().Be("Net");

        var imported = await ImportAsync(id, "Value Date,Narrative,Net\n09-03-2026,Hello,25.00\n");
        imported.GetProperty("imported").GetInt32().Should().Be(1);

        using var relink = await SendAsync(new HttpMethod("PATCH"), $"/api/v1/admin/finance/bank-accounts/{id}", Manager, new { ledgerAccount = "1030" });
        (await ErrorCodeAsync(relink, HttpStatusCode.Conflict)).Should().Be("LEDGER_ACCOUNT_LOCKED");

        var cleared = await OkAsync(new HttpMethod("PATCH"), $"/api/v1/admin/finance/bank-accounts/{id}", Manager, new { clearCsvMapping = true });
        cleared.TryGetProperty("csvMapping", out var mapping).Should().BeTrue();
        mapping.ValueKind.Should().Be(JsonValueKind.Null);

        using var invalid = await SendAsync(new HttpMethod("PATCH"), $"/api/v1/admin/finance/bank-accounts/{id}", Manager, new { name = " " });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var absent = await SendAsync(new HttpMethod("PATCH"), "/api/v1/admin/finance/bank-accounts/nope", Manager, new { name = "x" });
        absent.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- import ---------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABadRowRejectsTheWholeFileAndEveryBadRowIsListed()
    {
        var id = await AccountAsync();
        using var response = await ImportRawAsync(id, Csv("09/03/2026,Good,,,10.00,10.00", "nope,Bad date,,,10.00,", "09/03/2026,Bad amount,,,1.234,", "09/03/2026,Fine,,,5.00,15.00"));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, text);
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("IMPORT_REJECTED");
        body.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("row").GetInt32()).Should().Equal(3, 4);
        (await DbAsync(db => db.BankLines.CountAsync())).Should().Be(0);
        (await DbAsync(db => db.BankStatements.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task TheSameFileCannotBeImportedTwiceButAnOverlappingStatementReportsItsDuplicates()
    {
        var id = await AccountAsync();
        var first = Csv("01/03/2026,Gift A,,,100.00,100.00", "02/03/2026,Gift B,,,200.00,300.00");
        var report = await ImportAsync(id, first);
        report.GetProperty("imported").GetInt32().Should().Be(2);
        report.GetProperty("duplicates").GetArrayLength().Should().Be(0);

        using var again = await ImportRawAsync(id, first);
        (await ErrorCodeAsync(again, HttpStatusCode.Conflict)).Should().Be("ALREADY_IMPORTED");

        var overlapping = Csv("01/03/2026,Gift A,,,100.00,100.00", "02/03/2026,Gift B,,,200.00,300.00", "03/03/2026,Gift C,,,50.00,350.00");
        var second = await ImportAsync(id, overlapping);
        second.GetProperty("imported").GetInt32().Should().Be(1);
        var duplicates = second.GetProperty("duplicates").EnumerateArray().ToList();
        duplicates.Select(d => d.GetProperty("row").GetInt32()).Should().Equal(2, 3);
        duplicates.Select(d => d.GetProperty("description").GetString()).Should().Equal("Gift A", "Gift B");
        (await DbAsync(db => db.BankLines.CountAsync())).Should().Be(3);
        (await DbAsync(db => db.BankStatements.CountAsync())).Should().Be(2);
        (await OkAsync(HttpMethod.Get, $"/api/v1/admin/finance/bank-accounts/{id}/statements", Viewer)).GetProperty("items").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task TwoIdenticalLinesOnOneDayAreBothKeptAndNeitherIsLaterCalledADuplicate()
    {
        var id = await AccountAsync();
        var twice = Csv("01/03/2026,Cash deposit,,,100.00,", "01/03/2026,Cash deposit,,,100.00,");
        (await ImportAsync(id, twice)).GetProperty("imported").GetInt32().Should().Be(2);

        var three = Csv("01/03/2026,Cash deposit,,,100.00,", "01/03/2026,Cash deposit,,,100.00,", "01/03/2026,Cash deposit,,,100.00,");
        var report = await ImportAsync(id, three);
        report.GetProperty("imported").GetInt32().Should().Be(1, "only the third identical line is new");
        report.GetProperty("duplicates").GetArrayLength().Should().Be(2);
        (await DbAsync(db => db.BankLines.CountAsync())).Should().Be(3);
    }

    [Fact]
    public async Task AnOfxFileIsDetectedAndItsCurrencyMustMatchTheAccount()
    {
        var id = await AccountAsync();
        string Ofx(string currency) => $"OFXHEADER:100\n<OFX><CURDEF>{currency}\n<BANKTRANLIST><STMTTRN><TRNTYPE>CREDIT\n<DTPOSTED>20260309\n<TRNAMT>75.00\n<FITID>Z9\n<NAME>Donor</STMTTRN></BANKTRANLIST></OFX>";
        using var wrong = await ImportRawAsync(id, Ofx("USD"));
        var text = await wrong.Content.ReadAsStringAsync();
        wrong.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, text);
        text.Should().Contain("USD").And.Contain("BDT");
        (await DbAsync(db => db.BankLines.CountAsync())).Should().Be(0);

        var report = await ImportAsync(id, Ofx("BDT"));
        report.GetProperty("statement").GetProperty("format").GetString().Should().Be("ofx");
        var line = Line(await ReconciliationAsync(), "Donor");
        line.GetProperty("amount").GetString().Should().Be("75.00");
        line.GetProperty("reference").GetString().Should().Be("Z9");
    }

    [Fact]
    public async Task FilesThatAreEmptyTooBigOrForUnknownThingsAreRefused()
    {
        var id = await AccountAsync();
        using var empty = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/bank-accounts/{id}/statements", Reconciler);
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var big = await ImportRawAsync(id, new string('x', 4100));
        (await ErrorCodeAsync(big, HttpStatusCode.RequestEntityTooLarge)).Should().Be("FILE_TOO_LARGE");
        using var format = await ImportRawAsync(id, Csv("09/03/2026,Gift,,,1.00,"), "pdf");
        format.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var unknown = await ImportRawAsync("nope", Csv("09/03/2026,Gift,,,1.00,"));
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var forbidden = await ImportRawAsync(id, Csv("09/03/2026,Gift,,,1.00,"), token: Manager);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden, "importing needs finance.reconcile, not finance.bank.manage");
        using var anonymous = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/bank-accounts/{id}/statements", null, raw: "x");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- automatic matching ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AUniqueEntryOfTheSameAmountSideAndCurrencyInTheWindowIsMatchedAutomatically()
    {
        var id = await AccountAsync();
        var deposit = await PostAsync("1020", "4010", "5000.00", Ago(10));
        var withdrawal = await PostAsync("5010", "1020", "1200.50", Ago(9));
        var report = await ImportAsync(id, Csv($"{Ago(8)},Cheque deposit,,,5000.00,5000.00", $"{Ago(7)},Cheque issued,,1200.50,,3799.50", $"{Ago(6)},Mystery,,,77.00,3876.50"));
        report.GetProperty("autoMatched").GetInt32().Should().Be(2);
        report.GetProperty("unmatched").GetInt32().Should().Be(1);

        var rec = await ReconciliationAsync();
        var lines = rec.GetProperty("lines").EnumerateArray().ToList();
        lines[0].GetProperty("description").GetString().Should().Be("Mystery", "unmatched lines come first");
        lines[0].GetProperty("status").GetString().Should().Be("unmatched");
        lines[0].GetProperty("matchedEntryId").ValueKind.Should().Be(JsonValueKind.Null);
        var entryByMemo = rec.GetProperty("entries").EnumerateArray().ToList();
        entryByMemo.Select(e => e.GetProperty("id").GetString()).Should().Contain(Line(rec, "Cheque deposit").GetProperty("matchedEntryId").GetString()!);
        Line(rec, "Cheque deposit").GetProperty("status").GetString().Should().Be("matched");
        deposit.Should().BeGreaterThan(0);
        withdrawal.Should().BeGreaterThan(0);

        var events = await DbAsync(db => db.BankMatchEvents.ToListAsync());
        events.Should().HaveCount(2).And.OnlyContain(e => e.Action == "auto_matched" && e.By == null);
        (await DbAsync(db => db.BankLineLinks.CountAsync(l => l.Automatic))).Should().Be(2);
    }

    [Fact]
    public async Task ADepositNeverMatchesACreditAndACurrencyNeverMatchesAnother()
    {
        var id = await AccountAsync();
        await PostAsync("5010", "1020", "100.00", Ago(5));
        var report = await ImportAsync(id, Csv($"{Ago(5)},Money in,,,100.00,"));
        report.GetProperty("autoMatched").GetInt32().Should().Be(0, "money in is a debit of the bank account, this entry is a credit");

        await PostAsync("1020", "4010", "100.00", Ago(4), currency: "USD");
        (await OkAsync(HttpMethod.Post, $"/api/v1/admin/finance/bank-accounts/{id}/rematch", Reconciler)).GetProperty("matched").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task AnEntryOutsideTheDateWindowIsLeftForAPerson()
    {
        var id = await AccountAsync();
        await PostAsync("1020", "4010", "300.00", Ago(20));
        var near = await ImportAsync(id, Csv($"{Ago(14)},Six days later,,,300.00,"));
        near.GetProperty("autoMatched").GetInt32().Should().Be(0);
        var inside = await ImportAsync(id, Csv($"{Ago(15)},Five days later,,,300.00,"));
        inside.GetProperty("autoMatched").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task SeveralCandidatesAreNotGuessedAtButAReferenceInTheMemoNarrowsThem()
    {
        var id = await AccountAsync();
        await PostAsync("1020", "4010", "250.00", Ago(5), memo: "Donation KJ-AAAA1111");
        await PostAsync("1020", "4010", "250.00", Ago(5), memo: "Donation KJ-BBBB2222");
        var ambiguous = await ImportAsync(id, Csv($"{Ago(5)},Transfer,,,250.00,"));
        ambiguous.GetProperty("autoMatched").GetInt32().Should().Be(0);
        ambiguous.GetProperty("unmatched").GetInt32().Should().Be(1);

        var ambiguousLine = Line(await ReconciliationAsync(), "Transfer").GetProperty("id").GetString()!;
        await OkAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/dismiss", Reconciler, new { lineId = ambiguousLine, reason = "Set aside so it does not compete for the entries" });
        var narrowed = await ImportAsync(id, Csv($"{Ago(5)},Transfer KJ-BBBB2222 online,,,250.00,300.00"));
        narrowed.GetProperty("autoMatched").GetInt32().Should().Be(1);
        var rec = await ReconciliationAsync("?status=matched");
        var matchedEntry = rec.GetProperty("lines").EnumerateArray().Single().GetProperty("matchedEntryId").GetString();
        rec.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("id").GetString() == matchedEntry).GetProperty("memo").GetString().Should().Contain("BBBB2222");
    }

    [Fact]
    public async Task TwoLinesCompetingForOneEntryAreNeitherMatchedWhateverTheirOrder()
    {
        var id = await AccountAsync();
        await PostAsync("1020", "4010", "80.00", Ago(5));
        var report = await ImportAsync(id, Csv($"{Ago(5)},First,,,80.00,", $"{Ago(5)},Second,,,80.00,"));
        report.GetProperty("autoMatched").GetInt32().Should().Be(0);
        report.GetProperty("unmatched").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ARematchPicksUpEntriesPostedAfterTheImport()
    {
        var id = await AccountAsync();
        (await ImportAsync(id, Csv($"{Ago(3)},Late posting,,,60.00,"))).GetProperty("autoMatched").GetInt32().Should().Be(0);
        await PostAsync("1020", "4010", "60.00", Ago(3));
        (await OkAsync(HttpMethod.Post, $"/api/v1/admin/finance/bank-accounts/{id}/rematch", Reconciler)).GetProperty("matched").GetInt32().Should().Be(1);
        (await OkAsync(HttpMethod.Post, $"/api/v1/admin/finance/bank-accounts/{id}/rematch", Reconciler)).GetProperty("matched").GetInt32().Should().Be(0);
        using var absent = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/bank-accounts/nope/rematch", Reconciler);
        absent.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnEntryOnAnotherBanksLedgerAccountIsNeverAMatch()
    {
        var main = await AccountAsync();
        await AccountAsync("777777", "1030");
        await PostAsync("1030", "4010", "90.00", Ago(4));
        var report = await ImportAsync(main, Csv($"{Ago(4)},Elsewhere,,,90.00,"));
        report.GetProperty("autoMatched").GetInt32().Should().Be(0);
    }

    // ---- resolving by hand -------------------------------------------------------------------------------------------------------------------------------

    private async Task<(string Account, string LineId, string EntryId)> UnmatchedPairAsync(string amount = "500.00")
    {
        var id = await AccountAsync();
        await PostAsync("1020", "4010", amount, Ago(2), memo: "Manual one");
        await PostAsync("1020", "4010", amount, Ago(2), memo: "Manual two");
        await ImportAsync(id, Csv($"{Ago(2)},Ambiguous,,,{amount},"));
        var rec = await ReconciliationAsync();
        return (id, Line(rec, "Ambiguous").GetProperty("id").GetString()!, rec.GetProperty("entries").EnumerateArray().First().GetProperty("id").GetString()!);
    }

    [Fact]
    public async Task APersonMatchesUnmatchesAndRematchesAndEveryStepIsRecorded()
    {
        var (_, lineId, entryId) = await UnmatchedPairAsync();
        var matched = await OkAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/match", Reconciler, new { lineId, entryId });
        matched.GetProperty("status").GetString().Should().Be("matched");
        matched.GetProperty("matchedEntryId").GetString().Should().Be(entryId);
        matched.GetProperty("automatic").GetBoolean().Should().BeFalse();

        using var twice = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/match", Reconciler, new { lineId, entryId });
        (await ErrorCodeAsync(twice, HttpStatusCode.Conflict)).Should().Be("LINE_ALREADY_RESOLVED");

        var unmatched = await OkAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/unmatch", Reconciler, new { lineId });
        unmatched.GetProperty("status").GetString().Should().Be("unmatched");
        using var again = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/unmatch", Reconciler, new { lineId });
        (await ErrorCodeAsync(again, HttpStatusCode.Conflict)).Should().Be("NOT_MATCHED");

        (await OkAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/match", Reconciler, new { lineId, entryId })).GetProperty("status").GetString().Should().Be("matched");
        var history = await DbAsync(db => db.BankMatchEvents.OrderBy(e => e.Id).ToListAsync());
        history.Select(e => e.Action).Should().Equal("matched", "unmatched", "matched");
        history.Should().OnlyContain(e => e.By == "usr_rec" && e.LedgerEntryId == long.Parse(entryId, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task AMatchMustAgreeOnAmountSideAccountAndBeFree()
    {
        var id = await AccountAsync();
        await PostAsync("1020", "4010", "500.00", Ago(2), memo: "A");
        await PostAsync("1020", "4010", "500.00", Ago(2), memo: "B");
        await PostAsync("1020", "4010", "501.00", Ago(2), memo: "Wrong amount");
        await PostAsync("5010", "1020", "500.00", Ago(2), memo: "Wrong side");
        await PostAsync("1010", "4010", "500.00", Ago(2), memo: "Other account");
        await ImportAsync(id, Csv($"{Ago(2)},Ambiguous,,,500.00,", $"{Ago(2)},Ambiguous again,,,500.00,"));
        var rec = await ReconciliationAsync();
        var first = Line(rec, "Ambiguous").GetProperty("id").GetString()!;
        var second = Line(rec, "Ambiguous again").GetProperty("id").GetString()!;
        string EntryOf(string memo) => rec.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("memo").GetString() == memo).GetProperty("id").GetString()!;

        async Task<string> Refused(string line, string entry, HttpStatusCode status)
        {
            using var response = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/match", Reconciler, new { lineId = line, entryId = entry });
            return await ErrorCodeAsync(response, status);
        }

        (await Refused(first, EntryOf("Wrong amount"), HttpStatusCode.UnprocessableEntity)).Should().Be("AMOUNT_MISMATCH");
        (await Refused(first, EntryOf("Wrong side"), HttpStatusCode.UnprocessableEntity)).Should().Be("AMOUNT_MISMATCH");
        (await Refused(first, "999999", HttpStatusCode.UnprocessableEntity)).Should().Be("ENTRY_NOT_ON_ACCOUNT");
        (await Refused("999999", EntryOf("A"), HttpStatusCode.NotFound)).Should().Be("NOT_FOUND");
        await OkAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/match", Reconciler, new { lineId = first, entryId = EntryOf("A") });
        (await Refused(second, EntryOf("A"), HttpStatusCode.Conflict)).Should().Be("ENTRY_ALREADY_MATCHED");

        using var malformed = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/match", Reconciler, new { lineId = "x", entryId = "1" });
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var forbidden = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/match", Viewer, new { lineId = first, entryId = "1" });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ADismissedLineNeedsAReasonStaysOutOfTheQueueAndCanBeReopenedButNotUnmatched()
    {
        var id = await AccountAsync();
        await ImportAsync(id, Csv($"{Ago(2)},Duplicate of a payment,,,40.00,"));
        var lineId = Line(await ReconciliationAsync(), "Duplicate of a payment").GetProperty("id").GetString()!;

        using var noReason = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/dismiss", Reconciler, new { lineId, reason = " " });
        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var dismissed = await OkAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/dismiss", Reconciler, new { lineId, reason = "Bank error, reversed next day" });
        dismissed.GetProperty("status").GetString().Should().Be("dismissed");
        dismissed.GetProperty("reason").GetString().Should().Be("Bank error, reversed next day");
        (await ReconciliationAsync("?status=unmatched")).GetProperty("lines").GetArrayLength().Should().Be(0);
        (await ReconciliationAsync("?status=dismissed")).GetProperty("lines").GetArrayLength().Should().Be(1);

        using var dismissTwice = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/dismiss", Reconciler, new { lineId, reason = "again" });
        (await ErrorCodeAsync(dismissTwice, HttpStatusCode.Conflict)).Should().Be("LINE_ALREADY_RESOLVED");
        using var unmatch = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/unmatch", Reconciler, new { lineId });
        (await ErrorCodeAsync(unmatch, HttpStatusCode.Conflict)).Should().Be("NOT_MATCHED");

        (await OkAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/reopen", Reconciler, new { lineId })).GetProperty("status").GetString().Should().Be("unmatched");
        using var reopenTwice = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/reopen", Reconciler, new { lineId });
        (await ErrorCodeAsync(reopenTwice, HttpStatusCode.Conflict)).Should().Be("NOT_DISMISSED");
        (await DbAsync(db => db.BankMatchEvents.OrderBy(e => e.Id).Select(e => e.Action).ToListAsync())).Should().Equal("dismissed", "reopened");
    }

    [Fact]
    public async Task PostAndMatchBooksTheMissingEntryOnceAndNeedsTheLedgerAdjustPermission()
    {
        var id = await AccountAsync();
        await ImportAsync(id, Csv($"{Ago(2)},Monthly bank charge,,150.00,,", $"{Ago(2)},Interest,,,12.25,"));
        var rec = await ReconciliationAsync();
        var charge = Line(rec, "Monthly bank charge").GetProperty("id").GetString()!;
        var interest = Line(rec, "Interest").GetProperty("id").GetString()!;

        using var reconcilerOnly = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/post-and-match", Reconciler, new { lineId = charge, account = "5900" });
        reconcilerOnly.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var done = await OkAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/post-and-match", Adjuster, new { lineId = charge, account = "5900", memo = "March bank charge" });
        done.GetProperty("status").GetString().Should().Be("matched");
        await OkAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/post-and-match", Adjuster, new { lineId = interest, account = "4900" });

        var chargeEntries = await DbAsync(async db =>
            await (from e in db.LedgerEntries
                   join p in db.Postings on e.PostingId equals p.Id
                   join a in db.Accounts on e.AccountId equals a.Id
                   where p.SourceId == "bank-line:" + charge
                   select new { a.Code, e.Debit, e.Credit, p.Memo, p.Reason, p.CreatedBy, p.PostedOn }).ToListAsync());
        chargeEntries.Should().HaveCount(2);
        chargeEntries.Single(e => e.Code == "1020").Credit.Should().Be(150.00m, "money out credits the bank account");
        chargeEntries.Single(e => e.Code == "5900").Debit.Should().Be(150.00m);
        chargeEntries.Should().OnlyContain(e => e.Memo == "March bank charge" && e.CreatedBy == "usr_adj" && e.PostedOn == Today.AddDays(-2));
        var interestEntries = await DbAsync(async db =>
            await (from e in db.LedgerEntries
                   join p in db.Postings on e.PostingId equals p.Id
                   join a in db.Accounts on e.AccountId equals a.Id
                   where p.SourceId == "bank-line:" + interest
                   select new { a.Code, e.Debit }).ToListAsync());
        interestEntries.Single(e => e.Code == "1020").Debit.Should().Be(12.25m, "money in debits the bank account");

        using var again = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/post-and-match", Adjuster, new { lineId = charge, account = "5900" });
        (await ErrorCodeAsync(again, HttpStatusCode.Conflict)).Should().Be("LINE_ALREADY_RESOLVED");
        (await DbAsync(db => db.Postings.CountAsync(p => p.SourceId == "bank-line:" + charge))).Should().Be(1);
        (await DbAsync(db => db.BankMatchEvents.Where(e => e.Action == "posted_and_matched").Select(e => e.PostingId).ToListAsync())).Should().HaveCount(2).And.OnlyContain(p => p != null);

        // Undoing the match leaves the posting alone and frees the line, but a new post-and-match returns the same posting rather than posting twice.
        await OkAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/unmatch", Reconciler, new { lineId = charge });
        await OkAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/post-and-match", Adjuster, new { lineId = charge, account = "5900" });
        (await DbAsync(db => db.Postings.CountAsync(p => p.SourceId == "bank-line:" + charge))).Should().Be(1);
    }

    [Fact]
    public async Task PostAndMatchFollowsTheLedgerRules()
    {
        var id = await AccountAsync();
        await ImportAsync(id, Csv($"{Ago(2)},Charge,,10.00,,"));
        var lineId = Line(await ReconciliationAsync(), "Charge").GetProperty("id").GetString()!;
        foreach (var account in new[] { "1020", "", "9999" })
        {
            using var response = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/post-and-match", Adjuster, new { lineId, account });
            response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
        }

        using var period = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/periods/{Today.AddDays(-2):yyyy-MM}/close", Token("usr_close", Permissions.FinancePeriodClose));
        period.StatusCode.Should().Be(HttpStatusCode.OK, await period.Content.ReadAsStringAsync());
        using var closed = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/post-and-match", Adjuster, new { lineId, account = "5900" });
        (await ErrorCodeAsync(closed, HttpStatusCode.Conflict)).Should().Be("PERIOD_CLOSED");
        (await DbAsync(db => db.BankLineLinks.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task TheSummaryProvesTheLedgerAgainstTheStatement()
    {
        var id = await AccountAsync();
        await PostAsync("1020", "4010", "5000.00", Ago(6));
        await PostAsync("5010", "1020", "800.00", Ago(5));
        await PostAsync("1020", "4010", "123.00", Ago(1));
        await ImportAsync(id, Csv($"{Ago(6)},Deposit,,,5000.00,5000.00", $"{Ago(5)},Payment,,800.00,,4200.00", $"{Ago(3)},Bank fee,,20.00,,4180.00"));
        var summary = await OkAsync(HttpMethod.Get, $"/api/v1/admin/finance/bank-accounts/{id}/summary", Viewer);
        summary.GetProperty("ledgerBalance").GetString().Should().Be("4323.00");
        summary.GetProperty("bankBalance").GetString().Should().Be("4180.00");
        summary.GetProperty("matched").GetInt32().Should().Be(2);
        summary.GetProperty("unmatchedLines").GetInt32().Should().Be(1);
        summary.GetProperty("unmatchedLinesNet").GetString().Should().Be("-20.00");
        summary.GetProperty("unmatchedEntries").GetInt32().Should().Be(1);
        summary.GetProperty("unmatchedEntriesNet").GetString().Should().Be("123.00");
        summary.GetProperty("unexplained").GetString().Should().Be("0.00", "every difference is an item in the queue");

        var lineId = Line(await ReconciliationAsync(), "Bank fee").GetProperty("id").GetString()!;
        await OkAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/dismiss", Reconciler, new { lineId, reason = "Waived by the bank" });
        var after = await OkAsync(HttpMethod.Get, $"/api/v1/admin/finance/bank-accounts/{id}/summary", Viewer);
        after.GetProperty("dismissed").GetInt32().Should().Be(1);
        after.GetProperty("dismissedNet").GetString().Should().Be("-20.00");
        after.GetProperty("unexplained").GetString().Should().Be("0.00");

        using var absent = await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/bank-accounts/nope/summary", Viewer);
        absent.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheReconciliationListHonoursItsFiltersAndRefusesAnUnknownStatus()
    {
        var first = await AccountAsync();
        var second = await AccountAsync("222222", "1030");
        await ImportAsync(first, Csv($"{Ago(2)},In first,,,10.00,"));
        await ImportAsync(second, Csv($"{Ago(2)},In second,,,20.00,"));
        (await ReconciliationAsync()).GetProperty("lines").GetArrayLength().Should().Be(2);
        var filtered = await ReconciliationAsync("?account=" + second);
        filtered.GetProperty("lines").EnumerateArray().Single().GetProperty("description").GetString().Should().Be("In second");
        (await ReconciliationAsync("?limit=1")).GetProperty("lines").GetArrayLength().Should().Be(1);
        using var bad = await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/reconciliation?status=weird", Viewer);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var anonymous = await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/reconciliation", null);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ApplicationGuardsRefuseEditingStatementsLinesEventsAndRevealLogs()
    {
        var id = await AccountAsync();
        await ImportAsync(id, Csv($"{Ago(2)},Guard,,,10.00,"));
        var lineId = long.Parse(Line(await ReconciliationAsync(), "Guard").GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
        await OkAsync(HttpMethod.Post, "/api/v1/admin/finance/reconciliation/dismiss", Reconciler, new { lineId = lineId.ToString(CultureInfo.InvariantCulture), reason = "for the guard" });
        await OkAsync(HttpMethod.Get, $"/api/v1/admin/finance/bank-accounts/{id}/number", Revealer);

        await DbAsync(async db =>
        {
            (await db.BankLines.FirstAsync()).Amount = 99m;
            await db.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
            return 0;
        });
        await DbAsync(async db =>
        {
            db.BankStatements.Remove(await db.BankStatements.FirstAsync());
            await db.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
            return 0;
        });
        await DbAsync(async db =>
        {
            (await db.BankMatchEvents.FirstAsync()).Reason = "edited";
            await db.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
            return 0;
        });
        await DbAsync(async db =>
        {
            db.BankReveals.Remove(await db.BankReveals.FirstAsync());
            await db.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
            return 0;
        });
    }

    [Fact]
    public async Task EveryBankingChangeIsInTheAuditLogAndTheNumberNeverIs()
    {
        var id = await AccountAsync("0123456789012");
        await ImportAsync(id, Csv($"{Ago(2)},Audited,,,10.00,"));
        var audit = await OkAsync(HttpMethod.Get, "/api/v1/admin/finance/audit?limit=100", TestTokens.Issue([Permissions.AuditView]));
        var text = audit.GetRawText();
        text.Should().NotContain("0123456789012");
        var entities = audit.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("entity").GetString()).ToList();
        entities.Should().Contain(["BankAccount", "BankStatement", "BankLine"]);
    }
}
