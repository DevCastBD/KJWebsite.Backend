namespace SponsorshipService.Students;

/// <summary>Create and edit share one body. On create the required fields must be present; on PATCH a field left out is unchanged and optional text sent as an empty string is cleared.</summary>
public sealed record StudentRequest(
    string? Name, string? PreferredName, string? DateOfBirth, string? Gender, string? School, string? Class, string? Programme,
    string? District, string? Address, string? GuardianName, string? GuardianPhone, string? StoryEn, string? StoryBn, string? Status);

public sealed record StudentConsentRequest(bool? Granted, string? Note);

public sealed record ProgrammeRef(string Id, string Name);

public sealed record StudentConsentState(bool Granted, DateTimeOffset? RecordedAt, string? ActorId);

public sealed record StudentConsentRow(long Id, bool Granted, bool HasForm, DateTimeOffset RecordedAt, string ActorId, string? Note);

/// <summary>A row of the staff list. Search covers the full name, the preferred name and the id.</summary>
public sealed record StudentSummary(
    string Id, string Name, string PreferredName, string Class, string School, ProgrammeRef Programme, string District, string Status,
    bool Sponsored, bool HasPhoto, bool ConsentGranted);

public sealed record StudentList(IReadOnlyList<StudentSummary> Items, int Total, int Limit, int Offset);

/// <summary>Every field staff keep for a student. Links are filled in by the link endpoints (#214); until then the list is empty.</summary>
public sealed record StudentDetail(
    string Id, string Name, string PreferredName, string DateOfBirth, string? Gender, string School, string Class, ProgrammeRef Programme,
    string District, string? Address, string GuardianName, string? GuardianPhone, string? StoryEn, string? StoryBn, string Status,
    bool HasPhoto, bool HasConsentForm, string? ConsentFormName, StudentConsentState Consent, IReadOnlyList<StudentConsentRow> Consents,
    IReadOnlyList<object> Links, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// The one allow-list of what a donor may see (D2). Photo and story appear only while guardian consent is granted; the story is the
/// donor language, falling back to the other one. The donor endpoints of #214 use this same record, so the two cannot disagree.
/// </summary>
public sealed record DonorStudentView(
    string Id, string PreferredName, string Class, string School, ProgrammeRef Programme, string District, string Status,
    bool HasPhoto, string? Story, string? StoryLanguage);
