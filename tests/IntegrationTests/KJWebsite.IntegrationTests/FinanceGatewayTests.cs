using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Payments;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Finance;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class FinanceGatewayTests
{
    private const string SslPassword = "ssl-secret-PASSWORD-9";
    private const string BkashPassword = "bk-pass-ZZZ-17";
    private const string BkashSecret = "bk-app-secret-QQQ-42";

    private sealed class Harness : IDisposable
    {
        public FinanceApiFactory Factory { get; } = new();
        private HttpClient? _client;
        public HttpClient Client => _client ??= Factory.CreateClient();

        public Harness(ScriptedGatewayHandler? handler = null, params string[] enable)
        {
            Factory.Settings["Finance:Payments:PublicApiBaseUrl"] = "https://api.example.org";
            Factory.Settings["Finance:Payments:SiteBaseUrl"] = "https://site.example.org";
            Factory.GatewayHandler = handler;
            foreach (var gateway in enable)
            {
                Enable(gateway);
            }
        }

        public Harness Enable(string gateway)
        {
            var s = Factory.Settings;
            switch (gateway)
            {
                case GatewayIds.SslCommerz:
                    s["Finance:Payments:SslCommerz:Enabled"] = "true";
                    s["Finance:Payments:SslCommerz:StoreId"] = "kjtest001";
                    s["Finance:Payments:SslCommerz:StorePassword"] = SslPassword;
                    break;
                case GatewayIds.Bkash:
                    s["Finance:Payments:Bkash:Enabled"] = "true";
                    s["Finance:Payments:Bkash:Username"] = "bk-user";
                    s["Finance:Payments:Bkash:Password"] = BkashPassword;
                    s["Finance:Payments:Bkash:AppKey"] = "bk-app-key";
                    s["Finance:Payments:Bkash:AppSecret"] = BkashSecret;
                    break;
            }

            return this;
        }

        public async Task<JsonElement> DonateAsync(object body, string? key = null, HttpStatusCode expected = HttpStatusCode.Created)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/donations") { Content = JsonContent.Create(body) };
            if (key is not null)
            {
                request.Headers.Add("Idempotency-Key", key);
            }

            using var response = await Client.SendAsync(request);
            response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        public async Task<T> WithDbAsync<T>(Func<FinanceDbContext, Task<T>> action)
        {
            using var scope = Factory.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<FinanceDbContext>());
        }

        public void Dispose()
        {
            _client?.Dispose();
            Factory.Dispose();
        }
    }

    private static object Donation(string amount = "1000.00", string currency = "BDT", string? gateway = null, bool coverFees = false, string language = "en") => new
    {
        amount,
        currency,
        frequency = "once",
        designation = new { type = "general" },
        donor = new { name = "Rahim Uddin", email = "rahim@example.org", phone = "01710000000" },
        anonymous = false,
        coverFees,
        language,
        gateway
    };

    private static HttpResponseMessage SslOk(RecordedRequest _) =>
        ScriptedGatewayHandler.Json(new { status = "SUCCESS", sessionkey = "SK-123", GatewayPageURL = "https://sandbox.sslcommerz.com/EasyCheckOut/SK-123" });

    private static ScriptedGatewayHandler Routing(Func<RecordedRequest, HttpResponseMessage?> ssl, Func<RecordedRequest, HttpResponseMessage?>? bkash = null) =>
        new(request => request.Url.Host switch
        {
            "sandbox.sslcommerz.com" or "securepay.sslcommerz.com" => ssl(request)!,
            "tokenized.sandbox.bka.sh" => bkash?.Invoke(request) ?? throw new InvalidOperationException("unexpected bKash call"),
            _ => throw new InvalidOperationException($"unexpected call to {request.Url}")
        });

    private static ScriptedGatewayHandler BkashHandler(List<string>? grants = null, int failCreateWithTokenInvalid = 0) =>
        Routing(_ => throw new InvalidOperationException("ssl"), request =>
        {
            if (request.Url.AbsolutePath.EndsWith("/token/grant", StringComparison.Ordinal))
            {
                grants?.Add("grant");
                request.Headers["username"].Should().Be("bk-user");
                request.Headers["password"].Should().Be(BkashPassword);
                request.Json.GetProperty("app_key").GetString().Should().Be("bk-app-key");
                request.Json.GetProperty("app_secret").GetString().Should().Be(BkashSecret);
                return ScriptedGatewayHandler.Json(new { id_token = $"tok-{grants?.Count ?? 1}", expires_in = 3600, statusCode = "0000" });
            }

            request.Url.AbsolutePath.Should().EndWith("/tokenized/checkout/create");
            if (failCreateWithTokenInvalid > 0 && grants is { Count: var n } && n <= failCreateWithTokenInvalid)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            request.Headers["x-app-key"].Should().Be("bk-app-key");
            request.Headers["authorization"].Should().Be($"tok-{grants?.Count ?? 1}");
            return ScriptedGatewayHandler.Json(new { statusCode = "0000", paymentID = "TR0011-ABC", bkashURL = "https://sandbox.payment.bkash.com/?paymentId=TR0011-ABC" });
        });

    [Fact]
    public async Task SslCommerzStartsAHostedCheckoutWithTheChargeAndOurReturnUrls()
    {
        var handler = Routing(SslOk);
        using var harness = new Harness(handler, GatewayIds.SslCommerz);

        var created = await harness.DonateAsync(Donation("1000.00", coverFees: true));

        created.GetProperty("gateway").GetString().Should().Be("sslcommerz");
        created.GetProperty("checkoutUrl").GetString().Should().Be("https://sandbox.sslcommerz.com/EasyCheckOut/SK-123");
        created.GetProperty("status").GetString().Should().Be("pending");
        created.TryGetProperty("fallback", out _).Should().BeFalse();
        var reference = created.GetProperty("reference").GetString()!;
        var form = handler.Requests.Single().Form;
        (form["store_id"], form["tran_id"], form["currency"]).Should().Be(("kjtest001", reference, "BDT"));
        form["total_amount"].Should().Be("1025.65", "the donor covers the fee, so the gateway charges amount + fee");
        form["success_url"].Should().Be($"https://api.example.org/api/v1/donations/{reference}/return?result=success");
        form["fail_url"].Should().EndWith("result=fail");
        form["cancel_url"].Should().EndWith("result=cancel");
        handler.Requests.Single().Url.ToString().Should().StartWith("https://sandbox.sslcommerz.com/gwprocess/v4/api.php");

        (await harness.WithDbAsync(db => db.PaymentAttempts.AsNoTracking().ToListAsync())).Should().ContainSingle()
            .Which.Should().Match<PaymentAttemptEntity>(a => a.Gateway == "sslcommerz" && a.Status == "created" && a.ProviderPaymentId == "SK-123");
        var status = await harness.Client.GetFromJsonAsync<JsonElement>($"/api/v1/donations/{reference}/status");
        status.GetProperty("gateway").GetString().Should().Be("sslcommerz");
        status.GetProperty("status").GetString().Should().Be("pending", "starting a checkout confirms nothing");
    }

    [Fact]
    public async Task TheBackOfficeShowsTheGatewayAsTheMethod()
    {
        using var harness = new Harness(Routing(SslOk), GatewayIds.SslCommerz);
        var created = await harness.DonateAsync(Donation());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/finance/donations?method=sslcommerz");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue([Permissions.FinanceView]));
        using var response = await harness.Client.SendAsync(request);
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();

        list.GetProperty("total").GetInt32().Should().Be(1);
        list.GetProperty("items")[0].GetProperty("method").GetString().Should().Be("sslcommerz");
        list.GetProperty("items")[0].GetProperty("id").GetString().Should().Be(created.GetProperty("id").GetString());
    }

    [Fact]
    public async Task BkashGrantsATokenOnceAndReusesItAcrossDonations()
    {
        var grants = new List<string>();
        var handler = BkashHandler(grants);
        using var harness = new Harness(handler, GatewayIds.Bkash);

        var first = await harness.DonateAsync(Donation("500.00", gateway: "bkash"));
        var second = await harness.DonateAsync(Donation("700.00", gateway: "bkash"));

        first.GetProperty("gateway").GetString().Should().Be("bkash");
        first.GetProperty("checkoutUrl").GetString().Should().Be("https://sandbox.payment.bkash.com/?paymentId=TR0011-ABC");
        second.GetProperty("gateway").GetString().Should().Be("bkash");
        grants.Should().HaveCount(1, "the token is cached until shortly before it expires");
        var create = handler.Requests.Last(r => r.Url.AbsolutePath.EndsWith("/create", StringComparison.Ordinal)).Json;
        (create.GetProperty("amount").GetString(), create.GetProperty("currency").GetString(), create.GetProperty("intent").GetString(), create.GetProperty("mode").GetString())
            .Should().Be(("700.00", "BDT", "sale", "0011"));
        create.GetProperty("merchantInvoiceNumber").GetString().Should().Be(second.GetProperty("reference").GetString());
        create.GetProperty("callbackURL").GetString().Should().Contain("/return?result=success");
    }

    [Fact]
    public async Task BkashRefreshesARevokedTokenAndRetriesOnce()
    {
        var grants = new List<string>();
        using var harness = new Harness(BkashHandler(grants, failCreateWithTokenInvalid: 1), GatewayIds.Bkash);

        var created = await harness.DonateAsync(Donation("500.00", gateway: "bkash"));

        created.GetProperty("gateway").GetString().Should().Be("bkash");
        grants.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("pem")]
    [InlineData("base64")]
    public async Task NagadInitializesThenCompletesWithEncryptedSignedPayloads(string keyFormat)
    {
        using var nagad = new NagadDouble();
        using var harness = new Harness(nagad.Handler);
        harness.Factory.Settings["Finance:Payments:Nagad:Enabled"] = "true";
        harness.Factory.Settings["Finance:Payments:Nagad:MerchantId"] = "683002007104225";
        harness.Factory.Settings["Finance:Payments:Nagad:MerchantPrivateKey"] = keyFormat == "pem" ? nagad.Merchant.ExportPkcs8PrivateKeyPem() : Convert.ToBase64String(nagad.Merchant.ExportPkcs8PrivateKey());
        harness.Factory.Settings["Finance:Payments:Nagad:PgPublicKey"] = keyFormat == "pem" ? nagad.Gateway.ExportSubjectPublicKeyInfoPem() : Convert.ToBase64String(nagad.Gateway.ExportSubjectPublicKeyInfo());
        harness.Factory.GatewayHandler = nagad.Handler;

        var created = await harness.DonateAsync(Donation("1250.50", gateway: "nagad"));

        nagad.Problems.Should().BeEmpty();
        created.GetProperty("gateway").GetString().Should().Be("nagad");
        created.GetProperty("checkoutUrl").GetString().Should().Be("https://sandbox.mynagad.com/pay/PR-NAGAD-1");
        var reference = created.GetProperty("reference").GetString()!;
        var init = nagad.Initialized.Should().ContainSingle().Subject;
        init.GetProperty("orderId").GetString().Should().Be(reference.Replace("-", string.Empty));
        init.GetProperty("merchantId").GetString().Should().Be("683002007104225");
        init.GetProperty("datetime").GetString().Should().MatchRegex(@"^\d{14}$");
        var complete = nagad.Completed.Should().ContainSingle().Subject;
        (complete.GetProperty("amount").GetString(), complete.GetProperty("currencyCode").GetString(), complete.GetProperty("challenge").GetString())
            .Should().Be(("1250.50", "050", nagad.Challenge));
        (await harness.WithDbAsync(db => db.PaymentAttempts.AsNoTracking().SingleAsync())).ProviderPaymentId.Should().Be("PR-NAGAD-1");
    }

    [Fact]
    public async Task ABrokenNagadKeyDegradesToOfflineWithoutLeakingTheKey()
    {
        using var nagad = new NagadDouble();
        using var harness = new Harness(nagad.Handler);
        harness.Factory.Settings["Finance:Payments:Nagad:Enabled"] = "true";
        harness.Factory.Settings["Finance:Payments:Nagad:MerchantId"] = "m1";
        harness.Factory.Settings["Finance:Payments:Nagad:MerchantPrivateKey"] = "bm90LWEta2V5";
        harness.Factory.Settings["Finance:Payments:Nagad:PgPublicKey"] = "bm90LWEta2V5";

        var created = await harness.DonateAsync(Donation(gateway: "nagad"));

        created.GetProperty("fallback").GetString().Should().Be("offline");
        (await harness.WithDbAsync(db => db.PaymentAttempts.AsNoTracking().SingleAsync())).ErrorCode.Should().Be("NAGAD_KEY_INVALID");
    }

    [Fact]
    public async Task TheDefaultGatewayIsTheFirstEnabledOneInConfiguredOrderThatSupportsTheCurrency()
    {
        var handler = new ScriptedGatewayHandler(request => request.Url.Host == "sandbox.sslcommerz.com" ? SslOk(request) : BkashHandlerResponse(request));
        using var harness = new Harness(handler, GatewayIds.SslCommerz, GatewayIds.Bkash);

        (await harness.DonateAsync(Donation("100.00"))).GetProperty("gateway").GetString().Should().Be("sslcommerz");
        (await harness.DonateAsync(Donation("10.00", "USD"))).GetProperty("gateway").GetString().Should().Be("sslcommerz", "bKash takes BDT only");

        using var reordered = new Harness(handler, GatewayIds.SslCommerz, GatewayIds.Bkash);
        reordered.Factory.Settings["Finance:Payments:Order:0"] = "bkash";
        reordered.Factory.Settings["Finance:Payments:Order:1"] = "sslcommerz";
        (await reordered.DonateAsync(Donation("100.00"))).GetProperty("gateway").GetString().Should().Be("bkash");
        (await reordered.DonateAsync(Donation("10.00", "GBP"))).GetProperty("gateway").GetString().Should().Be("sslcommerz");
    }

    private static HttpResponseMessage BkashHandlerResponse(RecordedRequest request) =>
        request.Url.AbsolutePath.EndsWith("/token/grant", StringComparison.Ordinal)
            ? ScriptedGatewayHandler.Json(new { id_token = "t", expires_in = 3600, statusCode = "0000" })
            : ScriptedGatewayHandler.Json(new { statusCode = "0000", paymentID = "P1", bkashURL = "https://sandbox.payment.bkash.com/?paymentId=P1" });

    [Fact]
    public async Task ARequestedGatewayThatIsOffOrCannotTakeTheCurrencyFallsBackToOfflineInsteadOfPayingElsewhere()
    {
        using var harness = new Harness(Routing(SslOk), GatewayIds.SslCommerz);

        foreach (var body in new[] { Donation(gateway: "bkash"), Donation(gateway: "stripe"), Donation("10.00", "USD", "bkash") })
        {
            var created = await harness.DonateAsync(body);
            created.GetProperty("fallback").GetString().Should().Be("offline");
            created.TryGetProperty("checkoutUrl", out _).Should().BeFalse();
            created.TryGetProperty("gateway", out _).Should().BeFalse();
        }

        (await harness.WithDbAsync(db => db.Donations.CountAsync())).Should().Be(3, "the donations are recorded, pending");
        (await harness.WithDbAsync(db => db.PaymentAttempts.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task WithNoGatewaySwitchedOnTheDonationStillSucceedsAsPendingOffline()
    {
        using var harness = new Harness();

        var created = await harness.DonateAsync(Donation());

        created.GetProperty("fallback").GetString().Should().Be("offline");
        created.GetProperty("status").GetString().Should().Be("pending");
    }

    [Fact]
    public async Task AHalfConfiguredGatewayIsSimplyOff()
    {
        using var harness = new Harness(Routing(SslOk));
        harness.Factory.Settings["Finance:Payments:SslCommerz:Enabled"] = "true";
        harness.Factory.Settings["Finance:Payments:SslCommerz:StoreId"] = "kjtest001";

        var created = await harness.DonateAsync(Donation());

        created.GetProperty("fallback").GetString().Should().Be("offline");
        ((ScriptedGatewayHandler)harness.Factory.GatewayHandler!).Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("unreachable", "GATEWAY_UNREACHABLE")]
    [InlineData("timeout", "GATEWAY_TIMEOUT")]
    [InlineData("500", "GATEWAY_HTTP_500")]
    [InlineData("garbage", "GATEWAY_BAD_RESPONSE")]
    [InlineData("refused", "SSLCOMMERZ_REFUSED")]
    [InlineData("forbidden", "GATEWAY_HTTP_403")]
    [InlineData("insecure-url", "GATEWAY_BAD_RESPONSE")]
    public async Task AGatewayOutageNeverFailsTheDonationAndTheFailureIsRecorded(string failure, string expectedCode)
    {
        var handler = Routing(_ => failure switch
        {
            "unreachable" => throw new HttpRequestException("connection refused"),
            "timeout" => throw new TaskCanceledException("timed out"),
            "500" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            "garbage" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>maintenance</html>") },
            "forbidden" => new HttpResponseMessage(HttpStatusCode.Forbidden),
            "insecure-url" => ScriptedGatewayHandler.Json(new { status = "SUCCESS", sessionkey = "SK", GatewayPageURL = "http://evil.example.org/pay" }),
            _ => ScriptedGatewayHandler.Json(new { status = "FAILED", failedreason = "Invalid Store Password" })
        });
        using var harness = new Harness(handler, GatewayIds.SslCommerz);
        harness.Factory.Settings["Finance:Payments:SslCommerz:Sandbox"] = "false";

        var created = await harness.DonateAsync(Donation());

        created.GetProperty("fallback").GetString().Should().Be("offline");
        created.GetProperty("status").GetString().Should().Be("pending");
        var attempt = await harness.WithDbAsync(db => db.PaymentAttempts.AsNoTracking().SingleAsync());
        (attempt.Status, attempt.ErrorCode, attempt.ProviderPaymentId).Should().Be(("failed", expectedCode, null));
        var donation = await harness.WithDbAsync(db => db.Donations.Include(d => d.Events).AsNoTracking().SingleAsync());
        donation.Events.Should().ContainSingle().Which.Status.Should().Be("pending", "a gateway failure leaves the donation pending, never in a wrong terminal state");
    }

    [Fact]
    public async Task WhenTheDonorDidNotPickAGatewayTheNextOneIsTriedButAChosenOneIsNot()
    {
        var handler = new ScriptedGatewayHandler(request => request.Url.Host == "sandbox.sslcommerz.com" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : BkashHandlerResponse(request));
        using var harness = new Harness(handler, GatewayIds.SslCommerz, GatewayIds.Bkash);

        var automatic = await harness.DonateAsync(Donation());
        var chosen = await harness.DonateAsync(Donation(gateway: "sslcommerz"));

        automatic.GetProperty("gateway").GetString().Should().Be("bkash");
        chosen.GetProperty("fallback").GetString().Should().Be("offline");
        var attempts = await harness.WithDbAsync(db => db.PaymentAttempts.AsNoTracking().OrderBy(a => a.Id).ToListAsync());
        attempts.Select(a => (a.Gateway, a.Status)).Should().Equal(("sslcommerz", "failed"), ("bkash", "created"), ("sslcommerz", "failed"));
    }

    [Fact]
    public async Task AnUnknownGatewayNameIsRefused()
    {
        using var harness = new Harness();

        using var response = await harness.Client.PostAsJsonAsync("/api/v1/donations", Donation(gateway: "bitcoin"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("gateway");
    }

    [Fact]
    public async Task ARepeatedRequestReplaysTheCheckoutInsteadOfStartingAnother()
    {
        var handler = Routing(SslOk);
        using var harness = new Harness(handler, GatewayIds.SslCommerz);

        var first = await harness.DonateAsync(Donation(), "gateway-key-1");
        var second = await harness.DonateAsync(Donation(), "gateway-key-1");

        second.GetProperty("checkoutUrl").GetString().Should().Be(first.GetProperty("checkoutUrl").GetString());
        handler.Requests.Should().HaveCount(1);
        (await harness.WithDbAsync(db => db.Donations.CountAsync())).Should().Be(1);
    }

    [Theory]
    [InlineData("GET", "success", "return")]
    [InlineData("POST", "fail", "return")]
    [InlineData("GET", "cancel", "cancel")]
    [InlineData("POST", "weird", "return")]
    public async Task TheReturnEndpointOnlyRedirectsToTheStatusPage(string method, string result, string expectedResult)
    {
        using var harness = new Harness(Routing(SslOk), GatewayIds.SslCommerz);
        var created = await harness.DonateAsync(Donation(language: "bn"));
        var reference = created.GetProperty("reference").GetString()!;
        using var noRedirect = harness.Factory.CreateClient(new() { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/v1/donations/{reference}/return?result={result}");
        if (method == "POST")
        {
            // What a gateway (or a forger) might post: it is not read.
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["status"] = "VALID", ["tran_id"] = reference, ["amount"] = "1.00" });
        }

        using var response = await noRedirect.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.SeeOther);
        response.Headers.Location!.ToString().Should().Be($"https://site.example.org/bn/donate/status?ref={reference}&gateway=sslcommerz&result={expectedResult}");
        (await harness.Client.GetFromJsonAsync<JsonElement>($"/api/v1/donations/{reference}/status")).GetProperty("status").GetString()
            .Should().Be("pending", "a return from the gateway never confirms payment");
        (await harness.WithDbAsync(db => db.DonationEvents.CountAsync())).Should().Be(1);
    }

    [Theory]
    [InlineData("KJD-0000000000")]
    [InlineData("rubbish")]
    public async Task TheReturnEndpointSendsUnknownReferencesToThePlainLookupPage(string reference)
    {
        using var harness = new Harness();
        using var noRedirect = harness.Factory.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await noRedirect.GetAsync($"/api/v1/donations/{reference}/return?result=success");

        response.StatusCode.Should().Be(HttpStatusCode.SeeOther);
        response.Headers.Location!.ToString().Should().Be("https://site.example.org/en/donate/status");
    }

    [Fact]
    public async Task CredentialsNeverAppearInResponsesLogsOrStoredAttempts()
    {
        using var nagad = new NagadDouble();
        var handler = new ScriptedGatewayHandler(request => request.Url.Host switch
        {
            "sandbox.sslcommerz.com" => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent($"upstream said {SslPassword}") },
            "sandbox.mynagad.com" => nagad.Respond(request),
            _ => BkashHandlerResponse(request)
        });
        using var harness = new Harness(handler, GatewayIds.SslCommerz, GatewayIds.Bkash);
        harness.Factory.Settings["Finance:Payments:Nagad:Enabled"] = "true";
        harness.Factory.Settings["Finance:Payments:Nagad:MerchantId"] = "m1";
        harness.Factory.Settings["Finance:Payments:Nagad:MerchantPrivateKey"] = nagad.Merchant.ExportPkcs8PrivateKeyPem();
        harness.Factory.Settings["Finance:Payments:Nagad:PgPublicKey"] = nagad.Gateway.ExportSubjectPublicKeyInfoPem();
        harness.Factory.Settings["Finance:Payments:Order:0"] = "sslcommerz";

        var responses = new List<string>
        {
            (await harness.DonateAsync(Donation())).GetRawText(),
            (await harness.DonateAsync(Donation(gateway: "sslcommerz"))).GetRawText(),
            (await harness.DonateAsync(Donation(gateway: "nagad"))).GetRawText()
        };
        var attempts = await harness.WithDbAsync(db => db.PaymentAttempts.AsNoTracking().ToListAsync());
        var everything = string.Join('\n', responses.Concat(harness.Factory.Logs.Lines).Concat(attempts.Select(a => $"{a.Gateway} {a.Status} {a.ProviderPaymentId} {a.ErrorCode}")));

        foreach (var secret in new[] { SslPassword, BkashPassword, BkashSecret, "BEGIN PRIVATE KEY", Convert.ToBase64String(nagad.Merchant.ExportPkcs8PrivateKey())[..40] })
        {
            everything.Should().NotContain(secret);
        }

        harness.Factory.Logs.Lines.Should().Contain(l => l.Contains("GATEWAY_HTTP_500", StringComparison.Ordinal), "the failure itself is logged, by code only");
    }

    [Fact]
    public void AppsettingsHoldNoGatewayCredentials()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KJWebsite.Backend.slnx")))
        {
            root = root.Parent;
        }

        root.Should().NotBeNull();
        foreach (var name in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            var json = File.ReadAllText(Path.Combine(root!.FullName, "src", "Services", "FinanceService", name));
            foreach (var credential in new[] { "StoreId", "StorePassword", "AppKey", "AppSecret", "Username", "Password", "MerchantId", "MerchantPrivateKey", "PgPublicKey" })
            {
                json.Should().NotContain($"\"{credential}\"", $"{name} must not carry gateway credentials: they come from the environment or a vault");
            }
        }
    }

    [Fact]
    public async Task AddingAProviderNeedsOnlyItsOwnClassAndARegistration()
    {
        var wallet = new TestWalletGateway();
        using var harness = new Harness();
        harness.Factory.ConfigureTestServices = services => services.AddSingleton<IPaymentGateway>(wallet);

        var created = await harness.DonateAsync(Donation(gateway: "testpay"));

        created.GetProperty("gateway").GetString().Should().Be("testpay");
        created.GetProperty("checkoutUrl").GetString().Should().Be("https://pay.example.org/checkout/" + created.GetProperty("reference").GetString());
        wallet.Requests.Should().ContainSingle().Which.Charge.Should().Be(Money.Of(1000.00m, "BDT"));
        (await harness.Client.GetFromJsonAsync<JsonElement>($"/api/v1/donations/{created.GetProperty("reference").GetString()}/status")).GetProperty("gateway").GetString().Should().Be("testpay");
    }

    [Fact]
    public async Task PaymentAttemptsAreAppendOnly()
    {
        using var harness = new Harness(Routing(SslOk), GatewayIds.SslCommerz);
        await harness.DonateAsync(Donation());

        await harness.WithDbAsync(async db =>
        {
            (await db.PaymentAttempts.FirstAsync()).Status = "failed";
            await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
            db.ChangeTracker.Clear();
            db.PaymentAttempts.Remove(await db.PaymentAttempts.FirstAsync());
            await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<InvalidOperationException>();
            return 0;
        });
    }

    private sealed class TestWalletGateway : IPaymentGateway
    {
        public List<CheckoutRequest> Requests { get; } = [];
        public string Id => "testpay";
        public bool IsEnabled => true;
        public bool Supports(string currency) => currency == Currencies.Bdt;

        public Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new CheckoutSession("tp-" + request.Reference, new Uri("https://pay.example.org/checkout/" + request.Reference)));
        }
    }
}
