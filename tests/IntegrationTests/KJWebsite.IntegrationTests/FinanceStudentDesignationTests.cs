using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using FinanceService.Offline;
using FinanceService.Receipts;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace KJWebsite.IntegrationTests;

public sealed class FinanceStudentDesignationTests : IDisposable
{
    private const string Route = "/api/v1/admin/finance/donations";
    private const string Student = "stu_0123456789abcdef0123456789abcdef";
    private static readonly string Maker = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationRecord, Permissions.FinanceDonationApprove, Permissions.FinanceDonorManage], subject: "usr_maker");
    private static readonly string Checker = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationApprove], subject: "usr_checker");
    private readonly FinanceApiFactory _factory = new();
    private readonly FakeSponsorshipLinks _links = new();
    private readonly HttpClient _client;

    public FinanceStudentDesignationTests()
    {
        _factory.ConfigureTestServices = services => { services.RemoveAll<ISponsorshipLinks>(); services.AddSingleton<ISponsorshipLinks>(_links); };
        _client = _factory.CreateClient();
    }

    public void Dispose() { _client.Dispose(); _factory.Dispose(); }

    private async Task<T> Scope<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }
    private Task<T> Db<T>(Func<FinanceDbContext, Task<T>> action) => Scope(s => action(s.GetRequiredService<FinanceDbContext>()));
    private async Task<string> Donor(string email = "original@example.org", string? subject = null)
    {
        return await Scope(async s =>
        {
            var db = s.GetRequiredService<FinanceDbContext>();
            var protector = s.GetRequiredService<IDonorProtector>();
            var key = s.GetRequiredService<DonorKeyHasher>().ForEmail(email);
            var donor = new DonorEntity { Id = "dnr_" + Guid.NewGuid().ToString("N"), DonorKey = key, EmailKey = key, NameProtected = protector.Protect("Donor"), EmailProtected = protector.Protect(email), AccountSubject = subject, AccountStatus = subject is null ? "none" : "active", CreatedAt = DateTimeOffset.UtcNow };
            db.Donors.Add(donor);
            await db.SaveChangesAsync();
            return donor.Id;
        });
    }
    private async Task Edit(string id, Action<DonorEntity, IServiceProvider> edit) => await Scope(async s =>
    {
        var db = s.GetRequiredService<FinanceDbContext>();
        edit(await db.Donors.SingleAsync(d => d.Id == id), s);
        return await db.SaveChangesAsync();
    });
    private static OfflineDonationRequest Gift(string? donorId, string? studentId = Student, string designation = "student", string? email = null, bool anonymous = false) =>
        new("Donor", email, null, "500.00", "BDT", "cash", null, designation, anonymous, null, null, null, null, DonorId: donorId, DesignationId: studentId);
    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, string? token = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Maker);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (method == HttpMethod.Post) request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await _client.SendAsync(request);
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private async Task<string> Record(OfflineDonationRequest gift)
    {
        using var response = await Send(HttpMethod.Post, Route, gift);
        return (await Json(response, HttpStatusCode.Created)).GetProperty("id").GetString()!;
    }
    private async Task Approve(string id)
    {
        using var response = await Send(HttpMethod.Post, $"{Route}/{id}/approve", new { }, Checker);
        await Json(response);
    }

    [Theory]
    [InlineData("missing", "donorId")]
    [InlineData("unknown", "donorId")]
    [InlineData("anonymous", "anonymous")]
    [InlineData("malformed", "designationId")]
    [InlineData("extra-id", "designationId")]
    [InlineData("string-form", "designation")]
    [InlineData("inactive", "designationId")]
    public async Task InvalidStudentGiftsSaveNothing(string scenario, string field)
    {
        var donor = await Donor();
        var gift = Gift(donor);
        gift = scenario switch
        {
            "missing" => gift with { DonorId = null },
            "unknown" => gift with { DonorId = "dnr_unknown" },
            "anonymous" => gift with { Anonymous = true },
            "malformed" => gift with { DesignationId = "stu_bad" },
            "extra-id" => gift with { Designation = "general" },
            "string-form" => gift with { Designation = "student:" + Student },
            _ => gift
        };
        if (scenario == "inactive") _links.Results[(donor, Student, DateOnly.FromDateTime(DateTime.UtcNow))] = new(false, null);
        var before = await Db(async db => (await db.Donations.CountAsync(), await db.OfflineRecords.CountAsync()));
        using var response = await Send(HttpMethod.Post, Route, gift);
        var json = await Json(response, HttpStatusCode.BadRequest);
        json.GetProperty("error").GetProperty("code").GetString().Should().Be("VALIDATION_ERROR");
        (await response.Content.ReadAsStringAsync()).Should().Contain(field);
        (await Db(async db => (await db.Donations.CountAsync(), await db.OfflineRecords.CountAsync()))).Should().Be(before);
    }

    [Fact]
    public async Task UnavailableSponsorshipSavesNothing()
    {
        var donor = await Donor();
        _links.Down = true;
        using var response = await Send(HttpMethod.Post, Route, Gift(donor));
        (await Json(response, HttpStatusCode.ServiceUnavailable)).GetProperty("error").GetProperty("code").GetString().Should().Be("SPONSORSHIP_UNAVAILABLE");
        (await Db(db => db.Donations.CountAsync())).Should().Be(0);
        (await Db(db => db.OfflineRecords.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task ActiveGiftCarriesIdsAndStaffBearerAndEnforcesMakerChecker()
    {
        var donor = await Donor();
        using var response = await Send(HttpMethod.Post, Route, Gift(donor) with { DonorName = null });
        var json = await Json(response, HttpStatusCode.Created);
        json.GetProperty("donorId").GetString().Should().Be(donor);
        json.GetProperty("studentId").GetString().Should().Be(Student);
        json.GetProperty("studentName").GetString().Should().Be("Student name");
        _links.Checks.Single().Should().Be((donor, Student, DateOnly.FromDateTime(DateTime.UtcNow), Maker));
        var id = json.GetProperty("id").GetString()!;
        var gift = await Db(db => db.Donations.SingleAsync());
        (gift.DonorId, gift.DesignationType, gift.DesignationId).Should().Be((donor, "student", Student));
        using var refused = await Send(HttpMethod.Post, $"{Route}/{id}/approve", new { });
        (await Json(refused, HttpStatusCode.Forbidden)).GetProperty("error").GetProperty("code").GetString().Should().Be("SEPARATION_OF_DUTIES");
        await Approve(id);
        (await Db(db => db.Receipts.SingleAsync())).DonorKey.Should().Be((await Db(db => db.Donors.SingleAsync())).DonorKey);
        (await Db(db => db.Donors.CountAsync())).Should().Be(1);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("conflict")]
    [InlineData("account")]
    [InlineData("apply")]
    public async Task ApprovalAlwaysAttachesByIdAndOnlyAppliesAnAllowedEmail(string scenario)
    {
        var donor = await Donor();
        var original = await Db(db => db.Donors.SingleAsync());
        var id = await Record(Gift(donor, email: "new@example.org"));
        (await Db(db => db.Donors.SingleAsync())).EmailKey.Should().Be(original.EmailKey);
        if (scenario == "conflict") await Donor("new@example.org");
        if (scenario == "account") await Edit(donor, (d, _) => d.AccountSubject = "usr_donor");
        if (scenario == "changed") await Edit(donor, (d, s) => { d.EmailKey = s.GetRequiredService<DonorKeyHasher>().ForEmail("edited@example.org"); d.EmailProtected = s.GetRequiredService<IDonorProtector>().Protect("edited@example.org"); });
        await Approve(id);
        var updated = await Db(db => db.Donors.SingleAsync(d => d.Id == donor));
        updated.DonorKey.Should().Be(original.DonorKey);
        updated.Version.Should().Be(original.Version + 1);
        var expectedEmail = scenario is "conflict" or "account" ? "original@example.org" : "new@example.org";
        (await Scope(s => Task.FromResult(s.GetRequiredService<IDonorProtector>().Unprotect(updated.EmailProtected)))).Should().Be(expectedEmail);
        updated.EmailKey.Should().Be(await Scope(s => Task.FromResult(s.GetRequiredService<DonorKeyHasher>().ForEmail(expectedEmail))));
        (await Db(db => db.Receipts.SingleAsync())).DonorKey.Should().Be(original.DonorKey);
        (await Db(db => db.Donors.CountAsync())).Should().Be(scenario == "conflict" ? 2 : 1);
    }

    [Fact]
    public async Task EmailChangedBetweenRecordAndApprovalDoesNotCreateAnotherDonor()
    {
        var donor = await Donor();
        var id = await Record(Gift(donor));
        await Edit(donor, (d, s) => { d.EmailKey = s.GetRequiredService<DonorKeyHasher>().ForEmail("edited@example.org"); d.EmailProtected = s.GetRequiredService<IDonorProtector>().Protect("edited@example.org"); });
        await Approve(id);
        (await Db(db => db.Donors.CountAsync())).Should().Be(1);
        (await Db(db => db.Receipts.SingleAsync())).DonorKey.Should().Be((await Db(db => db.Donors.SingleAsync())).DonorKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmailChangesAreRefusedForConflictsAndSignIns(bool signIn)
    {
        var donor = await Donor(subject: signIn ? "usr_donor" : null);
        if (!signIn) await Donor("taken@example.org");
        using var response = await Send(HttpMethod.Post, Route, Gift(donor, email: "taken@example.org"));
        await Json(response, HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("email");
        (await Db(db => db.Donations.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task ListsAndTotalsFilterStudentsAndNamesAreOptionalAndBatched()
    {
        var donor = await Donor();
        var first = await Record(Gift(donor));
        await Record(Gift(donor));
        await Record(Gift(donor, null, "general"));
        using var response = await Send(HttpMethod.Get, Route + "?studentId=" + Student);
        var page = await Json(response);
        page.GetProperty("total").GetInt32().Should().Be(2);
        foreach (var row in page.GetProperty("items").EnumerateArray())
        {
            row.GetProperty("donorId").GetString().Should().Be(donor);
            row.GetProperty("studentId").GetString().Should().Be(Student);
            row.GetProperty("studentName").GetString().Should().Be("Student name");
        }
        _links.NameCalls.Should().ContainSingle();
        _links.NameCalls[0].Ids.Should().Equal(Student);
        _links.NameCalls[0].Bearer.Should().Be(Maker);
        using var totalsResponse = await Send(HttpMethod.Get, Route + "/totals?studentId=" + Student);
        (await Json(totalsResponse)).GetProperty("items")[0].GetProperty("count").GetInt32().Should().Be(2);
        _links.Down = true;
        using var detailResponse = await Send(HttpMethod.Get, Route + "/" + first);
        (await Json(detailResponse)).GetProperty("donation").GetProperty("studentName").ValueKind.Should().Be(JsonValueKind.Null);
        using var downResponse = await Send(HttpMethod.Get, Route + "?designation=student:" + Student);
        var down = await Json(downResponse);
        down.GetProperty("total").GetInt32().Should().Be(2);
        down.GetProperty("items")[0].GetProperty("studentName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task DonorHistoriesUseStableKeyAndHideStudentIdentity()
    {
        var donor = await Donor(subject: "usr_donor");
        var id = await Record(Gift(donor));
        await Approve(id);
        var token = TestTokens.Issue([], subject: "usr_donor", extraClaims: [new Claim(KjClaims.Role, KjRoles.Donor)]);
        using var selfResponse = await Send(HttpMethod.Get, "/api/v1/donors/me/donations", token: token);
        var self = await Json(selfResponse);
        self.GetProperty("items")[0].GetProperty("designation").GetString().Should().Be("Student sponsorship");
        self.GetProperty("items")[0].GetProperty("designationId").ValueKind.Should().Be(JsonValueKind.Null);
        using var crmResponse = await Send(HttpMethod.Get, $"/api/v1/admin/finance/donors/{donor}/donations");
        (await Json(crmResponse)).GetProperty("items").GetArrayLength().Should().Be(1);
        ReceiptPdf.DesignationLabel("student", Student).Should().Contain("Student sponsorship").And.Contain("শিক্ষার্থী পৃষ্ঠপোষকতা").And.NotContain(Student);
    }

    [Fact]
    public async Task ReversalCopiesDonorIdAndKeepsStudentFilterAndRefundedStatus()
    {
        var donor = await Donor();
        var id = await Record(Gift(donor));
        await Approve(id);
        await Scope(async services =>
        {
            var result = await services.GetRequiredService<DonationReversal>().StageAsync(id, "Correction", "usr_checker", default);
            result.IsSuccess.Should().BeTrue();
            result.Value!.DonorId.Should().Be(donor);
            return await services.GetRequiredService<FinanceDbContext>().SaveChangesAsync();
        });
        using var response = await Send(HttpMethod.Get, Route + "?studentId=" + Student);
        var page = await Json(response);
        page.GetProperty("total").GetInt32().Should().Be(2);
        page.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id).GetProperty("status").GetString().Should().Be("refunded");
    }

    [Theory]
    [InlineData("en", "Student sponsorship")]
    [InlineData("bn", "শিক্ষার্থী পৃষ্ঠপোষকতা")]
    public async Task DonorLabelFollowsLanguage(string language, string label)
    {
        var donor = await Donor(subject: "usr_donor");
        var id = await Record(Gift(donor));
        await Approve(id);
        await Edit(donor, (d, _) => d.Language = language);
        var token = TestTokens.Issue([], subject: "usr_donor", extraClaims: [new Claim(KjClaims.Role, KjRoles.Donor)]);
        using var response = await Send(HttpMethod.Get, "/api/v1/donors/me/donations", token: token);
        var row = (await Json(response)).GetProperty("items")[0];
        row.GetProperty("designation").GetString().Should().Be(label);
        row.GetProperty("designationId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task PublicFormStillRefusesStudentDesignation()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/donations", new { amount = "500.00", currency = "BDT", frequency = "once", designation = new { type = "student", id = Student }, donor = new { name = "Donor", email = "donor@example.org" }, language = "en" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReceiptAndAnnualStatementRenderStudentLabel()
    {
        var donor = await Donor();
        var id = await Record(Gift(donor));
        await Approve(id);
        var receipt = await Db(db => db.Receipts.SingleAsync());
        var document = await Scope(s => s.GetRequiredService<ReceiptService>().DocumentForAsync(receipt, default));
        ReceiptPdf.DesignationLabel(document!.DesignationType, document.DesignationId).Should().Contain("Student sponsorship").And.NotContain(Student);
        var bytes = ReceiptPdf.Render(document);
        System.Text.Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
        System.Text.Encoding.Latin1.GetString(bytes).Should().Contain("NotoSansBengali").And.NotContain(Student);
        using var statement = await Send(HttpMethod.Get, $"/api/v1/admin/finance/statements/{receipt.DonorKey}/pdf?fiscalYear={receipt.Series}");
        statement.StatusCode.Should().Be(HttpStatusCode.OK);
        var statementBytes = await statement.Content.ReadAsByteArrayAsync();
        System.Text.Encoding.ASCII.GetString(statementBytes, 0, 5).Should().Be("%PDF-");
        System.Text.Encoding.Latin1.GetString(statementBytes).Should().Contain("NotoSansBengali").And.NotContain(Student);
    }

    [Fact]
    public async Task GeneralGiftWithDonorIdAttachesWithoutCallingSponsorship()
    {
        var donor = await Donor();
        var id = await Record(Gift(donor, null, "general"));
        _links.Checks.Should().BeEmpty();
        await Approve(id);
        (await Db(db => db.Receipts.SingleAsync())).DonorKey.Should().Be((await Db(db => db.Donors.SingleAsync())).DonorKey);
    }

    [Fact]
    public async Task ModelHasNullableIndexedDonorId()
    {
        await Db(db =>
        {
            var entity = db.Model.FindEntityType(typeof(DonationEntity))!;
            var property = entity.FindProperty(nameof(DonationEntity.DonorId))!;
            property.IsNullable.Should().BeTrue();
            property.GetMaxLength().Should().Be(40);
            entity.GetIndexes().Should().Contain(i => i.Properties.Contains(property));
            return Task.FromResult(0);
        });
    }

    [RequiresPostgresFact]
    public async Task DonationDonorIdMigrationAppliesWithoutBackfill()
    {
        var database = await TestPostgres.CreateUniqueDatabaseAsync("finance_student");
        try
        {
            var options = new DbContextOptionsBuilder<FinanceDbContext>().UseNpgsql(TestPostgres.ConnectionStringFor(database), n => n.MigrationsHistoryTable("__EFMigrationsHistory", "finance")).Options;
            await using var db = new FinanceDbContext(options);
            var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20261109145119_DonorProfile");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO finance.donations ("Id","Reference","Kind","Amount","FeeAmount","Currency","DesignationType","IsAnonymous","DonorNameProtected","DonorEmailProtected","Language","CreatedAt","ReceivedOn")
                VALUES ('don_old','OLD','one_time',500,0,'BDT','general',false,'name','email','en',now(),current_date)
                """);
            await migrator.MigrateAsync();
            (await db.Donations.SingleAsync()).DonorId.Should().BeNull();
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        }
        finally { await TestPostgres.DropDatabaseAsync(database); }
    }
}

internal sealed class FakeSponsorshipLinks : ISponsorshipLinks
{
    public bool Down { get; set; }
    public Dictionary<(string Donor, string Student, DateOnly Date), DesignationCheck> Results { get; } = [];
    public Dictionary<string, string> Names { get; } = [];
    public List<(string Donor, string Student, DateOnly Date, string Bearer)> Checks { get; } = [];
    public List<(string[] Ids, string Bearer)> NameCalls { get; } = [];
    public Task<SponsorshipResult<DesignationCheck>> CheckAsync(string donorId, string studentId, DateOnly receivedOn, string bearer, CancellationToken ct)
    {
        Checks.Add((donorId, studentId, receivedOn, bearer));
        return Task.FromResult(Down ? new SponsorshipResult<DesignationCheck>(SponsorshipOutcome.Unavailable) : new(SponsorshipOutcome.Ok, Results.GetValueOrDefault((donorId, studentId, receivedOn), new(true, "Student name"))));
    }
    public Task<SponsorshipResult<IReadOnlyDictionary<string, string>>> NamesAsync(IReadOnlyCollection<string> studentIds, string bearer, CancellationToken ct)
    {
        NameCalls.Add((studentIds.ToArray(), bearer));
        return Task.FromResult(Down ? new SponsorshipResult<IReadOnlyDictionary<string, string>>(SponsorshipOutcome.Unavailable) : new(SponsorshipOutcome.Ok, studentIds.ToDictionary(id => id, id => Names.GetValueOrDefault(id, "Student name"))));
    }
}

public sealed class FinanceSponsorshipLinksTests
{
    [Theory]
    [InlineData(401, "{}")]
    [InlineData(403, "{}")]
    [InlineData(404, "{}")]
    [InlineData(500, "{}")]
    [InlineData(201, "{}")]
    [InlineData(200, "not json")]
    [InlineData(200, "{}")]
    public async Task NonCleanResponsesAreUnavailable(int status, string body)
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
        using var factory = new ClientFactory(handler);
        var links = new SponsorshipLinks(factory, NullLogger<SponsorshipLinks>.Instance);
        (await links.CheckAsync("dnr_id", "stu_id", new DateOnly(2026, 11, 1), "staff", default)).Outcome.Should().Be(SponsorshipOutcome.Unavailable);
        (await links.NamesAsync(["stu_id"], "staff", default)).Outcome.Should().Be(SponsorshipOutcome.Unavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportAndTimeoutErrorsAreUnavailable(bool timeout)
    {
        using var handler = new StubHandler(_ => throw (timeout ? new TaskCanceledException() : (Exception)new HttpRequestException()));
        using var factory = new ClientFactory(handler);
        var links = new SponsorshipLinks(factory, NullLogger<SponsorshipLinks>.Instance);
        (await links.CheckAsync("dnr_id", "stu_id", new DateOnly(2026, 11, 1), "staff", default)).Outcome.Should().Be(SponsorshipOutcome.Unavailable);
    }

    [Fact]
    public async Task NamesDeduplicateBatchAndForwardBearerAndSkipEmptyInput()
    {
        var batches = new List<string[]>();
        using var handler = new StubHandler(request =>
        {
            request.Headers.Authorization!.ToString().Should().Be("Bearer staff");
            var ids = Uri.UnescapeDataString(request.RequestUri!.Query[5..]).Split(',');
            batches.Add(ids);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { items = ids.Select(id => new { id, preferredName = "Name" }) }) };
        });
        using var factory = new ClientFactory(handler);
        var links = new SponsorshipLinks(factory, NullLogger<SponsorshipLinks>.Instance);
        (await links.NamesAsync([], "staff", default)).Value.Should().BeEmpty();
        batches.Should().BeEmpty();
        var ids = Enumerable.Range(0, 205).Select(i => "stu_" + i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var result = await links.NamesAsync([.. ids, ids[0]], "staff", default);
        result.Outcome.Should().Be(SponsorshipOutcome.Ok);
        result.Value.Should().HaveCount(205);
        batches.Select(b => b.Length).Should().Equal(100, 100, 5);
    }

    [Fact]
    public async Task CheckUsesIdsDateAndBearer()
    {
        using var handler = new StubHandler(request =>
        {
            request.RequestUri!.PathAndQuery.Should().Be("/api/v1/admin/sponsorship/donors/dnr_123/students/stu_123/designation?date=2026-11-01");
            request.Headers.Authorization!.ToString().Should().Be("Bearer staff");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { active = true, studentName = "Name" }) };
        });
        using var factory = new ClientFactory(handler);
        var links = new SponsorshipLinks(factory, NullLogger<SponsorshipLinks>.Instance);
        (await links.CheckAsync("dnr_123", "stu_123", new DateOnly(2026, 11, 1), "staff", default)).Value.Should().Be(new DesignationCheck(true, "Name"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }
    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(handler, disposeHandler: false) { BaseAddress = new Uri("http://sponsorship.test/") };
        public HttpClient CreateClient(string name) { name.Should().Be("sponsorship"); return _client; }
        public void Dispose() => _client.Dispose();
    }
}
