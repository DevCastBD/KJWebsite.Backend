namespace FinanceService.Data.Entities;

/// <summary>
/// A gateway delivery that has been handled. `Gateway` + `EventKey` is unique, so a replayed webhook finds its earlier record and does nothing again.
/// Written only after the delivery was dealt with, so a call that failed midway (for example the gateway could not be asked) can be redelivered and retried.
/// </summary>
public sealed class PaymentEventEntity
{
    public long Id { get; set; }
    public string Gateway { get; set; } = string.Empty;
    public string EventKey { get; set; } = string.Empty;
    public string? DonationId { get; set; }

    /// <summary>`confirmed`, `already_confirmed`, `failed`, `cancelled` or `ignored`.</summary>
    public string Outcome { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }
}

public static class RejectionReasons
{
    public const string BadSignature = "BAD_SIGNATURE";
    public const string UnknownGateway = "UNKNOWN_GATEWAY";
    public const string GatewayDisabled = "GATEWAY_DISABLED";
    public const string UnknownPayment = "UNKNOWN_PAYMENT";
    public const string ReferenceMismatch = "REFERENCE_MISMATCH";
    public const string AmountMismatch = "AMOUNT_MISMATCH";
}

/// <summary>A webhook or callback that was refused (unsigned, wrongly signed, unknown payment, or a paid amount that does not match the donation). The alerting record: append-only.</summary>
public sealed class WebhookRejectionEntity
{
    public long Id { get; set; }
    public string Gateway { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public string SourceIp { get; set; } = string.Empty;

    /// <summary>The donation reference the call named, trimmed; never the body.</summary>
    public string? Reference { get; set; }

    public DateTimeOffset At { get; set; }
}

public static class MatchStatuses
{
    public const string Matched = "matched";
    public const string Unmatched = "unmatched";
    public const string AmountMismatch = "amount_mismatch";
    public const string Duplicate = "duplicate";
}

/// <summary>One settlement a gateway paid out, imported by finance. Append-only; `Gateway` + `PayoutRef` is unique so a batch cannot be imported twice.</summary>
public sealed class PayoutEntity
{
    public long Id { get; set; }
    public string Gateway { get; set; } = string.Empty;
    public string PayoutRef { get; set; } = string.Empty;
    public DateOnly PaidOn { get; set; }
    public string? ImportedBy { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
    public List<PayoutLineEntity> Lines { get; set; } = [];
}

/// <summary>One payment inside a payout: what the gateway says it settled (`Gross`, `Fee`, `Net`) and how that compared with our donation.</summary>
public sealed class PayoutLineEntity
{
    public long Id { get; set; }
    public long PayoutId { get; set; }
    public PayoutEntity? Payout { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string? DonationId { get; set; }
    public decimal Gross { get; set; }
    public decimal Fee { get; set; }
    public decimal Net { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string MatchStatus { get; set; } = MatchStatuses.Unmatched;

    /// <summary>Why a line did not match, as a short code.</summary>
    public string? Reason { get; set; }

    public long? PostingId { get; set; }
    public PostingEntity? Posting { get; set; }
}

/// <summary>
/// What the gateway actually settled for a confirmed donation. Kept apart from the donation, whose `Amount` and `Currency` are what the donor designated:
/// the two can differ (a card charged in another currency, less the gateway's fee) and neither is ever converted into the other. One per donation, append-only.
/// </summary>
public sealed class PaymentSettlementEntity
{
    public long Id { get; set; }
    public string DonationId { get; set; } = string.Empty;
    public string Gateway { get; set; } = string.Empty;

    /// <summary>The gateway's own transaction (Stripe payment intent, PayPal capture): how refunds and disputes find the donation.</summary>
    public string? TransactionId { get; set; }

    public decimal SettledAmount { get; set; }
    public string SettledCurrency { get; set; } = string.Empty;
    public decimal? Fee { get; set; }

    /// <summary>The rate the gateway reports, when it converted; recorded as given, never used by us to convert anything.</summary>
    public decimal? ExchangeRate { get; set; }

    public DateTimeOffset At { get; set; }
}

public static class NoticeKinds
{
    public const string Refund = "refund";
    public const string Dispute = "dispute";
}

/// <summary>A signed refund or dispute message from a gateway, kept for finance to act on (reversal stays a human step until BE-068). Unique per gateway event; append-only.</summary>
public sealed class ProviderNoticeEntity
{
    public long Id { get; set; }
    public string Gateway { get; set; } = string.Empty;
    public string EventKey { get; set; } = string.Empty;
    public string Kind { get; set; } = NoticeKinds.Refund;
    public string DonationId { get; set; } = string.Empty;
    public string? TransactionId { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public DateTimeOffset At { get; set; }
}
