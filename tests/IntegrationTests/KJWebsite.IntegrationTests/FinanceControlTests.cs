using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Ledger;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class FinanceControlTests : IDisposable
{
    private static string Token(string subject, params string[] permissions) => TestTokens.Issue([Permissions.FinanceView, .. permissions], subject: subject);
    private static readonly string Alice = Token("usr_alice", Permissions.FinanceLedgerAdjust, Permissions.FinanceDonationReverse, Permissions.FinancePeriodClose, Permissions.FinanceControlApprove);
    private static readonly string Bob = Token("usr_bob", Permissions.FinanceLedgerAdjust, Permissions.FinanceDonationReverse, Permissions.FinancePeriodClose, Permissions.FinanceControlApprove);
    private static readonly string OnlyProposer = Token("usr_prop", Permissions.FinanceLedgerAdjust, Permissions.FinanceDonationReverse, Permissions.FinancePeriodClose);
    private static readonly string Viewer = Token("usr_viewer");
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static string Ymd(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;

    public FinanceControlTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<T> ScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    private Task<T> DbAsync<T>(Func<FinanceDbContext, Task<T>> action) => ScopeAsync(s => action(s.GetRequiredService<FinanceDbContext>()));

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

    private async Task<JsonElement> JsonAsync(HttpMethod method, string path, string token, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await SendAsync(method, path, token, body);
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static object Adjustment(string amount = "100.00", string? postedOn = null, string debit = "1010", string credit = "4900") => new
    {
        postedOn,
        memo = "Controlled adjustment",
        reason = "Correcting the opening balance",
        lines = new[]
        {
            new { account = debit, debit = (string?)amount, credit = (string?)null, currency = "BDT" },
            new { account = credit, debit = (string?)null, credit = (string?)amount, currency = "BDT" }
        }
    };

    private Task<JsonElement> AskAdjustmentAsync(string token, object? body = null) =>
        JsonAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", token, body ?? Adjustment(), HttpStatusCode.Accepted);

    private Task<JsonElement> ApproveAsync(string requestId, string token) =>
        JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/controls/requests/{requestId}/approve", token, new { });

    private Task<int> PostingCountAsync() => DbAsync(db => db.Postings.CountAsync());

    // ---- four-eyes on adjustments --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAdjustmentIsOnlyARequestUntilADifferentPersonApprovesIt()
    {
        var asked = await AskAdjustmentAsync(Alice);
        asked.GetProperty("status").GetString().Should().Be("pending");
        asked.GetProperty("kind").GetString().Should().Be("adjustment");
        asked.GetProperty("requestedBy").GetString().Should().Be("usr_alice");
        asked.GetProperty("details").GetProperty("lines").GetArrayLength().Should().Be(2);
        (await PostingCountAsync()).Should().Be(0, "nothing reaches the ledger before approval");
        (await JsonAsync(HttpMethod.Get, "/api/v1/admin/finance/controls/requests?status=pending", Viewer)).GetProperty("items").GetArrayLength().Should().Be(1);

        var approved = await ApproveAsync(asked.GetProperty("id").GetString()!, Bob);
        approved.GetProperty("status").GetString().Should().Be("approved");
        approved.GetProperty("decision").GetProperty("decidedBy").GetString().Should().Be("usr_bob");
        var postingId = approved.GetProperty("postingId").GetString()!;
        var posting = await JsonAsync(HttpMethod.Get, $"/api/v1/admin/finance/ledger/postings/{postingId}", Viewer);
        posting.GetProperty("createdBy").GetString().Should().Be("usr_alice", "the posting is the requester's, approved by someone else");
        posting.GetProperty("sourceId").GetString().Should().Be("control:" + asked.GetProperty("id").GetString());
        posting.GetProperty("reason").GetString().Should().Be("Correcting the opening balance");
        (await PostingCountAsync()).Should().Be(1);
        (await JsonAsync(HttpMethod.Get, "/api/v1/admin/finance/controls/requests?status=pending", Viewer)).GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task NoOneCanApproveOrRejectTheirOwnRequestAndTheDatabaseAgrees()
    {
        var id = (await AskAdjustmentAsync(Alice)).GetProperty("id").GetString()!;
        foreach (var action in new[] { "approve", "reject" })
        {
            using var own = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/controls/requests/{id}/{action}", Alice, new { });
            (await CodeAsync(own, HttpStatusCode.Forbidden)).Should().Be("SEGREGATION_OF_DUTIES");
        }

        using var others = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/controls/requests/{id}/withdraw", Bob, new { });
        (await CodeAsync(others, HttpStatusCode.Forbidden)).Should().Be("SEGREGATION_OF_DUTIES");
        (await PostingCountAsync()).Should().Be(0);
        (await DbAsync(db => db.ControlDecisions.CountAsync())).Should().Be(0);

        // Even a caller that skips the service cannot record a self-approval: that is enforced by a PostgreSQL trigger (see FinancePostgresTests).
        var viewerApprover = Token("usr_alice", Permissions.FinanceControlApprove);
        using var again = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/controls/requests/{id}/approve", viewerApprover, new { });
        (await CodeAsync(again, HttpStatusCode.Forbidden)).Should().Be("SEGREGATION_OF_DUTIES", "holding every permission does not let you approve your own request");
    }

    [Fact]
    public async Task ARejectedOrWithdrawnRequestPostsNothingAndCannotBeDecidedAgain()
    {
        var rejected = (await AskAdjustmentAsync(Alice)).GetProperty("id").GetString()!;
        var withdrawn = (await AskAdjustmentAsync(Alice)).GetProperty("id").GetString()!;
        var done = await JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/controls/requests/{rejected}/reject", Bob, new { note = "Wrong account" });
        done.GetProperty("status").GetString().Should().Be("rejected");
        done.GetProperty("decision").GetProperty("note").GetString().Should().Be("Wrong account");
        (await JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/controls/requests/{withdrawn}/withdraw", Alice, new { })).GetProperty("status").GetString().Should().Be("withdrawn");

        foreach (var id in new[] { rejected, withdrawn })
        {
            using var late = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/controls/requests/{id}/approve", Bob, new { });
            (await CodeAsync(late, HttpStatusCode.Conflict)).Should().Be("ALREADY_DECIDED");
        }

        (await PostingCountAsync()).Should().Be(0);
        (await JsonAsync(HttpMethod.Get, "/api/v1/admin/finance/controls/requests?status=rejected", Viewer)).GetProperty("items").GetArrayLength().Should().Be(1);
        (await JsonAsync(HttpMethod.Get, "/api/v1/admin/finance/controls/requests?status=withdrawn", Viewer)).GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ManyApproversAtOnceStillPostExactlyOnce()
    {
        var id = (await AskAdjustmentAsync(Alice)).GetProperty("id").GetString()!;
        var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            using var response = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/controls/requests/{id}/approve", Token("usr_approver" + i, Permissions.FinanceControlApprove), new { });
            return response.StatusCode;
        }));
        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(1);
        statuses.Where(s => s != HttpStatusCode.OK).Should().OnlyContain(s => s == HttpStatusCode.Conflict);
        (await PostingCountAsync()).Should().Be(1);
        (await DbAsync(db => db.ControlDecisions.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task RequestsAreCheckedWhenAskedAndAgainWhenApproved()
    {
        using var future = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", Alice, Adjustment(postedOn: Ymd(Today.AddDays(5))));
        future.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var unbalanced = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", Alice, new
        {
            memo = "m",
            reason = "reason",
            lines = new[] { new { account = "1010", debit = (string?)"10.00", credit = (string?)null, currency = "BDT" }, new { account = "4900", debit = (string?)null, credit = (string?)"9.00", currency = "BDT" } }
        });
        unbalanced.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DbAsync(db => db.ControlRequests.CountAsync())).Should().Be(0, "an invalid request is never recorded");

        // Valid when asked, but the month is closed before anyone approves: the approval fails, the request stays open and can be rejected.
        var id = (await AskAdjustmentAsync(Alice)).GetProperty("id").GetString()!;
        await JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/periods/{Today:yyyy-MM}/close", Alice, new { });
        using var late = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/controls/requests/{id}/approve", Bob, new { });
        (await CodeAsync(late, HttpStatusCode.Conflict)).Should().Be("PERIOD_CLOSED");
        (await JsonAsync(HttpMethod.Get, $"/api/v1/admin/finance/controls/requests/{id}", Viewer)).GetProperty("status").GetString().Should().Be("pending");
        (await PostingCountAsync()).Should().Be(0);
        (await JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/controls/requests/{id}/reject", Bob, new { note = "Month closed" })).GetProperty("status").GetString().Should().Be("rejected");
    }

    [Fact]
    public async Task ReversingAPostingAlsoNeedsASecondPersonAndOnlyOneOpenRequestPerPosting()
    {
        var original = await PostedAsync();
        var asked = await JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/postings/{original}/reversal", Alice, new { reason = "Entered twice" }, HttpStatusCode.Accepted);
        asked.GetProperty("kind").GetString().Should().Be("posting_reversal");
        (await PostingCountAsync()).Should().Be(1, "the reversal is not appended yet");
        using var duplicate = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/postings/{original}/reversal", Bob, new { reason = "Entered twice" });
        (await CodeAsync(duplicate, HttpStatusCode.Conflict)).Should().Be("ALREADY_REQUESTED");

        var approved = await ApproveAsync(asked.GetProperty("id").GetString()!, Bob);
        var reversal = await JsonAsync(HttpMethod.Get, $"/api/v1/admin/finance/ledger/postings/{approved.GetProperty("postingId").GetString()}", Viewer);
        reversal.GetProperty("reversesPostingId").GetString().Should().Be(original.ToString(CultureInfo.InvariantCulture));
        using var again = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/postings/{original}/reversal", Alice, new { reason = "Again" });
        (await CodeAsync(again, HttpStatusCode.Conflict)).Should().Be("ALREADY_REVERSED");
        using var missing = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings/9999/reversal", Alice, new { reason = "x1y" });
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<long> PostedAsync()
    {
        var asked = await AskAdjustmentAsync(Alice);
        var approved = await ApproveAsync(asked.GetProperty("id").GetString()!, Bob);
        return long.Parse(approved.GetProperty("postingId").GetString()!, CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task OnlyTheRightPermissionsCanAskApproveOrReadAndTheFinanceOfficerRoleCanDoAllFour()
    {
        using var viewerAsks = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", Viewer, Adjustment());
        viewerAsks.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var id = (await AskAdjustmentAsync(OnlyProposer)).GetProperty("id").GetString()!;
        using var proposerApproves = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/controls/requests/{id}/approve", Token("usr_other", Permissions.FinanceLedgerAdjust), new { });
        proposerApproves.StatusCode.Should().Be(HttpStatusCode.Forbidden, "adjusting does not include approving");
        using var anonymous = await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/controls/requests", null);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var withoutView = await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/controls/requests", TestTokens.Issue([Permissions.ContentView]));
        withoutView.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        KjRoles.Seed[KjRoles.FinanceOfficer].Should().Contain([Permissions.FinanceLedgerAdjust, Permissions.FinanceDonationReverse, Permissions.FinancePeriodClose, Permissions.FinanceControlApprove]);
        KjRoles.Seed[KjRoles.Admin].Should().Contain(Permissions.FinanceControlApprove);
        using var badFilter = await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/controls/requests?status=maybe", Viewer);
        badFilter.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var unknown = await SendAsync(HttpMethod.Get, "/api/v1/admin/finance/controls/requests/ctl_nope", Viewer);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var noteTooLong = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/controls/requests/{id}/reject", Bob, new { note = new string('x', 501) });
        noteTooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- refunds ------------------------------------------------------------------------------------------------------------------------------------------

    private async Task<(string Id, string Reference)> ConfirmedDonationAsync(string amount = "500.00", string email = "donor@example.org")
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/donations", new
        {
            amount,
            currency = "BDT",
            frequency = "once",
            designation = new { type = "general" },
            donor = new { name = "Donor", email },
            anonymous = false,
            coverFees = false,
            language = "en",
            communicationConsent = false
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = json.GetProperty("id").GetString()!;
        (await ScopeAsync(s => s.GetRequiredService<DonationConfirmation>().ConfirmAsync(id, "bkash", null))).IsSuccess.Should().BeTrue();
        return (id, json.GetProperty("reference").GetString()!);
    }

    private Task<int> AddNoticeAsync(string donationId, decimal? amount, string? currency, string gateway = "stripe") =>
        DbAsync(async db =>
        {
            db.ProviderNotices.Add(new ProviderNoticeEntity { Gateway = gateway, EventKey = Guid.NewGuid().ToString("N"), Kind = NoticeKinds.Refund, DonationId = donationId, Amount = amount, Currency = currency, At = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });

    [Fact]
    public async Task ARefundReversesTheGiftAndItsPostingOnlyAfterASecondPersonApproves()
    {
        var (donationId, reference) = await ConfirmedDonationAsync();
        var asked = await JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{donationId}/reversal", Alice, new { reason = "Donor asked for a refund" }, HttpStatusCode.Accepted);
        asked.GetProperty("kind").GetString().Should().Be("refund");
        (await DbAsync(db => db.Donations.CountAsync())).Should().Be(1, "no reversing entry before approval");
        using var duplicate = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{donationId}/reversal", Bob, new { reason = "Again please" });
        (await CodeAsync(duplicate, HttpStatusCode.Conflict)).Should().Be("ALREADY_REQUESTED");
        using var own = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/controls/requests/{asked.GetProperty("id").GetString()}/approve", Alice, new { });
        own.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var approved = await ApproveAsync(asked.GetProperty("id").GetString()!, Bob);
        var reversalId = approved.GetProperty("donationId").GetString()!;
        var reversal = await DbAsync(db => db.Donations.SingleAsync(d => d.Id == reversalId));
        (reversal.Amount, reversal.Kind, reversal.ReversesDonationId, reversal.CreatedBy).Should().Be((-500.00m, "reversal", donationId, "usr_alice"));
        (await DbAsync(db => db.Postings.CountAsync(p => p.ReversesPostingId != null))).Should().Be(1);
        (await JsonAsync(HttpMethod.Get, $"/api/v1/admin/finance/donations/{donationId}", Viewer)).GetProperty("donation").GetProperty("status").GetString().Should().Be("refunded");
        using var again = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{donationId}/reversal", Alice, new { reason = "Once more" });
        (await CodeAsync(again, HttpStatusCode.Conflict)).Should().Be("ALREADY_REVERSED");
        reference.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RefundsAreReconciledAgainstTheGatewaysOwnNoticesAndStrayNoticesAreListed()
    {
        var (matched, _) = await ConfirmedDonationAsync("500.00", "a@example.org");
        var (mismatched, _) = await ConfirmedDonationAsync("300.00", "b@example.org");
        var (waiting, _) = await ConfirmedDonationAsync("200.00", "c@example.org");
        var (unapproved, _) = await ConfirmedDonationAsync("150.00", "d@example.org");
        var (neverAsked, _) = await ConfirmedDonationAsync("100.00", "e@example.org");
        foreach (var donation in new[] { matched, mismatched, waiting })
        {
            var asked = await JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{donation}/reversal", Alice, new { reason = "Refund requested" }, HttpStatusCode.Accepted);
            await ApproveAsync(asked.GetProperty("id").GetString()!, Bob);
        }

        await JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{unapproved}/reversal", Alice, new { reason = "Not yet approved" }, HttpStatusCode.Accepted);
        await AddNoticeAsync(matched, 500.00m, "BDT");
        await AddNoticeAsync(mismatched, 250.00m, "BDT");
        await AddNoticeAsync(unapproved, 150.00m, "BDT");
        await AddNoticeAsync(neverAsked, 100.00m, "BDT", "paypal");

        var report = await JsonAsync(HttpMethod.Get, "/api/v1/admin/finance/controls/refunds", Viewer);
        var rows = report.GetProperty("items").EnumerateArray().ToDictionary(r => r.GetProperty("donationId").GetString()!);
        rows[matched].GetProperty("reconciliation").GetString().Should().Be("reconciled");
        rows[matched].GetProperty("noticeGateway").GetString().Should().Be("stripe");
        rows[mismatched].GetProperty("reconciliation").GetString().Should().Be("amount_mismatch");
        rows[mismatched].GetProperty("noticeAmount").GetString().Should().Be("250.00");
        rows[waiting].GetProperty("reconciliation").GetString().Should().Be("awaiting_gateway");
        rows[unapproved].GetProperty("status").GetString().Should().Be("pending");
        rows[unapproved].GetProperty("reconciliation").GetString().Should().Be("n/a");

        var stray = report.GetProperty("unexpectedNotices").EnumerateArray().ToDictionary(n => n.GetProperty("donationId").GetString()!);
        stray.Keys.Should().BeEquivalentTo([unapproved, neverAsked]);
        stray[unapproved].GetProperty("why").GetString().Should().Contain("not approved yet");
        stray[neverAsked].GetProperty("why").GetString().Should().Contain("never approved");
        stray[neverAsked].GetProperty("gateway").GetString().Should().Be("paypal");
    }

    // ---- reopening closed periods and years ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AClosedMonthReopensOnlyWithASecondPersonAndCanBeClosedAgain()
    {
        var month = Today.AddMonths(-2);
        var period = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var day = new DateOnly(month.Year, month.Month, 10);
        using var notClosed = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/periods/{period}/reopen", Alice, new { reason = "Missed an invoice" });
        (await CodeAsync(notClosed, HttpStatusCode.Conflict)).Should().Be("NOT_CLOSED");

        await JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/periods/{period}/close", Alice, new { });
        using var blocked = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", Alice, Adjustment(postedOn: Ymd(day)));
        (await CodeAsync(blocked, HttpStatusCode.Conflict)).Should().Be("PERIOD_CLOSED");

        using var noReason = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/periods/{period}/reopen", Alice, new { reason = "x" });
        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var asked = await JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/periods/{period}/reopen", Alice, new { reason = "Missed an invoice" }, HttpStatusCode.Accepted);
        asked.GetProperty("kind").GetString().Should().Be("period_reopen");
        using var stillClosed = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", Alice, Adjustment(postedOn: Ymd(day)));
        stillClosed.StatusCode.Should().Be(HttpStatusCode.Conflict, "asking does not reopen anything");
        using var own = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/controls/requests/{asked.GetProperty("id").GetString()}/approve", Alice, new { });
        own.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DbAsync(db => db.PeriodCloses.CountAsync())).Should().Be(1);

        await ApproveAsync(asked.GetProperty("id").GetString()!, Bob);
        (await DbAsync(db => db.PeriodCloses.CountAsync())).Should().Be(0);
        var adjusted = await AskAdjustmentAsync(Alice, Adjustment(postedOn: Ymd(day)));
        (await ApproveAsync(adjusted.GetProperty("id").GetString()!, Bob)).GetProperty("status").GetString().Should().Be("approved");

        await JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/periods/{period}/close", Bob, new { });
        var events = await DbAsync(db => db.CloseEvents.OrderBy(e => e.Id).ToListAsync());
        events.Select(e => e.Kind).Should().Equal("closed", "reopened", "closed");
        events[1].Should().Match<CloseEventEntity>(e => e.By == "usr_alice" && e.ApprovedBy == "usr_bob" && e.Reason == "Missed an invoice" && e.Scope == "period" && e.Key == period);
        using var closedAgain = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", Alice, Adjustment(postedOn: Ymd(day)));
        closedAgain.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AClosedFiscalYearReopensTheSameWay()
    {
        var start = FiscalCalendar.TryRange(FiscalCalendar.FiscalYear(Today, 7), 7, out var s, out _) ? s : default;
        var label = FiscalCalendar.FiscalYear(start.AddDays(-1), 7);
        var day = start.AddDays(-30);
        await JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/fiscal-years/{label}/close", Alice, new { });
        using var blocked = await SendAsync(HttpMethod.Post, "/api/v1/admin/finance/ledger/postings", Alice, Adjustment(postedOn: Ymd(day)));
        blocked.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var asked = await JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/fiscal-years/{label}/reopen", Alice, new { reason = "Auditor adjustment" }, HttpStatusCode.Accepted);
        asked.GetProperty("kind").GetString().Should().Be("fiscal_year_reopen");
        await ApproveAsync(asked.GetProperty("id").GetString()!, Bob);
        var adjusted = await AskAdjustmentAsync(Alice, Adjustment(postedOn: Ymd(day)));
        await ApproveAsync(adjusted.GetProperty("id").GetString()!, Bob);
        (await DbAsync(db => db.CloseEvents.Where(e => e.Scope == "year").OrderBy(e => e.Id).Select(e => e.Kind).ToListAsync())).Should().Equal("closed", "reopened");
        using var notClosed = await SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/fiscal-years/{label}/reopen", Alice, new { reason = "Already open" });
        (await CodeAsync(notClosed, HttpStatusCode.Conflict)).Should().Be("NOT_CLOSED");
    }

    // ---- guards and coverage -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoCodePathCanRemoveACloseWithoutAnApprovedReopenOrEditARequest()
    {
        var month = Today.AddMonths(-3).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        await JsonAsync(HttpMethod.Post, $"/api/v1/admin/finance/ledger/periods/{month}/close", Alice, new { });
        await DbAsync(async db =>
        {
            db.PeriodCloses.Remove(await db.PeriodCloses.FirstAsync());
            await db.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*only an approved reopen*");
            return 0;
        });
        await DbAsync(async db =>
        {
            (await db.PeriodCloses.FirstAsync()).ClosedBy = "someone";
            await db.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
            return 0;
        });
        await DbAsync(async db =>
        {
            db.CloseEvents.Add(new CloseEventEntity { Scope = "period", Key = month, Kind = "reopened", By = "usr_x", ApprovedBy = "usr_x", At = DateTimeOffset.UtcNow });
            db.PeriodCloses.Remove(await db.PeriodCloses.FirstAsync());
            await db.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*only an approved reopen*", "a reopen approved by the requester themselves does not count");
            return 0;
        });

        var id = (await AskAdjustmentAsync(Alice)).GetProperty("id").GetString()!;
        await DbAsync(async db =>
        {
            (await db.ControlRequests.FirstAsync(r => r.Id == id)).Reason = "edited";
            await db.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
            return 0;
        });
        await ApproveAsync(id, Bob);
        await DbAsync(async db =>
        {
            db.ControlDecisions.Remove(await db.ControlDecisions.FirstAsync());
            await db.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
            return 0;
        });
    }

    [Fact]
    public async Task EveryFinancialRecordTypeIsAuditedAndAChangeShowsBeforeAndAfter()
    {
        var unaudited = await ScopeAsync(s =>
        {
            var audit = s.GetRequiredService<AuditOptions>();
            var model = s.GetRequiredService<FinanceDbContext>().Model;
            return Task.FromResult(model.GetEntityTypes().Select(t => t.ClrType).Where(t => t != typeof(AuditEvent) && !audit.IsAudited(t)).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList());
        });
        // Not records of money or approvals: the outbox of donor messages (written and marked sent by the mailer), the gateway replay guard and the log of refused webhook calls (both written by the system for anonymous callers).
        unaudited.Should().BeSubsetOf(["FinanceNotificationEntity", "PaymentEventEntity", "WebhookRejectionEntity"], "every financial record must be audited; add the type to Program.cs or justify it here");

        var created = await JsonAsync(HttpMethod.Post, "/api/v1/admin/finance/budgets", Token("usr_budget", Permissions.FinanceBudgetManage), new { category = "programme", planned = "1000.00", currency = "BDT" }, HttpStatusCode.Created);
        await JsonAsync(HttpMethod.Put, $"/api/v1/admin/finance/budgets/{created.GetProperty("id").GetString()}", Token("usr_budget", Permissions.FinanceBudgetManage), new { planned = "1500.00" });
        var audit2 = await JsonAsync(HttpMethod.Get, "/api/v1/admin/finance/audit?limit=100", TestTokens.Issue([Permissions.AuditView]));
        var change = audit2.GetProperty("items").EnumerateArray().First(e => e.GetProperty("entity").GetString() == "Budget" && e.GetProperty("action").GetString() == "update");
        change.GetProperty("actorId").GetString().Should().Be("usr_budget");
        change.GetProperty("before").GetProperty("plannedAmount").GetDecimal().Should().Be(1000.00m);
        change.GetProperty("after").GetProperty("plannedAmount").GetDecimal().Should().Be(1500.00m);
    }
}
