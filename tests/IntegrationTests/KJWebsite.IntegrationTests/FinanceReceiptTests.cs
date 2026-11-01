using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Ledger;
using FinanceService.Receipts;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace KJWebsite.IntegrationTests;

public sealed class FinanceReceiptTests : IDisposable
{
    private static readonly string Viewer = TestTokens.Issue([Permissions.FinanceView], subject: "usr_viewer");
    private static readonly string Manager = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceReceiptManage], subject: "usr_manager");
    private static readonly string Reverser = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationReverse], subject: "usr_reverser");
    private static readonly string Maker = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationRecord], subject: "usr_maker");
    private static readonly string Checker = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationApprove], subject: "usr_checker");
    private static readonly string CurrentSeries = FiscalCalendar.FiscalYear(DateOnly.FromDateTime(DateTime.UtcNow), 7);

    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _proofs = Path.Combine(Path.GetTempPath(), $"kj-test-proofs-{Guid.NewGuid():N}");

    public FinanceReceiptTests()
    {
        _factory.Settings["Finance:Proofs:Path"] = _proofs;
        _factory.Settings["Finance:Payments:PublicApiBaseUrl"] = "https://api.example.org";
        _factory.Settings["Finance:Receipts:Organisation:RegistrationNumber"] = "REG-2019-0457";
        _factory.Settings["Finance:Receipts:Organisation:Address"] = "House 1, Road 2, Dhaka";
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

    private async Task<(string Id, string Reference)> DonateAsync(string email = "donor@example.org", string amount = "1000.00", string currency = "BDT", string name = "Rahim Uddin")
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/donations", new
        {
            amount,
            currency,
            frequency = "once",
            designation = new { type = "general" },
            donor = new { name, email },
            anonymous = false,
            coverFees = false,
            language = "bn"
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("id").GetString()!, json.GetProperty("reference").GetString()!);
    }

    private async Task<(string Id, string Reference)> ConfirmedAsync(string email = "donor@example.org", string amount = "1000.00", string currency = "BDT", string name = "Rahim Uddin")
    {
        var (id, reference) = await DonateAsync(email, amount, currency, name);
        (await ScopeAsync(s => s.GetRequiredService<DonationConfirmation>().ConfirmAsync(id, "bkash", null))).IsSuccess.Should().BeTrue();
        return (id, reference);
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

    private async Task<JsonElement> GetAsync(string path, string? token = null)
    {
        using var response = await SendAsync(HttpMethod.Get, path, token ?? Viewer);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<List<ReceiptEntity>> ReceiptsAsync() => DbAsync(async db => (await db.Receipts.AsNoTracking().ToListAsync()).OrderBy(r => r.Series, StringComparer.Ordinal).ThenBy(r => r.Seq).ToList());

    [Fact]
    public async Task ConfirmingADonationIssuesTheNextNumberInTheFiscalYearSeries()
    {
        await ConfirmedAsync();
        await ConfirmedAsync();

        var receipts = await ReceiptsAsync();

        receipts.Select(r => r.Number).Should().Equal($"KJR-{CurrentSeries}-000001", $"KJR-{CurrentSeries}-000002");
        receipts[1].PrevHash.Should().Be(receipts[0].Hash);
        receipts[0].PrevHash.Should().Be(new string('0', 64));
        (await GetAsync($"/api/v1/admin/finance/receipts/verify?series={CurrentSeries}")).GetProperty("valid").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task OnlyAConfirmedDonationGetsAReceiptAndOnlyOnce()
    {
        var pending = await DonateAsync();
        (await ReceiptsAsync()).Should().BeEmpty();
        var (id, _) = await ConfirmedAsync();

        for (var i = 0; i < 10; i++)
        {
            (await ScopeAsync(s => s.GetRequiredService<DonationConfirmation>().ConfirmAsync(id, "bkash", null))).Value.Should().BeFalse();
        }

        (await ReceiptsAsync()).Should().ContainSingle();
        (await DbAsync(db => db.Receipts.CountAsync(r => r.DonationId == pending.Id))).Should().Be(0);
    }

    [Fact]
    public async Task ARaceForTheSameNumberIsDecidedByTheDatabaseAndTheLoserGetsTheNext()
    {
        var a = await DonateAsync();
        var b = await DonateAsync();
        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();
        var donationA = await scopeA.ServiceProvider.GetRequiredService<FinanceDbContext>().Donations.AsNoTracking().SingleAsync(d => d.Id == a.Id);

        await scopeA.ServiceProvider.GetRequiredService<ReceiptService>().StageIssueAsync(donationA, "bkash", default); // A has picked number 1 but not saved
        (await scopeB.ServiceProvider.GetRequiredService<DonationConfirmation>().ConfirmAsync(b.Id, "bkash", null)).IsSuccess.Should().BeTrue(); // B takes number 1
        await ((Func<Task>)(() => scopeA.ServiceProvider.GetRequiredService<FinanceDbContext>().SaveChangesAsync())).Should().ThrowAsync<DbUpdateException>();

        (await ScopeAsync(s => s.GetRequiredService<DonationConfirmation>().ConfirmAsync(a.Id, "bkash", null))).Value.Should().BeTrue();
        var receipts = await ReceiptsAsync();
        receipts.Select(r => (r.Seq, r.DonationId)).Should().Equal((1L, b.Id), (2L, a.Id));
        (await GetAsync($"/api/v1/admin/finance/receipts/verify?series={CurrentSeries}")).GetProperty("valid").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task EachFiscalYearHasItsOwnSeriesStartingAtOne()
    {
        await ConfirmedAsync();
        var today = DateTime.UtcNow;
        var previousYearEnd = new DateTime(today.Month >= 7 ? today.Year : today.Year - 1, 6, 30, 0, 0, 0, DateTimeKind.Utc);
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/donations", Maker, new
        {
            donorName = "Old gift",
            amount = "50.00",
            currency = "BDT",
            method = "cash",
            date = previousYearEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{id}/approve", Checker, new { })).StatusCode.Should().Be(HttpStatusCode.OK);

        var receipts = await ReceiptsAsync();

        receipts.Select(r => r.Number).Should().Contain($"KJR-{CurrentSeries}-000001").And.Contain(n => n.EndsWith("-000001", StringComparison.Ordinal) && !n.Contains(CurrentSeries, StringComparison.Ordinal));
        receipts.Select(r => r.Series).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task ManyConfirmationsAcrossSeriesKeepEveryChainValidAndGapless()
    {
        for (var i = 0; i < 30; i++)
        {
            await ConfirmedAsync($"d{i % 7}@example.org", $"{100 + i}.00");
        }

        var check = await GetAsync($"/api/v1/admin/finance/receipts/verify?series={CurrentSeries}");

        (check.GetProperty("count").GetInt32(), check.GetProperty("valid").GetBoolean()).Should().Be((30, true));
        (await ReceiptsAsync()).Select(r => r.Seq).Should().Equal(Enumerable.Range(1, 30).Select(n => (long)n));
    }

    [Fact]
    public async Task TheChainShowsAnAlteredReceiptAndAMissingOne()
    {
        for (var i = 0; i < 4; i++)
        {
            await ConfirmedAsync();
        }

        await DbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE receipts SET Amount = 1 WHERE Seq = 2");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM receipts WHERE Seq = 3");
            return 0;
        });

        var check = await GetAsync($"/api/v1/admin/finance/receipts/verify?series={CurrentSeries}");

        check.GetProperty("valid").GetBoolean().Should().BeFalse();
        var problems = check.GetProperty("problems").EnumerateArray().Select(p => p.GetString()!).ToList();
        problems.Should().Contain(p => p.Contains("altered", StringComparison.Ordinal) && p.Contains("000002", StringComparison.Ordinal));
        problems.Should().Contain(p => p.Contains("Gap before", StringComparison.Ordinal) && p.Contains("000004", StringComparison.Ordinal));
        problems.Should().Contain(p => p.Contains("does not follow", StringComparison.Ordinal));
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/receipts/verify", Viewer)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheDonorGetsTheirReceiptAsABilingualPdfOnceConfirmed()
    {
        var (_, reference) = await DonateAsync();
        (await _client.GetAsync($"/api/v1/donations/{reference}/receipt.pdf")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetFromJsonAsync<JsonElement>($"/api/v1/donations/{reference}/status")).TryGetProperty("receiptUrl", out _).Should().BeFalse();
        await ScopeAsync(s => s.GetRequiredService<DonationConfirmation>().ConfirmAsync((reference == null ? "" : DbIdFor(reference).Result), "bkash", null));

        var status = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/donations/{reference}/status");
        status.GetProperty("receiptUrl").GetString().Should().Be($"https://api.example.org/api/v1/donations/{reference}/receipt.pdf");

        using var pdf = await _client.GetAsync($"/api/v1/donations/{reference}/receipt.pdf");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        pdf.Headers.CacheControl!.NoStore.Should().BeTrue();
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
        bytes.Length.Should().BeGreaterThan(5_000, "the Bengali font is embedded in the file");
        (await _client.GetAsync("/api/v1/donations/KJD-0000000000/receipt.pdf")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/api/v1/donations/nonsense/receipt.pdf")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private Task<string> DbIdFor(string reference) => DbAsync(db => db.Donations.Where(d => d.Reference == reference).Select(d => d.Id).SingleAsync());

    [Fact]
    public async Task BengaliNumeralsAndBilingualLabelsAreProducedForTheBengaliLine()
    {
        ReceiptPdf.BengaliDigits("KJR-2026-27-000123 1250.50").Should().Be("KJR-২০২৬-২৭-০০০১২৩ ১২৫০.৫০");
        ReceiptPdf.BengaliDigits(string.Empty).Should().BeEmpty();
        ReceiptPdf.MethodLabel("bank").Should().Contain("ব্যাংক");
        ReceiptPdf.MethodLabel("in_kind").Should().Contain("সামগ্রী");
        ReceiptPdf.MethodLabel(null).Should().Be("-");
        ReceiptPdf.DesignationLabel("campaign", "flood").Should().Contain("flood").And.Contain("ক্যাম্পেইন");
        ReceiptPdf.DesignationLabel("general", null).Should().Contain("সাধারণ");

        // The PDF itself: the embedded Bengali font is what makes conjuncts render, so check it is in the file.
        var (id, _) = await ConfirmedAsync(name: "ক্ষমতা নন্দিনী");
        var receipt = (await ReceiptsAsync()).Single();
        var document = await ScopeAsync(s => s.GetRequiredService<ReceiptService>().DocumentForAsync(receipt, default));
        var bytes = ReceiptPdf.Render(document!);
        Encoding.Latin1.GetString(bytes).Should().Contain("NotoSansBengali");
        document!.DonorName.Should().Be("ক্ষমতা নন্দিনী");
        id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TheReceiptCarriesTheOrganisationsLegalDetailsAsTheyWereWhenIssued()
    {
        await ConfirmedAsync();

        var receipt = (await ReceiptsAsync()).Single();

        receipt.OrganisationJson.Should().Contain("REG-2019-0457").And.Contain("House 1, Road 2, Dhaka");
        var document = await ScopeAsync(s => s.GetRequiredService<ReceiptService>().DocumentForAsync(receipt, default));
        document!.Organisation.RegistrationNumber.Should().Be("REG-2019-0457");
    }

    [Fact]
    public async Task ReissuingKeepsTheOriginalAndNumberAndIsRecordedAndMarksLaterCopies()
    {
        var (_, reference) = await ConfirmedAsync();
        var receipt = (await ReceiptsAsync()).Single();
        var before = await (await _client.GetAsync($"/api/v1/donations/{reference}/receipt.pdf")).Content.ReadAsByteArrayAsync();

        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/receipts/{receipt.Id}/reissue", Viewer, new { reason = "Donor lost it" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/receipts/{receipt.Id}/reissue", Manager, new { reason = "x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var first = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/receipts/{receipt.Id}/reissue", Manager, new { reason = "Donor lost it", resend = true });
        using var second = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/receipts/{receipt.Id}/reissue", Manager, new { reason = "Asked again" });

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("copies").GetInt32().Should().Be(2);
        var after = (await ReceiptsAsync()).Single();
        (after.Number, after.Hash, after.IssuedAt).Should().Be((receipt.Number, receipt.Hash, receipt.IssuedAt), "the original is preserved");
        var detail = await GetAsync($"/api/v1/admin/finance/receipts/{receipt.Id}");
        detail.GetProperty("reissues").GetArrayLength().Should().Be(2);
        detail.GetProperty("reissues")[0].GetProperty("reissuedBy").GetString().Should().Be("usr_manager");
        var duplicate = await (await _client.GetAsync($"/api/v1/donations/{reference}/receipt.pdf")).Content.ReadAsByteArrayAsync();
        duplicate.Should().NotEqual(before, "the copy is marked as a duplicate");
        (await DbAsync(db => db.Notifications.CountAsync(n => n.Template == "donation.receipt"))).Should().Be(2, "the original email plus the resend");

        var audit = await GetAsync("/api/v1/admin/finance/audit?limit=100", TestTokens.Issue([Permissions.AuditView]));
        audit.GetProperty("items").EnumerateArray().Select(e => (e.GetProperty("entity").GetString(), e.GetProperty("actorId").GetString())).Should().Contain(("ReceiptReissue", "usr_manager"));
    }

    [Fact]
    public async Task ReversingTheDonationVoidsTheReceiptWithoutBreakingTheSeries()
    {
        var (id, reference) = await ConfirmedAsync();
        await ConfirmedAsync();
        var receipt = (await ReceiptsAsync()).First();

        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{id}/reversal", Reverser, new { reason = "Chargeback" })).StatusCode.Should().Be(HttpStatusCode.Created);

        (await _client.GetAsync($"/api/v1/donations/{reference}/receipt.pdf")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetFromJsonAsync<JsonElement>($"/api/v1/donations/{reference}/status")).TryGetProperty("receiptUrl", out _).Should().BeFalse();
        var voided = await GetAsync($"/api/v1/admin/finance/receipts?status=void");
        voided.GetProperty("items").EnumerateArray().Single().GetProperty("number").GetString().Should().Be(receipt.Number);
        (await GetAsync($"/api/v1/admin/finance/receipts/{receipt.Id}")).GetProperty("voidReason").GetString().Should().Be("Chargeback");
        (await GetAsync("/api/v1/admin/finance/receipts?status=valid")).GetProperty("total").GetInt32().Should().Be(1);
        (await GetAsync($"/api/v1/admin/finance/receipts/verify?series={CurrentSeries}")).GetProperty("valid").GetBoolean().Should().BeTrue();
        using var again = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/receipts/{receipt.Id}/reissue", Manager, new { reason = "Please reissue" });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var pdf = await SendAsync(HttpMethod.Get, $"/api/v1/admin/finance/receipts/{receipt.Id}/pdf", Viewer);
        pdf.StatusCode.Should().Be(HttpStatusCode.OK, "staff can still print the void receipt, marked VOID");
    }

    [Fact]
    public async Task TheReceiptEmailGoesToTheOutboxOnlyWhenThereIsAnAddress()
    {
        await ConfirmedAsync("donor@example.org");
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/donations", Maker, new { donorName = "Walk-in", amount = "100.00", currency = "BDT", method = "cash" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{id}/approve", Checker, new { });

        var rows = await DbAsync(db => db.Notifications.AsNoTracking().ToListAsync());

        rows.Should().ContainSingle();
        (rows[0].Template, rows[0].Language, rows[0].SentAt).Should().Be(("donation.receipt", "bn", null));
        await ScopeAsync(s =>
        {
            var protector = s.GetRequiredService<FinanceService.Donations.IDonorProtector>();
            protector.Unprotect(rows[0].RecipientProtected).Should().Be("donor@example.org");
            protector.Unprotect(rows[0].PayloadProtected).Should().Contain($"KJR-{CurrentSeries}-000001");
            rows[0].RecipientProtected.Should().NotContain("example.org");
            return Task.FromResult(0);
        });
        (await ReceiptsAsync()).Should().HaveCount(2, "the walk-in donor still gets a receipt to print");
    }

    [Fact]
    public async Task OneDonorsGiftsShareAKeyRegardlessOfEmailCaseAndOtherDonorsDoNot()
    {
        await ConfirmedAsync("Same@Example.org");
        await ConfirmedAsync("same@example.org");
        await ConfirmedAsync("other@example.org");

        var keys = (await ReceiptsAsync()).Select(r => r.DonorKey).ToList();

        keys[0].Should().Be(keys[1]);
        keys[2].Should().NotBe(keys[0]);
        keys.Should().OnlyContain(k => k.Length == 64);
    }

    [Fact]
    public async Task AnnualStatementsAggregateCorrectlyPerDonorAndYear()
    {
        await ConfirmedAsync("a@example.org", "100.00", name: "Donor A");
        await ConfirmedAsync("A@example.org", "250.50", name: "Donor A");
        await ConfirmedAsync("a@example.org", "10.00", "USD", name: "Donor A");
        var (voidedId, _) = await ConfirmedAsync("a@example.org", "999.00", name: "Donor A");
        await ConfirmedAsync("b@example.org", "60.00", name: "Donor B");
        using var kind = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/donations", Maker, new
        {
            donorName = "Donor A",
            email = "a@example.org",
            amount = "1000.00",
            currency = "BDT",
            method = "in_kind",
            valuation = new { basis = "appraisal", itemDescription = "Desks" }
        });
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{(await kind.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()}/approve", Checker, new { });
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{voidedId}/reversal", Reverser, new { reason = "Chargeback" });

        var statements = await GetAsync($"/api/v1/admin/finance/statements?fiscalYear={CurrentSeries}");

        var items = statements.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(2);
        var a = items.Single(i => i.GetProperty("donorName").GetString() == "Donor A");
        a.GetProperty("receipts").GetInt32().Should().Be(4, "the voided gift is left out");
        var totals = a.GetProperty("totals").EnumerateArray().ToDictionary(t => t.GetProperty("currency").GetString()!, t => (t.GetProperty("cash").GetString(), t.GetProperty("inKind").GetString()));
        totals["BDT"].Should().Be(("350.50", "1000.00"));
        totals["USD"].Should().Be(("10.00", "0.00"));
        var b = items.Single(i => i.GetProperty("donorName").GetString() == "Donor B");
        b.GetProperty("totals")[0].GetProperty("cash").GetString().Should().Be("60.00");
        (await GetAsync("/api/v1/admin/finance/statements?fiscalYear=1999-00")).GetProperty("items").GetArrayLength().Should().Be(0);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/statements", Viewer)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var pdf = await SendAsync(HttpMethod.Get, $"/api/v1/admin/finance/statements/{a.GetProperty("donorKey").GetString()}/pdf?fiscalYear={CurrentSeries}", Viewer);
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync()).AsSpan(0, 5)).Should().Be("%PDF-");
        (await SendAsync(HttpMethod.Get, $"/api/v1/admin/finance/statements/unknownkey/pdf?fiscalYear={CurrentSeries}", Viewer)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StatementsAreQueuedOncePerDonorAndYear()
    {
        await ConfirmedAsync("a@example.org");
        await ConfirmedAsync("b@example.org");
        using var walkIn = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/donations", Maker, new { donorName = "Walk-in", amount = "10.00", currency = "BDT", method = "cash" });
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{(await walkIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()}/approve", Checker, new { });

        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/statements/send", Viewer, new { fiscalYear = CurrentSeries })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/statements/send", Manager, new { fiscalYear = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var first = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/statements/send", Manager, new { fiscalYear = CurrentSeries });
        using var second = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/statements/send", Manager, new { fiscalYear = CurrentSeries });

        var one = await first.Content.ReadFromJsonAsync<JsonElement>();
        (one.GetProperty("queued").GetInt32(), one.GetProperty("alreadyQueued").GetInt32(), one.GetProperty("noEmail").GetInt32()).Should().Be((2, 0, 1));
        var two = await second.Content.ReadFromJsonAsync<JsonElement>();
        (two.GetProperty("queued").GetInt32(), two.GetProperty("alreadyQueued").GetInt32()).Should().Be((0, 2));
        (await DbAsync(db => db.Notifications.CountAsync(n => n.Template == "donor.annual_statement"))).Should().Be(2);
        (await DbAsync(db => db.StatementSends.CountAsync())).Should().Be(2);
        var list = await GetAsync($"/api/v1/admin/finance/statements?fiscalYear={CurrentSeries}");
        list.GetProperty("items").EnumerateArray().Count(i => i.GetProperty("queued").GetBoolean()).Should().Be(2);
    }

    [Fact]
    public async Task ReceiptsAreAppendOnlyAndNeedTheirPermissions()
    {
        await ConfirmedAsync();
        await DbAsync(async db =>
        {
            async Task Refused(Action change)
            {
                change();
                await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<InvalidOperationException>();
                db.ChangeTracker.Clear();
            }

            await Refused(() => db.Receipts.First().Amount = 1m);
            await Refused(() => db.Receipts.Remove(db.Receipts.First()));
            return 0;
        });

        (await _client.GetAsync("/api/v1/admin/finance/receipts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/receipts", TestTokens.Issue([Permissions.ContentView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/receipts/rcp_missing", Viewer)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/receipts/rcp_missing/reissue", Manager, new { reason = "No such receipt" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/receipts/rcp_missing/pdf", Viewer)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        KjRoles.Seed[KjRoles.FinanceOfficer].Should().Contain(Permissions.FinanceReceiptManage);
        var list = await GetAsync($"/api/v1/admin/finance/receipts?series={CurrentSeries}&limit=5");
        list.GetProperty("items")[0].GetProperty("donorName").GetString().Should().Be("Rahim Uddin");
    }

    [Fact]
    public void TheDonorKeySecretIsRequiredOutsideDevelopment()
    {
        var empty = Options.Create(new ReceiptOptions());

        ((Func<DonorKeyHasher>)(() => new DonorKeyHasher(empty, new FakeEnvironment("Production")))).Should().Throw<InvalidOperationException>();
        new DonorKeyHasher(empty, new FakeEnvironment("Development")).Hash("x").Should().HaveLength(64);
        var a = new DonorKeyHasher(Options.Create(new ReceiptOptions { DonorKeySecret = "one" }), new FakeEnvironment("Production")).Hash("x");
        var b = new DonorKeyHasher(Options.Create(new ReceiptOptions { DonorKeySecret = "two" }), new FakeEnvironment("Production")).Hash("x");
        a.Should().NotBe(b);
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
