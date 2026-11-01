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

/// <summary>Where the gateway sends the donor back, plus our webhook address for gateways that call back or notify. Every URL lands on our own return endpoint, which only redirects to the site: a return never confirms a payment.</summary>
public sealed record ReturnUrls(Uri Success, Uri Fail, Uri Cancel, Uri Webhook);

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

    /// <summary>
    /// Reads an incoming webhook or browser callback. It checks the gateway's signature where the gateway signs and says which payment the call is about;
    /// it never decides that money arrived. A call that should be signed but is not, or whose signature is wrong, is <see cref="WebhookAuthenticity.Invalid"/>.
    /// </summary>
    Task<WebhookParse> ParseWebhookAsync(IncomingWebhook webhook, CancellationToken cancellationToken);

    /// <summary>Asks the gateway itself, with our credentials, what became of a payment. This answer, not the call that prompted it, is what confirms or fails a donation.</summary>
    Task<PaymentVerification> VerifyAsync(VerifyRequest request, CancellationToken cancellationToken);
}

/// <summary>An incoming call from a gateway or from a donor's browser coming back from one. Values are as received: untrusted until verified.</summary>
public sealed record IncomingWebhook(
    string Method,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Form,
    string SourceIp,
    IReadOnlyDictionary<string, string>? Headers = null,
    string RawBody = "")
{
    /// <summary>Header values by lower-case name; signatures that cover the exact bytes of the body need the raw body too.</summary>
    public IReadOnlyDictionary<string, string> HeaderValues => Headers ?? new Dictionary<string, string>();
}

/// <summary>A gateway message that is not a payment result: money going back, or a dispute opened. Recorded for finance, never acted on automatically.</summary>
public enum NoticeKind
{
    Refund,
    Dispute
}

/// <summary>What the gateway actually settled for a payment, which can differ from the donor's designated amount (a different currency, less the gateway's fee).</summary>
public sealed record SettlementInfo(Money Settled, Money? Fee, decimal? ExchangeRate, string? TransactionId);

public enum WebhookAuthenticity
{
    /// <summary>The gateway signs its calls and this signature checked out.</summary>
    Verified,

    /// <summary>This gateway does not sign its callbacks; the status re-query is the only check.</summary>
    NotSigned,

    /// <summary>A signature was required and is missing or wrong, or the call is malformed: reject it.</summary>
    Invalid
}

/// <summary>What a webhook is about. `EventKey` identifies this delivery (so a replay is recognised); the identifiers locate the donation and are checked against the gateway's answer.</summary>
/// <summary>`Irrelevant` marks a genuine, authentic message about something we do not act on (an event type we did not ask for): acknowledged, never rejected.</summary>
public sealed record WebhookParse(
    WebhookAuthenticity Authenticity,
    string? Reference,
    string? ProviderPaymentId,
    string? EventKey,
    NoticeKind? Notice = null,
    string? TransactionId = null,
    Money? NoticeAmount = null,
    bool Irrelevant = false);

public sealed record VerifyRequest(string Reference, string? ProviderPaymentId);

public enum PaymentOutcome
{
    Paid,
    Failed,
    Cancelled,
    Pending
}

/// <summary>The gateway's own account of a payment: what happened, and for `Paid` the exact amount and reference it holds.</summary>
public sealed record PaymentVerification(PaymentOutcome Outcome, Money? Amount, string? Reference, SettlementInfo? Settlement = null);

public abstract class GatewayException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>A short stable code kept with the failed attempt; never contains credentials or donor data.</summary>
    public string Code { get; } = code;
}

/// <summary>Timeout, network failure, 5xx or an unreadable answer: the gateway may well work on the next try.</summary>
public sealed class GatewayUnavailableException(string code, string message, Exception? inner = null) : GatewayException(code, message, inner);

/// <summary>The gateway understood and refused (bad credentials, invalid request): retrying the same call will not help.</summary>
public sealed class GatewayRejectedException(string code, string message) : GatewayException(code, message);
