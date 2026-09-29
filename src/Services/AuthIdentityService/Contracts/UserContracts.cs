namespace AuthIdentityService.Contracts;

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshTokenRequest(string RefreshToken);

// Legacy-aligned registration/profile fields extracted from legacy/kj-registration ApplicationUser.
public sealed record LegacyRegistrationRequest(
    string Email,
    string Password,
    string? FirstName,
    string? LastName,
    string? Gender,
    string? ReasonForJoining,
    string? PresentOrganization,
    string? VolunteeingExperience,
    string? DateOfBirth,
    string? CityOfResidence,
    string? CountryOfResidence,
    string? PermanentAddress,
    string? MailingAddress,
    bool? IsMailingAddressSameAsPermanentAddress,
    string? BloodGroup,
    string? AreasOfExpertise,
    string? HighestDegree,
    string? DisabilitiesIfAny,
    string? Nationality,
    string? PersonalWebPage,
    string? SocialMediaLink,
    string? Roles);

public sealed record UserProfile(
    string? FirstName,
    string? LastName,
    string? Gender,
    string? ReasonForJoining,
    string? PresentOrganization,
    string? VolunteeingExperience,
    string? DateOfBirth,
    string? CityOfResidence,
    string? CountryOfResidence,
    string? PermanentAddress,
    string? MailingAddress,
    bool IsMailingAddressSameAsPermanentAddress,
    string? BloodGroup,
    string? AreasOfExpertise,
    string? HighestDegree,
    string? DisabilitiesIfAny,
    string? Nationality,
    string? PersonalWebPage,
    string? SocialMediaLink);

public sealed record UserRecord(
    string Id,
    string Email,
    string Password,
    string Role,
    string Status,
    UserProfile Profile);

public static class LegacyRegistrationMapper
{
    public static UserRecord ToUserRecord(LegacyRegistrationRequest request)
    {
        var role = string.IsNullOrWhiteSpace(request.Roles) ? "editor" : request.Roles.Split(',')[0].Trim().ToLowerInvariant();
        if (role is not ("admin" or "editor"))
        {
            role = "editor";
        }

        return new UserRecord(
            $"usr_{Guid.NewGuid():N}",
            request.Email.Trim().ToLowerInvariant(),
            request.Password,
            role,
            "active",
            new UserProfile(
                request.FirstName,
                request.LastName,
                request.Gender,
                request.ReasonForJoining,
                request.PresentOrganization,
                request.VolunteeingExperience,
                request.DateOfBirth,
                request.CityOfResidence,
                request.CountryOfResidence,
                request.PermanentAddress,
                request.MailingAddress,
                request.IsMailingAddressSameAsPermanentAddress ?? false,
                request.BloodGroup,
                request.AreasOfExpertise,
                request.HighestDegree,
                request.DisabilitiesIfAny,
                request.Nationality,
                request.PersonalWebPage,
                request.SocialMediaLink));
    }
}
