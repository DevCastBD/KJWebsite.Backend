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

public sealed class FinanceWebhookTests : IDisposable
{
    private const string StorePassword = "ssl-secret-PASSWORD-9";
    private static readonly string Viewer = TestTokens.Issue([Permissions.FinanceView]);
    private static readonly string Reconciler = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceReconcile]);

    private readonly NagadDouble _nagad = new();
    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly ScriptedGatewayHandler _handler;
    private HttpClient? _noRedirect;

    // What the fake gateways currently say about a payment; tests change these.
    private string _sslStatus = "VALID";
    private string? _sslAmount;
    private string _sslCurrency = "BDT";
    private bool _sslDown;
    private string _bkashState = "Authorized";
    private string? _bkashAmount;
    private string _nagadStatus = "Success";
    private string? _nagadAmount;
    private int _sslVerifyCalls;

    public FinanceWebhookTests(int reconcileAfterMinutes = 0)
    {
        _handler = new ScriptedGatewayHandler(Respond);
        var s = _factory.Settings;
        s["Finance:Payments:PublicApiBaseUrl"] = "https://api.example.org";
        s["Finance:Payments:SiteBaseUrl"] = "https://site.example.org";
        s["Finance:Payments:ReconcileAfterMinutes"] = reconcileAfterMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        s["Finance:Payments:ReconcileIntervalMinutes"] = "0";
        s["Finance:Payments:SslCommerz:Enabled"] = "true";
        s["Finance:Payments:SslCommerz:StoreId"] = "kjtest001";
        s["Finance:Payments:SslCommerz:StorePassword"] = StorePassword;
        s["Finance:Payments:Bkash:Enabled"] = "true";
        s["Finance:Payments:Bkash:Username"] = "bk-user";
        s["Finance:Payments:Bkash:Password"] = "bk-pass";
        s["Finance:Payments:Bkash:AppKey"] = "bk-key";
        s["Finance:Payments:Bkash:AppSecret"] = "bk-secret";
        s["Finance:Payments:Nagad:Enabled"] = "true";
        s["Finance:Payments:Nagad:MerchantId"] = "m1";
        s["Finance:Payments:Nagad:MerchantPrivateKey"] = _nagad.Merchant.ExportPkcs8PrivateKeyPem();
        s["Finance:Payments:Nagad:PgPublicKey"] = _nagad.Gateway.ExportSubjectPublicKeyInfoPem();
        _factory.GatewayHandler = _handler;
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _noRedirect?.Dispose();
        _factory.Dispose();
        _nagad.Dispose();
    }

    private HttpResponseMessage Respond(RecordedRequest request)
    {
        switch (request.Url.Host)
        {
            case "sandbox.sslcommerz.com":
                if (request.Url.AbsolutePath.EndsWith("api.php", StringComparison.Ordinal))
                {
                    return ScriptedGatewayHandler.Json(new { status = "SUCCESS", sessionkey = "SK-" + request.Form["tran_id"], GatewayPageURL = "https://sandbox.sslcommerz.com/EasyCheckOut/x" });
                }

                _sslVerifyCalls++;
                if (_sslDown)
                {
                    throw new HttpRequestException("down");
                }

                var tran = System.Web.HttpUtility.ParseQueryString(request.Url.Query)["tran_id"]!;
                request.Url.Query.Should().Contain("store_id=kjtest001").And.Contain("format=json");
                return ScriptedGatewayHandler.Json(new { APIConnect = "DONE", no_of_trans_found = 1, element = new[] { new { status = _sslStatus, tran_id = tran, amount = _sslAmount ?? "1025.65", currency_type = _sslCurrency } } });

            case "tokenized.sandbox.bka.sh":
                var path = request.Url.AbsolutePath;
                if (path.EndsWith("/token/grant", StringComparison.Ordinal))
                {
                    return ScriptedGatewayHandler.Json(new { id_token = "tok", expires_in = 3600, statusCode = "0000" });
                }

                if (path.EndsWith("/create", StringComparison.Ordinal))
                {
                    return ScriptedGatewayHandler.Json(new { statusCode = "0000", paymentID = "BKP-" + request.Json.GetProperty("merchantInvoiceNumber").GetString(), bkashURL = "https://sandbox.payment.bkash.com/x" });
                }

                var payment = request.Json.GetProperty("paymentID").GetString()!;
                var invoice = payment["BKP-".Length..];
                if (path.EndsWith("/payment/status", StringComparison.Ordinal))
                {
                    return ScriptedGatewayHandler.Json(new { statusCode = "0000", transactionStatus = _bkashState, merchantInvoiceNumber = invoice, amount = _bkashAmount ?? "500.00", currency = "BDT" });
                }

                path.Should().EndWith("/execute");
                _bkashState = "Completed";
                return ScriptedGatewayHandler.Json(new { statusCode = "0000", transactionStatus = "Completed", merchantInvoiceNumber = invoice, amount = _bkashAmount ?? "500.00", currency = "BDT" });

            default:
                if (request.Url.AbsolutePath.Contains("/verify/payment/", StringComparison.Ordinal))
                {
                    return ScriptedGatewayHandler.Json(new { status = _nagadStatus, orderId = _nagadOrder, amount = _nagadAmount ?? "700.00" });
                }

                var reply = _nagad.Respond(request);
                if (request.Url.AbsolutePath.Contains("/initialize/", StringComparison.Ordinal))
                {
                    _nagadOrder = _nagad.Initialized[^1].GetProperty("orderId").GetString()!;
                }

                return reply;
        }
    }

    private string _nagadOrder = string.Empty;

    private async Task<JsonElement> DonateAsync(string amount, string gateway, bool coverFees = false)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/donations", new
        {
            amount,
            currency = "BDT",
            frequency = "once",
            designation = new { type = "general" },
            donor = new { name = "A Donor", email = "d@example.org" },
            anonymous = false,
            coverFees,
            language = "bn",
            gateway
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<T> DbAsync<T>(Func<FinanceDbContext, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<FinanceDbContext>());
    }

#pragma warning disable CA5351 // SSLCommerz defines its IPN signature as MD5; this test mirrors it.
    private static string Md5(string value) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
#pragma warning restore CA5351

    // The SSLCommerz IPN signature, written out independently of the adapter.
    private static Dictionary<string, string> Ipn(string reference, string status = "VALID", string valId = "VAL-1", string amount = "1025.65", string? password = null, string? tamperAfterSigning = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["tran_id"] = reference,
            ["val_id"] = valId,
            ["status"] = status,
            ["amount"] = amount,
            ["currency"] = "BDT",
            ["bank_tran_id"] = "BANK1",
            ["store_id"] = "kjtest001"
        };
        const string keyList = "tran_id,val_id,status,amount,currency,bank_tran_id,store_id";
        var signed = fields.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}={f.Value}").Append($"store_passwd={Md5(password ?? StorePassword)}").OrderBy(x => x, StringComparer.Ordinal).ToList();
        fields["verify_key"] = keyList;
        fields["verify_sign"] = Md5(string.Join("&", signed));
        if (tamperAfterSigning is not null)
        {
            fields["amount"] = tamperAfterSigning;
        }

        return fields;
    }

    private async Task<HttpResponseMessage> PostIpnAsync(Dictionary<string, string> fields, string gateway = "sslcommerz") =>
        await _client.PostAsync($"/api/v1/donations/webhooks/{gateway}", new FormUrlEncodedContent(fields));

    private async Task<HttpResponseMessage> GetAdminAsync(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private async Task<JsonElement> AdminJsonAsync(string path, string? token = null)
    {
        using var response = await GetAdminAsync(path, token ?? Viewer);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpResponseMessage> PostAdminAsync(string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private async Task<string> StatusOfAsync(string reference) =>
        (await _client.GetFromJsonAsync<JsonElement>($"/api/v1/donations/{reference}/status")).GetProperty("status").GetString()!;

    private Task<int> RejectionCountAsync(string? reason = null) => DbAsync(db => db.WebhookRejections.CountAsync(r => reason == null || r.ReasonCode == reason));

    [Fact]
    public async Task ASignedIpnConfirmsOnlyAfterTheGatewayItselfSaysPaid()
    {
        var created = await DonateAsync("1000.00", "sslcommerz", coverFees: true);
        var reference = created.GetProperty("reference").GetString()!;

        using var response = await PostIpnAsync(Ipn(reference));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(reference)).Should().Be("confirmed");
        _sslVerifyCalls.Should().Be(1, "the status was re-queried at the gateway, not taken from the IPN");
        var postings = await DbAsync(db => db.Postings.Include(p => p.Entries).AsNoTracking().ToListAsync());
        postings.Should().ContainSingle().Which.Entries.Should().Contain(e => e.Debit == 1025.65m);
        (await DbAsync(db => db.PaymentEvents.AsNoTracking().SingleAsync())).Should().Match<PaymentEventEntity>(e => e.Gateway == "sslcommerz" && e.Outcome == "confirmed" && e.EventKey == "VAL-1:VALID");
    }

    [Fact]
    public async Task ReplayingAWebhookTenTimesMakesOneConfirmationAndOnePosting()
    {
        var reference = (await DonateAsync("1000.00", "sslcommerz", coverFees: true)).GetProperty("reference").GetString()!;
        var ipn = Ipn(reference);

        for (var i = 0; i < 10; i++)
        {
            using var response = await PostIpnAsync(ipn);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(1);
        (await DbAsync(db => db.DonationEvents.CountAsync(e => e.Status == "confirmed"))).Should().Be(1);
        (await DbAsync(db => db.PaymentEvents.CountAsync())).Should().Be(1);
        _sslVerifyCalls.Should().Be(1, "replays are recognised by their key and do not hammer the gateway");
    }

    [Fact]
    public async Task ARedeliveryUnderAnotherEventKeyStillPostsOnce()
    {
        var reference = (await DonateAsync("1000.00", "sslcommerz", coverFees: true)).GetProperty("reference").GetString()!;

        for (var i = 0; i < 5; i++)
        {
            (await PostIpnAsync(Ipn(reference, valId: $"VAL-{i}"))).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(1);
        (await DbAsync(db => db.DonationEvents.CountAsync(e => e.Status == "confirmed"))).Should().Be(1);
        (await DbAsync(db => db.PaymentEvents.CountAsync())).Should().Be(5);
        (await DbAsync(db => db.PaymentEvents.CountAsync(e => e.Outcome == "already_confirmed"))).Should().Be(4);
    }

    [Theory]
    [InlineData("wrong-password")]
    [InlineData("tampered-amount")]
    [InlineData("missing-sign")]
    [InlineData("missing-key")]
    [InlineData("signed-field-absent")]
    [InlineData("no-form")]
    public async Task UnsignedOrBadlySignedWebhooksAreRejectedAndRecordedAndCostNothing(string problem)
    {
        var reference = (await DonateAsync("1000.00", "sslcommerz", coverFees: true)).GetProperty("reference").GetString()!;
        var fields = problem switch
        {
            "wrong-password" => Ipn(reference, password: "not-the-password"),
            "tampered-amount" => Ipn(reference, tamperAfterSigning: "1.00"),
            "missing-sign" => Ipn(reference).Where(f => f.Key != "verify_sign").ToDictionary(f => f.Key, f => f.Value),
            "missing-key" => Ipn(reference).Where(f => f.Key != "verify_key").ToDictionary(f => f.Key, f => f.Value),
            "signed-field-absent" => Ipn(reference).Where(f => f.Key != "bank_tran_id").ToDictionary(f => f.Key, f => f.Value),
            _ => []
        };

        using var response = await PostIpnAsync(fields);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StatusOfAsync(reference)).Should().Be("pending");
        _sslVerifyCalls.Should().Be(0, "nothing is asked of the gateway for a call that is not authentic");
        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(0);
        var rejection = await DbAsync(db => db.WebhookRejections.AsNoTracking().SingleAsync());
        (rejection.Gateway, rejection.ReasonCode).Should().Be(("sslcommerz", "BAD_SIGNATURE"));
        _factory.Logs.Lines.Should().Contain(l => l.Contains("BAD_SIGNATURE", StringComparison.Ordinal) && l.Contains("sslcommerz", StringComparison.Ordinal));
        _factory.Logs.Lines.Should().NotContain(l => l.Contains(StorePassword, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AValidSignatureForAnUnknownPaymentIsRejected()
    {
        using var response = await PostIpnAsync(Ipn("KJD-0000000000"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RejectionCountAsync("UNKNOWN_PAYMENT")).Should().Be(1);
        _sslVerifyCalls.Should().Be(0);
    }

    [Fact]
    public async Task APaidAmountThatDiffersFromTheChargeConfirmsNothing()
    {
        var reference = (await DonateAsync("1000.00", "sslcommerz", coverFees: true)).GetProperty("reference").GetString()!;
        _sslAmount = "1000.00"; // the donor covered the fee, so 1025.65 was due

        using var response = await PostIpnAsync(Ipn(reference));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StatusOfAsync(reference)).Should().Be("pending");
        (await RejectionCountAsync("AMOUNT_MISMATCH")).Should().Be(1);
        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(0);

        _sslCurrency = "USD";
        _sslAmount = "1025.65";
        (await PostIpnAsync(Ipn(reference, valId: "VAL-2"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RejectionCountAsync("AMOUNT_MISMATCH")).Should().Be(2, "the right number in the wrong currency is also a mismatch");
    }

    [Fact]
    public async Task AFailedPaymentIsRecordedAndALaterSuccessStillConfirms()
    {
        var reference = (await DonateAsync("1000.00", "sslcommerz", coverFees: true)).GetProperty("reference").GetString()!;
        _sslStatus = "FAILED";
        (await PostIpnAsync(Ipn(reference, "FAILED", "VAL-F"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(reference)).Should().Be("failed");

        _sslStatus = "VALID";
        (await PostIpnAsync(Ipn(reference, valId: "VAL-OK"))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await StatusOfAsync(reference)).Should().Be("confirmed");
        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task ALateFailureNeverUndoesAConfirmedDonation()
    {
        var reference = (await DonateAsync("1000.00", "sslcommerz", coverFees: true)).GetProperty("reference").GetString()!;
        (await PostIpnAsync(Ipn(reference))).StatusCode.Should().Be(HttpStatusCode.OK);

        _sslStatus = "FAILED";
        (await PostIpnAsync(Ipn(reference, "FAILED", "VAL-LATE"))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await StatusOfAsync(reference)).Should().Be("confirmed");
        (await DbAsync(db => db.DonationEvents.CountAsync(e => e.Status == "failed"))).Should().Be(0);
    }

    [Fact]
    public async Task ATimeoutAtTheGatewayLeavesTheDonationPendingAndARedeliveryCompletesIt()
    {
        var reference = (await DonateAsync("1000.00", "sslcommerz", coverFees: true)).GetProperty("reference").GetString()!;
        _sslDown = true;

        using var first = await PostIpnAsync(Ipn(reference));

        first.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        first.Headers.RetryAfter.Should().NotBeNull();
        (await StatusOfAsync(reference)).Should().Be("pending");
        (await DbAsync(db => db.PaymentEvents.CountAsync())).Should().Be(0, "an undecided delivery is not remembered, so the redelivery is looked at");
        (await RejectionCountAsync()).Should().Be(0, "a gateway outage is not a hostile call");

        _sslDown = false;
        (await PostIpnAsync(Ipn(reference))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(reference)).Should().Be("confirmed");
    }

    [Fact]
    public async Task ABkashCallbackExecutesTheAuthorizedPaymentThenConfirmsAndRedirectsTheDonor()
    {
        var created = await DonateAsync("500.00", "bkash");
        var reference = created.GetProperty("reference").GetString()!;
        _noRedirect = _factory.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await _noRedirect.GetAsync($"/api/v1/donations/webhooks/bkash?paymentID=BKP-{reference}&status=success");

        response.StatusCode.Should().Be(HttpStatusCode.SeeOther);
        response.Headers.Location!.ToString().Should().Be($"https://site.example.org/bn/donate/status?ref={reference}&gateway=bkash&result=return");
        (await StatusOfAsync(reference)).Should().Be("confirmed");
        _handler.Requests.Should().Contain(r => r.Url.AbsolutePath.EndsWith("/execute", StringComparison.Ordinal));
        (await DbAsync(db => db.Postings.Include(p => p.Entries).AsNoTracking().SingleAsync())).Entries.Should().Contain(e => e.Debit == 500.00m);
    }

    [Fact]
    public async Task AForgedBkashCallbackThatTheGatewayDoesNotBackChangesNothing()
    {
        var reference = (await DonateAsync("500.00", "bkash")).GetProperty("reference").GetString()!;
        _bkashState = "Initiated";
        _noRedirect = _factory.CreateClient(new() { AllowAutoRedirect = false });

        using var forged = await _noRedirect.GetAsync($"/api/v1/donations/webhooks/bkash?paymentID=BKP-{reference}&status=success");
        using var unknown = await _noRedirect.GetAsync("/api/v1/donations/webhooks/bkash?paymentID=BKP-NOPE&status=success");
        using var noId = await _noRedirect.GetAsync("/api/v1/donations/webhooks/bkash?status=success");

        forged.StatusCode.Should().Be(HttpStatusCode.SeeOther);
        (await StatusOfAsync(reference)).Should().Be("pending", "bKash says the payment was only initiated");
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noId.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RejectionCountAsync("UNKNOWN_PAYMENT")).Should().Be(1);
        (await RejectionCountAsync("BAD_SIGNATURE")).Should().Be(1);
        _handler.Requests.Should().NotContain(r => r.Url.AbsolutePath.EndsWith("/execute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABkashPaymentForTheWrongAmountIsRejected()
    {
        var reference = (await DonateAsync("500.00", "bkash")).GetProperty("reference").GetString()!;
        _bkashAmount = "5.00";

        using var response = await _client.GetAsync($"/api/v1/donations/webhooks/bkash?paymentID=BKP-{reference}&status=success");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StatusOfAsync(reference)).Should().Be("pending");
        (await RejectionCountAsync("AMOUNT_MISMATCH")).Should().Be(1);
    }

    [Theory]
    [InlineData("Success", "confirmed")]
    [InlineData("Aborted", "cancelled")]
    [InlineData("Failed", "failed")]
    [InlineData("Pending", "pending")]
    public async Task ANagadCallbackIsDecidedByNagadsVerification(string nagadStatus, string expected)
    {
        var reference = (await DonateAsync("700.00", "nagad")).GetProperty("reference").GetString()!;
        _nagadStatus = nagadStatus;
        _noRedirect = _factory.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await _noRedirect.GetAsync("/api/v1/donations/webhooks/nagad?payment_ref_id=PR-NAGAD-1&status=Success");

        response.StatusCode.Should().Be(HttpStatusCode.SeeOther);
        (await StatusOfAsync(reference)).Should().Be(expected);
    }

    [Fact]
    public async Task ANagadPaymentForTheWrongAmountIsRejected()
    {
        var reference = (await DonateAsync("700.00", "nagad")).GetProperty("reference").GetString()!;
        _nagadAmount = "7.00";

        using var response = await _client.GetAsync("/api/v1/donations/webhooks/nagad?payment_ref_id=PR-NAGAD-1&status=Success");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StatusOfAsync(reference)).Should().Be("pending");
        (await RejectionCountAsync("AMOUNT_MISMATCH")).Should().Be(1);
    }

    [Theory]
    [InlineData("stripe")]
    [InlineData("nonsense")]
    public async Task WebhooksForUnknownOrSwitchedOffGatewaysAreRejected(string gateway)
    {
        using var response = await _client.PostAsync($"/api/v1/donations/webhooks/{gateway}", new FormUrlEncodedContent(new Dictionary<string, string> { ["a"] = "b" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RejectionCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TheRejectionListIsForFinanceStaffAndFilters()
    {
        await PostIpnAsync(Ipn("KJD-0000000000"));
        await PostIpnAsync(Ipn("KJD-0000000000", password: "bad"));

        (await _client.GetAsync("/api/v1/admin/finance/webhook-rejections")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await GetAdminAsync("/api/v1/admin/finance/webhook-rejections", TestTokens.Issue([Permissions.ContentView]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var all = await AdminJsonAsync("/api/v1/admin/finance/webhook-rejections");
        all.GetProperty("total").GetInt32().Should().Be(2);
        all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("reasonCode").GetString()).Should().BeEquivalentTo(["BAD_SIGNATURE", "UNKNOWN_PAYMENT"]);
        (await AdminJsonAsync("/api/v1/admin/finance/webhook-rejections?reason=BAD_SIGNATURE")).GetProperty("total").GetInt32().Should().Be(1);
        (await AdminJsonAsync("/api/v1/admin/finance/webhook-rejections?gateway=bkash")).GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task TheResolverSettlesStalePendingCheckoutsAndLeavesUndecidedOnesPending()
    {
        var paid = (await DonateAsync("1000.00", "sslcommerz", coverFees: true)).GetProperty("reference").GetString()!;
        var bkash = (await DonateAsync("500.00", "bkash")).GetProperty("reference").GetString()!;
        var nagad = (await DonateAsync("700.00", "nagad")).GetProperty("reference").GetString()!;
        _nagadStatus = "Pending";
        _bkashState = "Initiated";

        (await PostAdminAsync("/api/v1/admin/finance/reconciliation/pending/run", Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var first = await PostAdminAsync("/api/v1/admin/finance/reconciliation/pending/run", Reconciler);
        var summary = await first.Content.ReadFromJsonAsync<JsonElement>();

        (summary.GetProperty("checked").GetInt32(), summary.GetProperty("confirmed").GetInt32(), summary.GetProperty("stillPending").GetInt32()).Should().Be((3, 1, 2));
        (await StatusOfAsync(paid)).Should().Be("confirmed");
        (await StatusOfAsync(bkash)).Should().Be("pending");
        (await StatusOfAsync(nagad)).Should().Be("pending");

        _bkashState = "Authorized";
        _nagadStatus = "Failed";
        using var second = await PostAdminAsync("/api/v1/admin/finance/reconciliation/pending/run", Reconciler);
        var again = await second.Content.ReadFromJsonAsync<JsonElement>();
        (again.GetProperty("checked").GetInt32(), again.GetProperty("confirmed").GetInt32(), again.GetProperty("failed").GetInt32()).Should().Be((2, 1, 1));
        (await StatusOfAsync(bkash)).Should().Be("confirmed");
        (await StatusOfAsync(nagad)).Should().Be("failed");

        using var third = await PostAdminAsync("/api/v1/admin/finance/reconciliation/pending/run", Reconciler);
        (await third.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("checked").GetInt32().Should().Be(0, "settled donations are not asked about again");
        (await DbAsync(db => db.Postings.CountAsync())).Should().Be(2);
    }

    [Fact]
    public async Task AGatewayTimeoutDuringReconciliationLeavesTheDonationPending()
    {
        var reference = (await DonateAsync("1000.00", "sslcommerz", coverFees: true)).GetProperty("reference").GetString()!;
        _sslDown = true;

        using var response = await PostAdminAsync("/api/v1/admin/finance/reconciliation/pending/run", Reconciler);

        var summary = await response.Content.ReadFromJsonAsync<JsonElement>();
        (summary.GetProperty("errors").GetInt32(), summary.GetProperty("confirmed").GetInt32()).Should().Be((1, 0));
        (await StatusOfAsync(reference)).Should().Be("pending");

        _sslDown = false;
        await PostAdminAsync("/api/v1/admin/finance/reconciliation/pending/run", Reconciler);
        (await StatusOfAsync(reference)).Should().Be("confirmed");
    }

    [Fact]
    public async Task ResolverMismatchesAreRejectedLikeWebhookOnes()
    {
        await DonateAsync("1000.00", "sslcommerz", coverFees: true);
        _sslAmount = "1.00";

        using var response = await PostAdminAsync("/api/v1/admin/finance/reconciliation/pending/run", Reconciler);

        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mismatched").GetInt32().Should().Be(1);
        (await DbAsync(db => db.WebhookRejections.AsNoTracking().SingleAsync())).Should().Match<WebhookRejectionEntity>(r => r.ReasonCode == "AMOUNT_MISMATCH" && r.SourceIp == "reconciliation");
    }

    private static object Payout(string payoutRef, params object[] lines) => new { gateway = "sslcommerz", payoutRef, paidOn = (string?)null, lines };

    private static object Line(string reference, string gross = "1025.65", string fee = "25.65", string net = "1000.00", string currency = "BDT") => new { reference, gross, fee, net, currency };

    private async Task<string> ConfirmedSslAsync(string amount = "1000.00")
    {
        var reference = (await DonateAsync(amount, "sslcommerz", coverFees: true)).GetProperty("reference").GetString()!;
        (await PostIpnAsync(Ipn(reference, valId: "V" + Guid.NewGuid().ToString("N")[..8]))).StatusCode.Should().Be(HttpStatusCode.OK);
        return reference;
    }

    [Fact]
    public async Task APayoutMatchesConfirmedDonationsAndPostsTheSettlement()
    {
        var reference = await ConfirmedSslAsync();
        var unpaidBefore = await AdminJsonAsync("/api/v1/admin/finance/reconciliation/unpaid?gateway=sslcommerz");
        unpaidBefore.GetProperty("total").GetInt32().Should().Be(1);
        unpaidBefore.GetProperty("items")[0].GetProperty("charge").GetString().Should().Be("1025.65");

        using var response = await PostAdminAsync("/api/v1/admin/finance/payouts", Reconciler, Payout("SETTLE-1", Line(reference)));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        view.GetProperty("counts").GetProperty("matched").GetInt32().Should().Be(1);
        view.GetProperty("lines")[0].GetProperty("postingId").GetString().Should().NotBeNull();

        var settlement = (await DbAsync(db => db.Postings.Include(p => p.Entries).ThenInclude(e => e.Account).AsNoTracking().Where(p => p.SourceType == "payout").ToListAsync())).Single();
        settlement.Entries.Select(e => (e.Account!.Code, e.Debit, e.Credit)).Should().BeEquivalentTo([("1020", 1000.00m, 0m), ("5040", 25.65m, 0m), ("1110", 0m, 1025.65m)]);
        var balance = await AdminJsonAsync("/api/v1/admin/finance/ledger/trial-balance");
        balance.GetProperty("currencies")[0].GetProperty("balanced").GetBoolean().Should().BeTrue();
        var clearing = balance.GetProperty("currencies")[0].GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("code").GetString() == "1110");
        clearing.GetProperty("balance").GetString().Should().Be("0.00", "the gateway's clearing account is cleared by the payout");
        (await AdminJsonAsync("/api/v1/admin/finance/reconciliation/unpaid")).GetProperty("total").GetInt32().Should().Be(0);
        (await AdminJsonAsync("/api/v1/admin/finance/payouts")).GetProperty("total").GetInt32().Should().Be(1);
        (await AdminJsonAsync($"/api/v1/admin/finance/payouts/{view.GetProperty("id").GetString()}")).GetProperty("payoutRef").GetString().Should().Be("SETTLE-1");
        (await GetAdminAsync("/api/v1/admin/finance/payouts/999999", Viewer)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EveryKindOfLineIsClassifiedNeverForcedToMatch()
    {
        var good = await ConfirmedSslAsync();
        var mismatch = await ConfirmedSslAsync();
        var pending = (await DonateAsync("300.00", "sslcommerz")).GetProperty("reference").GetString()!;
        var bkash = (await DonateAsync("500.00", "bkash")).GetProperty("reference").GetString()!;

        using var response = await PostAdminAsync("/api/v1/admin/finance/payouts", Reconciler, Payout("MIX-1",
            Line(good), Line(mismatch, "1000.00", "20.00", "980.00"), Line("KJD-ZZZZZZZZZZ"), Line(pending, "300.00", "5.00", "295.00"), Line(bkash, "500.00", "10.00", "490.00"), Line(good)));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var lines = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lines").EnumerateArray().Select(l => (l.GetProperty("matchStatus").GetString(), l.GetProperty("reason").ValueKind == JsonValueKind.Null ? null : l.GetProperty("reason").GetString())).ToList();
        lines.Should().Equal(("matched", null), ("amount_mismatch", "AMOUNT_MISMATCH"), ("unmatched", "UNKNOWN_PAYMENT"), ("unmatched", "NOT_CONFIRMED"), ("unmatched", "UNKNOWN_PAYMENT"), ("duplicate", "ALREADY_SETTLED"));
        (await DbAsync(db => db.Postings.CountAsync(p => p.SourceType == "payout"))).Should().Be(1, "only the matched line posts");
    }

    [Fact]
    public async Task ALaterPayoutCannotSettleTheSameDonationAgain()
    {
        var reference = await ConfirmedSslAsync();
        await PostAdminAsync("/api/v1/admin/finance/payouts", Reconciler, Payout("FIRST", Line(reference)));

        using var second = await PostAdminAsync("/api/v1/admin/finance/payouts", Reconciler, Payout("SECOND", Line(reference)));

        second.StatusCode.Should().Be(HttpStatusCode.Created);
        (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("counts").GetProperty("duplicate").GetInt32().Should().Be(1);
        (await DbAsync(db => db.Postings.CountAsync(p => p.SourceType == "payout"))).Should().Be(1);
    }

    [Fact]
    public async Task APayoutCanBeImportedOnlyOnce()
    {
        var reference = await ConfirmedSslAsync();
        await PostAdminAsync("/api/v1/admin/finance/payouts", Reconciler, Payout("ONCE", Line(reference)));

        using var again = await PostAdminAsync("/api/v1/admin/finance/payouts", Reconciler, Payout("ONCE", Line(reference)));

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync()).Should().Contain("ALREADY_IMPORTED");
        (await DbAsync(db => db.Payouts.CountAsync())).Should().Be(1);
    }

    [Theory]
    [InlineData("gross-not-fee-plus-net")]
    [InlineData("bad-reference")]
    [InlineData("bad-amount")]
    [InlineData("negative-fee")]
    [InlineData("bad-currency")]
    [InlineData("no-lines")]
    [InlineData("unknown-gateway")]
    [InlineData("no-ref")]
    [InlineData("bad-date")]
    public async Task InvalidPayoutsAreRefusedWholesale(string problem)
    {
        object body = problem switch
        {
            "gross-not-fee-plus-net" => Payout("P", Line("KJD-AAAAAAAAAA", "100.00", "5.00", "90.00")),
            "bad-reference" => Payout("P", Line("nope")),
            "bad-amount" => Payout("P", Line("KJD-AAAAAAAAAA", "1e3", "0.00", "1000")),
            "negative-fee" => Payout("P", Line("KJD-AAAAAAAAAA", "100.00", "-5.00", "105.00")),
            "bad-currency" => Payout("P", Line("KJD-AAAAAAAAAA", currency: "XYZ")),
            "no-lines" => Payout("P"),
            "unknown-gateway" => new { gateway = "bitcoin", payoutRef = "P", lines = new[] { Line("KJD-AAAAAAAAAA") } },
            "no-ref" => new { gateway = "sslcommerz", payoutRef = "", lines = new[] { Line("KJD-AAAAAAAAAA") } },
            _ => new { gateway = "sslcommerz", payoutRef = "P", paidOn = "31/12/2026", lines = new[] { Line("KJD-AAAAAAAAAA") } }
        };

        using var response = await PostAdminAsync("/api/v1/admin/finance/payouts", Reconciler, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await DbAsync(db => db.Payouts.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task APayoutIntoAClosedPeriodWritesNothingAtAll()
    {
        var reference = await ConfirmedSslAsync();
        var closer = TestTokens.Issue([Permissions.FinancePeriodClose]);
        (await PostAdminAsync($"/api/v1/admin/finance/ledger/periods/{DateTime.UtcNow:yyyy-MM}/close", closer)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await PostAdminAsync("/api/v1/admin/finance/payouts", Reconciler, Payout("CLOSED", Line(reference)));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("PERIOD_CLOSED");
        (await DbAsync(db => db.Payouts.CountAsync())).Should().Be(0);
        (await DbAsync(db => db.PayoutLines.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task PayoutsNeedTheReconcilePermissionAndAreAudited()
    {
        var reference = await ConfirmedSslAsync();
        (await PostAdminAsync("/api/v1/admin/finance/payouts", Viewer, Payout("X", Line(reference)))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await PostAdminAsync("/api/v1/admin/finance/payouts", Reconciler, Payout("AUDITED", Line(reference)));

        var audit = await AdminJsonAsync("/api/v1/admin/finance/audit?limit=100", TestTokens.Issue([Permissions.AuditView]));

        audit.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("entity").GetString()).Should().Contain(["Payout", "PayoutLine"]);
    }

    [Fact]
    public async Task EveryFinanceEndpointStillDeclaresAPolicyOrIsExplicitlyAnonymous()
    {
        foreach (var endpoint in _factory.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>().SelectMany(s => s.Endpoints))
        {
            var anonymous = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null;
            var authorize = endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Any();
            (anonymous || authorize).Should().BeTrue(endpoint.DisplayName);
        }

        await Task.CompletedTask;
    }
}
