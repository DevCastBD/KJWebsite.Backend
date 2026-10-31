using KJWebsite.BuildingBlocks.Finance;

namespace FinanceService.Payments;

public static class GatewayIds
{
    public const string Bkash = "bkash";
    public const string Nagad = "nagad";
    public const string SslCommerz = "sslcommerz";
    public const string Stripe = "stripe";
    public const string PayPal = "paypal";

    public static readonly IReadOnlyList<string> All = [Bkash, Nagad, SslCommerz, Stripe, PayPal];
}

/// <summary>Where the gateway sends the donor back. Every URL lands on our own return endpoint, which only redirects to the site: a return never confirms a payment.</summary>
public sealed record ReturnUrls(Uri Success, Uri Fail, Uri Cancel);

public sealed record CheckoutRequest(
    string DonationId,
    string Reference,
    Money Charge,
    string DonorName,
    string DonorEmail,
    string? DonorPhone,
    string Language,
    ReturnUrls Returns,
    string ClientIp);

/// <summary>A hosted checkout the donor is redirected to. `ProviderPaymentId` is the gateway's own handle, kept so confirmation (BE-056) can find the payment.</summary>
public sealed record CheckoutSession(string ProviderPaymentId, Uri RedirectUrl);

/// <summary>
/// One payment provider. Everything gateway-specific lives in its implementation: adding a provider means writing a class that implements
/// this and registering it (<c>AddPaymentGateway</c>); donation, receipt and ledger code never changes.
/// Implementations only start a hosted checkout. They never decide that money arrived (webhooks do, BE-056) and must throw
/// <see cref="GatewayUnavailableException"/> or <see cref="GatewayRejectedException"/>, never return a half-made session.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>The id the frontend sends and the status API echoes: one of <see cref="GatewayIds"/>.</summary>
    string Id { get; }

    /// <summary>True when the adapter is switched on and holds every credential it needs; a half-configured adapter is simply off.</summary>
    bool IsEnabled { get; }

    bool Supports(string currency);

    Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken);
}

public abstract class GatewayException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>A short stable code kept with the failed attempt; never contains credentials or donor data.</summary>
    public string Code { get; } = code;
}

/// <summary>Timeout, network failure, 5xx or an unreadable answer: the gateway may well work on the next try.</summary>
public sealed class GatewayUnavailableException(string code, string message, Exception? inner = null) : GatewayException(code, message, inner);

/// <summary>The gateway understood and refused (bad credentials, invalid request): retrying the same call will not help.</summary>
public sealed class GatewayRejectedException(string code, string message) : GatewayException(code, message);
