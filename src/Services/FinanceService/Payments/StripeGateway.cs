using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KJWebsite.BuildingBlocks.Finance;
using Microsoft.Extensions.Options;

namespace FinanceService.Payments;

/// <summary>
/// Stripe Checkout. A Checkout Session is created (with an idempotency key, so a retry cannot start a second one) and the donor is sent to its hosted page.
/// Webhooks carry a `Stripe-Signature` header: an HMAC-SHA256 of `timestamp.body` with the endpoint secret, rejected when wrong or older than five minutes.
/// As with every gateway the webhook only prompts a look: the session is fetched again (with its balance transaction, for the settled amount and fee) and
/// that answer decides. Built to the published API and exercised against test doubles only: it has not been run against a live sandbox account.
/// </summary>
public sealed class StripeGateway(IHttpClientFactory httpClients, IOptions<PaymentOptions> options, TimeProvider clock) : IPaymentGateway
{
    public const string ClientName = "gateway-stripe";
    private const string ApiUrl = "https://api.stripe.com";
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private StripeOptions Settings => options.Value.Stripe;

    public string Id => GatewayIds.Stripe;

    public bool IsEnabled => Settings.Enabled && !string.IsNullOrWhiteSpace(Settings.SecretKey) && !string.IsNullOrWhiteSpace(Settings.WebhookSecret);

    public bool Supports(string currency) => currency is Currencies.Bdt or Currencies.Usd or Currencies.Gbp or Currencies.Eur;

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["mode"] = "payment",
            ["success_url"] = request.Returns.Success.ToString(),
            ["cancel_url"] = request.Returns.Cancel.ToString(),
            ["client_reference_id"] = request.Reference,
            ["customer_email"] = request.DonorEmail,
            ["line_items[0][quantity]"] = "1",
            ["line_items[0][price_data][currency]"] = request.Charge.Currency.ToLowerInvariant(),
            ["line_items[0][price_data][unit_amount]"] = Minor(request.Charge.Amount).ToString(CultureInfo.InvariantCulture),
            ["line_items[0][price_data][product_data][name]"] = "Donation",
            ["metadata[reference]"] = request.Reference,
            ["payment_intent_data[metadata][reference]"] = request.Reference
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/checkout/sessions") { Content = new FormUrlEncodedContent(form) };
        Authorize(message);
        message.Headers.TryAddWithoutValidation("Idempotency-Key", $"kj-checkout-{request.Reference}");
        var body = await GatewayHttp.SendForJsonAsync(Client(), message, cancellationToken);

        var id = GatewayHttp.Text(body, "id");
        if (string.IsNullOrEmpty(id))
        {
            throw new GatewayUnavailableException("GATEWAY_BAD_RESPONSE", "Stripe returned no session id.");
        }

        return new CheckoutSession(id, GatewayHttp.RedirectUrl(GatewayHttp.Text(body, "url"), "GATEWAY_BAD_RESPONSE", Settings.Sandbox));
    }

    public Task<WebhookParse> ParseWebhookAsync(IncomingWebhook webhook, CancellationToken cancellationToken) => Task.FromResult(Parse(webhook));

    private WebhookParse Parse(IncomingWebhook webhook)
    {
        var invalid = new WebhookParse(WebhookAuthenticity.Invalid, null, null, null);
        if (!webhook.HeaderValues.TryGetValue("stripe-signature", out var header) || string.IsNullOrWhiteSpace(Settings.WebhookSecret) || webhook.RawBody.Length == 0)
        {
            return invalid;
        }

        string? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            if (pair[0] == "t")
            {
                timestamp = pair[1];
            }
            else if (pair[0] == "v1")
            {
                signatures.Add(pair[1]);
            }
        }

        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || (clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
        {
            return invalid;
        }

        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Settings.WebhookSecret), Encoding.UTF8.GetBytes($"{timestamp}.{webhook.RawBody}"))).ToLowerInvariant();
        if (!signatures.Any(s => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(s.ToLowerInvariant()))))
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
        var type = GatewayHttp.Text(evt, "type");
        var obj = GatewayHttp.At(evt, "data", "object");
        if (eventId is null || type is null)
        {
            return invalid;
        }

        switch (type)
        {
            case "checkout.session.completed" or "checkout.session.async_payment_succeeded" or "checkout.session.async_payment_failed" or "checkout.session.expired":
                return new WebhookParse(WebhookAuthenticity.Verified, GatewayHttp.Text(obj, "client_reference_id"), GatewayHttp.Text(obj, "id"), eventId);
            case "charge.refunded":
                return new WebhookParse(WebhookAuthenticity.Verified, GatewayHttp.TextAt(obj, "metadata", "reference"), null, eventId, NoticeKind.Refund, GatewayHttp.Text(obj, "payment_intent"),
                    FromMinor(GatewayHttp.LongAt(obj, "amount_refunded"), GatewayHttp.Text(obj, "currency")));
            case "charge.dispute.created":
                return new WebhookParse(WebhookAuthenticity.Verified, null, null, eventId, NoticeKind.Dispute, GatewayHttp.Text(obj, "payment_intent"),
                    FromMinor(GatewayHttp.LongAt(obj, "amount"), GatewayHttp.Text(obj, "currency")));
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

        using var message = new HttpRequestMessage(HttpMethod.Get,
            $"{BaseUrl}/v1/checkout/sessions/{Uri.EscapeDataString(request.ProviderPaymentId)}?expand[]=payment_intent.latest_charge.balance_transaction");
        Authorize(message);
        var session = await GatewayHttp.SendForJsonAsync(Client(), message, cancellationToken);

        var reference = GatewayHttp.Text(session, "client_reference_id");
        var intent = GatewayHttp.At(session, "payment_intent");
        var intentStatus = GatewayHttp.Text(intent, "status");
        if (GatewayHttp.Text(session, "payment_status") == "paid")
        {
            var amount = FromMinor(GatewayHttp.LongAt(session, "amount_total"), GatewayHttp.Text(session, "currency"));
            return new PaymentVerification(PaymentOutcome.Paid, amount, reference, SettlementFrom(intent));
        }

        if (GatewayHttp.Text(session, "status") == "expired" || intentStatus == "canceled")
        {
            return new PaymentVerification(PaymentOutcome.Cancelled, null, reference);
        }

        if (GatewayHttp.Text(session, "status") == "complete" && intentStatus == "requires_payment_method")
        {
            return new PaymentVerification(PaymentOutcome.Failed, null, reference);
        }

        return new PaymentVerification(PaymentOutcome.Pending, null, reference);
    }

    private static SettlementInfo? SettlementFrom(JsonElement intent)
    {
        var balance = GatewayHttp.At(intent, "latest_charge", "balance_transaction");
        var settled = FromMinor(GatewayHttp.LongAt(balance, "amount"), GatewayHttp.Text(balance, "currency"));
        if (settled is not { } amount)
        {
            return null;
        }

        var fee = FromMinor(GatewayHttp.LongAt(balance, "fee"), GatewayHttp.Text(balance, "currency"));
        decimal? rate = GatewayHttp.At(balance, "exchange_rate") is { ValueKind: JsonValueKind.Number } r && r.TryGetDecimal(out var parsed) ? parsed : null;
        return new SettlementInfo(amount, fee, rate, GatewayHttp.Text(intent, "id"));
    }

    private static long Minor(decimal amount) => decimal.ToInt64(amount * 100m);

    private static Money? FromMinor(long? minor, string? currency) =>
        minor is { } value && currency is not null && Money.TryCreate(value / 100m, currency.ToUpperInvariant()) is { IsSuccess: true } made ? made.Value : null;

    private void Authorize(HttpRequestMessage message) => message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Settings.SecretKey}");

    private string BaseUrl => (Settings.BaseUrl ?? ApiUrl).TrimEnd('/');

    private HttpClient Client() => GatewayHttp.Client(httpClients, ClientName, options.Value.TimeoutSeconds);
}
