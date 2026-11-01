using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KJWebsite.BuildingBlocks.Finance;
using Microsoft.Extensions.Options;

namespace FinanceService.Payments;

/// <summary>
/// bKash tokenized checkout. A token is granted with the merchant's username/password and app key/secret and cached until shortly before it expires;
/// a payment is created with it and the donor is sent to `bkashURL`. The callback is only a return; execution and confirmation belong to
/// BE-056. Built to the published API and exercised against test doubles only: it has not been run against a live sandbox account.
/// </summary>
public sealed class BkashGateway(IHttpClientFactory httpClients, IOptions<PaymentOptions> options, TimeProvider clock) : IPaymentGateway, IDisposable
{
    public const string ClientName = "gateway-bkash";
    private const string SandboxUrl = "https://tokenized.sandbox.bka.sh/v1.2.0-beta";
    private const string LiveUrl = "https://tokenized.pay.bka.sh/v1.2.0-beta";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpires;

    private BkashOptions Settings => options.Value.Bkash;

    public string Id => GatewayIds.Bkash;

    public bool IsEnabled => Settings.Enabled
        && !string.IsNullOrWhiteSpace(Settings.Username) && !string.IsNullOrWhiteSpace(Settings.Password)
        && !string.IsNullOrWhiteSpace(Settings.AppKey) && !string.IsNullOrWhiteSpace(Settings.AppSecret);

    public bool Supports(string currency) => currency == Currencies.Bdt;

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await CreateAsync(request, cancellationToken);
        }
        catch (GatewayRejectedException exception) when (exception.Code is "GATEWAY_HTTP_401" or "BKASH_TOKEN_INVALID")
        {
            // The cached token can be revoked before it expires: grant a fresh one and try exactly once more.
            await InvalidateTokenAsync();
            return await CreateAsync(request, cancellationToken);
        }
    }

    private async Task<CheckoutSession> CreateAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(cancellationToken);
        var payload = new
        {
            mode = "0011",
            payerReference = request.DonorPhone ?? request.Reference,
            callbackURL = request.Returns.Webhook.ToString(),
            amount = request.Charge.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            currency = request.Charge.Currency,
            intent = "sale",
            merchantInvoiceNumber = request.Reference
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/tokenized/checkout/create") { Content = Json(payload) };
        message.Headers.TryAddWithoutValidation("Authorization", token);
        message.Headers.TryAddWithoutValidation("X-APP-Key", Settings.AppKey);
        var body = await GatewayHttp.SendForJsonAsync(Client(), message, cancellationToken);

        var code = GatewayHttp.Text(body, "statusCode");
        if (code == "2079")
        {
            throw new GatewayRejectedException("BKASH_TOKEN_INVALID", "bKash refused the access token.");
        }

        if (code != "0000")
        {
            throw new GatewayRejectedException("BKASH_REFUSED", "bKash refused to create the payment.");
        }

        var paymentId = GatewayHttp.Text(body, "paymentID");
        if (string.IsNullOrEmpty(paymentId))
        {
            throw new GatewayUnavailableException("GATEWAY_BAD_RESPONSE", "bKash returned no payment id.");
        }

        return new CheckoutSession(paymentId, GatewayHttp.RedirectUrl(GatewayHttp.Text(body, "bkashURL"), "GATEWAY_BAD_RESPONSE", Settings.Sandbox));
    }

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && clock.GetUtcNow() < _tokenExpires - RefreshMargin)
            {
                return _token;
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/tokenized/checkout/token/grant")
            {
                Content = Json(new { app_key = Settings.AppKey, app_secret = Settings.AppSecret })
            };
            message.Headers.TryAddWithoutValidation("username", Settings.Username);
            message.Headers.TryAddWithoutValidation("password", Settings.Password);
            var body = await GatewayHttp.SendForJsonAsync(Client(), message, cancellationToken);

            var token = GatewayHttp.Text(body, "id_token");
            if (string.IsNullOrEmpty(token))
            {
                throw new GatewayRejectedException("BKASH_TOKEN_REFUSED", "bKash did not grant an access token.");
            }

            var seconds = body.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number ? expires.GetInt32() : 3600;
            _token = token;
            _tokenExpires = clock.GetUtcNow().AddSeconds(seconds);
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task InvalidateTokenAsync()
    {
        await _tokenLock.WaitAsync();
        try
        {
            _token = null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>bKash does not sign its browser callback: `paymentID` and `status` are claims, and the payment-status query decides.</summary>
    public Task<WebhookParse> ParseWebhookAsync(IncomingWebhook webhook, CancellationToken cancellationToken) => Task.FromResult(ParseWebhook(webhook));

    private static WebhookParse ParseWebhook(IncomingWebhook webhook) =>
        webhook.Query.TryGetValue("paymentID", out var paymentId) && paymentId.Length is > 0 and <= 100
            ? new WebhookParse(WebhookAuthenticity.NotSigned, null, paymentId, $"{paymentId}:{(webhook.Query.TryGetValue("status", out var status) ? status : string.Empty)}")
            : new WebhookParse(WebhookAuthenticity.Invalid, null, null, null);

    /// <summary>
    /// Queries the payment. bKash only takes the money when the merchant executes an `Authorized` payment, so that call is made here, once, and its answer
    /// (`Completed`) is what counts. Anything not clearly finished stays pending for the reconciliation job.
    /// </summary>
    public async Task<PaymentVerification> VerifyAsync(VerifyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.ProviderPaymentId))
        {
            return new PaymentVerification(PaymentOutcome.Pending, null, null);
        }

        try
        {
            return await VerifyOnceAsync(request.ProviderPaymentId, cancellationToken);
        }
        catch (GatewayRejectedException exception) when (exception.Code is "GATEWAY_HTTP_401" or "BKASH_TOKEN_INVALID")
        {
            await InvalidateTokenAsync();
            return await VerifyOnceAsync(request.ProviderPaymentId, cancellationToken);
        }
    }

    private async Task<PaymentVerification> VerifyOnceAsync(string paymentId, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(cancellationToken);
        var status = await CallAsync("payment/status", paymentId, token, cancellationToken);
        if (GatewayHttp.Text(status, "transactionStatus") == "Authorized")
        {
            status = await CallAsync("execute", paymentId, token, cancellationToken);
        }

        var state = GatewayHttp.Text(status, "transactionStatus");
        return state switch
        {
            "Completed" => new PaymentVerification(PaymentOutcome.Paid, GatewayHttp.Amount(GatewayHttp.Text(status, "amount"), GatewayHttp.Text(status, "currency")), GatewayHttp.Text(status, "merchantInvoiceNumber")),
            "Failed" or "Expired" => new PaymentVerification(PaymentOutcome.Failed, null, GatewayHttp.Text(status, "merchantInvoiceNumber")),
            "Cancelled" => new PaymentVerification(PaymentOutcome.Cancelled, null, GatewayHttp.Text(status, "merchantInvoiceNumber")),
            _ => new PaymentVerification(PaymentOutcome.Pending, null, GatewayHttp.Text(status, "merchantInvoiceNumber"))
        };
    }

    private async Task<JsonElement> CallAsync(string path, string paymentId, string token, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/tokenized/checkout/{path}") { Content = Json(new { paymentID = paymentId }) };
        message.Headers.TryAddWithoutValidation("Authorization", token);
        message.Headers.TryAddWithoutValidation("X-APP-Key", Settings.AppKey);
        var body = await GatewayHttp.SendForJsonAsync(Client(), message, cancellationToken);
        if (GatewayHttp.Text(body, "statusCode") == "2079")
        {
            throw new GatewayRejectedException("BKASH_TOKEN_INVALID", "bKash refused the access token.");
        }

        return body;
    }

    public void Dispose() => _tokenLock.Dispose();

    private string BaseUrl => (Settings.BaseUrl ?? (Settings.Sandbox ? SandboxUrl : LiveUrl)).TrimEnd('/');

    private HttpClient Client() => GatewayHttp.Client(httpClients, ClientName, options.Value.TimeoutSeconds);

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
}
