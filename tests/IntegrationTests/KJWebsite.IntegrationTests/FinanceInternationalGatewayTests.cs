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

public sealed class FinanceInternationalGatewayTests : IDisposable
{
    private const string StripeSecret = "sk_test_SECRETKEY123";
    private const string StripeHook = "whsec_HOOKSECRET456";
    private const string PayPalSecret = "pp-client-SECRET789";

    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly ScriptedGatewayHandler _handler;
    private HttpClient? _noRedirect;

    // What the fake gateways currently say.
    private string _stripePaymentStatus = "paid";
    private string _stripeSessionStatus = "complete";
    private string _stripeIntentStatus = "succeeded";
    private string? _stripeAmountTotal;
    private string _stripeCurrency = "usd";
    private bool _stripeDown;
    private string _ppOrderStatus = "APPROVED";
    private string _ppCaptureStatus = "COMPLETED";
    private string? _ppAmount;
    private bool _ppSignatureOk = true;
    private bool _ppVerifyDown;
    private int _ppTokenCalls;
    private int _stripeFetches;

    public FinanceInternationalGatewayTests()
    {
        _handler = new ScriptedGatewayHandler(Respond);
        var s = _factory.Settings;
        s["Finance:Payments:PublicApiBaseUrl"] = "https://api.example.org";
        s["Finance:Payments:SiteBaseUrl"] = "https://site.example.org";
        s["Finance:Payments:ReconcileIntervalMinutes"] = "0";
        s["Finance:Payments:Stripe:Enabled"] = "true";
        s["Finance:Payments:Stripe:SecretKey"] = StripeSecret;
        s["Finance:Payments:Stripe:WebhookSecret"] = StripeHook;
        s["Finance:Payments:PayPal:Enabled"] = "true";
        s["Finance:Payments:PayPal:ClientId"] = "pp-client-id";
        s["Finance:Payments:PayPal:ClientSecret"] = PayPalSecret;
        s["Finance:Payments:PayPal:WebhookId"] = "WH-1";
        _factory.GatewayHandler = _handler;
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _noRedirect?.Dispose();
        _factory.Dispose();
    }

    private HttpResponseMessage Respond(RecordedRequest request)
    {
        var path = request.Url.AbsolutePath;
        if (request.Url.Host == "api.stripe.com")
        {
            request.Headers["authorization"].Should().Be($"Bearer {StripeSecret}");
            if (request.Method == HttpMethod.Post)
            {
                request.Headers["idempotency-key"].Should().StartWith("kj-checkout-KJD-");
                return ScriptedGatewayHandler.Json(new { id = "cs_test_" + request.Form["client_reference_id"], url = "https://checkout.stripe.com/c/pay/x" });
            }

            _stripeFetches++;
            if (_stripeDown)
            {
                throw new HttpRequestException("down");
            }

            var reference = path["/v1/checkout/sessions/cs_test_".Length..];
            request.Url.Query.Should().Contain("expand[]=payment_intent.latest_charge.balance_transaction");
            return ScriptedGatewayHandler.Json(new
            {
                id = "cs_test_" + reference,
                status = _stripeSessionStatus,
                payment_status = _stripePaymentStatus,
                client_reference_id = reference,
                amount_total = long.Parse(_stripeAmountTotal ?? "2500", System.Globalization.CultureInfo.InvariantCulture),
                currency = _stripeCurrency,
                payment_intent = new { id = "pi_1", status = _stripeIntentStatus, latest_charge = new { balance_transaction = new { amount = 1950, currency = "gbp", fee = 87, exchange_rate = 0.78 } } }
            });
        }

        // PayPal
        if (path.EndsWith("/v1/oauth2/token", StringComparison.Ordinal))
        {
            _ppTokenCalls++;
            request.Headers["authorization"].Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"pp-client-id:{PayPalSecret}")));
            return ScriptedGatewayHandler.Json(new { access_token = "pp-tok", expires_in = 3600 });
        }

        request.Headers["authorization"].Should().Be("Bearer pp-tok");
        if (path.EndsWith("/v1/notifications/verify-webhook-signature", StringComparison.Ordinal))
        {
            if (_ppVerifyDown)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return ScriptedGatewayHandler.Json(new { verification_status = _ppSignatureOk ? "SUCCESS" : "FAILURE" });
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/v2/checkout/orders", StringComparison.Ordinal))
        {
            request.Headers["paypal-request-id"].Should().StartWith("kj-order-KJD-");
            var custom = request.Json.GetProperty("purchase_units")[0].GetProperty("custom_id").GetString();
            return ScriptedGatewayHandler.Json(new { id = "ORD-" + custom, status = "PAYER_ACTION_REQUIRED", links = new[] { new { rel = "self", href = "https://api.paypal.com/x" }, new { rel = "payer-action", href = "https://www.sandbox.paypal.com/checkoutnow?token=ORD-" + custom } } });
        }

        var orderId = path.Split('/')[4];
        var captured = request.Method == HttpMethod.Post && path.EndsWith("/capture", StringComparison.Ordinal);
        if (captured)
        {
            request.Headers["paypal-request-id"].Should().StartWith("kj-capture-KJD-");
            _ppOrderStatus = _ppCaptureStatus == "COMPLETED" || _ppCaptureStatus == "DECLINED" ? "COMPLETED" : _ppOrderStatus;
        }

        return ScriptedGatewayHandler.Json(Order(orderId));
    }

    private object Order(string orderId)
    {
        var custom = orderId["ORD-".Length..];
        if (_ppOrderStatus != "COMPLETED")
        {
            return new { id = orderId, status = _ppOrderStatus, purchase_units = new[] { new { custom_id = custom } } };
        }

        return new
        {
            id = orderId,
            status = "COMPLETED",
            purchase_units = new[]
            {
                new
                {
                    custom_id = custom,
                    payments = new
                    {
                        captures = new[]
                        {
                            new
                            {
                                id = "CAP-1", status = _ppCaptureStatus, amount = new { currency_code = "USD", value = _ppAmount ?? "25.00" },
                                seller_receivable_breakdown = new { receivable_amount = new { currency_code = "EUR", value = "21.10" }, paypal_fee = new { currency_code = "EUR", value = "1.05" }, exchange_rate = new { value = "0.9" } }
                            }
                        }
                    }
                }
            }
        };
    }

    private async Task<JsonElement> DonateAsync(string amount, string currency, string? gateway = null, bool coverFees = false)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/donations", new
        {
            amount,
            currency,
            frequency = "once",
            designation = new { type = "general" },
            donor = new { name = "A Donor", email = "d@example.org" },
            anonymous = false,
            coverFees,
            language = "en",
            gateway
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<T> DbAsync<T>(Func<FinanceDbContext, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<FinanceDbContext>());
    }

    private async Task<string> StatusOfAsync(string reference) => (await _client.GetFromJsonAsync<JsonElement>($"/api/v1/donations/{reference}/status")).GetProperty("status").GetString()!;

    private Task<int> RejectionsAsync(string? reason = null) => DbAsync(db => db.WebhookRejections.CountAsync(r => reason == null || r.ReasonCode == reason));

    // ---- Stripe helpers
    private static string StripeEvent(string type, object obj, string id = "evt_1") => JsonSerializer.Serialize(new { id, type, data = new { @object = obj } });

    private static string StripeSignature(string body, long? timestamp = null, string secret = StripeHook)
    {
        var t = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $"t={t},v1={Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"))).ToLowerInvariant()}";
    }

    private async Task<HttpResponseMessage> PostStripeAsync(string body, string? signature = null, bool sign = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/donations/webhooks/stripe") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (sign)
        {
            request.Headers.Add("Stripe-Signature", signature ?? StripeSignature(body));
        }

        return await _client.SendAsync(request);
    }

    private async Task<string> StripeDonationAsync() => (await DonateAsync("25.00", "USD", "stripe")).GetProperty("reference").GetString()!;

    private static string SessionDone(string reference, string id = "evt_1") =>
        StripeEvent("checkout.session.completed", new { id = "cs_test_" + reference, client_reference_id = reference }, id);

    // ---- PayPal helpers
    private async Task<HttpResponseMessage> PostPayPalAsync(string rawEvent, bool headers = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/donations/webhooks/paypal") { Content = new StringContent(rawEvent, Encoding.UTF8, "application/json") };
        if (headers)
        {
            request.Headers.Add("PAYPAL-TRANSMISSION-SIG", "sig==");
            request.Headers.Add("PAYPAL-TRANSMISSION-ID", "tx-1");
            request.Headers.Add("PAYPAL-TRANSMISSION-TIME", "2026-10-31T10:00:00Z");
            request.Headers.Add("PAYPAL-CERT-URL", "https://api.paypal.com/certs/1");
            request.Headers.Add("PAYPAL-AUTH-ALGO", "SHA256withRSA");
        }

        return await _client.SendAsync(request);
    }

    private static string PayPalEvent(string type, object resource, string id = "WH-EVT-1") => JsonSerializer.Serialize(new { id, event_type = type, resource });

    private async Task<string> PayPalDonationAsync() => (await DonateAsync("25.00", "USD", "paypal")).GetProperty("reference").GetString()!;

    private static string Approved(string reference, string id = "WH-EVT-1") => PayPalEvent("CHECKOUT.ORDER.APPROVED", new { id = "ORD-" + reference, purchase_units = new[] { new { custom_id = reference } } }, id);

    // ================= Stripe =================

    [Fact]
    public async Task StripeStartsACheckoutSessionWithTheExactChargeInMinorUnits()
    {
        var created = await DonateAsync("25.00", "USD", "stripe");

        created.GetProperty("gateway").GetString().Should().Be("stripe");
        created.GetProperty("checkoutUrl").GetString().Should().Be("https://checkout.stripe.com/c/pay/x");
        var reference = created.GetProperty("reference").GetString()!;
        var form = _handler.Requests.Single().Form;
        (form["mode"], form["line_items[0][price_data][unit_amount]"], form["line_items[0][price_data][currency]"], form["client_reference_id"]).Should().Be(("payment", "2500", "usd", reference));
        form["success_url"].Should().Be($"https://api.example.org/api/v1/donations/{reference}/return?result=success");
        form["cancel_url"].Should().EndWith("result=cancel");
        form["payment_intent_data[metadata][reference]"].Should().Be(reference);
    }

    [Fact]
    public async Task AFeeCoveredStripeDonationChargesAmountPlusFee()
    {
        await DonateAsync("100.00", "EUR", "stripe", coverFees: true);

        _handler.Requests.Single().Form["line_items[0][price_data][unit_amount]"].Should().Be("10406"); // ceil(100 / 0.961)
    }

    [Fact]
    public async Task ForeignCurrencyGiftsPreferTheInternationalGatewaysByDefaultAndBdtDoesNot()
    {
        (await DonateAsync("25.00", "USD")).GetProperty("gateway").GetString().Should().Be("stripe");
        (await DonateAsync("1000.00", "BDT")).GetProperty("gateway").GetString().Should().Be("stripe", "only Stripe is switched on among the BDT-capable ones here");
        (await DonateAsync("25.00", "GBP", "paypal")).GetProperty("gateway").GetString().Should().Be("paypal");
        var bdtPaypal = await DonateAsync("1000.00", "BDT", "paypal");
        bdtPaypal.GetProperty("fallback").GetString().Should().Be("offline", "PayPal does not take BDT");
    }

    [Fact]
    public async Task ASignedStripeWebhookConfirmsAfterTheSessionIsFetchedAndRecordsTheSettlementApartFromTheGift()
    {
        var reference = await StripeDonationAsync();

        using var response = await PostStripeAsync(SessionDone(reference));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(reference)).Should().Be("confirmed");
        _stripeFetches.Should().Be(1);
        var donation = await DbAsync(db => db.Donations.AsNoTracking().SingleAsync());
        (donation.Amount, donation.Currency).Should().Be((25.00m, "USD"), "the designated gift is untouched");
        var settlement = await DbAsync(db => db.PaymentSettlements.AsNoTracking().SingleAsync());
        (settlement.SettledAmount, settlement.SettledCurrency, settlement.Fee, settlement.ExchangeRate, settlement.TransactionId, settlement.Gateway)
            .Should().Be((19.50m, "GBP", 0.87m, 0.78m, "pi_1", "stripe"));
        var posting = await DbAsync(db => db.Postings.Include(p => p.Entries).ThenInclude(e => e.Account).AsNoTracking().SingleAsync());
        posting.Entries.Select(e => (e.Account!.Code, e.Debit, e.Credit, e.Currency)).Should().BeEquivalentTo([("1140", 25.00m, 0m, "USD"), ("4010", 0m, 25.00m, "USD")]);

        using var detail = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/admin/finance/donations/{donation.Id}");
        detail.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue([Permissions.FinanceView]));
        var view = await (await _client.SendAsync(detail)).Content.ReadFromJsonAsync<JsonElement>();
        var shown = view.GetProperty("settlement");
        (shown.GetProperty("amount").GetString(), shown.GetProperty("currency").GetString(), shown.GetProperty("fee").GetString(), shown.GetProperty("exchangeRate").GetString()).Should().Be(("19.50", "GBP", "0.87", "0.78"));
        view.GetProperty("donation").GetProperty("amount").GetString().Should().Be("25.00");
    }

    [Fact]
    public async Task ReplayingAStripeEventTenTimesMakesOneConfirmationOnePostingOneSettlement()
    {
        var reference = await StripeDonationAsync();
        var body = SessionDone(reference);

        for (var i = 0; i < 10; i++)
        {
            (await PostStripeAsync(body)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(1);
        (await DbAsync(db => db.PaymentSettlements.CountAsync())).Should().Be(1);
        (await DbAsync(db => db.DonationEvents.CountAsync(e => e.Status == "confirmed"))).Should().Be(1);
        _stripeFetches.Should().Be(1);
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("tampered-body")]
    [InlineData("stale-timestamp")]
    [InlineData("future-timestamp")]
    [InlineData("missing-header")]
    [InlineData("no-v1")]
    [InlineData("garbage-header")]
    public async Task ForgedOrStaleStripeSignaturesAreRejectedWithoutAskingStripe(string problem)
    {
        var reference = await StripeDonationAsync();
        var body = SessionDone(reference);
        using var response = problem switch
        {
            "wrong-secret" => await PostStripeAsync(body, StripeSignature(body, secret: "whsec_other")),
            "tampered-body" => await PostStripeAsync(body.Replace("evt_1", "evt_2", StringComparison.Ordinal), StripeSignature(body)),
            "stale-timestamp" => await PostStripeAsync(body, StripeSignature(body, DateTimeOffset.UtcNow.AddMinutes(-11).ToUnixTimeSeconds())),
            "future-timestamp" => await PostStripeAsync(body, StripeSignature(body, DateTimeOffset.UtcNow.AddMinutes(11).ToUnixTimeSeconds())),
            "missing-header" => await PostStripeAsync(body, sign: false),
            "no-v1" => await PostStripeAsync(body, $"t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"),
            _ => await PostStripeAsync(body, "nonsense")
        };

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StatusOfAsync(reference)).Should().Be("pending");
        _stripeFetches.Should().Be(0);
        (await RejectionsAsync("BAD_SIGNATURE")).Should().Be(1);
        _factory.Logs.Lines.Should().NotContain(l => l.Contains(StripeHook, StringComparison.Ordinal) || l.Contains(StripeSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStripeSessionPaidInTheWrongAmountOrCurrencyIsNotConfirmed()
    {
        var reference = await StripeDonationAsync();
        _stripeAmountTotal = "250";

        (await PostStripeAsync(SessionDone(reference, "evt_a"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _stripeAmountTotal = null;
        _stripeCurrency = "eur";
        (await PostStripeAsync(SessionDone(reference, "evt_b"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await StatusOfAsync(reference)).Should().Be("pending");
        (await RejectionsAsync("AMOUNT_MISMATCH")).Should().Be(2);
    }

    [Theory]
    [InlineData("unpaid", "complete", "processing", "checkout.session.completed", "pending")]
    [InlineData("unpaid", "complete", "requires_payment_method", "checkout.session.async_payment_failed", "failed")]
    [InlineData("unpaid", "expired", "requires_payment_method", "checkout.session.expired", "cancelled")]
    [InlineData("unpaid", "open", "canceled", "checkout.session.expired", "cancelled")]
    public async Task StripeOutcomesFollowTheSessionNotTheEventName(string payment, string session, string intent, string eventType, string expected)
    {
        var reference = await StripeDonationAsync();
        (_stripePaymentStatus, _stripeSessionStatus, _stripeIntentStatus) = (payment, session, intent);

        using var response = await PostStripeAsync(StripeEvent(eventType, new { id = "cs_test_" + reference, client_reference_id = reference }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(reference)).Should().Be(expected);
    }

    [Fact]
    public async Task EventTypesWeDoNotActOnAreAcknowledgedNotRejected()
    {
        using var response = await PostStripeAsync(StripeEvent("customer.created", new { id = "cus_1" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RejectionsAsync()).Should().Be(0);
        (await DbAsync(db => db.PaymentEvents.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task AStripeOutageDuringVerificationAsksForRedeliveryAndNothingIsDecided()
    {
        var reference = await StripeDonationAsync();
        _stripeDown = true;

        using var first = await PostStripeAsync(SessionDone(reference));

        first.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await StatusOfAsync(reference)).Should().Be("pending");
        _stripeDown = false;
        (await PostStripeAsync(SessionDone(reference))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(reference)).Should().Be("confirmed");
    }

    [Fact]
    public async Task StripeRefundsAndDisputesAreRecordedForFinanceAndChangeNothingByThemselves()
    {
        var reference = await StripeDonationAsync();
        await PostStripeAsync(SessionDone(reference));

        var refund = StripeEvent("charge.refunded", new { id = "ch_1", payment_intent = "pi_1", amount_refunded = 2500, currency = "usd", metadata = new { reference } }, "evt_refund");
        var dispute = StripeEvent("charge.dispute.created", new { id = "dp_1", payment_intent = "pi_1", amount = 2500, currency = "usd" }, "evt_dispute");

        (await PostStripeAsync(refund)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostStripeAsync(refund)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostStripeAsync(dispute)).StatusCode.Should().Be(HttpStatusCode.OK);

        var notices = await DbAsync(db => db.ProviderNotices.AsNoTracking().OrderBy(n => n.Id).ToListAsync());
        notices.Select(n => (n.Kind, n.Amount, n.Currency, n.TransactionId)).Should().Equal(("refund", 2500m / 100m, "USD", "pi_1"), ("dispute", 25.00m, "USD", "pi_1"));
        (await StatusOfAsync(reference)).Should().Be("confirmed", "a provider notice never reverses anything by itself");
        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(1);
        _factory.Logs.Lines.Should().Contain(l => l.Contains("finance should review", StringComparison.Ordinal));

        using var list = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/finance/provider-notices?kind=dispute");
        list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue([Permissions.FinanceView]));
        var page = await (await _client.SendAsync(list)).Content.ReadFromJsonAsync<JsonElement>();
        (page.GetProperty("total").GetInt32(), page.GetProperty("items")[0].GetProperty("reference").GetString()).Should().Be((1, reference));
        (await _client.GetAsync("/api/v1/admin/finance/provider-notices")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ARefundNoticeForAnUnknownPaymentOrWithABadSignatureIsRejected()
    {
        var unknown = StripeEvent("charge.refunded", new { id = "ch_x", payment_intent = "pi_nope", amount_refunded = 100, currency = "usd", metadata = new { reference = "KJD-0000000000" } }, "evt_x");

        (await PostStripeAsync(unknown)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PostStripeAsync(unknown, StripeSignature(unknown, secret: "whsec_other"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await RejectionsAsync("UNKNOWN_PAYMENT")).Should().Be(1);
        (await RejectionsAsync("BAD_SIGNATURE")).Should().Be(1);
        (await DbAsync(db => db.ProviderNotices.CountAsync())).Should().Be(0);
    }

    // ================= PayPal =================

    [Fact]
    public async Task PayPalCreatesAnOrderAndSendsTheDonorToTheApprovalLink()
    {
        var created = await DonateAsync("25.00", "USD", "paypal", coverFees: true);

        created.GetProperty("gateway").GetString().Should().Be("paypal");
        var reference = created.GetProperty("reference").GetString()!;
        created.GetProperty("checkoutUrl").GetString().Should().Be($"https://www.sandbox.paypal.com/checkoutnow?token=ORD-{reference}");
        var order = _handler.Requests.Single(r => r.Url.AbsolutePath.EndsWith("/v2/checkout/orders", StringComparison.Ordinal)).Json;
        var unit = order.GetProperty("purchase_units")[0];
        (unit.GetProperty("amount").GetProperty("value").GetString(), unit.GetProperty("amount").GetProperty("currency_code").GetString(), unit.GetProperty("custom_id").GetString())
            .Should().Be(("26.02", "USD", reference)); // ceil(25 / 0.961 = 26.0146)
        var context = order.GetProperty("payment_source").GetProperty("paypal").GetProperty("experience_context");
        context.GetProperty("return_url").GetString().Should().Be("https://api.example.org/api/v1/donations/webhooks/paypal");
        context.GetProperty("cancel_url").GetString().Should().EndWith("result=cancel");
    }

    [Fact]
    public async Task PayPalGrantsOneTokenAndReusesIt()
    {
        await DonateAsync("25.00", "USD", "paypal");
        await DonateAsync("30.00", "USD", "paypal");

        _ppTokenCalls.Should().Be(1);
    }

    [Fact]
    public async Task TheDonorReturningFromPayPalTriggersCaptureThenConfirmationWithSettlement()
    {
        var reference = await PayPalDonationAsync();
        _noRedirect = _factory.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await _noRedirect.GetAsync($"/api/v1/donations/webhooks/paypal?token=ORD-{reference}&PayerID=PAYER1");

        response.StatusCode.Should().Be(HttpStatusCode.SeeOther);
        response.Headers.Location!.ToString().Should().Be($"https://site.example.org/en/donate/status?ref={reference}&gateway=paypal&result=return");
        (await StatusOfAsync(reference)).Should().Be("confirmed");
        _handler.Requests.Count(r => r.Url.AbsolutePath.EndsWith("/capture", StringComparison.Ordinal)).Should().Be(1);
        var settlement = await DbAsync(db => db.PaymentSettlements.AsNoTracking().SingleAsync());
        (settlement.SettledAmount, settlement.SettledCurrency, settlement.Fee, settlement.ExchangeRate, settlement.TransactionId).Should().Be((21.10m, "EUR", 1.05m, 0.9m, "CAP-1"));
        (await DbAsync(db => db.Donations.AsNoTracking().SingleAsync())).Should().Match<DonationEntity>(d => d.Amount == 25.00m && d.Currency == "USD");
        (await DbAsync(db => db.Postings.Include(p => p.Entries).AsNoTracking().SingleAsync())).Entries.Should().Contain(e => e.Debit == 25.00m && e.Currency == "USD");
    }

    [Fact]
    public async Task AForgedPayPalReturnForAnOrderThePayerNeverApprovedCapturesNothing()
    {
        var reference = await PayPalDonationAsync();
        _ppOrderStatus = "CREATED";

        _noRedirect = _factory.CreateClient(new() { AllowAutoRedirect = false });
        using var forged = await _noRedirect.GetAsync($"/api/v1/donations/webhooks/paypal?token=ORD-{reference}");
        using var unknown = await _client.GetAsync("/api/v1/donations/webhooks/paypal?token=ORD-NOPE");
        using var noToken = await _client.GetAsync("/api/v1/donations/webhooks/paypal");

        (await StatusOfAsync(reference)).Should().Be("pending");
        _handler.Requests.Should().NotContain(r => r.Url.AbsolutePath.EndsWith("/capture", StringComparison.Ordinal));
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noToken.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        forged.StatusCode.Should().Be(HttpStatusCode.SeeOther, "the donor is sent on to the status page, which shows it pending");
    }

    [Fact]
    public async Task APayPalWebhookIsVerifiedByPayPalWithTheRawEventThenCaptured()
    {
        var reference = await PayPalDonationAsync();
        var raw = Approved(reference);

        using var response = await PostPayPalAsync(raw);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(reference)).Should().Be("confirmed");
        var check = _handler.Requests.Single(r => r.Url.AbsolutePath.EndsWith("/verify-webhook-signature", StringComparison.Ordinal));
        check.Body.Should().Contain(raw, "the event is sent to PayPal exactly as received");
        var json = check.Json;
        (json.GetProperty("webhook_id").GetString(), json.GetProperty("transmission_id").GetString(), json.GetProperty("auth_algo").GetString()).Should().Be(("WH-1", "tx-1", "SHA256withRSA"));
    }

    [Fact]
    public async Task APayPalWebhookThatPayPalDoesNotVouchForIsRejectedAndCapturesNothing()
    {
        var reference = await PayPalDonationAsync();
        _ppSignatureOk = false;

        using var bad = await PostPayPalAsync(Approved(reference));
        using var noHeaders = await PostPayPalAsync(Approved(reference, "WH-EVT-2"), headers: false);

        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noHeaders.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StatusOfAsync(reference)).Should().Be("pending");
        (await RejectionsAsync("BAD_SIGNATURE")).Should().Be(2);
        _handler.Requests.Should().NotContain(r => r.Url.AbsolutePath.EndsWith("/capture", StringComparison.Ordinal));
        _factory.Logs.Lines.Should().NotContain(l => l.Contains(PayPalSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task IfPayPalCannotCheckTheSignatureTheWebhookIsRetriedNotRejected()
    {
        var reference = await PayPalDonationAsync();
        _ppVerifyDown = true;

        using var response = await PostPayPalAsync(Approved(reference));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await RejectionsAsync()).Should().Be(0);
        _ppVerifyDown = false;
        (await PostPayPalAsync(Approved(reference))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(reference)).Should().Be("confirmed");
    }

    [Fact]
    public async Task ReplayingAPayPalWebhookTenTimesCapturesAndPostsOnce()
    {
        var reference = await PayPalDonationAsync();
        var raw = Approved(reference);

        for (var i = 0; i < 10; i++)
        {
            (await PostPayPalAsync(raw)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(1);
        (await DbAsync(db => db.PaymentSettlements.CountAsync())).Should().Be(1);
        _handler.Requests.Count(r => r.Url.AbsolutePath.EndsWith("/capture", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task AWrongCapturedAmountOrADeclinedOrVoidedOrderIsHandledSafely()
    {
        var mismatch = await PayPalDonationAsync();
        _ppAmount = "2.50";
        (await PostPayPalAsync(Approved(mismatch, "E1"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StatusOfAsync(mismatch)).Should().Be("pending");

        var declined = await PayPalDonationAsync();
        (_ppAmount, _ppCaptureStatus) = (null, "DECLINED");
        (await PostPayPalAsync(Approved(declined, "E2"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(declined)).Should().Be("failed");

        var voided = await PayPalDonationAsync();
        (_ppOrderStatus, _ppCaptureStatus) = ("VOIDED", "COMPLETED");
        (await PostPayPalAsync(Approved(voided, "E3"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(voided)).Should().Be("cancelled");
        (await RejectionsAsync("AMOUNT_MISMATCH")).Should().Be(1);
    }

    [Fact]
    public async Task PayPalRefundAndDisputeMessagesAreRecordedViaTheSettlementTransaction()
    {
        var reference = await PayPalDonationAsync();
        await PostPayPalAsync(Approved(reference));

        var refund = PayPalEvent("PAYMENT.CAPTURE.REFUNDED", new { id = "REF-1", amount = new { currency_code = "USD", value = "25.00" }, links = new[] { new { rel = "up", href = "https://api.paypal.com/v2/payments/captures/CAP-1" } } }, "EVT-REFUND");
        var dispute = PayPalEvent("CUSTOMER.DISPUTE.CREATED", new { dispute_amount = new { currency_code = "USD", value = "25.00" }, disputed_transactions = new[] { new { seller_transaction_id = "CAP-1" } } }, "EVT-DISPUTE");

        (await PostPayPalAsync(refund)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostPayPalAsync(dispute)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostPayPalAsync(dispute)).StatusCode.Should().Be(HttpStatusCode.OK);

        var notices = await DbAsync(db => db.ProviderNotices.AsNoTracking().OrderBy(n => n.Id).ToListAsync());
        notices.Select(n => (n.Gateway, n.Kind, n.TransactionId, n.Amount)).Should().Equal(("paypal", "refund", "CAP-1", 25.00m), ("paypal", "dispute", "CAP-1", 25.00m));
        (await StatusOfAsync(reference)).Should().Be("confirmed");
    }

    [Fact]
    public async Task UnrelatedPayPalEventsAreAcknowledged()
    {
        using var response = await PostPayPalAsync(PayPalEvent("BILLING.PLAN.CREATED", new { id = "P-1" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RejectionsAsync()).Should().Be(0);
    }
}
