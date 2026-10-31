using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KJWebsite.BuildingBlocks.Finance;
using Microsoft.Extensions.Options;

namespace FinanceService.Payments;

/// <summary>
/// Nagad hosted checkout: an `initialize` call, then a `complete` call, each with the payload encrypted to Nagad's public key and signed with the
/// merchant's private key, and each answer opened with the merchant key. The donor is sent to the `callBackUrl` of the second answer. Built to the
/// published API and exercised against a test double that performs the same RSA round trip; it has not been run against a live sandbox account.
/// </summary>
public sealed class NagadGateway(IHttpClientFactory httpClients, IOptions<PaymentOptions> options, TimeProvider clock) : IPaymentGateway
{
    public const string ClientName = "gateway-nagad";
    private const string SandboxUrl = "http://sandbox.mynagad.com:10080/remote-payment-gateway-1.0/api/dfs";
    private const string LiveUrl = "https://api.mynagad.com/api/dfs";
    private static readonly TimeSpan Dhaka = TimeSpan.FromHours(6);

    private NagadOptions Settings => options.Value.Nagad;

    public string Id => GatewayIds.Nagad;

    public bool IsEnabled => Settings.Enabled && !string.IsNullOrWhiteSpace(Settings.MerchantId)
        && !string.IsNullOrWhiteSpace(Settings.MerchantPrivateKey) && !string.IsNullOrWhiteSpace(Settings.PgPublicKey);

    public bool Supports(string currency) => currency == Currencies.Bdt;

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        using var merchantKey = Import(Settings.MerchantPrivateKey, privateKey: true);
        using var gatewayKey = Import(Settings.PgPublicKey, privateKey: false);
        var client = GatewayHttp.Client(httpClients, ClientName, options.Value.TimeoutSeconds);
        var baseUrl = (Settings.BaseUrl ?? (Settings.Sandbox ? SandboxUrl : LiveUrl)).TrimEnd('/');
        // Nagad order ids are alphanumeric.
        var orderId = request.Reference.Replace("-", string.Empty, StringComparison.Ordinal);
        var challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));

        var initialize = new { merchantId = Settings.MerchantId, datetime = Timestamp(), orderId, challenge };
        var initializeAnswer = await PostAsync(client, $"{baseUrl}/check-out/initialize/{Settings.MerchantId}/{orderId}",
            new { dateTime = initialize.datetime, sensitiveData = Encrypt(gatewayKey, initialize), signature = Sign(merchantKey, initialize) },
            request.ClientIp, cancellationToken);
        var opened = Open(merchantKey, initializeAnswer);
        var paymentReferenceId = GatewayHttp.Text(opened, "paymentReferenceId");
        var serverChallenge = GatewayHttp.Text(opened, "challenge");
        if (string.IsNullOrEmpty(paymentReferenceId) || string.IsNullOrEmpty(serverChallenge))
        {
            throw new GatewayRejectedException("NAGAD_REFUSED", "Nagad refused to initialise the payment.");
        }

        var complete = new
        {
            merchantId = Settings.MerchantId,
            orderId,
            currencyCode = "050",
            amount = request.Charge.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            challenge = serverChallenge
        };
        var completeAnswer = await PostAsync(client, $"{baseUrl}/check-out/complete/{paymentReferenceId}",
            new
            {
                sensitiveData = Encrypt(gatewayKey, complete),
                signature = Sign(merchantKey, complete),
                merchantCallbackURL = request.Returns.Webhook.ToString(),
                additionalMerchantInfo = new { donationReference = request.Reference }
            },
            request.ClientIp, cancellationToken);

        if (!string.Equals(GatewayHttp.Text(completeAnswer, "status"), "Success", StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayRejectedException("NAGAD_REFUSED", "Nagad refused to complete the payment.");
        }

        return new CheckoutSession(paymentReferenceId, GatewayHttp.RedirectUrl(GatewayHttp.Text(completeAnswer, "callBackUrl"), "GATEWAY_BAD_RESPONSE", Settings.Sandbox));
    }

    /// <summary>Nagad does not sign its browser callback: `payment_ref_id` and `status` are claims, and the payment-verification query decides.</summary>
    public WebhookParse ParseWebhook(IncomingWebhook webhook)
    {
        if (!webhook.Query.TryGetValue("payment_ref_id", out var paymentRef) || paymentRef.Length is 0 or > 100)
        {
            return new WebhookParse(WebhookAuthenticity.Invalid, null, null, null);
        }

        return new WebhookParse(WebhookAuthenticity.NotSigned, null, paymentRef, $"{paymentRef}:{(webhook.Query.TryGetValue("status", out var status) ? status : string.Empty)}");
    }

    public async Task<PaymentVerification> VerifyAsync(VerifyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.ProviderPaymentId))
        {
            return new PaymentVerification(PaymentOutcome.Pending, null, null);
        }

        var baseUrl = (Settings.BaseUrl ?? (Settings.Sandbox ? SandboxUrl : LiveUrl)).TrimEnd('/');
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/verify/payment/{Uri.EscapeDataString(request.ProviderPaymentId)}");
        message.Headers.TryAddWithoutValidation("X-KM-Api-Version", "v-0.2.0");
        message.Headers.TryAddWithoutValidation("X-KM-IP-V4", "127.0.0.1");
        message.Headers.TryAddWithoutValidation("X-KM-Client-Type", "PC_WEB");
        var body = await GatewayHttp.SendForJsonAsync(GatewayHttp.Client(httpClients, ClientName, options.Value.TimeoutSeconds), message, cancellationToken);

        // The order id is our reference without its hyphen (KJD-XXXXXXXXXX).
        var order = GatewayHttp.Text(body, "orderId");
        var reference = order is { Length: 13 } && order.StartsWith("KJD", StringComparison.Ordinal) ? $"KJD-{order[3..]}" : order;
        return GatewayHttp.Text(body, "status")?.ToUpperInvariant() switch
        {
            "SUCCESS" => new PaymentVerification(PaymentOutcome.Paid, GatewayHttp.Amount(GatewayHttp.Text(body, "amount"), Currencies.Bdt), reference),
            "FAILED" or "FAIL" => new PaymentVerification(PaymentOutcome.Failed, null, reference),
            "ABORTED" or "CANCELLED" => new PaymentVerification(PaymentOutcome.Cancelled, null, reference),
            _ => new PaymentVerification(PaymentOutcome.Pending, null, reference)
        };
    }

    private string Timestamp() => clock.GetUtcNow().ToOffset(Dhaka).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> PostAsync(HttpClient client, string url, object payload, string clientIp, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        message.Headers.TryAddWithoutValidation("X-KM-Api-Version", "v-0.2.0");
        message.Headers.TryAddWithoutValidation("X-KM-IP-V4", clientIp);
        message.Headers.TryAddWithoutValidation("X-KM-Client-Type", "PC_WEB");
        return await GatewayHttp.SendForJsonAsync(client, message, cancellationToken);
    }

    // Nagad's answers carry the useful part encrypted to the merchant key. A plain error body (no sensitiveData) is a refusal.
    private static JsonElement Open(RSA merchantKey, JsonElement answer)
    {
        var sensitive = GatewayHttp.Text(answer, "sensitiveData");
        if (sensitive is null)
        {
            throw new GatewayRejectedException("NAGAD_REFUSED", "Nagad refused the request.");
        }

        try
        {
            var plain = merchantKey.Decrypt(Convert.FromBase64String(sensitive), RSAEncryptionPadding.Pkcs1);
            using var document = JsonDocument.Parse(plain);
            return document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException)
        {
            throw new GatewayUnavailableException("GATEWAY_BAD_RESPONSE", "Nagad's answer could not be opened.", exception);
        }
    }

    private static string Encrypt(RSA gatewayKey, object payload) =>
        Convert.ToBase64String(gatewayKey.Encrypt(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), RSAEncryptionPadding.Pkcs1));

    private static string Sign(RSA merchantKey, object payload) =>
        Convert.ToBase64String(merchantKey.SignData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    /// <summary>Keys arrive as PEM or as the bare base64 DER Nagad's portal shows.</summary>
    internal static RSA Import(string key, bool privateKey)
    {
        var rsa = RSA.Create();
        try
        {
            if (key.Contains("-----BEGIN", StringComparison.Ordinal))
            {
                rsa.ImportFromPem(key);
            }
            else if (privateKey)
            {
                rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(key), out _);
            }
            else
            {
                rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            }

            return rsa;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            rsa.Dispose();
            throw new GatewayRejectedException("NAGAD_KEY_INVALID", "The configured Nagad key could not be read.");
        }
    }
}
