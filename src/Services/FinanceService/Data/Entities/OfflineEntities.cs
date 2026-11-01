namespace FinanceService.Data.Entities;

public static class OfflineMethods
{
    public const string Bank = "bank";
    public const string Cash = "cash";
    public const string Cheque = "cheque";
    public const string Bkash = "bkash";
    public const string Nagad = "nagad";
    public const string InKind = "in_kind";

    public static readonly IReadOnlyList<string> All = [Bank, Cash, Cheque, Bkash, Nagad, InKind];

    /// <summary>Money that arrives through a bank or a wallet leaves a trace someone can show; cash and goods may have none, so proof is optional for them.</summary>
    public static bool RequiresProof(string method) => method is Bank or Cheque or Bkash or Nagad;
}

public static class ValuationBases
{
    public const string MarketPrice = "market_price";
    public const string Appraisal = "appraisal";
    public const string DonorReceipt = "donor_receipt";
    public const string Cost = "cost";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [MarketPrice, Appraisal, DonorReceipt, Cost, Other];
}

public static class OfflineDecisions
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

/// <summary>
/// The facts of a gift that arrived outside the gateways, recorded by one person. The donation itself stays `pending` and unposted until a different person
/// approves it. Append-only: a mistake is rejected and recorded again, never edited. For an in-kind gift the donation `Amount` is the valuation, and the basis
/// and a description of the item are required.
/// </summary>
public sealed class OfflineRecordEntity
{
    public string Id { get; set; } = string.Empty;
    public string DonationId { get; set; } = string.Empty;
    public string Method { get; set; } = OfflineMethods.Bank;

    /// <summary>The day the money (or goods) arrived, which can be earlier than the day it was recorded.</summary>
    public DateOnly ReceivedOn { get; set; }

    /// <summary>A bank transaction id, cheque number or wallet transaction id, to match against statements.</summary>
    public string? ReferenceNo { get; set; }

    public string? Note { get; set; }
    public string? ItemDescription { get; set; }
    public string? ValuationBasis { get; set; }
    public string? ValuationNote { get; set; }
    public string RecordedBy { get; set; } = string.Empty;
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>The one decision on a record. Unique per record, so two approvers cannot both decide it.</summary>
public sealed class OfflineDecisionEntity
{
    public long Id { get; set; }
    public string OfflineRecordId { get; set; } = string.Empty;
    public string Decision { get; set; } = OfflineDecisions.Approved;
    public string DecidedBy { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>A proof document (bank slip, cheque photo, receipt). A financial record: never deleted by any API, kept until `RetainUntil`, and its SHA-256 is kept so tampering shows.</summary>
public sealed class OfflineProofEntity
{
    public string Id { get; set; } = string.Empty;
    public string OfflineRecordId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public string UploadedBy { get; set; } = string.Empty;
    public DateTimeOffset UploadedAt { get; set; }
    public DateOnly RetainUntil { get; set; }
}
