namespace MembershipService.Data.Entities;

public static class VolunteerHourStatuses
{
    public const string Submitted = "submitted";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

/// <summary>
/// Hours a member says they volunteered. Nothing counts anywhere until an organiser other than the member approves it; a rejection
/// carries a reason the volunteer can read. Published totals are sums over approved rows only.
/// </summary>
public sealed class VolunteerHourEntity
{
    public string Id { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;

    /// <summary>Content slug of the project the work was for.</summary>
    public string ProjectSlug { get; set; } = string.Empty;

    /// <summary>Content slug of the event the work was at, when it was at one.</summary>
    public string? EventSlug { get; set; }

    public DateOnly WorkDate { get; set; }
    public int Minutes { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = VolunteerHourStatuses.Submitted;
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>Identity user id of the organiser who decided.</summary>
    public string? ReviewedBy { get; set; }

    public string? RejectionReason { get; set; }
}

/// <summary>A volunteer opportunity on the board: bilingual text, the skills it wants and its shifts.</summary>
public sealed class VolunteerOpportunityEntity
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string TitleEn { get; set; } = string.Empty;
    public string TitleBn { get; set; } = string.Empty;
    public string DescriptionEn { get; set; } = string.Empty;
    public string DescriptionBn { get; set; } = string.Empty;
    public string? ProjectSlug { get; set; }

    /// <summary>Lower-case skills, comma-separated; matched against `MemberEntity.Expertise`.</summary>
    public string Skills { get; set; } = string.Empty;

    public bool IsOpen { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public List<VolunteerShiftEntity> Shifts { get; set; } = [];
}

public sealed class VolunteerShiftEntity
{
    public string Id { get; set; } = string.Empty;
    public string OpportunityId { get; set; } = string.Empty;
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public int Slots { get; set; }

    /// <summary>Active sign-ups. A concurrency token, so two people taking the last slot cannot both succeed.</summary>
    public int SignedUp { get; set; }
}

public sealed class VolunteerSignupEntity
{
    public string Id { get; set; } = string.Empty;
    public string ShiftId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string Status { get; set; } = "signed_up";
    public DateTimeOffset CreatedAt { get; set; }
}
