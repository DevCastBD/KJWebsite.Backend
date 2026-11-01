namespace FinanceService.Donations;

public sealed record DesignationRequest(string? Type, string? Id);

public sealed record DonorRequest(string? Name, string? Email, string? Phone);

/// <summary>The public donation form. `Amount` is a decimal string ("1250.50"), never a JSON number.</summary>
public sealed record DonationRequest(
    string? Amount,
    string? Currency,
    string? Frequency,
    DesignationRequest? Designation,
    DonorRequest? Donor,
    bool Anonymous,
    string? Dedication,
    bool CoverFees,
    string? Language,
    string? Gateway = null);

/// <summary>
/// What the donor gets back. `Fee` and `Total` are present only when the donor covers the fee; `Amount` is always the designated gift.
/// `Gateway` and `CheckoutUrl` are present when a hosted checkout was started: the frontend sends the donor there. When none could be
/// (no gateway switched on, or it was down) `Fallback` is `offline` and the donation stays pending for the offline-giving path. No donor detail is ever echoed.
/// </summary>
public sealed record CreatedDonation(
    string Id,
    string Reference,
    string Status,
    string Amount,
    string Currency,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Fee,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Total,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Gateway = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? CheckoutUrl = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Fallback = null);

public sealed record DonationStatusView(
    string Reference,
    string Status,
    string Amount,
    string Currency,
    bool Anonymous,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Gateway = null);

public sealed record CurrencyPresets(string Code, string[] Presets, string Min, string Max);

public sealed record DonationPresets(string DefaultCurrency, IReadOnlyList<CurrencyPresets> Currencies, string[] Frequencies);

/// <summary>A row of the finance back office. `Method` is the gateway of the latest started checkout; null until one exists (offline recording, BE-058, adds the rest).</summary>
public sealed record FinanceDonationRow(
    string Id,
    string Reference,
    string Date,
    string DonorName,
    bool Anonymous,
    string Amount,
    string Fee,
    string Currency,
    string? Method,
    bool Offline,
    string Designation,
    string Kind,
    string Status);

public sealed record FinanceDonationEvent(string Status, string? Note, DateTimeOffset At);

/// <summary>What the gateway settled, apart from the designated amount: `ExchangeRate` is as the gateway reported it.</summary>
public sealed record SettlementView(string Gateway, string Amount, string Currency, string? Fee, string? ExchangeRate, string? TransactionId);

public sealed record FinanceDonationDetail(
    FinanceDonationRow Donation,
    string DonorEmail,
    string? DonorPhone,
    string? Dedication,
    string? RecurringPlanId,
    string? ReversesDonationId,
    string? ReversedByDonationId,
    string? ReversalReason,
    IReadOnlyList<FinanceDonationEvent> Events,
    SettlementView? Settlement = null,
    FinanceService.Offline.OfflineView? Offline = null);

public sealed record ReversalRequest(string? Reason);

public sealed record CurrencyTotals(string Currency, string Confirmed, string Pending, int Count);

public sealed record RecurringPlanRow(string Id, string Interval, string Amount, string Fee, string Currency, string Designation, bool Anonymous, string DonorName, string StartsOn);
