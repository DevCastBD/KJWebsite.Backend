namespace FinanceService.Data.Entities;

/// <summary>
/// The receipt for one confirmed donation. Numbers run `KJR-{fiscal year}-{sequence}` with no gaps: a number is only ever taken by a receipt that is saved, receipts are
/// never deleted, and each one carries the hash of the one before it, so a missing or altered receipt shows up when the chain is checked. The fields are a snapshot
/// taken at issue, so the PDF can be produced again exactly as it was. Never updated; corrections are a void or a re-issue, both separate rows.
/// </summary>
public sealed class ReceiptEntity
{
    public string Id { get; set; } = string.Empty;
    public string DonationId { get; set; } = string.Empty;

    /// <summary>The fiscal-year label that names the series, for example `2026-27`.</summary>
    public string Series { get; set; } = string.Empty;

    public long Seq { get; set; }
    public string Number { get; set; } = string.Empty;
    public string PrevHash { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public DateTimeOffset IssuedAt { get; set; }
    public DateOnly ReceivedOn { get; set; }

    public string DonorNameProtected { get; set; } = string.Empty;
    public string DonorEmailProtected { get; set; } = string.Empty;

    /// <summary>A keyed hash of the donor's email, which groups one donor's receipts for statements without decrypting anything.</summary>
    public string DonorKey { get; set; } = string.Empty;

    public decimal Amount { get; set; }
    public decimal FeeAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string? Method { get; set; }
    public string DesignationType { get; set; } = Designations.General;
    public string? DesignationId { get; set; }
    public bool IsAnonymous { get; set; }
    public string Language { get; set; } = "en";
    public bool IsInKind { get; set; }
    public string? ItemDescription { get; set; }
    public string? ValuationBasis { get; set; }

    /// <summary>The organisation details as printed on this receipt when it was issued (JSON).</summary>
    public string OrganisationJson { get; set; } = "{}";
}

/// <summary>A receipt withdrawn because its donation was reversed. Appended, never edited: the receipt keeps its number, so the series stays gapless.</summary>
public sealed class ReceiptVoidEntity
{
    public long Id { get; set; }
    public string ReceiptId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? VoidedBy { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>A copy of a receipt issued again. The original is untouched and keeps its number; each copy is recorded with who asked and why.</summary>
public sealed class ReceiptReissueEntity
{
    public long Id { get; set; }
    public string ReceiptId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string ReissuedBy { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
}

/// <summary>A message for a donor, saved in the same commit as the change that caused it. Delivery is the email work of BE-032/BE-033; until then rows wait here (`SentAt` stays empty).</summary>
public sealed class FinanceNotificationEntity
{
    public long Id { get; set; }
    public string Template { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public string RecipientProtected { get; set; } = string.Empty;

    /// <summary>Data-protected JSON.</summary>
    public string PayloadProtected { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>Records that a donor's statement for a fiscal year was queued. Unique per donor and year, so it is queued at most once.</summary>
public sealed class StatementSendEntity
{
    public long Id { get; set; }
    public string DonorKey { get; set; } = string.Empty;
    public string FiscalYear { get; set; } = string.Empty;
    public string QueuedBy { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
}
