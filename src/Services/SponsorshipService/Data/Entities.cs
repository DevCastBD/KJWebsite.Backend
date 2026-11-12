namespace SponsorshipService.Data;

public static class StudentStatuses
{
    public const string Active = "active";
    public const string Inactive = "inactive";
    public const string Graduated = "graduated";

    public static readonly string[] All = [Active, Inactive, Graduated];
}

/// <summary>
/// A child in the sponsorship programme. Name, birth date, address and guardian details are encrypted at rest, kept out of the audit
/// log and never shown to a donor (D2). The programme is a project slug owned by the Content service.
/// </summary>
public sealed class StudentEntity
{
    public string Id { get; set; } = string.Empty;
    public string NameProtected { get; set; } = string.Empty;
    public string PreferredName { get; set; } = string.Empty;
    public string DateOfBirthProtected { get; set; } = string.Empty;
    public string? Gender { get; set; }
    public string School { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string ProgrammeId { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string? AddressProtected { get; set; }
    public string GuardianNameProtected { get; set; } = string.Empty;
    public string? GuardianPhoneProtected { get; set; }
    public string? StoryEn { get; set; }
    public string? StoryBn { get; set; }
    public string Status { get; set; } = StudentStatuses.Active;
    public string? PhotoKey { get; set; }
    public string? PhotoContentType { get; set; }
    public string? FormKey { get; set; }
    public string? FormContentType { get; set; }
    public string? FormFileName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Counts every change a staff member can see; it is the ETag.</summary>
    public int Version { get; set; } = 1;
}

/// <summary>One grant or withdrawal of the guardian consent (photo and story together). Append-only: the latest row is current.</summary>
public sealed class ConsentEntity
{
    public long Id { get; set; }
    public string StudentId { get; set; } = string.Empty;
    public bool Granted { get; set; }

    /// <summary>The signed form on file when consent was granted; null for a withdrawal.</summary>
    public string? FormKey { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string? Note { get; set; }
}
