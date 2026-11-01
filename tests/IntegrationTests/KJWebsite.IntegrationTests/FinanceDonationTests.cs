using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class FinanceDonationTests : IDisposable
{
    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;
    private static readonly string FinanceViewer = TestTokens.Issue([Permissions.FinanceView]);
    private static readonly string Reverser = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationReverse]);

    public FinanceDonationTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static object Request(string amount = "1000.00", string currency = "BDT", string frequency = "once", bool anonymous = false, bool coverFees = false,
        object? designation = null, string? dedication = null, string name = "Rahim Uddin", string email = "rahim@example.org") => new
        {
            amount,
            currency,
            frequency,
            designation = designation ?? new { type = "general" },
            donor = new { name, email, phone = "+8801710000000" },
            anonymous,
            dedication,
            coverFees,
            language = "en"
        };

    private async Task<JsonElement> DonateAsync(object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/donations") { Content = JsonContent.Create(body) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        using var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpResponseMessage> AdminAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private async Task<JsonElement> AdminJsonAsync(string path, string? token = null)
    {
        using var response = await AdminAsync(HttpMethod.Get, path, token ?? FinanceViewer);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task ConfirmAsync(string donationId)
    {
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<FinanceService.Ledger.DonationConfirmation>().ConfirmAsync(donationId, "bkash", null)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task PresetsListEveryCurrencyWithDecimalStringLimits()
    {
        var presets = await _client.GetFromJsonAsync<JsonElement>("/api/v1/donations/presets");

        presets.GetProperty("defaultCurrency").GetString().Should().Be("BDT");
        presets.GetProperty("currencies").EnumerateArray().Select(c => c.GetProperty("code").GetString()).Should().Equal("BDT", "USD", "GBP", "EUR");
        var bdt = presets.GetProperty("currencies")[0];
        bdt.GetProperty("presets").EnumerateArray().Select(p => p.GetString()).Should().Equal("500.00", "1000.00", "2500.00", "5000.00");
        bdt.GetProperty("min").GetString().Should().Be("50.00");
    }

    [Fact]
    public async Task ADonationIsRecordedPendingAndItsStatusIsReadableByReference()
    {
        var created = await DonateAsync(Request("1250.50"));

        created.GetProperty("status").GetString().Should().Be("pending");
        created.GetProperty("amount").GetString().Should().Be("1250.50");
        created.GetProperty("currency").GetString().Should().Be("BDT");
        created.TryGetProperty("fee", out _).Should().BeFalse();
        var reference = created.GetProperty("reference").GetString()!;
        reference.Should().MatchRegex("^KJD-[0-9A-Z]{10}$");

        var status = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/donations/{reference}/status");
        status.GetProperty("status").GetString().Should().Be("pending");
        status.GetProperty("amount").GetString().Should().Be("1250.50");
        status.GetProperty("anonymous").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task CoveringTheFeeChargesMoreButKeepsTheDesignatedAmount()
    {
        var created = await DonateAsync(Request("1000.00", coverFees: true));

        created.GetProperty("amount").GetString().Should().Be("1000.00");
        created.GetProperty("fee").GetString().Should().Be("25.65"); // ceil(1000 / 0.975) = 1025.65
        created.GetProperty("total").GetString().Should().Be("1025.65");

        using var scope = _factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Donations.SingleAsync();
        (row.Amount, row.FeeAmount).Should().Be((1000.00m, 25.65m));
    }

    [Fact]
    public async Task ForeignCurrencyIsStoredAsGivenNeverConverted()
    {
        var created = await DonateAsync(Request("25.00", "USD"));

        created.GetProperty("currency").GetString().Should().Be("USD");
        using var scope = _factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Donations.SingleAsync();
        (row.Amount, row.Currency).Should().Be((25.00m, "USD"));
    }

    [Theory]
    [InlineData("12.345", "BDT")]
    [InlineData("1e3", "BDT")]
    [InlineData("-500", "BDT")]
    [InlineData("0", "BDT")]
    [InlineData("49.99", "BDT")]
    [InlineData("5000000.01", "BDT")]
    [InlineData("1,000", "BDT")]
    [InlineData("১০০০", "BDT")]
    [InlineData("", "BDT")]
    [InlineData("100", "XYZ")]
    [InlineData("100", "bdt")]
    [InlineData("0.99", "USD")]
    public async Task InvalidAmountsAndCurrenciesAreRefused(string amount, string currency)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/donations", Request(amount, currency));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("VALIDATION_ERROR");
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Donations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AJsonNumberIsNotAcceptedAsAnAmount()
    {
        using var content = new StringContent("""{"amount":1000.5,"currency":"BDT","frequency":"once","donor":{"name":"A","email":"a@example.org"}}""", System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync("/api/v1/donations", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("frequency", "weekly")]
    [InlineData("name", "")]
    [InlineData("email", "not-an-email")]
    [InlineData("designationType", "fund")]
    [InlineData("designationId", "Not A Slug")]
    [InlineData("dedication", "TOO LONG")]
    public async Task OtherInvalidFieldsAreNamedInTheError(string field, string value)
    {
        var body = field switch
        {
            "frequency" => Request(frequency: value),
            "name" => Request(name: value),
            "email" => Request(email: value),
            "designationType" => Request(designation: new { type = value, id = "x" }),
            "designationId" => Request(designation: new { type = "campaign", id = value }),
            _ => Request(dedication: new string('x', 501))
        };

        using var response = await _client.PostAsJsonAsync("/api/v1/donations", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Invalid fields");
    }

    [Fact]
    public async Task ARepeatWithTheSameIdempotencyKeyCreatesNothingNew()
    {
        var body = Request("500.00");
        var first = await DonateAsync(body, "key-donation-1");
        using var repeat = new HttpRequestMessage(HttpMethod.Post, "/api/v1/donations") { Content = JsonContent.Create(body) };
        repeat.Headers.Add("Idempotency-Key", "key-donation-1");
        using var replay = await _client.SendAsync(repeat);

        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        replay.Headers.GetValues("Idempotency-Replayed").Should().Contain("true");
        (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reference").GetString().Should().Be(first.GetProperty("reference").GetString());
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Donations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AMonthlyDonationCreatesARecurringPlanAndItsFirstInstalment()
    {
        var created = await DonateAsync(Request("300.00", frequency: "monthly", designation: new { type = "campaign", id = "flood-relief" }));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        var plan = await db.RecurringPlans.SingleAsync();
        var donation = await db.Donations.SingleAsync();
        (plan.Interval, plan.Amount, plan.DesignationId).Should().Be(("monthly", 300.00m, "flood-relief"));
        (donation.Kind, donation.RecurringPlanId, donation.DesignationType, donation.DesignationId).Should().Be(("recurring", plan.Id, "campaign", "flood-relief"));
        created.GetProperty("id").GetString().Should().Be(donation.Id);

        var plans = await AdminJsonAsync("/api/v1/admin/finance/recurring-plans");
        plans.GetProperty("total").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task AnonymousDonationsNeverRevealTheDonorInAnyPublicResponse()
    {
        var created = await DonateAsync(Request("700.00", anonymous: true, dedication: "In memory of Karim", name: "Secret Giver", email: "secret@example.org"));
        var reference = created.GetProperty("reference").GetString()!;

        var createdText = created.GetRawText();
        using var statusResponse = await _client.GetAsync($"/api/v1/donations/{reference}/status");
        var statusText = await statusResponse.Content.ReadAsStringAsync();

        foreach (var text in new[] { createdText, statusText })
        {
            text.Should().NotContain("Secret").And.NotContain("secret@example.org").And.NotContain("Karim");
        }

        JsonDocument.Parse(statusText).RootElement.GetProperty("anonymous").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task DonorDetailsAreEncryptedAtRestAndFinanceStaffCanReadThem()
    {
        var created = await DonateAsync(Request("700.00", anonymous: true, name: "Secret Giver", email: "secret@example.org"));

        using (var scope = _factory.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Donations.SingleAsync();
            (row.DonorNameProtected + row.DonorEmailProtected + row.DonorPhoneProtected).Should().NotContain("Secret").And.NotContain("secret@example.org").And.NotContain("8801710000000");
        }

        var detail = await AdminJsonAsync($"/api/v1/admin/finance/donations/{created.GetProperty("id").GetString()}");
        detail.GetProperty("donorEmail").GetString().Should().Be("secret@example.org");
        detail.GetProperty("donation").GetProperty("donorName").GetString().Should().Be("Secret Giver");
        detail.GetProperty("donation").GetProperty("anonymous").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task UnknownAndMalformedReferencesAre404()
    {
        (await _client.GetAsync("/api/v1/donations/KJD-0000000000/status")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/api/v1/donations/nonsense/status")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AdminEndpointsNeedTheRightPermission()
    {
        (await _client.GetAsync("/api/v1/admin/finance/donations")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await AdminAsync(HttpMethod.Get, "/api/v1/admin/finance/donations", TestTokens.Issue([Permissions.ContentView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var created = await DonateAsync(Request());
        await ConfirmAsync(created.GetProperty("id").GetString()!);
        var reverse = await AdminAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{created.GetProperty("id").GetString()}/reversal", FinanceViewer, new { reason = "Chargeback" });
        reverse.StatusCode.Should().Be(HttpStatusCode.Forbidden, "viewing finance records does not allow reversing them");
    }

    [Fact]
    public async Task EveryFinanceEndpointDeclaresAPolicyOrIsExplicitlyAnonymous()
    {
        _ = _factory.CreateClient();
        foreach (var endpoint in _factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints))
        {
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any();
            (anonymous || authorize).Should().BeTrue($"{endpoint.DisplayName} must opt in to a policy or to anonymous access");
            if (endpoint is RouteEndpoint { RoutePattern.RawText: { } route } && route.StartsWith("/api/v1/admin/", StringComparison.Ordinal))
            {
                anonymous.Should().BeFalse($"{route} is an admin endpoint");
            }
        }
    }

    [Fact]
    public async Task LedgerRowsCannotBeUpdatedOrDeleted()
    {
        var created = await DonateAsync(Request("400.00", frequency: "monthly"));
        var id = created.GetProperty("id").GetString()!;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();

        var donation = await db.Donations.Include(d => d.Events).SingleAsync(d => d.Id == id);
        donation.Amount = 1m;
        await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
        db.ChangeTracker.Clear();

        donation = await db.Donations.Include(d => d.Events).SingleAsync(d => d.Id == id);
        db.Donations.Remove(donation);
        await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<InvalidOperationException>();
        db.ChangeTracker.Clear();

        (await db.DonationEvents.FirstAsync()).Status = DonationStatuses.Failed;
        await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<InvalidOperationException>();
        db.ChangeTracker.Clear();

        (await db.RecurringPlans.FirstAsync()).Amount = 2m;
        await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<InvalidOperationException>();
        db.ChangeTracker.Clear();

        (await db.Donations.CountAsync()).Should().Be(1);
        (await db.Donations.AsNoTracking().SingleAsync()).Amount.Should().Be(400.00m);
    }

    [Fact]
    public async Task AReversalIsANewNegativeEntryAndTheOriginalIsLeftAlone()
    {
        var created = await DonateAsync(Request("1000.00", coverFees: true));
        var id = created.GetProperty("id").GetString()!;
        await ConfirmAsync(id);

        using var response = await AdminAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{id}/reversal", Reverser, new { reason = "Duplicate gift" });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var reversal = (await response.Content.ReadFromJsonAsync<JsonElement>());
        reversal.GetProperty("donation").GetProperty("amount").GetString().Should().Be("-1000.00");
        reversal.GetProperty("donation").GetProperty("fee").GetString().Should().Be("-25.65");
        reversal.GetProperty("donation").GetProperty("kind").GetString().Should().Be("reversal");
        reversal.GetProperty("reversesDonationId").GetString().Should().Be(id);

        using (var scope = _factory.Services.CreateScope())
        {
            var original = await scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Donations.AsNoTracking().SingleAsync(d => d.Id == id);
            (original.Amount, original.FeeAmount).Should().Be((1000.00m, 25.65m));
        }

        var detail = await AdminJsonAsync($"/api/v1/admin/finance/donations/{id}");
        detail.GetProperty("donation").GetProperty("status").GetString().Should().Be("refunded");
        detail.GetProperty("reversedByDonationId").GetString().Should().Be(reversal.GetProperty("donation").GetProperty("id").GetString());

        var totals = await AdminJsonAsync("/api/v1/admin/finance/donations/totals");
        var bdt = totals.GetProperty("items")[0];
        bdt.GetProperty("confirmed").GetString().Should().Be("0.00", "the reversal nets the gift out");
        bdt.GetProperty("count").GetInt32().Should().Be(2);

        var reference = created.GetProperty("reference").GetString();
        (await _client.GetFromJsonAsync<JsonElement>($"/api/v1/donations/{reference}/status")).GetProperty("status").GetString().Should().Be("cancelled");
    }

    [Fact]
    public async Task AGiftCanOnlyBeReversedOnceAndOnlyWhenConfirmed()
    {
        var pending = await DonateAsync(Request());
        using var tooEarly = await AdminAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{pending.GetProperty("id").GetString()}/reversal", Reverser, new { reason = "Oops" });
        tooEarly.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await tooEarly.Content.ReadAsStringAsync()).Should().Contain("NOT_REVERSIBLE");

        var confirmed = await DonateAsync(Request());
        var id = confirmed.GetProperty("id").GetString()!;
        await ConfirmAsync(id);
        (await AdminAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{id}/reversal", Reverser, new { reason = "x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var first = await AdminAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{id}/reversal", Reverser, new { reason = "Refund requested" });
        first.StatusCode.Should().Be(HttpStatusCode.Created, await first.Content.ReadAsStringAsync());

        using var again = await AdminAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{id}/reversal", Reverser, new { reason = "Refund requested" });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync()).Should().Contain("ALREADY_REVERSED");

        var reversalId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("donation").GetProperty("id").GetString();
        using var ofReversal = await AdminAsync(HttpMethod.Post, $"/api/v1/admin/finance/donations/{reversalId}/reversal", Reverser, new { reason = "Reverse the reversal" });
        ofReversal.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AdminAsync(HttpMethod.Post, "/api/v1/admin/finance/donations/don_missing/reversal", Reverser, new { reason = "Missing one" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TotalsKeepEveryCurrencySeparate()
    {
        var bdt = await DonateAsync(Request("1000.00"));
        var usd = await DonateAsync(Request("25.00", "USD"));
        await DonateAsync(Request("10.00", "GBP"));
        await ConfirmAsync(bdt.GetProperty("id").GetString()!);
        await ConfirmAsync(usd.GetProperty("id").GetString()!);

        var items = (await AdminJsonAsync("/api/v1/admin/finance/donations/totals")).GetProperty("items").EnumerateArray().ToList();

        items.Select(i => i.GetProperty("currency").GetString()).Should().Equal("BDT", "GBP", "USD");
        items.Single(i => i.GetProperty("currency").GetString() == "BDT").GetProperty("confirmed").GetString().Should().Be("1000.00");
        items.Single(i => i.GetProperty("currency").GetString() == "USD").GetProperty("confirmed").GetString().Should().Be("25.00");
        var gbp = items.Single(i => i.GetProperty("currency").GetString() == "GBP");
        (gbp.GetProperty("confirmed").GetString(), gbp.GetProperty("pending").GetString()).Should().Be(("0.00", "10.00"));
    }

    [Fact]
    public async Task TheListFiltersByStatusDesignationAndPages()
    {
        var a = await DonateAsync(Request("100.00", designation: new { type = "campaign", id = "flood-relief" }));
        await DonateAsync(Request("200.00", designation: new { type = "project", id = "school" }));
        await DonateAsync(Request("300.00"));
        await ConfirmAsync(a.GetProperty("id").GetString()!);

        (await AdminJsonAsync("/api/v1/admin/finance/donations")).GetProperty("total").GetInt32().Should().Be(3);
        (await AdminJsonAsync("/api/v1/admin/finance/donations?status=confirmed")).GetProperty("total").GetInt32().Should().Be(1);
        (await AdminJsonAsync("/api/v1/admin/finance/donations?status=pending")).GetProperty("total").GetInt32().Should().Be(2);
        (await AdminJsonAsync("/api/v1/admin/finance/donations?designation=campaign:flood-relief")).GetProperty("total").GetInt32().Should().Be(1);
        (await AdminJsonAsync("/api/v1/admin/finance/donations?designation=project")).GetProperty("total").GetInt32().Should().Be(1);
        (await AdminJsonAsync("/api/v1/admin/finance/donations?method=bkash")).GetProperty("total").GetInt32().Should().Be(0);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        (await AdminJsonAsync($"/api/v1/admin/finance/donations?from={today}&to={today}")).GetProperty("total").GetInt32().Should().Be(3);
        (await AdminJsonAsync("/api/v1/admin/finance/donations?from=2999-01-01")).GetProperty("total").GetInt32().Should().Be(0);

        var page = await AdminJsonAsync("/api/v1/admin/finance/donations?limit=2&offset=2");
        page.GetProperty("items").GetArrayLength().Should().Be(1);
        page.GetProperty("limit").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task TheDonationRowCarriesNoPublicPaymentMethodYet()
    {
        var created = await DonateAsync(Request());
        var detail = await AdminJsonAsync($"/api/v1/admin/finance/donations/{created.GetProperty("id").GetString()}");

        detail.GetProperty("donation").GetProperty("status").GetString().Should().Be("pending");
        detail.GetProperty("events").GetArrayLength().Should().Be(1);
        detail.GetProperty("donation").GetProperty("designation").GetString().Should().Be("general");
    }
}
