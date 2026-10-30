namespace MembershipService.Contracts;

/// <summary>The legacy registration field set (name split into first and last) plus the optional legacy profile fields.</summary>
public sealed record ApplicationRequest(
    string? FirstName,
    string? LastName,
    string? Email,
    string? Phone,
    string? BloodGroup,
    string? CurrentAddress,
    string? AcademicInstitute,
    string? Occupation,
    string? ReasonForJoining,
    string? VolunteeringExperience,
    string? PresentOrganization,
    string? HeardAboutUs,
    string? Gender,
    string? DateOfBirth,
    string? CityOfResidence,
    string? CountryOfResidence,
    string? Language,
    DateTimeOffset? SubmittedAt);

public sealed record ApplicationCreated(string Id, string Reference, string Status, DateTimeOffset CreatedAt);

public sealed record DuplicateCheckRequest(string? Email, string? Phone);

public sealed record DuplicateCheckResult(bool Duplicate);

public sealed record AttachmentAccepted(string Kind, string FileName, string ContentType, long Bytes, string ScanStatus);
