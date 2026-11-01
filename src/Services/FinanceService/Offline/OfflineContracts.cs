namespace FinanceService.Offline;

public sealed record ValuationRequest(string? Basis, string? Note, string? ItemDescription);

/// <summary>An offline gift as the back office records it (the frontend's draft plus optional detail). `Designation` is `general`, `campaign:slug` or `project:slug`.</summary>
public sealed record OfflineDonationRequest(
    string? DonorName,
    string? Email,
    string? Phone,
    string? Amount,
    string? Currency,
    string? Method,
    string? Date,
    string? Designation,
    bool Anonymous,
    string? Dedication,
    string? ReferenceNo,
    string? Note,
    ValuationRequest? Valuation);

public sealed record RejectRequest(string? Reason);

public sealed record ProofView(string Id, string FileName, string ContentType, long SizeBytes, string Sha256, string UploadedBy, DateTimeOffset UploadedAt, string RetainUntil);

public sealed record DecisionView(string Decision, string DecidedBy, string? Reason, DateTimeOffset At);

public sealed record ValuationView(string Basis, string? Note, string ItemDescription);

/// <summary>`Status` is `pending` until a different person decides, then `approved` or `rejected`. For in-kind gifts the donation amount is the valuation.</summary>
public sealed record OfflineView(
    string Id,
    string Method,
    string ReceivedOn,
    string? ReferenceNo,
    string? Note,
    string RecordedBy,
    DateTimeOffset RecordedAt,
    string Status,
    ValuationView? Valuation,
    DecisionView? Decision,
    IReadOnlyList<ProofView> Proofs,
    bool ProofRequired);

public sealed record OfflineDonationCreated(
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
    string Status,
    OfflineView OfflineRecord);
