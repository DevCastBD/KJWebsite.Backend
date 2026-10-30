namespace MembershipService.Data.Entities;

/// <summary>
/// One membership application. Email, phone, address, blood group and date of birth are stored encrypted
/// (ADR-014); the two hashes let duplicate detection match without decrypting anything.
/// </summary>
public sealed class ApplicationEntity
{
    public string Id { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Status { get; set; } = ApplicationStatuses.Received;
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

    public string EmailProtected { get; set; } = string.Empty;
    public string PhoneProtected { get; set; } = string.Empty;
    public string BloodGroupProtected { get; set; } = string.Empty;
    public string CurrentAddressProtected { get; set; } = string.Empty;
    public string? DateOfBirthProtected { get; set; }

    public string EmailHash { get; set; } = string.Empty;
    public string PhoneHash { get; set; } = string.Empty;

    public List<AttachmentEntity> Attachments { get; set; } = [];
}

public static class ApplicationStatuses
{
    public const string Received = "received";
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
