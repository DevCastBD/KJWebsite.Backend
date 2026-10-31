namespace FinanceService.Data.Entities;

public static class DonationKinds
{
    public const string OneTime = "one_time";

    /// <summary>The first instalment of a recurring plan; later instalments are created when the plan is charged.</summary>
    public const string Recurring = "recurring";

    /// <summary>A correcting entry: negative amount and fee, pointing at the donation it reverses.</summary>
    public const string Reversal = "reversal";
}

public static class DonationStatuses
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Pending, Confirmed, Failed, Cancelled];
}

public static class Designations
{
    public const string General = "general";
    public const string Campaign = "campaign";
    public const string Project = "project";

    public static readonly IReadOnlyList<string> All = [General, Campaign, Project];
}

public static class RecurringIntervals
{
    public const string Monthly = "monthly";
    public const string Yearly = "yearly";

    public static readonly IReadOnlyList<string> All = [Monthly, Yearly];
}

/// <summary>
/// One entry in the donations ledger. Rows are never updated or deleted: a state change is a new <see cref="DonationEventEntity"/>, and a
/// correction is a new row of kind `reversal` with negated amounts. `Amount` is what the donor designated, `FeeAmount` the cover-the-fee
/// surcharge on top; the gateway is asked to charge `Amount + FeeAmount`, so both are kept for reconciliation. Donor details are encrypted
/// and only ever read back by finance staff, and never for display when `IsAnonymous` is set.
/// </summary>
public sealed class DonationEntity
{
    public string Id { get; set; } = string.Empty;

    /// <summary>The unguessable public reference the donor's status page asks about.</summary>
    public string Reference { get; set; } = string.Empty;

    public string Kind { get; set; } = DonationKinds.OneTime;
    public decimal Amount { get; set; }
    public decimal FeeAmount { get; set; }
    public string Currency { get; set; } = string.Empty;

    public string DesignationType { get; set; } = Designations.General;
    public string? DesignationId { get; set; }

    public bool IsAnonymous { get; set; }
    public string? Dedication { get; set; }
    public string DonorNameProtected { get; set; } = string.Empty;
    public string DonorEmailProtected { get; set; } = string.Empty;
    public string? DonorPhoneProtected { get; set; }
    public string Language { get; set; } = "en";

    public string? RecurringPlanId { get; set; }
    public string? ReversesDonationId { get; set; }
    public string? ReversalReason { get; set; }

    /// <summary>The caller who posted a reversal; null for public intake.</summary>
    public string? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The UTC date of <see cref="CreatedAt"/>, kept separately so date-range filters work on every provider.</summary>
    public DateOnly ReceivedOn { get; set; }

    public List<DonationEventEntity> Events { get; set; } = [];
}

/// <summary>One step in a donation's life. The latest event (highest id) is its current status.</summary>
public sealed class DonationEventEntity
{
    public long Id { get; set; }
    public string DonationId { get; set; } = string.Empty;
    public string Status { get; set; } = DonationStatuses.Pending;
    public string? Note { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>A standing instruction to give the same amount every interval. Created with the first instalment; changing or ending it will be new events, never edits.</summary>
public sealed class RecurringPlanEntity
{
    public string Id { get; set; } = string.Empty;
    public string Interval { get; set; } = RecurringIntervals.Monthly;
    public decimal Amount { get; set; }
    public decimal FeeAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string DesignationType { get; set; } = Designations.General;
    public string? DesignationId { get; set; }
    public bool IsAnonymous { get; set; }
    public string? Dedication { get; set; }
    public string DonorNameProtected { get; set; } = string.Empty;
    public string DonorEmailProtected { get; set; } = string.Empty;
    public string? DonorPhoneProtected { get; set; }
    public string Language { get; set; } = "en";
    public DateOnly StartsOn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
