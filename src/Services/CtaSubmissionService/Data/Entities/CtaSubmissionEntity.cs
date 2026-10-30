namespace CtaSubmissionService.Data.Entities;

public sealed class CtaSubmissionEntity
{
    public string Id { get; set; } = string.Empty;
    public string FormName { get; set; } = string.Empty;
    public string CtaType { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string ValuesJson { get; set; } = "{}";
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Triage state, one of <see cref="SubmissionStatuses"/>.</summary>
    public string Status { get; set; } = SubmissionStatuses.New;

    /// <summary>Identity user id of the team member handling it.</summary>
    public string? AssigneeId { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Gender { get; set; }
    public string? ReasonForJoining { get; set; }
    public string? PresentOrganization { get; set; }
    public string? VolunteeingExperience { get; set; }
    public string? DateOfBirth { get; set; }
    public string? CityOfResidence { get; set; }
    public string? CountryOfResidence { get; set; }
}

public static class SubmissionStatuses
{
    public const string New = "new";
    public const string InReview = "in_review";
    public const string Contacted = "contacted";
    public const string Resolved = "resolved";
    public const string Spam = "spam";
    public const string Archived = "archived";

    public static readonly string[] All = [New, InReview, Contacted, Resolved, Spam, Archived];
}

/// <summary>Who read or changed submissions (personal data). Holds field names and ids, never the data itself.</summary>
public sealed class SubmissionAuditEntity
{
    public Guid Id { get; set; }

    /// <summary>Null for list and export, which touch many submissions.</summary>
    public string? SubmissionId { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string? Detail { get; set; }

    public DateTimeOffset At { get; set; }
}
