namespace AuthIdentityService.Data.Entities;

/// <summary>
/// Legacy registration/profile fields. Interim home in the auth schema until the Membership module exists.
/// </summary>
public sealed class MemberProfileEntity
{
    public string UserId { get; set; } = string.Empty;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Gender { get; set; }
    public string? ReasonForJoining { get; set; }
    public string? PresentOrganization { get; set; }
    public string? VolunteeingExperience { get; set; }
    public string? DateOfBirth { get; set; }
    public string? CityOfResidence { get; set; }
    public string? CountryOfResidence { get; set; }
    public string? PermanentAddress { get; set; }
    public string? MailingAddress { get; set; }
    public bool IsMailingAddressSameAsPermanentAddress { get; set; }
    public string? BloodGroup { get; set; }
    public string? AreasOfExpertise { get; set; }
    public string? HighestDegree { get; set; }
    public string? DisabilitiesIfAny { get; set; }
    public string? Nationality { get; set; }
    public string? PersonalWebPage { get; set; }
    public string? SocialMediaLink { get; set; }
}
