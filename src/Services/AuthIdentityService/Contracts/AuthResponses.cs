using System.Text.Json.Serialization;

namespace AuthIdentityService.Contracts;

public sealed record AuthTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("token_type")] string TokenType,
    bool MustChangePassword,
    AuthUserResponse User);

public record AuthUserResponse(string Id, string? Email, string Role, IList<string> Roles, bool MustChangePassword, string[] Permissions);

public sealed record CurrentUserResponse(string Id, string? Email, string Role, IList<string> Roles, bool MustChangePassword, string[] Permissions, AuthProfileResponse Profile)
    : AuthUserResponse(Id, Email, Role, Roles, MustChangePassword, Permissions);

public sealed record AuthProfileResponse(
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
