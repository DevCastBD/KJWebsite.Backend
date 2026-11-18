using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using FinanceService.Ledger;
using FinanceService.Receipts;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class FinanceGiftAttributionTests : IDisposable
{
    private const string Donors = "/api/v1/admin/finance/donors";
    private static readonly string Manager = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonorManage]);
    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;

    public FinanceGiftAttributionTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignedInDonorWinsAndTypedContactsDoNotRefreshTheProfile(bool emailBelongsToAnotherDonor)
    {
        var a = await Donor("owner@example.org", "usr_owner");
        var b = emailBelongsToAnotherDonor ? await Donor("typed@example.org", "usr_other") : null;
        var before = await Db(db => db.Donors.AsNoTracking().SingleAsync(d => d.Id == a));
        var id = await Gift("typed@example.org", DonorToken("usr_owner"));
        var gift = await Db(db => db.Donations.SingleAsync(d => d.Id == id));
        gift.DonorId.Should().Be(a);
        using (var scope = _factory.Services.CreateScope())
        {
            var protector = scope.ServiceProvider.GetRequiredService<IDonorProtector>();
            protector.Unprotect(gift.DonorNameProtected).Should().Be("Typed Name");
            protector.Unprotect(gift.DonorEmailProtected).Should().Be("typed@example.org");
            protector.Unprotect(gift.DonorPhoneProtected!).Should().Be("+8801710000000");
        }
        await Confirm(id);
        await AssertOwner(id, a);
        var after = await Db(db => db.Donors.AsNoTracking().SingleAsync(d => d.Id == a));
        after.Should().BeEquivalentTo(before);
        (await Db(db => db.Donors.CountAsync())).Should().Be(b is null ? 1 : 2);
        (await History("usr_owner")).Should().Equal(id);
        using var staff = await Send(HttpMethod.Get, $"{Donors}/{a}", Manager);
        (await Json(staff)).GetProperty("gifts").GetArrayLength().Should().Be(1);
        if (b is not null) (await History("usr_other")).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmailPinSurvivesEmailChangeBeforeConfirmationAndReassignment(bool reassignBeforeConfirmation)
    {
        var a = await Donor("old@example.org");
        var id = await Gift("OLD@example.org");
        (await Db(db => db.Donations.SingleAsync(d => d.Id == id))).DonorId.Should().Be(a);
        using var patch = await Send(HttpMethod.Patch, $"{Donors}/{a}", Manager, new { email = "new@example.org" }, "\"1\"");
        patch.StatusCode.Should().Be(HttpStatusCode.OK, await patch.Content.ReadAsStringAsync());
        string? b = null;
        if (reassignBeforeConfirmation) b = await Donor("old@example.org", "usr_reassigned");
        await Confirm(id);
        await AssertOwner(id, a);
        (await Db(db => db.Donors.CountAsync())).Should().Be(reassignBeforeConfirmation ? 2 : 1);
        b ??= await Donor("old@example.org", "usr_reassigned");
        await AssertOwner(id, a);
        (await History("usr_reassigned")).Should().BeEmpty();
        using var original = await Send(HttpMethod.Get, $"{Donors}/{a}", Manager);
        (await Json(original)).GetProperty("gifts").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task FirstTimeEmailRemainsNullAndCreatesDonorAtConfirmation()
    {
        var id = await Gift("first@example.org");
        (await Db(db => db.Donations.SingleAsync(d => d.Id == id))).DonorId.Should().BeNull();
        (await Db(db => db.Donors.CountAsync())).Should().Be(0);
        await Confirm(id);
        var donor = await Db(db => db.Donors.SingleAsync());
        await AssertOwner(id, donor.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousGiftKeepsNullIdAndKnownEmailReceiptKeyWithOrWithoutToken(bool token)
    {
        var owner = await Donor("known@example.org", "usr_known");
        await Donor("token@example.org", "usr_token");
        var id = await Gift("known@example.org", token ? DonorToken("usr_token") : null, anonymous: true, frequency: "monthly");
        var gift = await Db(db => db.Donations.SingleAsync(d => d.Id == id));
        gift.DonorId.Should().BeNull();
        (await Db(db => db.RecurringPlans.SingleAsync(p => p.Id == gift.RecurringPlanId))).DonorId.Should().BeNull();
        await Confirm(id);
        await AssertOwner(id, owner);
        (await Db(db => db.Donors.CountAsync())).Should().Be(2);
        (await History("usr_known")).Should().BeEmpty();
        (await Db(db => db.Receipts.SingleAsync(r => r.DonationId == id))).IsAnonymous.Should().BeTrue();
    }

    [Theory]
    [InlineData("expired", true)]
    [InlineData("expired", false)]
    [InlineData("staff", true)]
    [InlineData("staff", false)]
    [InlineData("inactive", true)]
    [InlineData("inactive", false)]
    [InlineData("disabled", true)]
    [InlineData("disabled", false)]
    [InlineData("unknown", true)]
    [InlineData("unknown", false)]
    [InlineData("password", true)]
    [InlineData("password", false)]
    [InlineData("missing", true)]
    [InlineData("missing", false)]
    public async Task IneligibleTokenNeverRefusesAndFallsBackToEmail(string kind, bool knownEmail)
    {
        var tokenOwner = await Donor("token@example.org", "usr_token");
        var emailOwner = await Donor("email@example.org");
        await Edit(tokenOwner, d =>
        {
            if (kind == "inactive") d.Status = DonorStatuses.Inactive;
            if (kind == "disabled") d.AccountStatus = AccountStatuses.Disabled;
        });
        var token = kind switch
        {
            "expired" => TestTokens.Expired([], "usr_token", [new Claim(KjClaims.Role, KjRoles.Donor)]),
            "staff" => TestTokens.Issue([Permissions.FinanceView], subject: "usr_token", extraClaims: [new Claim(KjClaims.Role, KjRoles.Admin)]),
            "unknown" => DonorToken("usr_unknown"),
            "password" => DonorToken("usr_token", true),
            "missing" => null,
            _ => DonorToken("usr_token")
        };
        var id = await Gift(knownEmail ? "email@example.org" : "unowned@example.org", token);
        (await Db(db => db.Donations.SingleAsync(d => d.Id == id))).DonorId.Should().Be(knownEmail ? emailOwner : null);
    }

    [Fact]
    public async Task RevokedSessionFallsBackToEmailWithoutRefusing()
    {
        using var factory = new FinanceApiFactory
        {
            SessionCheckEnabled = true,
            SessionCheckHandler = new RevokedSessionHandler()
        };
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
            var keys = scope.ServiceProvider.GetRequiredService<DonorKeyHasher>();
            db.Donors.Add(new DonorEntity { Id = "dnr_email", DonorKey = keys.ForEmail("email@example.org"), EmailKey = keys.ForEmail("email@example.org"), NameProtected = "name", EmailProtected = "email" });
            db.Donors.Add(new DonorEntity { Id = "dnr_token", DonorKey = keys.ForEmail("token@example.org"), EmailKey = keys.ForEmail("token@example.org"), NameProtected = "name", EmailProtected = "email", AccountSubject = "usr_token", AccountStatus = AccountStatuses.Active });
            await db.SaveChangesAsync();
        }
        using var response = await SendGift(client, "email@example.org", DonorToken("usr_token"));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var id = (await Json(response)).GetProperty("id").GetString();
        using var read = factory.Services.CreateScope();
        (await read.ServiceProvider.GetRequiredService<FinanceDbContext>().Donations.SingleAsync(d => d.Id == id)).DonorId.Should().Be("dnr_email");
    }

    [Fact]
    public async Task IdempotentReplayKeepsOriginalDonationAndDonorId()
    {
        var a = await Donor("owner@example.org", "usr_owner");
        var key = Guid.NewGuid().ToString("N");
        var id = await Gift("typed@example.org", DonorToken("usr_owner"), key: key);
        await Edit(a, d => d.Status = DonorStatuses.Inactive);
        (await Gift("typed@example.org", DonorToken("usr_owner"), key: key)).Should().Be(id);
        (await Db(db => db.Donations.SingleAsync())).DonorId.Should().Be(a);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonthlyPlanStoresTheSameDonorIdAsItsFirstGift(bool token)
    {
        var owner = await Donor("owner@example.org", "usr_owner");
        var id = await Gift(token ? "typed@example.org" : "OWNER@example.org", token ? DonorToken("usr_owner") : null, frequency: "monthly");
        var gift = await Db(db => db.Donations.SingleAsync(d => d.Id == id));
        gift.DonorId.Should().Be(owner);
        (await Db(db => db.RecurringPlans.SingleAsync(p => p.Id == gift.RecurringPlanId))).DonorId.Should().Be(owner);
    }

    [Fact]
    public async Task LegacyNullDonorIdStillConfirmsByEmail()
    {
        var owner = await Donor("legacy@example.org");
        var id = "don_legacy";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<IDonorProtector>();
            db.Donations.Add(new DonationEntity
            {
                Id = id,
                Reference = DonationReference.New(),
                Amount = 500m,
                Currency = "BDT",
                DesignationType = "general",
                Kind = DonationKinds.OneTime,
                DonorNameProtected = protector.Protect("Legacy"),
                DonorEmailProtected = protector.Protect("legacy@example.org"),
                CreatedAt = DateTimeOffset.UtcNow,
                ReceivedOn = DateOnly.FromDateTime(DateTime.UtcNow),
                Events = [new DonationEventEntity { Status = DonationStatuses.Pending, At = DateTimeOffset.UtcNow }]
            });
            await db.SaveChangesAsync();
        }
        await Confirm(id);
        await AssertOwner(id, owner);
        (await Db(db => db.Donations.SingleAsync(d => d.Id == id))).DonorId.Should().BeNull();
        (await Db(db => db.Donors.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task PlanModelHasNullableIndexedDonorId()
    {
        await Db(db =>
        {
            var entity = db.Model.FindEntityType(typeof(RecurringPlanEntity))!;
            var property = entity.FindProperty(nameof(RecurringPlanEntity.DonorId))!;
            property.IsNullable.Should().BeTrue();
            property.GetMaxLength().Should().Be(40);
            entity.GetIndexes().Should().Contain(i => i.Properties.Count == 1 && i.Properties.Contains(property));
            return Task.FromResult(0);
        });
    }

    [RequiresPostgresFact]
    public async Task RecurringPlanDonorIdMigrationAppliesWithoutBackfill()
    {
        var database = await TestPostgres.CreateUniqueDatabaseAsync("finance_attribution");
        try
        {
            var options = new DbContextOptionsBuilder<FinanceDbContext>().UseNpgsql(TestPostgres.ConnectionStringFor(database), n => n.MigrationsHistoryTable("__EFMigrationsHistory", "finance")).Options;
            await using var db = new FinanceDbContext(options);
            var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20261115083607_AddDonationDonorId");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO finance.recurring_plans ("Id","Interval","Amount","FeeAmount","Currency","DesignationType","IsAnonymous","DonorNameProtected","DonorEmailProtected","Language","StartsOn","CreatedAt")
                VALUES ('plan_old','monthly',500,0,'BDT','general',false,'name','email','en',current_date,now())
                """);
            await migrator.MigrateAsync();
            (await db.RecurringPlans.SingleAsync()).DonorId.Should().BeNull();
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            var indexCount = await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE schemaname = 'finance' AND indexname = 'IX_recurring_plans_DonorId'").SingleAsync();
            indexCount.Should().Be(1);
        }
        finally { await TestPostgres.DropDatabaseAsync(database); }
    }

    private static string DonorToken(string subject, bool mustChange = false) => TestTokens.Issue([], subject: subject,
        extraClaims: mustChange ? [new Claim(KjClaims.Role, KjRoles.Donor), new Claim(KjClaims.PasswordChangeRequired, "true")] : [new Claim(KjClaims.Role, KjRoles.Donor)]);

    private async Task<string> Donor(string email, string? subject = null)
    {
        using var response = await Send(HttpMethod.Post, Donors, Manager, new { name = "Profile Name", email, phone = "+8801711000001", language = "en" });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var id = (await Json(response)).GetProperty("donor").GetProperty("id").GetString()!;
        if (subject is not null) await Edit(id, d => { d.AccountSubject = subject; d.AccountStatus = AccountStatuses.Active; });
        return id;
    }

    private async Task Edit(string id, Action<DonorEntity> action) => await Db(async db =>
    {
        action(await db.Donors.SingleAsync(d => d.Id == id));
        return await db.SaveChangesAsync();
    });

    private async Task<string> Gift(string email, string? token = null, bool anonymous = false, string frequency = "once", string? key = null)
    {
        using var response = await SendGift(_client, email, token, anonymous, frequency, key);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendGift(HttpClient client, string email, string? token, bool anonymous = false, string frequency = "once", string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/donations")
        {
            Content = JsonContent.Create(new { amount = "500.00", currency = "BDT", frequency, designation = new { type = "general" }, donor = new { name = "Typed Name", email, phone = "+8801710000000" }, anonymous, language = "en", communicationConsent = false })
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await TestControls.SendAsync(client, request);
    }

    private async Task Confirm(string id)
    {
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<DonationConfirmation>().ConfirmAsync(id, "bkash", null)).IsSuccess.Should().BeTrue();
    }

    private async Task AssertOwner(string id, string donorId)
    {
        var owner = await Db(db => db.Donors.SingleAsync(d => d.Id == donorId));
        (await Db(db => db.Receipts.SingleAsync(r => r.DonationId == id))).DonorKey.Should().Be(owner.DonorKey);
    }

    private async Task<string[]> History(string subject)
    {
        using var response = await Send(HttpMethod.Get, "/api/v1/donors/me/donations", DonorToken(subject));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await Json(response)).GetProperty("items").EnumerateArray().Select(g => g.GetProperty("id").GetString()!).ToArray();
    }

    private async Task<T> Db<T>(Func<FinanceDbContext, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<FinanceDbContext>());
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string token, object? body = null, string? etag = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (etag is not null) request.Headers.Add("If-Match", etag);
        return await TestControls.SendAsync(_client, request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private sealed class RevokedSessionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }
}
