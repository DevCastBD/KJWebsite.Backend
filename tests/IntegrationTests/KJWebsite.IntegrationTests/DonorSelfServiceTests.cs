using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Ledger;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

/// <summary>
/// The donor's own read-only data (BE-105, #212). High blast radius (personal data, ownership): the tests are mostly about who may NOT see
/// what, and about the state checks that must hold on every request, before an access token expires.
/// </summary>
public sealed class DonorSelfServiceTests : IDisposable
{
    private const string Me = "/api/v1/donors/me";
    private const string Donations = "/api/v1/donors/me/donations";
    private static readonly string Manager = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonorManage], subject: "usr_manager");
    private static readonly string Reverser = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationReverse], subject: "usr_reverser");

    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _proofs = Path.Combine(Path.GetTempPath(), $"kj-test-proofs-{Guid.NewGuid():N}");

    public DonorSelfServiceTests()
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

    private static string DonorToken(string subject, bool mustChange = false) =>
        TestTokens.Issue([], subject: subject, extraClaims: Claims(KjRoles.Donor, mustChange));

    private static IEnumerable<Claim> Claims(string role, bool mustChange = false) =>
        mustChange ? [new Claim(KjClaims.Role, role), new Claim(KjClaims.PasswordChangeRequired, "true")] : [new Claim(KjClaims.Role, role)];

    // ---------------------------------------------------------------- profile

    [Fact]
    public async Task TheProfileHoldsOnlyTheFieldsADonorMaySeeAndIsNeverCached()
    {
        var id = await Donor("Rahim Uddin", "rahim@example.org", "usr_rahim", notes: "Prefers a call on Fridays.");

        using var response = await Get(Me, DonorToken("usr_rahim"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await Json(response);
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["name", "type", "organisation", "email", "phone", "address", "country", "language", "since"]);
        body.GetProperty("name").GetString().Should().Be("Rahim Uddin");
        body.GetProperty("email").GetString().Should().Be("rahim@example.org");
        body.GetProperty("address").GetString().Should().Be("12 Lake Road, Dhaka");
        body.GetProperty("since").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$");
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Fridays").And.NotContain(id);
        response.Headers.CacheControl!.ToString().Should().Be("no-store, private");
        response.Headers.Vary.Should().Contain("Authorization");
    }

    [Fact]
    public async Task ADonorWithNoOptionalDetailsGetsEmptyStringsNotNulls()
    {
        await Donor("Plain Donor", "plain@example.org", "usr_plain", full: false);

        var body = await Json(await Get(Me, DonorToken("usr_plain")));

        foreach (var field in new[] { "organisation", "phone", "address", "country" })
        {
            body.GetProperty(field).GetString().Should().BeEmpty(field);
        }
    }

    // ---------------------------------------------------------------- who may call

    [Fact]
    public async Task OnlyTheDonorRoleMayCall()
    {
        await Donor("Rahim Uddin", "rahim@example.org", "usr_rahim");

        (await Status(Me, null)).Should().Be(HttpStatusCode.Unauthorized);
        // A back-office token (even one holding every finance permission) and a member token are refused.
        (await Status(Me, Manager)).Should().Be(HttpStatusCode.Forbidden);
        (await Status(Me, TestTokens.Issue([Permissions.FinanceView], subject: "usr_rahim"))).Should().Be(HttpStatusCode.Forbidden);
        (await Status(Me, TestTokens.Issue([], subject: "usr_rahim", extraClaims: Claims(KjRoles.Member)))).Should().Be(HttpStatusCode.Forbidden);
        (await Status(Donations, TestTokens.Issue([], subject: "usr_rahim", extraClaims: Claims(KjRoles.Admin)))).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ADonorRoleWithNoDonorRecordIsNotADonor()
    {
        using var response = await Get(Me, DonorToken("usr_nobody"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(response)).Should().Be("NOT_A_DONOR");
        (await Status(Donations, DonorToken("usr_nobody"))).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnInactiveDonorAndADisabledSignInAreRefusedOnEveryRequestEvenWithTheSameLiveToken()
    {
        var id = await Donor("Rahim Uddin", "rahim@example.org", "usr_rahim");
        var token = DonorToken("usr_rahim");
        (await Status(Me, token)).Should().Be(HttpStatusCode.OK);

        await Edit(id, d => d.Status = DonorStatuses.Inactive);
        using var inactive = await Get(Me, token);
        inactive.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(inactive)).Should().Be("DONOR_INACTIVE");
        (await Status(Donations, token)).Should().Be(HttpStatusCode.Forbidden);

        await Edit(id, d => { d.Status = DonorStatuses.Active; d.AccountStatus = AccountStatuses.Disabled; });
        using var disabled = await Get(Me, token);
        disabled.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(disabled)).Should().Be("ACCOUNT_DISABLED");
        (await Status(Donations, token)).Should().Be(HttpStatusCode.Forbidden);

        await Edit(id, d => d.AccountStatus = AccountStatuses.Active);
        (await Status(Me, token)).Should().Be(HttpStatusCode.OK);
        (await Status(Donations, token)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AccountThatMustStillChangeItsInitialPasswordCannotUseIt()
    {
        await Donor("Rahim Uddin", "rahim@example.org", "usr_rahim");

        using var response = await Get(Me, DonorToken("usr_rahim", mustChange: true));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(response)).Should().Be("PASSWORD_CHANGE_REQUIRED");
        (await Status(Donations, DonorToken("usr_rahim", mustChange: true))).Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------------------------------------------------------------- ownership

    [Fact]
    public async Task EachDonorSeesOnlyTheirOwnProfileAndGiftsWhateverIdIsSupplied()
    {
        var rahim = await Donor("Rahim Uddin", "rahim@example.org", "usr_rahim");
        var karim = await Donor("Karim Hossain", "karim@example.org", "usr_karim");
        await Gift("rahim@example.org", "100.00");
        await Gift("rahim@example.org", "200.00");
        await Gift("karim@example.org", "999.00", name: "Karim Hossain");

        var mine = await Json(await Get(Donations, DonorToken("usr_rahim")));
        mine.GetProperty("total").GetInt32().Should().Be(2);
        mine.GetProperty("items").EnumerateArray().Select(g => g.GetProperty("amount").GetString()).Should().BeEquivalentTo(["200.00", "100.00"]);

        var theirs = await Json(await Get(Donations, DonorToken("usr_karim")));
        theirs.GetProperty("items").EnumerateArray().Select(g => g.GetProperty("amount").GetString()).Should().Equal("999.00");

        // Naming the other donor, in any way a client might try, changes nothing.
        foreach (var query in new[] { $"?donorId={karim}", $"?id={karim}", $"?donor={karim}&donorKey=x", $"?email=karim@example.org" })
        {
            var profile = await Json(await Get(Me + query, DonorToken("usr_rahim")));
            profile.GetProperty("name").GetString().Should().Be("Rahim Uddin");
            var gifts = await Json(await Get(Donations + query, DonorToken("usr_rahim")));
            gifts.GetProperty("total").GetInt32().Should().Be(2, query);
        }

        using var withBody = new HttpRequestMessage(HttpMethod.Get, Me) { Content = JsonContent.Create(new { donorId = karim }) };
        withBody.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DonorToken("usr_rahim"));
        using var bodyResponse = await _client.SendAsync(withBody);
        (await Json(bodyResponse)).GetProperty("name").GetString().Should().Be("Rahim Uddin");
        rahim.Should().NotBe(karim);
    }

    // ---------------------------------------------------------------- read-only

    [Fact]
    public async Task EveryWriteMethodIsRefusedWith405AndChangesNothing()
    {
        var id = await Donor("Rahim Uddin", "rahim@example.org", "usr_rahim");
        await Gift("rahim@example.org", "100.00");
        var token = DonorToken("usr_rahim");

        foreach (var path in new[] { Me, Donations })
        {
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
            {
                using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(new { name = "Hacked", donorId = id }) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await _client.SendAsync(request);
                response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, $"{method} {path}");
                response.Content.Headers.Allow.Should().Contain("GET");
            }
        }

        (await Json(await Get(Me, token))).GetProperty("name").GetString().Should().Be("Rahim Uddin");
        (await Json(await Get(Donations, token))).GetProperty("total").GetInt32().Should().Be(1);
    }

    // ---------------------------------------------------------------- gifts

    [Fact]
    public async Task ReversedAndAnonymousGiftsAreAbsentFromHistoryAndTotals()
    {
        await Donor("Rahim Uddin", "rahim@example.org", "usr_rahim");
        var kept = await Gift("rahim@example.org", "100.00");
        var reversed = await Gift("rahim@example.org", "500.00");
        await Gift("rahim@example.org", "300.00", anonymous: true);
        using var reversal = await Send(HttpMethod.Post, $"/api/v1/admin/finance/donations/{reversed}/reversal", Reverser, new { reason = "Chargeback" });
        reversal.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.Accepted);

        var body = await Json(await Get(Donations, DonorToken("usr_rahim")));

        body.GetProperty("items").EnumerateArray().Select(g => g.GetProperty("id").GetString()).Should().Equal(kept);
        body.GetProperty("total").GetInt32().Should().Be(1);
        body.GetProperty("totals").EnumerateArray().Single().GetProperty("cash").GetString().Should().Be("100.00");
    }

    [Fact]
    public async Task GiftsAreNewestFirstPagedWithATotalAndTheTotalsCoverEveryPage()
    {
        await Donor("Rahim Uddin", "rahim@example.org", "usr_rahim");
        for (var i = 1; i <= 3; i++)
        {
            await Gift("rahim@example.org", $"{i * 100}.50");
        }

        var token = DonorToken("usr_rahim");
        var first = await Json(await Get($"{Donations}?limit=2", token));
        var second = await Json(await Get($"{Donations}?limit=2&offset=2", token));
        var beyond = await Json(await Get($"{Donations}?limit=2&offset=10", token));

        first.GetProperty("items").GetArrayLength().Should().Be(2);
        first.GetProperty("total").GetInt32().Should().Be(3);
        first.GetProperty("limit").GetInt32().Should().Be(2);
        first.GetProperty("offset").GetInt32().Should().Be(0);
        second.GetProperty("items").GetArrayLength().Should().Be(1);
        second.GetProperty("offset").GetInt32().Should().Be(2);
        beyond.GetProperty("items").GetArrayLength().Should().Be(0);
        beyond.GetProperty("total").GetInt32().Should().Be(3);

        var numbers = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(g => g.GetProperty("receiptNumber").GetString()).ToList();
        numbers.Should().BeInDescendingOrder(StringComparer.Ordinal).And.OnlyHaveUniqueItems();

        // 100.50 + 200.50 + 300.50, even though a page holds two.
        var total = first.GetProperty("totals").EnumerateArray().Single();
        total.GetProperty("currency").GetString().Should().Be("BDT");
        total.GetProperty("cash").GetString().Should().Be("601.50");
        total.GetProperty("inKind").GetString().Should().Be("0.00");
        var gift = first.GetProperty("items")[0];
        gift.GetProperty("status").GetString().Should().Be("confirmed");
        gift.GetProperty("currency").GetString().Should().Be("BDT");
        gift.GetProperty("designation").GetString().Should().Be("General");
        gift.GetProperty("designationType").GetString().Should().Be("general");
    }

    [Fact]
    public async Task TheYearFilterKeepsOnlyThatCalendarYear()
    {
        await Donor("Rahim Uddin", "rahim@example.org", "usr_rahim");
        await Gift("rahim@example.org", "100.00");
        var token = DonorToken("usr_rahim");
        var thisYear = DateTime.UtcNow.Year;

        (await Json(await Get($"{Donations}?year={thisYear}", token))).GetProperty("total").GetInt32().Should().Be(1);
        var other = await Json(await Get($"{Donations}?year={thisYear - 1}", token));
        other.GetProperty("total").GetInt32().Should().Be(0);
        other.GetProperty("items").GetArrayLength().Should().Be(0);
        other.GetProperty("totals").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("limit=101")]
    [InlineData("limit=0")]
    [InlineData("limit=-5")]
    [InlineData("offset=-1")]
    [InlineData("year=12")]
    public async Task OutOfRangePagingAndYearAreRefusedWith400(string query)
    {
        await Donor("Rahim Uddin", "rahim@example.org", "usr_rahim");

        using var response = await Get($"{Donations}?{query}", DonorToken("usr_rahim"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(response)).Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task ADonorWithNoGiftsGetsAnEmptyPageNotAnError()
    {
        await Donor("Rahim Uddin", "rahim@example.org", "usr_rahim");

        var body = await Json(await Get(Donations, DonorToken("usr_rahim")));

        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("total").GetInt32().Should().Be(0);
        body.GetProperty("limit").GetInt32().Should().Be(20);
        body.GetProperty("totals").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ARefusalIsAlsoNeverCached()
    {
        using var response = await Get(Me, null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.CacheControl!.ToString().Should().Be("no-store, private");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Makes a donor through the staff API, then gives them the sign-in the way provisioning would, without calling Identity.</summary>
    private async Task<string> Donor(string name, string email, string subject, string? notes = null, bool full = true)
    {
        using var response = await Send(HttpMethod.Post, "/api/v1/admin/finance/donors", Manager, full
            ? new { name, email, phone = "+8801711000001", language = "en", type = "individual", address = "12 Lake Road, Dhaka", country = "BD", notes }
            : new { name, email });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var id = (await Json(response)).GetProperty("donor").GetProperty("id").GetString()!;
        await Edit(id, d => { d.AccountSubject = subject; d.AccountStatus = AccountStatuses.Active; });
        return id;
    }

    private async Task Edit(string id, Action<DonorEntity> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        var donor = await db.Donors.SingleAsync(d => d.Id == id);
        change(donor);
        await db.SaveChangesAsync();
    }

    private async Task<string> Gift(string email, string amount, bool anonymous = false, string name = "Rahim Uddin")
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/donations", new
        {
            amount,
            currency = "BDT",
            frequency = "once",
            designation = new { type = "general" },
            donor = new { name, email },
            anonymous,
            coverFees = false,
            language = "en",
            communicationConsent = false
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var id = (await Json(response)).GetProperty("id").GetString()!;
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<DonationConfirmation>().ConfirmAsync(id, "bkash", null)).IsSuccess.Should().BeTrue();
        return id;
    }

    private Task<HttpResponseMessage> Get(string url, string? token) => Send(HttpMethod.Get, url, token);

    private async Task<HttpStatusCode> Status(string url, string? token)
    {
        using var response = await Get(url, token);
        return response.StatusCode;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return TestControls.SendAsync(_client, request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string?> ErrorCode(HttpResponseMessage response) =>
        (await Json(response)).GetProperty("error").GetProperty("code").GetString();
}
