namespace FinanceService.Data.Entities;

public static class ExpenseDecisions
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

/// <summary>
/// An expense claim. `RequiredApprovals` is fixed at submission from the amount tier then in force, so the bar an expense must clear does not move under it. Never
/// updated: approvals, the final decision, receipts and the payment are separate append-only rows, and a mistake is rejected and submitted again.
/// </summary>
public sealed class ExpenseEntity
{
    public string Id { get; set; } = string.Empty;
    public string Payee { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CategoryKey { get; set; } = string.Empty;

    /// <summary>The ledger expense account for the category, as it was when submitted.</summary>
    public string AccountCode { get; set; } = string.Empty;

    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string? ProjectSlug { get; set; }

    /// <summary>Money owed to the person who paid out of pocket (a reimbursement) rather than to a vendor: it decides the payable account.</summary>
    public bool IsReimbursement { get; set; }

    public DateOnly IncurredOn { get; set; }
    public int RequiredApprovals { get; set; }
    public string SubmittedBy { get; set; } = string.Empty;
    public string SubmittedByName { get; set; } = string.Empty;
    public DateTimeOffset SubmittedAt { get; set; }
}

public sealed class ExpenseReceiptEntity
{
    public string Id { get; set; } = string.Empty;
    public string ExpenseId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public string UploadedBy { get; set; } = string.Empty;
    public DateTimeOffset UploadedAt { get; set; }
    public DateOnly RetainUntil { get; set; }
}

/// <summary>One approver's yes. Unique per expense and approver, so nobody approves twice.</summary>
public sealed class ExpenseApprovalEntity
{
    public long Id { get; set; }
    public string ExpenseId { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>The end of the workflow: unique per expense. An approval decision carries the ledger posting made in the same commit.</summary>
public sealed class ExpenseDecisionEntity
{
    public long Id { get; set; }
    public string ExpenseId { get; set; } = string.Empty;
    public string Decision { get; set; } = ExpenseDecisions.Approved;
    public string DecidedBy { get; set; } = string.Empty;
    public string? Note { get; set; }
    public long? PostingId { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>The money going out for an approved expense, recorded by someone other than the submitter, with its own posting. Unique per expense.</summary>
public sealed class ExpensePaymentEntity
{
    public long Id { get; set; }
    public string ExpenseId { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public DateOnly PaidOn { get; set; }
    public string PaidBy { get; set; } = string.Empty;
    public long? PostingId { get; set; }
    public DateTimeOffset At { get; set; }
}
