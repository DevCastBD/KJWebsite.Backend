using System.Text.Json.Serialization;

namespace AuthIdentityService.Contracts;

// Response shapes, named so the OpenAPI contract describes what the API actually returns. Fields are only ever added.
public sealed record RegisteredUser(string Id, string? Email, string Role, bool EmailConfirmationRequired);

public sealed record AuthUserSummary(string Id, string? Email, string Role);

public sealed record AuthTokens(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("token_type")] string TokenType,
    AuthUserSummary User);

public sealed record MessageResponse(string Message);

public sealed record MemberProfileView(
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

public sealed record MeResponse(string Id, string? Email, string Role, MemberProfileView Profile);

public sealed record SessionView(string Id, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt, string? UserAgent, bool Current);

public sealed record SessionList(IReadOnlyList<SessionView> Items);
