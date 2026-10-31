using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinanceService.Data.Entities;
using KJWebsite.BuildingBlocks.Finance;
using Microsoft.Extensions.Options;

namespace FinanceService.Payments;

/// <summary>
/// SSLCommerz hosted checkout (cards, mobile wallets, net banking). A session is created with a form POST and the donor is sent to the returned
/// `GatewayPageURL`; the gateway later posts the outcome to the return URLs and, separately, to the webhook (BE-056). Built to the published API
/// and exercised against test doubles only: it has not been run against a live sandbox account.
/// </summary>
public sealed class SslCommerzGateway(IHttpClientFactory httpClients, IOptions<PaymentOptions> options) : IPaymentGateway
{
    public const string ClientName = "gateway-sslcommerz";
    private const string SandboxUrl = "https://sandbox.sslcommerz.com";
    private const string LiveUrl = "https://securepay.sslcommerz.com";

    private SslCommerzOptions Settings => options.Value.SslCommerz;

    public string Id => GatewayIds.SslCommerz;

    public bool IsEnabled => Settings.Enabled && !string.IsNullOrWhiteSpace(Settings.StoreId) && !string.IsNullOrWhiteSpace(Settings.StorePassword);

    public bool Supports(string currency) => currency is Currencies.Bdt or Currencies.Usd or Currencies.Eur or Currencies.Gbp;

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = (Settings.BaseUrl ?? (Settings.Sandbox ? SandboxUrl : LiveUrl)).TrimEnd('/');
        var form = new Dictionary<string, string>
        {
            ["store_id"] = Settings.StoreId,
            ["store_passwd"] = Settings.StorePassword,
            ["total_amount"] = request.Charge.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            ["currency"] = request.Charge.Currency,
            ["tran_id"] = request.Reference,
            ["success_url"] = request.Returns.Success.ToString(),
            ["fail_url"] = request.Returns.Fail.ToString(),
            ["cancel_url"] = request.Returns.Cancel.ToString(),
            ["ipn_url"] = request.Returns.Webhook.ToString(),
            ["cus_name"] = request.DonorName,
            ["cus_email"] = request.DonorEmail,
            ["cus_add1"] = "N/A",
            ["cus_city"] = "N/A",
            ["cus_country"] = "Bangladesh",
            ["cus_phone"] = request.DonorPhone ?? "N/A",
            ["shipping_method"] = "NO",
            ["product_name"] = "Donation",
            ["product_category"] = "Donation",
            ["product_profile"] = "non-physical-goods",
            ["value_a"] = request.DonationId
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/gwprocess/v4/api.php") { Content = new FormUrlEncodedContent(form) };
        var client = GatewayHttp.Client(httpClients, ClientName, options.Value.TimeoutSeconds);
        var body = await GatewayHttp.SendForJsonAsync(client, message, cancellationToken);

        if (!string.Equals(GatewayHttp.Text(body, "status"), "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayRejectedException("SSLCOMMERZ_REFUSED", "SSLCommerz refused to start the session.");
        }

        var session = GatewayHttp.Text(body, "sessionkey");
        if (string.IsNullOrEmpty(session))
        {
            throw new GatewayUnavailableException("GATEWAY_BAD_RESPONSE", "SSLCommerz returned no session key.");
        }

        return new CheckoutSession(session, GatewayHttp.RedirectUrl(GatewayHttp.Text(body, "GatewayPageURL"), "GATEWAY_BAD_RESPONSE", Settings.Sandbox));
    }

    /// <summary>
    /// The IPN is signed: `verify_key` lists the fields that were signed, and `verify_sign` is the MD5 of those fields plus `store_passwd` (itself the MD5 of
    /// the store password) sorted by name and joined as a query string. A missing, malformed or wrong signature is invalid; a good one only proves the
    /// call came from SSLCommerz, not that the payment succeeded.
    /// </summary>
    public WebhookParse ParseWebhook(IncomingWebhook webhook)
    {
        var form = webhook.Form;
        var invalid = new WebhookParse(WebhookAuthenticity.Invalid, null, null, null);
        if (!form.TryGetValue("verify_sign", out var sign) || !form.TryGetValue("verify_key", out var keyList) || !form.TryGetValue("tran_id", out var tran) || string.IsNullOrWhiteSpace(IsEnabledSecret()))
        {
            return invalid;
        }

        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keyList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!form.TryGetValue(key, out var value))
            {
                return invalid;
            }

            signed[key] = value;
        }

        signed["store_passwd"] = Md5(Settings.StorePassword);
        var expected = Md5(string.Join('&', signed.Select(p => $"{p.Key}={p.Value}")));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(sign.Trim().ToLowerInvariant())))
        {
            return invalid;
        }

        var eventId = form.TryGetValue("val_id", out var val) && val.Length > 0 ? val : tran;
        return new WebhookParse(WebhookAuthenticity.Verified, tran, null, $"{eventId}:{(form.TryGetValue("status", out var status) ? status : string.Empty)}");
    }

    /// <summary>Looks the transaction up by our `tran_id` (the donation reference), so the same call serves a webhook and a later reconciliation.</summary>
    public async Task<PaymentVerification> VerifyAsync(VerifyRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = (Settings.BaseUrl ?? (Settings.Sandbox ? SandboxUrl : LiveUrl)).TrimEnd('/');
        var url = $"{baseUrl}/validator/api/merchantTransIDvalidationAPI.php?tran_id={Uri.EscapeDataString(request.Reference)}&store_id={Uri.EscapeDataString(Settings.StoreId)}&store_passwd={Uri.EscapeDataString(Settings.StorePassword)}&format=json";
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        var body = await GatewayHttp.SendForJsonAsync(GatewayHttp.Client(httpClients, ClientName, options.Value.TimeoutSeconds), message, cancellationToken);

        if (!string.Equals(GatewayHttp.Text(body, "APIConnect"), "DONE", StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayRejectedException("SSLCOMMERZ_REFUSED", "SSLCommerz refused the status query.");
        }

        var rows = body.TryGetProperty("element", out var element) && element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().ToList() : [];
        // A donor may retry, which leaves several rows: one paid row wins over any failed one.
        var paid = rows.FirstOrDefault(r => GatewayHttp.Text(r, "status") is "VALID" or "VALIDATED");
        if (paid.ValueKind == JsonValueKind.Object)
        {
            return new PaymentVerification(PaymentOutcome.Paid, GatewayHttp.Amount(GatewayHttp.Text(paid, "amount"), GatewayHttp.Text(paid, "currency_type")), GatewayHttp.Text(paid, "tran_id"));
        }

        var last = rows.Count == 0 ? default : rows[^1];
        return new PaymentVerification(GatewayHttp.Text(last, "status")?.ToUpperInvariant() switch
        {
            "FAILED" => PaymentOutcome.Failed,
            "CANCELLED" => PaymentOutcome.Cancelled,
            _ => PaymentOutcome.Pending
        }, null, GatewayHttp.Text(last, "tran_id"));
    }

    private string IsEnabledSecret() => Settings.StorePassword;

    // SSLCommerz defines its IPN signature as MD5; this only checks a signature it made, it protects nothing else.
#pragma warning disable CA5351
    private static string Md5(string value) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
#pragma warning restore CA5351
}
