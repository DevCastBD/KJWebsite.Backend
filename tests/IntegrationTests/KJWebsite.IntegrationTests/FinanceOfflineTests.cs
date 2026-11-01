using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class FinanceOfflineTests : IDisposable
{
    private static readonly string Maker = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationRecord], subject: "usr_maker");
    private static readonly string Checker = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationApprove], subject: "usr_checker");
    private static readonly string Both = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationRecord, Permissions.FinanceDonationApprove], subject: "usr_maker");
    private static readonly string Viewer = TestTokens.Issue([Permissions.FinanceView], subject: "usr_viewer");
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\n");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _proofs = Path.Combine(Path.GetTempPath(), $"kj-test-proofs-{Guid.NewGuid():N}");

    public FinanceOfflineTests()
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

    private static string Ymd(DateTime date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<T> DbAsync<T>(Func<FinanceDbContext, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<FinanceDbContext>());
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, HttpContent? content = null, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PostJsonAsync(string path, string? token, object? body = null, string? key = null) => SendAsync(HttpMethod.Post, path, token, JsonContent.Create(body ?? new { }), key);

    private static object Gift(string method = "bank", string amount = "5000.00", string? date = null, string designation = "general", string currency = "BDT", object? valuation = null) => new
    {
        donorName = "Karim Hossain",
        email = "karim@example.org",
        amount,
        currency,
        method,
        date,
        designation,
        referenceNo = "TRX-991",
        note = "Transfer to the main account",
        valuation
    };

    private async Task<(string Id, JsonElement Body)> RecordAsync(object body, string? token = null)
    {
        using var response = await PostJsonAsync("/api/v1/admin/finance/donations", token ?? Maker, body);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("id").GetString()!, json);
    }

    private async Task<HttpResponseMessage> UploadAsync(string id, byte[] bytes, string contentType = "application/pdf", string? fileName = "slip.pdf", string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/finance/donations/{id}/proofs") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Maker);
        if (fileName is not null)
        {
            request.Headers.Add("X-Filename", Uri.EscapeDataString(fileName));
        }

        return await _client.SendAsync(request);
    }

    private async Task AttachProofAsync(string id) => (await UploadAsync(id, Pdf)).StatusCode.Should().Be(HttpStatusCode.Created);

    private Task<HttpResponseMessage> ApproveAsync(string id, string? token = null) => PostJsonAsync($"/api/v1/admin/finance/donations/{id}/approve", token ?? Checker);

    private async Task<JsonElement> GetAsync(string path, string? token = null)
    {
        using var response = await SendAsync(HttpMethod.Get, path, token ?? Viewer);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task ARecordedGiftIsPendingAndPostsNothingUntilApproved()
    {
        await TestCampaigns.SeedAsync(_factory.Services, "flood-relief");
        var (id, body) = await RecordAsync(Gift(designation: "campaign:flood-relief", date: Ymd(DateTime.UtcNow.AddDays(-3))));

        (body.GetProperty("status").GetString(), body.GetProperty("offline").GetBoolean(), body.GetProperty("method").GetString(), body.GetProperty("designation").GetString(), body.GetProperty("amount").GetString())
            .Should().Be(("pending", true, "bank", "campaign:flood-relief", "5000.00"));
        body.GetProperty("offlineRecord").GetProperty("status").GetString().Should().Be("pending");
        body.GetProperty("offlineRecord").GetProperty("recordedBy").GetString().Should().Be("usr_maker");
        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(0);

        var list = await GetAsync("/api/v1/admin/finance/donations?method=bank&status=pending");
        var row = list.GetProperty("items").EnumerateArray().Single();
        (row.GetProperty("id").GetString(), row.GetProperty("offline").GetBoolean(), row.GetProperty("method").GetString()).Should().Be((id, true, "bank"));
        (await GetAsync($"/api/v1/admin/finance/donations/{id}")).GetProperty("offline").GetProperty("method").GetString().Should().Be("bank");
    }

    [Theory]
    [InlineData("bad-method")]
    [InlineData("zero-amount")]
    [InlineData("third-decimal")]
    [InlineData("bad-currency")]
    [InlineData("future-date")]
    [InlineData("ancient-date")]
    [InlineData("bad-date-format")]
    [InlineData("bad-designation")]
    [InlineData("no-name")]
    [InlineData("bad-email")]
    [InlineData("in-kind-without-valuation")]
    [InlineData("in-kind-bad-basis")]
    [InlineData("in-kind-other-without-note")]
    [InlineData("in-kind-no-item")]
    [InlineData("valuation-on-cash")]
    [InlineData("long-note")]
    public async Task InvalidRecordsAreRefusedAndNothingIsSaved(string problem)
    {
        object body = problem switch
        {
            "bad-method" => Gift(method: "barter"),
            "zero-amount" => Gift(amount: "0.00"),
            "third-decimal" => Gift(amount: "10.005"),
            "bad-currency" => Gift(currency: "XYZ"),
            "future-date" => Gift(date: Ymd(DateTime.UtcNow.AddDays(2))),
            "ancient-date" => Gift(date: "2001-01-01"),
            "bad-date-format" => Gift(date: "31/12/2026"),
            "bad-designation" => Gift(designation: "campaign:Not A Slug"),
            "no-name" => new { donorName = "", amount = "10.00", currency = "BDT", method = "cash" },
            "bad-email" => new { donorName = "A", email = "nope", amount = "10.00", currency = "BDT", method = "cash" },
            "in-kind-without-valuation" => Gift(method: "in_kind"),
            "in-kind-bad-basis" => Gift(method: "in_kind", valuation: new { basis = "guess", itemDescription = "Laptops" }),
            "in-kind-other-without-note" => Gift(method: "in_kind", valuation: new { basis = "other", itemDescription = "Laptops" }),
            "in-kind-no-item" => Gift(method: "in_kind", valuation: new { basis = "market_price" }),
            "valuation-on-cash" => Gift(method: "cash", valuation: new { basis = "market_price", itemDescription = "x" }),
            _ => new { donorName = "A", amount = "10.00", currency = "BDT", method = "cash", note = new string('n', 1001) }
        };

        using var response = await PostJsonAsync("/api/v1/admin/finance/donations", Maker, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await DbAsync(db => db.Donations.CountAsync())).Should().Be(0);
        (await DbAsync(db => db.OfflineRecords.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task TheRecorderCannotApproveTheirOwnEntryEvenHoldingBothPermissions()
    {
        var (id, _) = await RecordAsync(Gift(method: "cash"), Both);

        using var response = await ApproveAsync(id, Both);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("SEPARATION_OF_DUTIES");
        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(0);
        (await DbAsync(db => db.OfflineDecisions.CountAsync())).Should().Be(0);
        (await GetAsync($"/api/v1/admin/finance/donations/{id}")).GetProperty("donation").GetProperty("status").GetString().Should().Be("pending");

        // Another approver can.
        (await ApproveAsync(id, Checker)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ABankGiftNeedsProofAndThenPostsDatedTheDayItArrived()
    {
        var received = Ymd(DateTime.UtcNow.AddDays(-2));
        var (id, _) = await RecordAsync(Gift(method: "bank", amount: "5000.00", date: received));

        using var withoutProof = await ApproveAsync(id);
        withoutProof.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await withoutProof.Content.ReadAsStringAsync()).Should().Contain("PROOF_REQUIRED");
        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(0);

        await AttachProofAsync(id);
        using var approved = await ApproveAsync(id);

        approved.StatusCode.Should().Be(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        var view = await approved.Content.ReadFromJsonAsync<JsonElement>();
        (view.GetProperty("status").GetString(), view.GetProperty("decision").GetProperty("decidedBy").GetString()).Should().Be(("approved", "usr_checker"));
        var posting = await DbAsync(db => db.Postings.Include(p => p.Entries).ThenInclude(e => e.Account).AsNoTracking().SingleAsync());
        posting.PostedOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).Should().Be(received);
        posting.Entries.Select(e => (e.Account!.Code, e.Debit, e.Credit)).Should().BeEquivalentTo([("1020", 5000.00m, 0m), ("4010", 0m, 5000.00m)]);
        (await GetAsync($"/api/v1/admin/finance/donations/{id}")).GetProperty("donation").GetProperty("status").GetString().Should().Be("confirmed");
    }

    [Theory]
    [InlineData("cash", "1010")]
    [InlineData("in_kind", "1250")]
    public async Task CashAndInKindNeedNoProofAndPostToTheirOwnAccounts(string method, string debitAccount)
    {
        object? valuation = method == "in_kind" ? new { basis = "market_price", itemDescription = "Ten school desks", note = "Local supplier quote" } : null;
        var (id, body) = await RecordAsync(Gift(method: method, amount: "12000.00", valuation: valuation));
        body.GetProperty("offlineRecord").GetProperty("proofRequired").GetBoolean().Should().BeFalse();

        (await ApproveAsync(id)).StatusCode.Should().Be(HttpStatusCode.OK);

        var posting = await DbAsync(db => db.Postings.Include(p => p.Entries).ThenInclude(e => e.Account).AsNoTracking().SingleAsync());
        posting.Entries.Select(e => (e.Account!.Code, e.Debit, e.Credit)).Should().BeEquivalentTo([(debitAccount, 12000.00m, 0m), ("4010", 0m, 12000.00m)]);
    }

    [Fact]
    public async Task AnInKindGiftKeepsItsValuationBasisAndDescription()
    {
        var (_, body) = await RecordAsync(Gift(method: "in_kind", amount: "30000.00", valuation: new { basis = "appraisal", itemDescription = "Used projector", note = "Appraised by Dhaka Electronics" }));

        var valuation = body.GetProperty("offlineRecord").GetProperty("valuation");
        (valuation.GetProperty("basis").GetString(), valuation.GetProperty("itemDescription").GetString(), valuation.GetProperty("note").GetString())
            .Should().Be(("appraisal", "Used projector", "Appraised by Dhaka Electronics"));
        body.GetProperty("amount").GetString().Should().Be("30000.00", "the valuation is the amount");
    }

    [Fact]
    public async Task ApprovingTenTimesPostsOnceAndAnApprovedOrRejectedGiftCannotBeReDecided()
    {
        var (id, _) = await RecordAsync(Gift(method: "cash"));

        for (var i = 0; i < 10; i++)
        {
            (await ApproveAsync(id)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(1);
        (await DbAsync(db => db.OfflineDecisions.CountAsync())).Should().Be(1);
        using var rejectAfter = await PostJsonAsync($"/api/v1/admin/finance/donations/{id}/reject", Checker, new { reason = "Changed my mind" });
        rejectAfter.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var (other, _) = await RecordAsync(Gift(method: "cash"));
        (await PostJsonAsync($"/api/v1/admin/finance/donations/{other}/reject", Checker, new { reason = "Duplicate entry" })).StatusCode.Should().Be(HttpStatusCode.OK);
        using var approveRejected = await ApproveAsync(other);
        approveRejected.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await approveRejected.Content.ReadAsStringAsync()).Should().Contain("ALREADY_DECIDED");
    }

    [Fact]
    public async Task RejectingCancelsTheDonationWithItsReasonAndPostsNothing()
    {
        var (id, _) = await RecordAsync(Gift(method: "cash"));

        (await PostJsonAsync($"/api/v1/admin/finance/donations/{id}/reject", Checker, new { reason = "x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var response = await PostJsonAsync($"/api/v1/admin/finance/donations/{id}/reject", Checker, new { reason = "Amount does not match the slip" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        (view.GetProperty("status").GetString(), view.GetProperty("decision").GetProperty("reason").GetString()).Should().Be(("rejected", "Amount does not match the slip"));
        var detail = await GetAsync($"/api/v1/admin/finance/donations/{id}");
        detail.GetProperty("donation").GetProperty("status").GetString().Should().Be("cancelled");
        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(0);
        (await UploadAsync(id, Pdf)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ProofsAreStoredWithTheirHashRetentionDateAndSafeName()
    {
        var (id, _) = await RecordAsync(Gift());

        using var response = await UploadAsync(id, Pdf, fileName: "../../etc/slip 1.pdf");

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var proof = await response.Content.ReadFromJsonAsync<JsonElement>();
        proof.GetProperty("fileName").GetString().Should().Be("slip 1.pdf");
        proof.GetProperty("sha256").GetString().Should().Be(Convert.ToHexString(SHA256.HashData(Pdf)).ToLowerInvariant());
        proof.GetProperty("sizeBytes").GetInt64().Should().Be(Pdf.Length);
        proof.GetProperty("retainUntil").GetString().Should().Be(DateTime.UtcNow.AddYears(7).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        proof.GetProperty("uploadedBy").GetString().Should().Be("usr_maker");

        using var download = await SendAsync(HttpMethod.Get, $"/api/v1/admin/finance/donations/{id}/proofs/{proof.GetProperty("id").GetString()}/file", Viewer);
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync()).Should().Equal(Pdf);
        (download.Content.Headers.ContentType!.MediaType, download.Content.Headers.ContentDisposition!.DispositionType).Should().Be(("application/pdf", "attachment"));
        download.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        (await SendAsync(HttpMethod.Get, $"/api/v1/admin/finance/donations/{id}/proofs/prf_missing/file", Viewer)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, $"/api/v1/admin/finance/donations/{id}/proofs/{proof.GetProperty("id").GetString()}/file", TestTokens.Issue([Permissions.ContentView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProofRulesRefuseTheWrongTypeSizeContentAndCount()
    {
        var (id, _) = await RecordAsync(Gift());

        (await UploadAsync(id, Pdf, "text/html")).StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await UploadAsync(id, Encoding.ASCII.GetBytes("<html>not a pdf</html>"), "application/pdf")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await UploadAsync(id, Pdf, "image/png")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "a PDF declared as a PNG");
        (await UploadAsync(id, [])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var huge = new byte[(5 * 1024 * 1024) + 1];
        Pdf.CopyTo(huge, 0);
        (await UploadAsync(id, huge)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await UploadAsync("don_missing", Pdf)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await DbAsync(db => db.OfflineProofs.CountAsync())).Should().Be(0);

        for (var i = 0; i < 10; i++)
        {
            (await UploadAsync(id, i % 2 == 0 ? Pdf : Png, i % 2 == 0 ? "application/pdf" : "image/png")).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        (await UploadAsync(id, Pdf)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ProofCannotBeAddedAfterTheDecisionAndNothingCanDeleteIt()
    {
        var (id, _) = await RecordAsync(Gift(method: "cash"));
        await AttachProofAsync(id);
        await ApproveAsync(id);

        (await UploadAsync(id, Pdf)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await SendAsync(HttpMethod.Delete, $"/api/v1/admin/finance/donations/{id}/proofs/anything", Both)).StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed, HttpStatusCode.Forbidden);
        await DbAsync(async db =>
        {
            db.OfflineProofs.Remove(await db.OfflineProofs.FirstAsync());
            await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<InvalidOperationException>();
            return 0;
        });
    }

    [Fact]
    public async Task OfflineRecordsAndDecisionsAreAppendOnly()
    {
        var (id, _) = await RecordAsync(Gift(method: "cash"));
        await ApproveAsync(id);

        await DbAsync(async db =>
        {
            async Task Refused(Action change)
            {
                change();
                await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<InvalidOperationException>();
                db.ChangeTracker.Clear();
            }

            await Refused(() => db.OfflineRecords.First().Note = "edited");
            await Refused(() => db.OfflineRecords.Remove(db.OfflineRecords.First()));
            await Refused(() => db.OfflineDecisions.First().Decision = "rejected");
            await Refused(() => db.OfflineDecisions.Remove(db.OfflineDecisions.First()));
            return 0;
        });
    }

    [Fact]
    public async Task AGiftDatedInAClosedMonthIsPostedTodayInstead()
    {
        var lastMonth = DateTime.UtcNow.AddMonths(-1);
        var key = lastMonth.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var date = new DateTime(lastMonth.Year, lastMonth.Month, 10).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var (id, _) = await RecordAsync(Gift(method: "cash", date: date));
        (await PostJsonAsync($"/api/v1/admin/finance/ledger/periods/{key}/close", TestTokens.Issue([Permissions.FinancePeriodClose]))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ApproveAsync(id)).StatusCode.Should().Be(HttpStatusCode.OK);

        var posting = await DbAsync(db => db.Postings.AsNoTracking().SingleAsync());
        posting.PostedOn.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
    }

    [Fact]
    public async Task EveryStepIsAuditedWithItsOwnActor()
    {
        var (id, _) = await RecordAsync(Gift(method: "bank"));
        await AttachProofAsync(id);
        await ApproveAsync(id);

        var audit = await GetAsync("/api/v1/admin/finance/audit?limit=100", TestTokens.Issue([Permissions.AuditView]));

        var events = audit.GetProperty("items").EnumerateArray().Select(e => (Entity: e.GetProperty("entity").GetString(), Actor: e.GetProperty("actorId").GetString())).ToList();
        events.Should().Contain(("OfflineRecord", "usr_maker")).And.Contain(("OfflineProof", "usr_maker")).And.Contain(("OfflineDecision", "usr_checker"));
        events.Should().Contain(("Donation", "usr_maker"));
    }

    [Fact]
    public async Task EachStepNeedsItsOwnPermission()
    {
        (await PostJsonAsync("/api/v1/admin/finance/donations", null, Gift())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostJsonAsync("/api/v1/admin/finance/donations", Viewer, Gift())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostJsonAsync("/api/v1/admin/finance/donations", Checker, Gift())).StatusCode.Should().Be(HttpStatusCode.Forbidden, "approving is not recording");
        var (id, _) = await RecordAsync(Gift(method: "cash"));
        (await ApproveAsync(id, Maker)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "recording is not approving");
        (await ApproveAsync(id, Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await UploadAsync(id, Pdf, token: Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostJsonAsync($"/api/v1/admin/finance/donations/{id}/reject", Maker, new { reason = "nope nope" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        KjRoles.Seed[KjRoles.FinanceOfficer].Should().Contain([Permissions.FinanceDonationRecord, Permissions.FinanceDonationApprove]);
        KjRoles.Seed[KjRoles.Admin].Should().Contain([Permissions.FinanceDonationRecord, Permissions.FinanceDonationApprove]);
        KjRoles.Seed[KjRoles.Member].Should().NotContain(Permissions.FinanceDonationApprove);
    }

    [Fact]
    public async Task ApprovingOrRejectingSomethingThatIsNotAnOfflineGiftIs404()
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

        (await ApproveAsync(id)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PostJsonAsync($"/api/v1/admin/finance/donations/{id}/reject", Checker, new { reason = "not offline" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, $"/api/v1/admin/finance/donations/{id}/offline", Viewer)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ApproveAsync("don_missing")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ARepeatedRecordRequestWithTheSameIdempotencyKeyCreatesOneDonation()
    {
        using var first = await PostJsonAsync("/api/v1/admin/finance/donations", Maker, Gift(method: "cash"), "offline-key-1");
        using var second = await PostJsonAsync("/api/v1/admin/finance/donations", Maker, Gift(method: "cash"), "offline-key-1");

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        (await DbAsync(db => db.Donations.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task ADonorWithoutAnEmailAndAnAnonymousGiftAreRecorded()
    {
        var (id, _) = await RecordAsync(new { donorName = "Walk-in donor", amount = "200.00", currency = "BDT", method = "cash", anonymous = true, dedication = "In memory of Rahim" });

        var detail = await GetAsync($"/api/v1/admin/finance/donations/{id}");
        (detail.GetProperty("donation").GetProperty("anonymous").GetBoolean(), detail.GetProperty("dedication").GetString(), detail.GetProperty("donorEmail").GetString()).Should().Be((true, "In memory of Rahim", string.Empty));
    }

    [Fact]
    public async Task ForeignCurrencyOfflineGiftsPostInTheirOwnCurrency()
    {
        var (id, _) = await RecordAsync(Gift(method: "bank", amount: "100.00", currency: "USD"));
        await AttachProofAsync(id);

        (await ApproveAsync(id)).StatusCode.Should().Be(HttpStatusCode.OK);

        var posting = await DbAsync(db => db.Postings.Include(p => p.Entries).AsNoTracking().SingleAsync());
        posting.Entries.Should().OnlyContain(e => e.Currency == "USD");
    }
}
