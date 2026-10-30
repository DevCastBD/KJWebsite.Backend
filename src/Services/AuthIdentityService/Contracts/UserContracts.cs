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

public sealed record ConfirmEmailRequest(string UserId, string Token);

public sealed record ResendConfirmationRequest(string Email);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);
