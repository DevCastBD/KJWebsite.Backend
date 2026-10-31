using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Expenses;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class ExpenseOptionsTests
{
    [Theory]
    [InlineData("BDT", "0.01", 1)]
    [InlineData("BDT", "9999.99", 1)]
    [InlineData("BDT", "10000.00", 2)]
    [InlineData("BDT", "99999.99", 2)]
    [InlineData("BDT", "100000.00", 3)]
    [InlineData("BDT", "5000000.00", 3)]
    [InlineData("USD", "99.99", 1)]
    [InlineData("USD", "100.00", 2)]
    [InlineData("USD", "1000.00", 3)]
    [InlineData("GBP", "79.99", 1)]
    [InlineData("GBP", "800.00", 3)]
    [InlineData("EUR", "89.99", 1)]
    [InlineData("EUR", "900.00", 3)]
    public void DefaultTiersFollowTheAmount(string currency, string amount, int expected)
    {
        new ExpenseOptions().RequiredApprovers(currency, decimal.Parse(amount, CultureInfo.InvariantCulture)).Should().Be(expected);
    }

    [Fact]
    public void ConfiguredTiersReplaceTheDefaultsForThatCurrencyOnly()
    {
        var options = new ExpenseOptions { Approvals = { ["BDT"] = [new ApprovalTier { Below = 500m, Approvers = 1 }, new ApprovalTier { Approvers = 5 }] } };

        options.RequiredApprovers("BDT", 499m).Should().Be(1);
        options.RequiredApprovers("BDT", 500m).Should().Be(5);
        options.RequiredApprovers("BDT", 5_000_000m).Should().Be(5);
        options.RequiredApprovers("USD", 150m).Should().Be(2, "USD still uses the defaults");
    }

    [Fact]
    public void TiersAreReadInAmountOrderNotListedOrderAndNeverBelowOne()
    {
        var options = new ExpenseOptions { Approvals = { ["BDT"] = [new ApprovalTier { Approvers = 4 }, new ApprovalTier { Below = 1_000m, Approvers = 0 }, new ApprovalTier { Below = 50m, Approvers = 1 }] } };

        options.RequiredApprovers("BDT", 10m).Should().Be(1);
        options.RequiredApprovers("BDT", 500m).Should().Be(1, "an approvers count below one is raised to one");
        options.RequiredApprovers("BDT", 5_000m).Should().Be(4);
    }

    [Fact]
    public void AnUnknownCurrencyGetsTheStrictestTierAndCategoriesFallBackToTheDefaults()
    {
        new ExpenseOptions().RequiredApprovers("JPY", 1m).Should().Be(3);
        new ExpenseOptions().EffectiveCategories["Programme"].Should().Be("5010", "category keys are matched without regard to case");
        new ExpenseOptions { Categories = { ["travel"] = "5050" } }.EffectiveCategories.Should().ContainKey("travel").And.NotContainKey("programme");
    }
}

public sealed class FinanceExpenseTests : IDisposable
{
    private static string Token(string subject, params string[] permissions) => TestTokens.Issue([Permissions.FinanceView, .. permissions], subject: subject);
    private static readonly string Submitter = Token("usr_sub", Permissions.FinanceExpenseSubmit);
    private static readonly string SubmitterWhoCanApprove = Token("usr_sub", Permissions.FinanceExpenseSubmit, Permissions.FinanceExpenseApprove, Permissions.FinanceExpensePay);
    private static readonly string A = Token("usr_a", Permissions.FinanceExpenseApprove);
    private static readonly string B = Token("usr_b", Permissions.FinanceExpenseApprove);
    private static readonly string C = Token("usr_c", Permissions.FinanceExpenseApprove);
    private static readonly string Payer = Token("usr_payer", Permissions.FinanceExpensePay);
    private static readonly string Viewer = Token("usr_viewer");
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\n");
    private static string Ymd(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _proofs = Path.Combine(Path.GetTempPath(), $"kj-test-proofs-{Guid.NewGuid():N}");

    public FinanceExpenseTests()
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

    private Task<T> DbAsync<T>(Func<FinanceDbContext, Task<T>> action) => ScopeAsync(s => action(s.GetRequiredService<FinanceDbContext>()));

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

        return await _client.SendAsync(request);
    }

    private async Task<JsonElement> GetAsync(string path, string? token = null)
    {
        using var response = await SendAsync(HttpMethod.Get, path, token ?? Viewer);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static object Claim(string amount = "5000.00", string currency = "BDT", string category = "programme", string? project = null, bool reimbursement = false, string? date = null) => new
    {
        payee = "Rahim Stationers",
        description = "Printing for the camp",
        category,
        amount,
        currency,
        project,
        reimbursement,
        date
    };

    private async Task<string> SubmitAsync(object? body = null, string? token = null, bool receipt = true, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/finance/expenses") { Content = JsonContent.Create(body ?? Claim()) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Submitter);
        using var response = await (client ?? _client).SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        if (receipt)
        {
            (await UploadAsync(id, Pdf, client: client)).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        return id;
    }

    private async Task<HttpResponseMessage> UploadAsync(string id, byte[] bytes, string contentType = "application/pdf", string? token = null, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/finance/expenses/{id}/receipts") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Submitter);
        request.Headers.Add("X-Filename", Uri.EscapeDataString("../../bill 1.pdf"));
        return await (client ?? _client).SendAsync(request);
    }

    private Task<HttpResponseMessage> ApproveAsync(string id, string token) => SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{id}/approve", token, new { note = "ok" });

    private async Task<JsonElement> ExpenseAsync(string id) => await GetAsync($"/api/v1/admin/finance/expenses/{id}");

    [Fact]
    public async Task AnExpenseIsSubmittedInTheFrontendShapeAndPendingWithItsApprovalBar()
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/expenses", Submitter, Claim("25000.00", category: "Programme", project: "school-roof", reimbursement: true));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var row = await response.Content.ReadFromJsonAsync<JsonElement>();
        (row.GetProperty("status").GetString(), row.GetProperty("approvals").GetInt32(), row.GetProperty("requiredApprovals").GetInt32(), row.GetProperty("paid").GetBoolean()).Should().Be(("pending", 0, 2, false));
        (row.GetProperty("payee").GetString(), row.GetProperty("category").GetString(), row.GetProperty("amount").GetString(), row.GetProperty("currency").GetString(), row.GetProperty("project").GetString(), row.GetProperty("reimbursement").GetBoolean())
            .Should().Be(("Rahim Stationers", "programme", "25000.00", "BDT", "school-roof", true));
        (row.GetProperty("submittedById").GetString(), row.GetProperty("submittedByName").GetString(), row.GetProperty("submittedOn").GetString()).Should().Be(("usr_sub", "usr_sub", Ymd(DateTime.UtcNow)));
        var detail = await ExpenseAsync(row.GetProperty("id").GetString()!);
        (detail.GetProperty("accountCode").GetString(), detail.GetProperty("approvalList").GetArrayLength(), detail.GetProperty("receipts").GetArrayLength()).Should().Be(("5010", 0, 0));
    }

    [Theory]
    [InlineData("no-payee")]
    [InlineData("no-description")]
    [InlineData("unknown-category")]
    [InlineData("zero-amount")]
    [InlineData("third-decimal")]
    [InlineData("bad-currency")]
    [InlineData("bad-project")]
    [InlineData("future-date")]
    [InlineData("ancient-date")]
    [InlineData("bad-date")]
    public async Task InvalidClaimsAreRefusedAndNothingIsSaved(string problem)
    {
        object body = problem switch
        {
            "no-payee" => new { payee = " ", description = "d", category = "programme", amount = "10.00", currency = "BDT" },
            "no-description" => new { payee = "p", description = "", category = "programme", amount = "10.00", currency = "BDT" },
            "unknown-category" => Claim(category: "yachts"),
            "zero-amount" => Claim(amount: "0.00"),
            "third-decimal" => Claim(amount: "10.005"),
            "bad-currency" => Claim(currency: "XYZ"),
            "bad-project" => Claim(project: "Not A Slug"),
            "future-date" => Claim(date: Ymd(DateTime.UtcNow.AddDays(2))),
            "ancient-date" => Claim(date: "2001-01-01"),
            _ => Claim(date: "31/12/2026")
        };

        using var response = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/expenses", Submitter, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await DbAsync(db => db.Expenses.CountAsync())).Should().Be(0);
    }

    [Theory]
    [InlineData("5000.00", "BDT", 1)]
    [InlineData("10000.00", "BDT", 2)]
    [InlineData("100000.00", "BDT", 3)]
    [InlineData("99.00", "USD", 1)]
    [InlineData("500.00", "USD", 2)]
    public async Task TheAmountTierSetsHowManyDifferentApproversAreNeeded(string amount, string currency, int required)
    {
        var id = await SubmitAsync(Claim(amount, currency));

        (await ExpenseAsync(id)).GetProperty("expense").GetProperty("requiredApprovals").GetInt32().Should().Be(required);
    }

    [Fact]
    public async Task ConfiguredTiersAndReceiptRulesAreEnforcedByTheService()
    {
        using var factory = new FinanceApiFactory();
        factory.Settings["Finance:Proofs:Path"] = _proofs;
        factory.Settings["Finance:Expenses:Approvals:BDT:0:Below"] = "500";
        factory.Settings["Finance:Expenses:Approvals:BDT:0:Approvers"] = "1";
        factory.Settings["Finance:Expenses:Approvals:BDT:1:Approvers"] = "5";
        factory.Settings["Finance:Expenses:ReceiptRequired"] = "false";
        using var client = factory.CreateClient();

        var small = await SubmitAsync(Claim("499.00"), receipt: false, client: client);
        var big = await SubmitAsync(Claim("500.00"), receipt: false, client: client);

        async Task<JsonElement> Get(string id)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/admin/finance/expenses/{id}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Viewer);
            return await (await client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
        }

        (await Get(small)).GetProperty("expense").GetProperty("requiredApprovals").GetInt32().Should().Be(1);
        (await Get(big)).GetProperty("expense").GetProperty("requiredApprovals").GetInt32().Should().Be(5);
        using var approve = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{small}/approve") { Content = JsonContent.Create(new { }) };
        approve.Headers.Authorization = new AuthenticationHeaderValue("Bearer", A);
        (await client.SendAsync(approve)).StatusCode.Should().Be(HttpStatusCode.OK, "no receipt was needed under this configuration");
    }

    [Fact]
    public async Task TheSubmitterCanNeverApproveRejectOrPayTheirOwnExpenseEvenHoldingEveryPermission()
    {
        var id = await SubmitAsync(token: SubmitterWhoCanApprove);

        foreach (var action in new[] { "approve", "reject" })
        {
            using var denied = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{id}/{action}", SubmitterWhoCanApprove, new { note = "my own expense" });
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await denied.Content.ReadAsStringAsync()).Should().Contain("SEPARATION_OF_DUTIES");
        }

        (await DbAsync(db => db.ExpenseApprovals.CountAsync())).Should().Be(0);
        (await DbAsync(db => db.ExpenseDecisions.CountAsync())).Should().Be(0);
        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(0);

        // Once someone else approves it, the submitter still cannot be the one who pays it.
        (await ApproveAsync(id, A)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var pay = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{id}/pay", SubmitterWhoCanApprove, new { method = "cash" });
        pay.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DbAsync(db => db.ExpensePayments.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task ApprovalNeedsAReceiptFirst()
    {
        var id = await SubmitAsync(receipt: false);

        using var refused = await ApproveAsync(id, A);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("RECEIPT_REQUIRED");
        (await DbAsync(db => db.ExpenseApprovals.CountAsync())).Should().Be(0);
        (await UploadAsync(id, Pdf)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await ApproveAsync(id, A)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ASmallExpenseIsApprovedByOnePersonAndPostsToTheLedger()
    {
        var incurred = Ymd(DateTime.UtcNow.AddDays(-2));
        var id = await SubmitAsync(Claim("5000.00", category: "events", date: incurred));

        using var response = await ApproveAsync(id, A);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var row = await response.Content.ReadFromJsonAsync<JsonElement>();
        (row.GetProperty("status").GetString(), row.GetProperty("approvals").GetInt32(), row.GetProperty("decidedByName").GetString()).Should().Be(("approved", 1, "usr_a"));
        var posting = await DbAsync(db => db.Postings.Include(p => p.Entries).ThenInclude(e => e.Account).AsNoTracking().SingleAsync());
        (posting.SourceType, posting.SourceId, Ymd(posting.PostedOn.ToDateTime(TimeOnly.MinValue))).Should().Be(("expense", id, incurred));
        posting.Entries.Select(e => (e.Account!.Code, e.Debit, e.Credit)).Should().BeEquivalentTo([("5050", 5000.00m, 0m), ("2100", 0m, 5000.00m)]);
        (await ExpenseAsync(id)).GetProperty("postingId").GetString().Should().Be(posting.Id.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task AReimbursementIsOwedToThePersonNotAVendor()
    {
        var id = await SubmitAsync(Claim("800.00", reimbursement: true));
        await ApproveAsync(id, A);

        var posting = await DbAsync(db => db.Postings.Include(p => p.Entries).ThenInclude(e => e.Account).AsNoTracking().SingleAsync());

        posting.Entries.Select(e => e.Account!.Code).Should().BeEquivalentTo(["5010", "2020"]);
    }

    [Fact]
    public async Task ALargerExpenseNeedsDifferentApproversInTurnAndPostsOnlyWhenTheLastOneApproves()
    {
        var id = await SubmitAsync(Claim("20000.00"));

        (await ApproveAsync(id, A)).StatusCode.Should().Be(HttpStatusCode.OK);
        var afterOne = await ExpenseAsync(id);
        (afterOne.GetProperty("expense").GetProperty("status").GetString(), afterOne.GetProperty("expense").GetProperty("approvals").GetInt32()).Should().Be(("pending", 1));
        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(0, "one of two approvals posts nothing");

        (await ApproveAsync(id, A)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ExpenseAsync(id)).GetProperty("expense").GetProperty("approvals").GetInt32().Should().Be(1, "the same person approving again does not count twice");

        (await ApproveAsync(id, B)).StatusCode.Should().Be(HttpStatusCode.OK);
        var done = await ExpenseAsync(id);
        done.GetProperty("expense").GetProperty("status").GetString().Should().Be("approved");
        done.GetProperty("approvalList").EnumerateArray().Select(a => a.GetProperty("approvedBy").GetString()).Should().Equal("usr_a", "usr_b");
        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(1);

        (await ApproveAsync(id, C)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await DbAsync(db => db.ExpenseApprovals.CountAsync())).Should().Be(2, "once decided, a late approver adds nothing");
        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task ARaceThatLeavesEnoughApprovalsButNoDecisionIsFinishedOnTheNextApprove()
    {
        var id = await SubmitAsync(Claim("20000.00"));
        await DbAsync(async db =>
        {
            db.ExpenseApprovals.AddRange(new ExpenseApprovalEntity { ExpenseId = id, ApprovedBy = "usr_a", At = DateTimeOffset.UtcNow }, new ExpenseApprovalEntity { ExpenseId = id, ApprovedBy = "usr_b", At = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });

        (await ApproveAsync(id, A)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ExpenseAsync(id)).GetProperty("expense").GetProperty("status").GetString().Should().Be("approved");
        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(1);
        (await DbAsync(db => db.ExpenseDecisions.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task ThreeApproversAreNeededForTheBiggestExpenses()
    {
        var id = await SubmitAsync(Claim("150000.00"));

        await ApproveAsync(id, A);
        await ApproveAsync(id, B);
        (await ExpenseAsync(id)).GetProperty("expense").GetProperty("status").GetString().Should().Be("pending");
        await ApproveAsync(id, C);

        (await ExpenseAsync(id)).GetProperty("expense").GetProperty("status").GetString().Should().Be("approved");
    }

    [Fact]
    public async Task OneRejectionEndsItWithItsNoteAndNothingIsPosted()
    {
        var id = await SubmitAsync(Claim("20000.00"));
        await ApproveAsync(id, A);

        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{id}/reject", B, new { note = "x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var rejected = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{id}/reject", B, new { note = "Not a programme cost" });

        rejected.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        (row.GetProperty("status").GetString(), row.GetProperty("note").GetString(), row.GetProperty("decidedByName").GetString()).Should().Be(("rejected", "Not a programme cost", "usr_b"));
        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(0);
        using var approveAfter = await ApproveAsync(id, C);
        approveAfter.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{id}/reject", C, new { note = "Again again" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await UploadAsync(id, Pdf)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PaymentPostsAgainstThePayableAndMarksItPaidAndNeverTwice()
    {
        var vendor = await SubmitAsync(Claim("5000.00"));
        var reimbursement = await SubmitAsync(Claim("700.00", reimbursement: true));
        await ApproveAsync(vendor, A);
        await ApproveAsync(reimbursement, A);

        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{vendor}/pay", A, new { method = "bank" })).StatusCode.Should().Be(HttpStatusCode.Forbidden, "approving is not paying");
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{vendor}/pay", Payer, new { method = "barter" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{vendor}/pay", Payer, new { method = "bank", paidOn = Ymd(DateTime.UtcNow.AddDays(2)) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var paid = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{vendor}/pay", Payer, new { method = "bank", reference = "TRX-77" });
        paid.StatusCode.Should().Be(HttpStatusCode.OK, await paid.Content.ReadAsStringAsync());
        using var cash = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{reimbursement}/pay", Payer, new { method = "bkash" });
        cash.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await ExpenseAsync(vendor);
        (detail.GetProperty("expense").GetProperty("paid").GetBoolean(), detail.GetProperty("payment").GetProperty("method").GetString(), detail.GetProperty("payment").GetProperty("reference").GetString(), detail.GetProperty("payment").GetProperty("paidBy").GetString())
            .Should().Be((true, "bank", "TRX-77", "usr_payer"));
        var postings = await DbAsync(db => db.Postings.Include(p => p.Entries).ThenInclude(e => e.Account).AsNoTracking().Where(p => p.SourceId!.EndsWith(":payment")).ToListAsync());
        postings.SelectMany(p => p.Entries).Select(e => (e.Account!.Code, e.Debit, e.Credit)).Should().BeEquivalentTo([("2100", 5000.00m, 0m), ("1020", 0m, 5000.00m), ("2020", 700.00m, 0m), ("1030", 0m, 700.00m)]);
        using var twice = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{vendor}/pay", Payer, new { method = "bank" });
        twice.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var balance = await GetAsync("/api/v1/admin/finance/ledger/trial-balance");
        balance.GetProperty("currencies")[0].GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("code").GetString() == "2100").GetProperty("balance").GetString().Should().Be("0.00", "approved and paid nets the payable to zero");
    }

    [Fact]
    public async Task OnlyAnApprovedExpenseCanBePaid()
    {
        var pending = await SubmitAsync();
        var rejected = await SubmitAsync();
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{rejected}/reject", A, new { note = "No good" });

        foreach (var id in new[] { pending, rejected })
        {
            using var response = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{id}/pay", Payer, new { method = "cash" });
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await response.Content.ReadAsStringAsync()).Should().Contain("NOT_APPROVED");
        }

        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/expenses/exp_missing/pay", Payer, new { method = "cash" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnExpenseFromAClosedMonthPostsTodayAndAClosedCurrentMonthStopsTheApprovalCold()
    {
        var lastMonth = DateTime.UtcNow.AddMonths(-1);
        var closer = Token("usr_closer", Permissions.FinancePeriodClose);
        var old = await SubmitAsync(Claim(date: Ymd(new DateTime(lastMonth.Year, lastMonth.Month, 10))));
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/periods/{lastMonth:yyyy-MM}/close", closer, new { })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ApproveAsync(old, A)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await DbAsync(db => db.Postings.AsNoTracking().SingleAsync())).PostedOn.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));

        var next = await SubmitAsync();
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/periods/{DateTime.UtcNow:yyyy-MM}/close", closer, new { });
        using var blocked = await ApproveAsync(next, A);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("PERIOD_CLOSED");
        (await DbAsync(db => db.ExpenseApprovals.CountAsync(a => a.ExpenseId == next))).Should().Be(0, "nothing was half-saved");
        (await DbAsync(db => db.ExpenseDecisions.CountAsync(d => d.ExpenseId == next))).Should().Be(0);
    }

    [Fact]
    public async Task ReceiptsAreCheckedKeptWithTheirHashAndServedBackAsAttachments()
    {
        var id = await SubmitAsync(receipt: false);

        (await UploadAsync(id, Pdf, "text/html")).StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await UploadAsync(id, Encoding.ASCII.GetBytes("<html>"), "application/pdf")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await UploadAsync(id, [])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var huge = new byte[(5 * 1024 * 1024) + 1];
        Pdf.CopyTo(huge, 0);
        (await UploadAsync(id, huge)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await UploadAsync("exp_missing", Pdf)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await UploadAsync(id, Pdf, token: Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var ok = await UploadAsync(id, Pdf);
        ok.StatusCode.Should().Be(HttpStatusCode.Created);
        var receipt = await ok.Content.ReadFromJsonAsync<JsonElement>();
        (receipt.GetProperty("fileName").GetString(), receipt.GetProperty("sizeBytes").GetInt64(), receipt.GetProperty("uploadedBy").GetString()).Should().Be(("bill 1.pdf", Pdf.Length, "usr_sub"));
        receipt.GetProperty("sha256").GetString().Should().Be(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Pdf)).ToLowerInvariant());
        receipt.GetProperty("retainUntil").GetString().Should().Be(Ymd(DateTime.UtcNow.AddYears(7)));
        using var download = await SendAsync(HttpMethod.Get, $"/api/v1/admin/finance/expenses/{id}/receipts/{receipt.GetProperty("id").GetString()}/file", Viewer);
        (await download.Content.ReadAsByteArrayAsync()).Should().Equal(Pdf);
        (download.Content.Headers.ContentDisposition!.DispositionType, download.Headers.GetValues("X-Content-Type-Options").Single()).Should().Be(("attachment", "nosniff"));
        (await SendAsync(HttpMethod.Get, $"/api/v1/admin/finance/expenses/{id}/receipts/rcx_missing/file", Viewer)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        for (var i = 0; i < 9; i++)
        {
            (await UploadAsync(id, Pdf)).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        (await UploadAsync(id, Pdf)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ListsFilterByStatusProjectAndCategoryWithPartApprovedStillPending()
    {
        var a = await SubmitAsync(Claim("5000.00", project: "school", category: "programme"));
        var b = await SubmitAsync(Claim("20000.00", project: "school", category: "events"));
        var c = await SubmitAsync(Claim("300.00", project: "clinic", category: "administrative"));
        await ApproveAsync(a, A);
        await ApproveAsync(b, A);
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{c}/reject", A, new { note = "Personal cost" });

        (await GetAsync("/api/v1/admin/finance/expenses")).GetProperty("total").GetInt32().Should().Be(3);
        (await GetAsync("/api/v1/admin/finance/expenses?status=pending")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Should().Equal(b);
        (await GetAsync("/api/v1/admin/finance/expenses?status=approved")).GetProperty("total").GetInt32().Should().Be(1);
        (await GetAsync("/api/v1/admin/finance/expenses?status=rejected")).GetProperty("total").GetInt32().Should().Be(1);
        (await GetAsync("/api/v1/admin/finance/expenses?project=school")).GetProperty("total").GetInt32().Should().Be(2);
        (await GetAsync("/api/v1/admin/finance/expenses?category=events")).GetProperty("total").GetInt32().Should().Be(1);
        var page = await GetAsync("/api/v1/admin/finance/expenses?limit=2&offset=2");
        (page.GetProperty("items").GetArrayLength(), page.GetProperty("limit").GetInt32()).Should().Be((1, 2));
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/expenses/exp_missing", Viewer)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RollupsPerProjectAndCategoryReconcileWithTheLedger()
    {
        var one = await SubmitAsync(Claim("5000.00", project: "school", category: "programme"));
        var two = await SubmitAsync(Claim("3000.00", project: "school", category: "events"));
        var three = await SubmitAsync(Claim("40.00", "USD", project: "clinic", category: "programme"));
        var pending = await SubmitAsync(Claim("9999.00", project: "school", category: "programme"));
        var rejected = await SubmitAsync(Claim("1.00", project: "school", category: "other"));
        foreach (var id in new[] { one, two, three })
        {
            await ApproveAsync(id, A);
        }

        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{rejected}/reject", A, new { note = "Not allowed" });
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{one}/pay", Payer, new { method = "cash" });

        var rollups = await GetAsync("/api/v1/admin/finance/expenses/rollups");
        var byProject = rollups.GetProperty("byProject").EnumerateArray().ToDictionary(l => (l.GetProperty("key").GetString()!, l.GetProperty("currency").GetString()!), l => (l.GetProperty("approved").GetString(), l.GetProperty("paid").GetString(), l.GetProperty("count").GetInt32()));
        byProject[("school", "BDT")].Should().Be(("8000.00", "5000.00", 2));
        byProject[("clinic", "USD")].Should().Be(("40.00", "0.00", 1));
        byProject.Should().HaveCount(2, "pending and rejected expenses are out");
        rollups.GetProperty("byCategory").EnumerateArray().Select(l => (l.GetProperty("key").GetString(), l.GetProperty("currency").GetString(), l.GetProperty("approved").GetString())).Should().BeEquivalentTo([("events", "BDT", "3000.00"), ("programme", "BDT", "5000.00"), ("programme", "USD", "40.00")]);

        var reconciliation = await GetAsync("/api/v1/admin/finance/expenses/rollups/reconciliation");
        reconciliation.GetProperty("reconciled").GetBoolean().Should().BeTrue(reconciliation.GetRawText());
        pending.Should().NotBeNull();

        (await GetAsync($"/api/v1/admin/finance/expenses/rollups?from={Ymd(DateTime.UtcNow.AddDays(1))}")).GetProperty("byProject").GetArrayLength().Should().Be(0);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/expenses/rollups?from=nope", Viewer)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await DbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE ledger_entries SET Debit = 4990.00 WHERE Debit = 5000.00 AND AccountId IN (SELECT Id FROM accounts WHERE Type = 'expense')");
            return 0;
        });
        var drift = await GetAsync("/api/v1/admin/finance/expenses/rollups/reconciliation");
        drift.GetProperty("reconciled").GetBoolean().Should().BeFalse();
        drift.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("difference").GetString() != "0.00").Select(l => (l.GetProperty("dimension").GetString(), l.GetProperty("key").GetString(), l.GetProperty("difference").GetString()))
            .Should().BeEquivalentTo([("project", "school", "10.00"), ("category", "programme", "10.00")]);
    }

    [Fact]
    public async Task EveryStepIsAuditedWithItsOwnActor()
    {
        var id = await SubmitAsync(Claim("20000.00"));
        await ApproveAsync(id, A);
        await ApproveAsync(id, B);
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{id}/pay", Payer, new { method = "cash" });

        var audit = await GetAsync("/api/v1/admin/finance/audit?limit=100", TestTokens.Issue([Permissions.AuditView]));

        var events = audit.GetProperty("items").EnumerateArray().Select(e => (e.GetProperty("entity").GetString(), e.GetProperty("actorId").GetString())).ToList();
        events.Should().Contain(("Expense", "usr_sub")).And.Contain(("ExpenseReceipt", "usr_sub")).And.Contain(("ExpenseApproval", "usr_a")).And.Contain(("ExpenseApproval", "usr_b"))
            .And.Contain(("ExpenseDecision", "usr_b")).And.Contain(("ExpensePayment", "usr_payer"));
    }

    [Fact]
    public async Task ExpenseRecordsAreAppendOnly()
    {
        var id = await SubmitAsync();
        await ApproveAsync(id, A);
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{id}/pay", Payer, new { method = "cash" });

        await DbAsync(async db =>
        {
            async Task Refused(Action change)
            {
                change();
                await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<InvalidOperationException>();
                db.ChangeTracker.Clear();
            }

            await Refused(() => db.Expenses.First().Amount = 1m);
            await Refused(() => db.Expenses.Remove(db.Expenses.First()));
            await Refused(() => db.ExpenseApprovals.First().ApprovedBy = "someone");
            await Refused(() => db.ExpenseDecisions.First().Decision = "rejected");
            await Refused(() => db.ExpensePayments.First().Method = "bank");
            await Refused(() => db.ExpenseReceipts.Remove(db.ExpenseReceipts.First()));
            return 0;
        });
    }

    [Fact]
    public async Task EachStepNeedsItsOwnPermissionAndTheSeededRolesSplitTheDuties()
    {
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/expenses", null, Claim())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/expenses", Viewer, Claim())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/expenses", A, Claim())).StatusCode.Should().Be(HttpStatusCode.Forbidden, "approving is not submitting");
        var id = await SubmitAsync();
        (await ApproveAsync(id, Submitter)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "submitting is not approving");
        (await ApproveAsync(id, Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/expenses/{id}/pay", Viewer, new { method = "cash" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/expenses", TestTokens.Issue([Permissions.ContentView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        KjRoles.Seed[KjRoles.FinanceOfficer].Should().Contain([Permissions.FinanceExpenseApprove, Permissions.FinanceExpensePay]).And.NotContain(Permissions.FinanceExpenseSubmit);
        KjRoles.Seed[KjRoles.EventManager].Should().Contain(Permissions.FinanceExpenseSubmit).And.NotContain([Permissions.FinanceExpenseApprove, Permissions.FinanceExpensePay]);
        KjRoles.Seed[KjRoles.Admin].Should().Contain([Permissions.FinanceExpenseSubmit, Permissions.FinanceExpenseApprove, Permissions.FinanceExpensePay]);
    }

    [Fact]
    public async Task RepeatedSubmitRequestsWithTheSameKeyCreateOneExpense()
    {
        async Task<HttpResponseMessage> Send()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/finance/expenses") { Content = JsonContent.Create(Claim()) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Submitter);
            request.Headers.Add("Idempotency-Key", "expense-key-1");
            return await _client.SendAsync(request);
        }

        (await Send()).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Send()).StatusCode.Should().Be(HttpStatusCode.Created);

        (await DbAsync(db => db.Expenses.CountAsync())).Should().Be(1);
    }
}
