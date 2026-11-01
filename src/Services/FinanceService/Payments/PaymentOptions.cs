namespace FinanceService.Payments;

/// <summary>
/// Gateway settings under `Finance:Payments`. Credentials come from the environment or a secret store (for example
/// `Finance__Payments__SslCommerz__StorePassword`) and are never committed: appsettings holds only the switches. An adapter with a missing credential is off.
/// </summary>
public sealed class PaymentOptions
{
    public const string SectionName = "Finance:Payments";

    /// <summary>The public base URL of the API as the gateway reaches it (the gateway's address), used to build return URLs.</summary>
    public string PublicApiBaseUrl { get; set; } = "http://localhost:7000";

    /// <summary>The public site the donor is sent back to after paying.</summary>
    public string SiteBaseUrl { get; set; } = "http://localhost:5173";

    /// <summary>
    /// Preference when the donor does not pick a gateway: the first enabled adapter that supports the currency wins. Empty means <see cref="DefaultOrder"/>.
    /// (A default array here would not work: configuration binding appends to an array rather than replacing it.)
    /// </summary>
    public string[] Order { get; set; } = [];

    public static readonly IReadOnlyList<string> DefaultOrder = [GatewayIds.SslCommerz, GatewayIds.Bkash, GatewayIds.Nagad];

    public IReadOnlyList<string> EffectiveOrder => Order is { Length: > 0 } ? Order : DefaultOrder;

    public int TimeoutSeconds { get; set; } = 10;

    public SslCommerzOptions SslCommerz { get; set; } = new();
    public BkashOptions Bkash { get; set; } = new();
    public NagadOptions Nagad { get; set; } = new();
}

public abstract class GatewayOptions
{
    public bool Enabled { get; set; }

    /// <summary>Sandbox until a live merchant account exists; flipping this is a deliberate production step.</summary>
    public bool Sandbox { get; set; } = true;

    /// <summary>Overrides the sandbox/live address (for a gateway's regional endpoint or a test double).</summary>
    public string? BaseUrl { get; set; }
}

public sealed class SslCommerzOptions : GatewayOptions
{
    public string StoreId { get; set; } = string.Empty;
    public string StorePassword { get; set; } = string.Empty;
}

public sealed class BkashOptions : GatewayOptions
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string AppKey { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;
}

public sealed class NagadOptions : GatewayOptions
{
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>The merchant's RSA private key (PKCS#8, PEM or base64 DER): signs requests and opens the gateway's answers.</summary>
    public string MerchantPrivateKey { get; set; } = string.Empty;

    /// <summary>Nagad's RSA public key (SPKI, PEM or base64 DER): encrypts what we send.</summary>
    public string PgPublicKey { get; set; } = string.Empty;
}
