using System.Globalization;
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
}
