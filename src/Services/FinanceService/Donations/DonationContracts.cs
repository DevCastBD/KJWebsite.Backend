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
    string? Language);

/// <summary>
/// What the donor gets back. `Fee` and `Total` are present only when the donor covers the fee; `Amount` is always the designated gift.
/// No donor detail is ever echoed.
/// </summary>
public sealed record CreatedDonation(
    string Id,
    string Reference,
    string Status,
    string Amount,
    string Currency,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Fee,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Total);

public sealed record DonationStatusView(string Reference, string Status, string Amount, string Currency, bool Anonymous);

public sealed record CurrencyPresets(string Code, string[] Presets, string Min, string Max);

public sealed record DonationPresets(string DefaultCurrency, IReadOnlyList<CurrencyPresets> Currencies, string[] Frequencies);

/// <summary>A row of the finance back office. `Method` is null until gateways and offline recording exist (BE-055, BE-058).</summary>
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

public sealed record FinanceDonationDetail(
    FinanceDonationRow Donation,
    string DonorEmail,
    string? DonorPhone,
    string? Dedication,
    string? RecurringPlanId,
    string? ReversesDonationId,
    string? ReversedByDonationId,
    string? ReversalReason,
    IReadOnlyList<FinanceDonationEvent> Events);

public sealed record ReversalRequest(string? Reason);

public sealed record CurrencyTotals(string Currency, string Confirmed, string Pending, int Count);

public sealed record RecurringPlanRow(string Id, string Interval, string Amount, string Fee, string Currency, string Designation, bool Anonymous, string DonorName, string StartsOn);
