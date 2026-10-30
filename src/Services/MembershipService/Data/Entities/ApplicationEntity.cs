namespace MembershipService.Data.Entities;

/// <summary>
/// One membership application. Email, phone, address, blood group and date of birth are stored encrypted
/// (ADR-014); the two hashes let duplicate detection match without decrypting anything.
/// </summary>
public sealed class ApplicationEntity
{
    public string Id { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string? CampaignId { get; set; }
    public string Language { get; set; } = string.Empty;
    public string Status { get; set; } = ApplicationStatuses.Awaiting;
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Gender { get; set; }
    public string AcademicInstitute { get; set; } = string.Empty;
    public string Occupation { get; set; } = string.Empty;
    public string? ReasonForJoining { get; set; }
    public string? PresentOrganization { get; set; }
    public string? VolunteeringExperience { get; set; }
    public string? HeardAboutUs { get; set; }
    public string? CityOfResidence { get; set; }
    public string? CountryOfResidence { get; set; }

    public string? ReviewerId { get; set; }
    public int? Score { get; set; }
    public DateTimeOffset? InterviewAt { get; set; }

    public string EmailProtected { get; set; } = string.Empty;
    public string PhoneProtected { get; set; } = string.Empty;
    public string BloodGroupProtected { get; set; } = string.Empty;
    public string CurrentAddressProtected { get; set; } = string.Empty;
    public string? DateOfBirthProtected { get; set; }

    public string EmailHash { get; set; } = string.Empty;
    public string PhoneHash { get; set; } = string.Empty;

    public List<AttachmentEntity> Attachments { get; set; } = [];
    public List<ApplicationStatusEventEntity> StatusEvents { get; set; } = [];
    public List<ApplicationNoteEntity> Notes { get; set; } = [];
}

public static class ApplicationStatuses
{
    public const string Awaiting = "awaiting";
    public const string UnderReview = "under_review";
    public const string Shortlisted = "shortlisted";
    public const string InterviewScheduled = "interview_scheduled";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Postponed = "postponed";
    public const string Withdrawn = "withdrawn";

    public static readonly IReadOnlyList<string> All =
        [Awaiting, UnderReview, Shortlisted, InterviewScheduled, Approved, Rejected, Postponed, Withdrawn];
}

public sealed class AttachmentEntity
{
    public string ApplicationId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string ScanStatus { get; set; } = AttachmentScanStatuses.Pending;
    public DateTimeOffset UploadedAt { get; set; }
}

public static class AttachmentScanStatuses
{
    /// <summary>Stored, not yet virus scanned or EXIF-stripped: the media pipeline (#41) is not built yet.</summary>
    public const string Pending = "pending";
}

/// <summary>One step in an application's public timeline: the status and when it was reached. Nothing else belongs here.</summary>
public sealed class ApplicationStatusEventEntity
{
    public int Id { get; set; }
    public string ApplicationId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }

    /// <summary>User id of the reviewer, or "system" for the application's first event.</summary>
    public string By { get; set; } = "system";

    /// <summary>Whether a notification to the applicant was queued with this change.</summary>
    public bool Notified { get; set; }
}

/// <summary>Internal reviewer note. Never part of any public response.</summary>
public sealed class ApplicationNoteEntity
{
    public string Id { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
    public string By { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// A message for the applicant, written in the same commit as the change that caused it. Delivery
/// (email, in the applicant's language) is the outbox/email work of #32/#33; until then rows wait here.
/// </summary>
public sealed class NotificationOutboxEntity
{
    public long Id { get; set; }
    public string ApplicationId { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string RecipientProtected { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>A recruitment season. Open while not closed by hand and inside its window.</summary>
public sealed class CampaignEntity
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameBn { get; set; } = string.Empty;
    public DateTimeOffset OpensAt { get; set; }
    public DateTimeOffset? ClosesAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool IsOpen(DateTimeOffset now) => ClosedAt is null && OpensAt <= now && (ClosesAt is null || now < ClosesAt);
}
