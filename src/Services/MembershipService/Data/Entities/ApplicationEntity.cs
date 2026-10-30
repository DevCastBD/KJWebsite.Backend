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
    /// <summary>Data-protected JSON: welcome messages carry a password-setup token, so no payload is stored in the clear.</summary>
    public string PayloadJson { get; set; } = string.Empty;
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

/// <summary>
/// A person admitted through an approved application. `UserId` is the Identity account created in the same
/// approval; `MemberCode` (KJ-yyyy-nnnn, numbered per year) is unique and never changes.
/// </summary>
public sealed class MemberEntity
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string MemberCode { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public DateTimeOffset JoinedAt { get; set; }

    /// <summary>Bengali spelling of the name, for the membership card and (with consent) the directory.</summary>
    public string? NameBn { get; set; }
    public string? Expertise { get; set; }

    // Sensitive profile fields are data-protected (ADR-014); the member edits them through the portal.
    public string? PhoneProtected { get; set; }
    public string? AddressProtected { get; set; }
    public string? DateOfBirthProtected { get; set; }
    public string? BloodGroupProtected { get; set; }

    // Publication consent is per field and opt-in: nothing is published until the member says so.
    public bool ConsentName { get; set; }
    public bool ConsentPhotograph { get; set; }
    public bool ConsentExpertise { get; set; }

    /// <summary>Former members are listed publicly only if they say so, separately from the other consents.</summary>
    public bool ConsentAlumni { get; set; }

    public bool NotifyNews { get; set; } = true;
    public bool NotifyEvents { get; set; } = true;
    public bool NotifyMembership { get; set; } = true;

    public string? PhotoKey { get; set; }
    public string? PhotoContentType { get; set; }
    public long? PhotoBytes { get; set; }
    public string? PhotoFileName { get; set; }
    public string? PhotoScanStatus { get; set; }
}

/// <summary>
/// The consent ledger: one row each time a person gives or withdraws consent for a purpose, saying who did it, where from and
/// which privacy notice applied. Append-only. Subjects are opaque ids (an application or a member), never names.
/// </summary>
public sealed class ConsentRecordEntity
{
    public long Id { get; set; }
    public string SubjectId { get; set; } = string.Empty;
    public string SubjectKind { get; set; } = "member";
    public string Purpose { get; set; } = string.Empty;
    public bool Granted { get; set; }
    public DateTimeOffset At { get; set; }

    /// <summary>public_form, portal, staff or system.</summary>
    public string Source { get; set; } = "system";

    /// <summary>Who acted: the data subject's own user id for portal changes, a staff id, or "system".</summary>
    public string ActorId { get; set; } = "system";

    public string? NoticeVersion { get; set; }
}

public static class ConsentPurposes
{
    public const string ApplicationProcessing = "application_processing";
    public const string PublishName = "publish_name";
    public const string PublishPhotograph = "publish_photograph";
    public const string PublishExpertise = "publish_expertise";
    public const string PublishAlumni = "publish_alumni";
}

public static class ProfileChangeStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

/// <summary>A member's edit to a sensitive field, waiting for (or past) review. The current value stays on the member until approval.</summary>
public sealed class ProfileChangeEntity
{
    public string Id { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string ValueProtected { get; set; } = string.Empty;
    public string Status { get; set; } = ProfileChangeStatuses.Pending;
    public string? Reason { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
}

/// <summary>A member's request to be erased. Carrying it out (and what must be kept) is the retention work of #58.</summary>
public sealed class DeletionRequestEntity
{
    public string Id { get; set; } = string.Empty;

    /// <summary>The member asking, or null when an applicant who was never approved asked (then `ApplicationId` is set).</summary>
    public string? MemberId { get; set; }
    public string? ApplicationId { get; set; }
    public string Reference { get; set; } = string.Empty;
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
}

/// <summary>One retention run: when, under which policy, and how many were purged. Holds no personal data.</summary>
public sealed class RetentionRunEntity
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public bool DryRun { get; set; }

    /// <summary>"worker" or the staff id who started it.</summary>
    public string StartedBy { get; set; } = "worker";
    public string PolicyJson { get; set; } = "{}";
    public int Purged { get; set; }
}

/// <summary>An application removed by a run: its opaque id, the status it was in and the day that status began. No personal data.</summary>
public sealed class RetentionItemEntity
{
    public long Id { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateOnly StatusSince { get; set; }
}

/// <summary>Proof an erasure was carried out: whose (opaque id), when, by whom and what was removed. No personal data.</summary>
public sealed class ErasureRecordEntity
{
    public string Id { get; set; } = string.Empty;
    public string SubjectKind { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public DateTimeOffset ExecutedAt { get; set; }
    public string ExecutedBy { get; set; } = string.Empty;
    public string SummaryJson { get; set; } = "{}";
}

public static class MemberStatuses
{
    public const string Active = "active";
    public const string OnLeave = "on_leave";
    public const string Inactive = "inactive";
    public const string Alumni = "alumni";
    public const string Terminated = "terminated";

    /// <summary>Set by an erasure, never by a transition: the personal data is gone and only the opaque record remains.</summary>
    public const string Erased = "erased";

    public static readonly IReadOnlyList<string> All = [Active, OnLeave, Inactive, Alumni, Terminated];
}

/// <summary>
/// One step in a member's life with the organisation. `EffectiveOn` is when the change took effect (it may be a few days
/// before it was recorded); `At` is when it was recorded. The termination reason is data-protected and read only with `membership.terminate`.
/// </summary>
public sealed class MemberStatusEventEntity
{
    public long Id { get; set; }
    public string MemberId { get; set; } = string.Empty;
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = string.Empty;
    public DateOnly EffectiveOn { get; set; }
    public DateTimeOffset At { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string? ReasonProtected { get; set; }
}
