using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Intake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class FinanceDataQualityTests : IDisposable
{
    private const string Route = "/api/v1/admin/finance/data-quality";
    private static readonly string[] Viewer = [Permissions.FinanceView];
    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;

    public FinanceDataQualityTests() => _client = _factory.CreateClient();

    private static HttpRequestMessage Req(HttpMethod method, string url, string[] permissions, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(permissions, subject: "usr_reviewer"));
        return request;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string[] permissions, object? body = null) => _client.SendAsync(Req(method, url, permissions, body));

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task Donation(string id, decimal amount, string status = DonationStatuses.Confirmed, string? donor = null, DateTimeOffset? at = null, bool posted = true, string currency = "BDT")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        var when = at ?? DateTimeOffset.UtcNow;
        db.Donations.Add(new DonationEntity { Id = id, Reference = $"REF-{id}", Amount = amount, Currency = currency, DonorId = donor, CreatedAt = when, ReceivedOn = DateOnly.FromDateTime(when.UtcDateTime), Events = [new DonationEventEntity { Status = status, At = when }] });
        if (posted && status == DonationStatuses.Confirmed)
        {
            var accounts = await db.Accounts.ToDictionaryAsync(a => a.Code, a => a.Id);
            db.Postings.Add(new PostingEntity
            {
                PostedOn = DateOnly.FromDateTime(when.UtcDateTime), PeriodKey = "2026-11", FiscalYear = "2026-27", Memo = id, SourceType = PostingSources.Donation, SourceId = id, CreatedAt = when,
                Entries = [new LedgerEntryEntity { AccountId = accounts["1010"], Debit = amount, Currency = currency }, new LedgerEntryEntity { AccountId = accounts["4010"], Credit = amount, Currency = currency }]
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task Donor(string id, string name, string? phone, string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IDonorProtector>();
        db.Donors.Add(new DonorEntity { Id = id, DonorKey = $"key-{id}", EmailKey = $"email-{email}", NameProtected = protector.Protect(name), EmailProtected = protector.Protect(email), PhoneProtected = phone is null ? null : protector.Protect(phone), CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ADonationThatLooksWrongIsFlaggedOnceAndNeverChanged()
    {
        for (var i = 0; i < 12; i++)
        {
            await Donation($"d{i:00}", 500);
        }

        await Donation("big", 250000);
        await Donation("twice-1", 700, donor: "dnr_x", at: DateTimeOffset.UtcNow.AddMinutes(-30));
        await Donation("twice-2", 700, donor: "dnr_x", at: DateTimeOffset.UtcNow.AddMinutes(-27));
        await Donation("unposted", 600, posted: false);

        var scan = await Json(await Send(HttpMethod.Post, $"{Route}/anomalies/scan", [Permissions.FinanceReconcile]));
        scan.GetProperty("flagged").GetInt32().Should().Be(3);
        var items = (await Json(await Send(HttpMethod.Get, $"{Route}/anomalies", Viewer))).GetProperty("items").EnumerateArray().ToList();
        items.Select(i => (i.GetProperty("rule").GetString(), i.GetProperty("donationId").GetString())).Should().BeEquivalentTo(
            [("large_amount", "big"), ("rapid_repeat", "twice-2"), ("confirmed_without_posting", "unposted")]);
        items.Single(i => i.GetProperty("rule").GetString() == "large_amount").GetProperty("amount").GetString().Should().Be("250000.00");

        (await Json(await Send(HttpMethod.Post, $"{Route}/anomalies/scan", [Permissions.FinanceReconcile]))).GetProperty("flagged").GetInt32().Should().Be(0, "each flag is raised once");

        var id = items[0].GetProperty("id").GetString()!;
        (await Send(HttpMethod.Post, $"{Route}/anomalies/{id}/review", [Permissions.FinanceDonationApprove], new { outcome = "dismissed", note = "ok" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Route}/anomalies/{id}/review", [Permissions.FinanceView], new { outcome = "dismissed", note = "Checked with the donor, a planned gift" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var reviewed = await Send(HttpMethod.Post, $"{Route}/anomalies/{id}/review", [Permissions.FinanceDonationApprove], new { outcome = "confirmed", note = "Bank confirms a duplicate transfer" });
        reviewed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(reviewed)).GetProperty("reviewedBy").GetString().Should().Be("usr_reviewer");
        (await Send(HttpMethod.Post, $"{Route}/anomalies/{id}/review", [Permissions.FinanceDonationApprove], new { outcome = "dismissed", note = "Changed my mind later" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Send(HttpMethod.Post, $"{Route}/anomalies/anm_missing/review", [Permissions.FinanceDonationApprove], new { outcome = "dismissed", note = "Checked this one" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Post, $"{Route}/anomalies/scan", [Permissions.FinanceReconcile])).EnsureSuccessStatusCode();
        (await Json(await Send(HttpMethod.Get, $"{Route}/anomalies?status=open", Viewer))).GetProperty("total").GetInt32().Should().Be(2, "a reviewed flag stays reviewed");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        (await db.Donations.CountAsync()).Should().Be(16);
        (await db.Donations.SingleAsync(d => d.Id == "big")).Amount.Should().Be(250000, "detection flags, it never changes a donation");
        (await db.DonationEvents.CountAsync()).Should().Be(16);
    }

    [Fact]
    public async Task ABurstOfFailedPaymentsIsFlaggedAsAGatewayProblemAndAQuietDayIsNot()
    {
        for (var i = 0; i < 12; i++)
        {
            await Donation($"ok{i}", 500);
        }

        (await Json(await Send(HttpMethod.Post, $"{Route}/anomalies/scan", [Permissions.FinanceReconcile]))).GetProperty("flagged").GetInt32().Should().Be(0);

        for (var i = 0; i < 14; i++)
        {
            await Donation($"bad{i}", 500, i % 2 == 0 ? DonationStatuses.Failed : DonationStatuses.Cancelled);
        }

        (await Json(await Send(HttpMethod.Post, $"{Route}/anomalies/scan", [Permissions.FinanceReconcile]))).GetProperty("flagged").GetInt32().Should().Be(1);
        var spike = (await Json(await Send(HttpMethod.Get, $"{Route}/anomalies?rule=gateway_failure_spike", Viewer))).GetProperty("items")[0];
        spike.GetProperty("donationId").ValueKind.Should().Be(JsonValueKind.Null);
        spike.GetProperty("detail").GetString().Should().Contain("14 of the last 26");
    }

    [Fact]
    public async Task OrdinaryDonationsAreNotFlaggedAndFiltersAreValidated()
    {
        for (var i = 0; i < 15; i++)
        {
            await Donation($"n{i}", 400 + i * 10);
        }

        await Donation("usd", 900, currency: "USD");
        (await Json(await Send(HttpMethod.Post, $"{Route}/anomalies/scan", [Permissions.FinanceReconcile]))).GetProperty("flagged").GetInt32().Should().Be(0);
        (await Send(HttpMethod.Get, $"{Route}/anomalies?status=weird", Viewer)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.GetAsync($"{Route}/anomalies")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Post, $"{Route}/anomalies/scan", Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DonorDuplicatesAreSurfacedWithEvidenceAndMergingIsADeliberateAuditedDecision()
    {
        await Donor("dnr_1", "Abdur Rahim", "+8801712345678", "rahim@example.org");
        await Donor("dnr_2", "A Rahim", "01712-345678", "arahim@example.org");
        await Donor("dnr_3", "Nusrat Jahan", null, "n1@example.org");
        await Donor("dnr_4", "nusrat jahan", null, "n2@example.org");
        await Donor("dnr_5", "Someone Else", "01999999999", "else@example.org");

        var list = await Json(await Send(HttpMethod.Get, $"{Route}/duplicates/donors", Viewer));
        list.GetProperty("total").GetInt32().Should().Be(2);
        var items = list.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("pairKey").GetString()!);
        items["dnr_1|dnr_2"].GetProperty("confidence").GetString().Should().Be("high");
        items["dnr_1|dnr_2"].GetProperty("evidence")[0].GetProperty("kind").GetString().Should().Be("same_phone");
        items["dnr_3|dnr_4"].GetProperty("confidence").GetString().Should().Be("low");
        list.ToString().Should().NotContain("01712").And.NotContain("rahim@example.org");

        (await Send(HttpMethod.Post, $"{Route}/duplicates/donors/decisions", [Permissions.FinanceView], new { leftId = "dnr_1", rightId = "dnr_2", decision = "confirmed_duplicate", note = "Same phone and name" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Post, $"{Route}/duplicates/donors/decisions", [Permissions.FinanceDonorManage], new { leftId = "dnr_1", rightId = "dnr_2", decision = "confirmed_duplicate", note = "x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Route}/duplicates/donors/decisions", [Permissions.FinanceDonorManage], new { leftId = "dnr_1", rightId = "dnr_missing", decision = "not_duplicate", note = "Checked records" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var decided = await Send(HttpMethod.Post, $"{Route}/duplicates/donors/decisions", [Permissions.FinanceDonorManage], new { leftId = "dnr_2", rightId = "dnr_1", decision = "confirmed_duplicate", note = "Same phone and name, checked receipts" });
        decided.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Json(decided)).GetProperty("decidedBy").GetString().Should().Be("usr_reviewer");
        (await Send(HttpMethod.Post, $"{Route}/duplicates/donors/decisions", [Permissions.FinanceDonorManage], new { leftId = "dnr_1", rightId = "dnr_2", decision = "not_duplicate", note = "Second thoughts here" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(await Send(HttpMethod.Get, $"{Route}/duplicates/donors", Viewer))).GetProperty("total").GetInt32().Should().Be(1, "a ruled pair does not come back");

        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Donors.CountAsync()).Should().Be(5, "nothing was merged or deleted");
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}

public sealed class MembershipDataQualityTests : IDisposable
{
    private const string Route = "/api/v1/admin/membership/data-quality";
    private static readonly string[] Officer = [Permissions.MembershipManage];
    private readonly MembershipApiFactory _factory = new();
    private readonly HttpClient _client;

    public MembershipDataQualityTests()
    {
        _client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IFieldProtector>();
        MemberEntity Member(string key, string first, string last, string? phone, bool full = false) => new()
        {
            Id = $"mem_{key}", UserId = $"usr_{key}", MemberCode = $"KJ-2026-{key}", ApplicationId = $"app_{key}", FirstName = first, LastName = last, JoinedAt = DateTimeOffset.UtcNow,
            PhoneProtected = phone is null ? null : protector.Protect(phone), NameBn = full ? "নাম" : null, Expertise = full ? "Teaching" : null, AddressProtected = full ? protector.Protect("Dhaka") : null,
            DateOfBirthProtected = full ? protector.Protect("2000-01-01") : null, BloodGroupProtected = full ? protector.Protect("O+") : null, PhotoKey = full ? "photo" : null
        };
        db.Members.AddRange(Member("a", "Abdur", "Rahim", "01712345678", full: true), Member("b", "A", "Rahim", "+8801712345678"), Member("c", "Nusrat", "Jahan", null), Member("d", "Nusrat", "Jahan", null));
        db.Beneficiaries.AddRange(
            new BeneficiaryEntity { Id = "ben_1", Slug = "school-1", NameEn = "School 1", NameBn = "স্কুল", District = "Dhaka", AddressProtected = protector.Protect("Road 1") },
            new BeneficiaryEntity { Id = "ben_2", Slug = "school-2", NameEn = "School 2", NameBn = "", District = "" },
            new BeneficiaryEntity { Id = "ben_old", Slug = "old", NameEn = "Old", IsArchived = true });
        db.Mous.Add(new MouEntity { Id = "mou_1", BeneficiaryId = "ben_1", SignedOn = new DateOnly(2026, 1, 1), SignatoryProtected = protector.Protect("Head") });
        db.ProgrammeEnrolments.Add(new ProgrammeEnrolmentEntity { Id = "enr_1", ProjectSlug = "p", BeneficiaryId = "ben_1", Students = 40 });
        db.SaveChanges();
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, string[] permissions, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(permissions, subject: "usr_officer"));
        return request;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task CompletenessShowsGapsByEntityAndFieldWithIdsOnly()
    {
        var response = await _client.SendAsync(Req(HttpMethod.Get, $"{Route}/completeness", Officer));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await Json(response);
        var entities = report.GetProperty("entities").EnumerateArray().ToDictionary(e => e.GetProperty("entity").GetString()!);

        var member = entities["member"];
        member.GetProperty("total").GetInt32().Should().Be(4);
        member.GetProperty("completeRecords").GetInt32().Should().Be(1);
        var fields = member.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("field").GetString()!);
        fields["phone"].GetProperty("missing").GetInt32().Should().Be(2);
        fields["photo"].GetProperty("missing").GetInt32().Should().Be(3);
        fields["photo"].GetProperty("sampleMissingIds").EnumerateArray().Select(i => i.GetString()).Should().Equal("mem_b", "mem_c", "mem_d");

        var beneficiary = entities["beneficiary"];
        beneficiary.GetProperty("total").GetInt32().Should().Be(2, "archived institutions are not chased");
        var bf = beneficiary.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("field").GetString()!);
        bf["mou"].GetProperty("sampleMissingIds")[0].GetString().Should().Be("ben_2");
        bf["district"].GetProperty("missing").GetInt32().Should().Be(1);
        bf["enrolment"].GetProperty("filled").GetInt32().Should().Be(1);
        report.ToString().Should().NotContain("Dhaka").And.NotContain("Road 1");
    }

    [Fact]
    public async Task MemberDuplicatesAreSurfacedAndRuledOnWithoutMergingAnything()
    {
        var list = await Json(await _client.SendAsync(Req(HttpMethod.Get, $"{Route}/duplicates/members", Officer)));
        list.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("pairKey").GetString(), i.GetProperty("confidence").GetString())).Should().BeEquivalentTo([("mem_a|mem_b", "high"), ("mem_c|mem_d", "low")]);

        (await _client.SendAsync(Req(HttpMethod.Post, $"{Route}/duplicates/members/decisions", Officer, new { leftId = "mem_c", rightId = "mem_d", decision = "not_duplicate", note = "Two people, different branches" }))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await _client.SendAsync(Req(HttpMethod.Post, $"{Route}/duplicates/members/decisions", Officer, new { leftId = "mem_c", rightId = "mem_d", decision = "not_duplicate", note = "Two people, different branches" }))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.SendAsync(Req(HttpMethod.Post, $"{Route}/duplicates/members/decisions", Officer, new { leftId = "mem_c", rightId = "mem_zzz", decision = "not_duplicate", note = "Two people, different branches" }))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(await _client.SendAsync(Req(HttpMethod.Get, $"{Route}/duplicates/members", Officer)))).GetProperty("total").GetInt32().Should().Be(1);

        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<MembershipDbContext>().Members.CountAsync()).Should().Be(4);
    }

    [Fact]
    public async Task ReadingAndDecidingNeedMembershipManage()
    {
        (await _client.GetAsync($"{Route}/completeness")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _client.SendAsync(Req(HttpMethod.Get, $"{Route}/completeness", [Permissions.FinanceView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.SendAsync(Req(HttpMethod.Post, $"{Route}/duplicates/members/decisions", [Permissions.MemberProfileView], new { }))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.SendAsync(Req(HttpMethod.Post, $"{Route}/duplicates/members/decisions", Officer, new { leftId = "a", rightId = "a" }))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
