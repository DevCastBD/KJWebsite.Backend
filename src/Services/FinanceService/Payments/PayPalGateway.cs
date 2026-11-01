using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KJWebsite.BuildingBlocks.Finance;
using Microsoft.Extensions.Options;

namespace FinanceService.Payments;

/// <summary>
/// PayPal Orders v2. An order is created and the donor is sent to PayPal's approval page; coming back (or PayPal's approval webhook) prompts a look, and the
/// look captures an approved order (once, with an idempotency key) and re-fetches it. Webhooks are authenticated by PayPal's own verify-webhook-signature call,
/// sent the raw event untouched. Only USD, GBP and EUR are offered: PayPal does not take BDT. Built to the published API and exercised against test doubles
/// only: it has not been run against a live sandbox account.
/// </summary>
public sealed class PayPalGateway(IHttpClientFactory httpClients, IOptions<PaymentOptions> options, TimeProvider clock) : IPaymentGateway, IDisposable
{
    public const string ClientName = "gateway-paypal";
    private const string SandboxUrl = "https://api-m.sandbox.paypal.com";
    private const string LiveUrl = "https://api-m.paypal.com";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpires;

    private PayPalOptions Settings => options.Value.PayPal;

    public string Id => GatewayIds.PayPal;

    public bool IsEnabled => Settings.Enabled && !string.IsNullOrWhiteSpace(Settings.ClientId) && !string.IsNullOrWhiteSpace(Settings.ClientSecret) && !string.IsNullOrWhiteSpace(Settings.WebhookId);

    public bool Supports(string currency) => currency is Currencies.Usd or Currencies.Gbp or Currencies.Eur;

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        var payload = new
        {
            intent = "CAPTURE",
            purchase_units = new[]
            {
                new
                {
                    reference_id = request.Reference,
                    custom_id = request.Reference,
                    invoice_id = request.Reference,
                    description = "Donation",
                    amount = new { currency_code = request.Charge.Currency, value = request.Charge.ToDecimalString() }
                }
            },
            payment_source = new
            {
                paypal = new
                {
                    experience_context = new
                    {
                        return_url = request.Returns.Webhook.ToString(),
                        cancel_url = request.Returns.Cancel.ToString(),
                        user_action = "PAY_NOW",
                        shipping_preference = "NO_SHIPPING"
                    }
                }
            }
        };

        var body = await WithTokenAsync(async token =>
        {
            using var message = Request(HttpMethod.Post, "/v2/checkout/orders", token, payload);
            message.Headers.TryAddWithoutValidation("PayPal-Request-Id", $"kj-order-{request.Reference}");
            return await GatewayHttp.SendForJsonAsync(Client(), message, cancellationToken);
        }, cancellationToken);

        var id = GatewayHttp.Text(body, "id");
        var approve = body.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array
            ? links.EnumerateArray().FirstOrDefault(l => GatewayHttp.Text(l, "rel") is "payer-action" or "approve")
            : default;
        if (string.IsNullOrEmpty(id) || approve.ValueKind != JsonValueKind.Object)
        {
            throw new GatewayUnavailableException("GATEWAY_BAD_RESPONSE", "PayPal returned no approval link.");
        }

        return new CheckoutSession(id, GatewayHttp.RedirectUrl(GatewayHttp.Text(approve, "href"), "GATEWAY_BAD_RESPONSE", Settings.Sandbox));
    }

    public async Task<WebhookParse> ParseWebhookAsync(IncomingWebhook webhook, CancellationToken cancellationToken)
    {
        // The donor's browser coming back from PayPal: `token` is the order id. Unsigned, so the order lookup decides.
        if (string.Equals(webhook.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return webhook.Query.TryGetValue("token", out var token) && token.Length is > 0 and <= 100
                ? new WebhookParse(WebhookAuthenticity.NotSigned, null, token, $"{token}:return")
                : new WebhookParse(WebhookAuthenticity.Invalid, null, null, null);
        }

        var invalid = new WebhookParse(WebhookAuthenticity.Invalid, null, null, null);
        var h = webhook.HeaderValues;
        if (webhook.RawBody.Length == 0 || !h.TryGetValue("paypal-transmission-sig", out var sig) || !h.TryGetValue("paypal-transmission-id", out var transmissionId)
            || !h.TryGetValue("paypal-transmission-time", out var time) || !h.TryGetValue("paypal-cert-url", out var certUrl) || !h.TryGetValue("paypal-auth-algo", out var algorithm))
        {
            return invalid;
        }

        // PayPal checks its own signature; the event is embedded exactly as received because the signature covers its bytes.
        var verification = $$"""
            {"auth_algo":{{JsonSerializer.Serialize(algorithm)}},"cert_url":{{JsonSerializer.Serialize(certUrl)}},"transmission_id":{{JsonSerializer.Serialize(transmissionId)}},"transmission_sig":{{JsonSerializer.Serialize(sig)}},"transmission_time":{{JsonSerializer.Serialize(time)}},"webhook_id":{{JsonSerializer.Serialize(Settings.WebhookId)}},"webhook_event":{{webhook.RawBody}}}
            """;
        JsonElement answer;
        try
        {
            answer = await WithTokenAsync(async token =>
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/notifications/verify-webhook-signature")
                {
                    Content = new StringContent(verification, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
                };
                message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                return await GatewayHttp.SendForJsonAsync(Client(), message, cancellationToken);
            }, cancellationToken);
        }
        catch (GatewayRejectedException)
        {
            // PayPal refusing the verification request itself means the event was not one it will vouch for.
            return invalid;
        }

        if (GatewayHttp.Text(answer, "verification_status") != "SUCCESS")
        {
            return invalid;
        }

        JsonElement evt;
        try
        {
            evt = JsonDocument.Parse(webhook.RawBody).RootElement.Clone();
        }
        catch (JsonException)
        {
            return invalid;
        }

        var eventId = GatewayHttp.Text(evt, "id");
        var resource = GatewayHttp.At(evt, "resource");
        if (eventId is null)
        {
            return invalid;
        }

        switch (GatewayHttp.Text(evt, "event_type"))
        {
            case "CHECKOUT.ORDER.APPROVED":
                return new WebhookParse(WebhookAuthenticity.Verified, GatewayHttp.TextAt(resource, "purchase_units", "0", "custom_id"), GatewayHttp.Text(resource, "id"), eventId);
            case "PAYMENT.CAPTURE.COMPLETED" or "PAYMENT.CAPTURE.DENIED" or "PAYMENT.CAPTURE.PENDING":
                return new WebhookParse(WebhookAuthenticity.Verified, GatewayHttp.Text(resource, "custom_id"), GatewayHttp.TextAt(resource, "supplementary_data", "related_ids", "order_id"), eventId);
            case "PAYMENT.CAPTURE.REFUNDED":
                return new WebhookParse(WebhookAuthenticity.Verified, GatewayHttp.Text(resource, "custom_id"), null, eventId, NoticeKind.Refund, CaptureIdFromLinks(resource),
                    GatewayHttp.Amount(GatewayHttp.TextAt(resource, "amount", "value"), GatewayHttp.TextAt(resource, "amount", "currency_code")));
            case "CUSTOMER.DISPUTE.CREATED":
                return new WebhookParse(WebhookAuthenticity.Verified, null, null, eventId, NoticeKind.Dispute, GatewayHttp.TextAt(resource, "disputed_transactions", "0", "seller_transaction_id"),
                    GatewayHttp.Amount(GatewayHttp.TextAt(resource, "dispute_amount", "value"), GatewayHttp.TextAt(resource, "dispute_amount", "currency_code")));
            default:
                return new WebhookParse(WebhookAuthenticity.Verified, null, null, eventId, Irrelevant: true);
        }
    }

    public async Task<PaymentVerification> VerifyAsync(VerifyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.ProviderPaymentId))
        {
            return new PaymentVerification(PaymentOutcome.Pending, null, null);
        }

        var order = await WithTokenAsync(async token =>
        {
            using var message = Request(HttpMethod.Get, $"/v2/checkout/orders/{Uri.EscapeDataString(request.ProviderPaymentId)}", token, null);
            var fetched = await GatewayHttp.SendForJsonAsync(Client(), message, cancellationToken);
            if (GatewayHttp.Text(fetched, "status") != "APPROVED")
            {
                return fetched;
            }

            // The donor approved: this call is what actually takes the money, once (the id makes a repeat harmless).
            using var capture = Request(HttpMethod.Post, $"/v2/checkout/orders/{Uri.EscapeDataString(request.ProviderPaymentId)}/capture", token, new { });
            capture.Headers.TryAddWithoutValidation("PayPal-Request-Id", $"kj-capture-{request.Reference}");
            return await GatewayHttp.SendForJsonAsync(Client(), capture, cancellationToken);
        }, cancellationToken);

        var reference = GatewayHttp.TextAt(order, "purchase_units", "0", "custom_id");
        var status = GatewayHttp.Text(order, "status");
        if (status == "VOIDED")
        {
            return new PaymentVerification(PaymentOutcome.Cancelled, null, reference);
        }

        if (status != "COMPLETED")
        {
            return new PaymentVerification(PaymentOutcome.Pending, null, reference);
        }

        var capturePath = new[] { "purchase_units", "0", "payments", "captures", "0" };
        var captured = GatewayHttp.At(order, capturePath);
        switch (GatewayHttp.Text(captured, "status"))
        {
            case "COMPLETED":
                var amount = GatewayHttp.Amount(GatewayHttp.TextAt(captured, "amount", "value"), GatewayHttp.TextAt(captured, "amount", "currency_code"));
                return new PaymentVerification(PaymentOutcome.Paid, amount, reference, SettlementFrom(captured));
            case "DECLINED" or "FAILED":
                return new PaymentVerification(PaymentOutcome.Failed, null, reference);
            default:
                return new PaymentVerification(PaymentOutcome.Pending, null, reference);
        }
    }

    private static SettlementInfo? SettlementFrom(JsonElement captured)
    {
        var breakdown = GatewayHttp.At(captured, "seller_receivable_breakdown");
        var settled = GatewayHttp.Amount(GatewayHttp.TextAt(breakdown, "receivable_amount", "value"), GatewayHttp.TextAt(breakdown, "receivable_amount", "currency_code"))
            ?? GatewayHttp.Amount(GatewayHttp.TextAt(captured, "amount", "value"), GatewayHttp.TextAt(captured, "amount", "currency_code"));
        if (settled is not { } amount)
        {
            return null;
        }

        var fee = GatewayHttp.Amount(GatewayHttp.TextAt(breakdown, "paypal_fee", "value"), GatewayHttp.TextAt(breakdown, "paypal_fee", "currency_code"));
        decimal? rate = decimal.TryParse(GatewayHttp.TextAt(breakdown, "exchange_rate", "value"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        return new SettlementInfo(amount, fee, rate, GatewayHttp.Text(captured, "id"));
    }

    private static string? CaptureIdFromLinks(JsonElement resource)
    {
        if (!resource.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var up = links.EnumerateArray().FirstOrDefault(l => GatewayHttp.Text(l, "rel") == "up");
        return GatewayHttp.Text(up, "href") is { } href ? href[(href.LastIndexOf('/') + 1)..] : null;
    }

    private async Task<JsonElement> WithTokenAsync(Func<string, Task<JsonElement>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call(await TokenAsync(cancellationToken));
        }
        catch (GatewayRejectedException exception) when (exception.Code == "GATEWAY_HTTP_401")
        {
            // A revoked token: get a fresh one and try exactly once more.
            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                _token = null;
            }
            finally
            {
                _tokenLock.Release();
            }

            return await call(await TokenAsync(cancellationToken));
        }
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

            using var message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/oauth2/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" })
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Settings.ClientId}:{Settings.ClientSecret}")));
            var body = await GatewayHttp.SendForJsonAsync(Client(), message, cancellationToken);

            var token = GatewayHttp.Text(body, "access_token");
            if (string.IsNullOrEmpty(token))
            {
                throw new GatewayRejectedException("PAYPAL_TOKEN_REFUSED", "PayPal did not grant an access token.");
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

    private HttpRequestMessage Request(HttpMethod method, string path, string token, object? body)
    {
        var message = new HttpRequestMessage(method, $"{BaseUrl}{path}");
        message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        if (body is not null)
        {
            message.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        return message;
    }

    public void Dispose() => _tokenLock.Dispose();

    private string BaseUrl => (Settings.BaseUrl ?? (Settings.Sandbox ? SandboxUrl : LiveUrl)).TrimEnd('/');

    private HttpClient Client() => GatewayHttp.Client(httpClients, ClientName, options.Value.TimeoutSeconds);
}
