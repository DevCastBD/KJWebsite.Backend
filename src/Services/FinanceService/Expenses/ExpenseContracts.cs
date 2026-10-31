namespace FinanceService.Expenses;

/// <summary>An expense claim as the frontend submits it, plus optional detail. `Category` is one of the configured keys (programme, fundraising, administrative, events, other).</summary>
public sealed record ExpenseRequest(string? Payee, string? Description, string? Category, string? Amount, string? Currency, string? Project, string? Date, bool Reimbursement);

public sealed record ExpenseDecisionRequest(string? Note);

public sealed record ExpensePayRequest(string? Method, string? Reference, string? PaidOn);

public sealed record ExpenseReceiptView(string Id, string FileName, string ContentType, long SizeBytes, string Sha256, string UploadedBy, DateTimeOffset UploadedAt, string RetainUntil);

public sealed record ExpenseApprovalView(string ApprovedBy, string? Note, DateTimeOffset At);

public sealed record ExpensePaymentView(string Method, string? Reference, string PaidOn, string PaidBy, DateTimeOffset At);

/// <summary>The frontend's expense row, with the approval progress. `Status` is pending, approved or rejected; `Paid` is separate.</summary>
public sealed record ExpenseRow(
    string Id,
    string Payee,
    string Description,
    string Category,
    string Amount,
    string Currency,
    string? Project,
    bool Reimbursement,
    string SubmittedById,
    string SubmittedByName,
    string SubmittedOn,
    string Status,
    int Approvals,
    int RequiredApprovals,
    bool Paid,
    string? DecidedByName,
    string? Note);

public sealed record ExpenseDetail(
    ExpenseRow Expense,
    string AccountCode,
    string IncurredOn,
    IReadOnlyList<ExpenseApprovalView> ApprovalList,
    IReadOnlyList<ExpenseReceiptView> Receipts,
    ExpensePaymentView? Payment,
    string? PostingId);

public sealed record RollupLine(string Key, string Currency, string Approved, string Paid, int Count);

public sealed record ExpenseRollups(IReadOnlyList<RollupLine> ByProject, IReadOnlyList<RollupLine> ByCategory);

public sealed record RollupReconciliationLine(string Dimension, string Key, string Currency, string Expenses, string Ledger, string Difference);

public sealed record RollupReconciliation(bool Reconciled, IReadOnlyList<RollupReconciliationLine> Lines);
