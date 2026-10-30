using AuthIdentityService.Contracts;
using FluentValidation;

namespace AuthIdentityService.Validation;

internal static class AuthRules
{
    public static IRuleBuilderOptions<T, string?> RequiredText<T>(this IRuleBuilder<T, string?> rule, int max) =>
        rule.NotEmpty().MaximumLength(max);

    public static IRuleBuilderOptions<T, string?> OptionalText<T>(this IRuleBuilder<T, string?> rule, int max) =>
        rule.MaximumLength(max);

    public static IRuleBuilderOptions<T, string?> ValidEmail<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().MaximumLength(256).EmailAddress();
}

public sealed class LegacyRegistrationRequestValidator : AbstractValidator<LegacyRegistrationRequest>
{
    public LegacyRegistrationRequestValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
        RuleFor(x => x.FirstName).OptionalText(100);
        RuleFor(x => x.LastName).OptionalText(100);
        RuleFor(x => x.Gender).OptionalText(50);
        RuleFor(x => x.ReasonForJoining).OptionalText(2000);
        RuleFor(x => x.PresentOrganization).OptionalText(200);
        RuleFor(x => x.VolunteeingExperience).OptionalText(2000);
        RuleFor(x => x.DateOfBirth).OptionalText(50);
        RuleFor(x => x.CityOfResidence).OptionalText(100);
        RuleFor(x => x.CountryOfResidence).OptionalText(100);
        RuleFor(x => x.PermanentAddress).OptionalText(500);
        RuleFor(x => x.MailingAddress).OptionalText(500);
        RuleFor(x => x.BloodGroup).OptionalText(10);
        RuleFor(x => x.AreasOfExpertise).OptionalText(1000);
        RuleFor(x => x.HighestDegree).OptionalText(200);
        RuleFor(x => x.DisabilitiesIfAny).OptionalText(500);
        RuleFor(x => x.Nationality).OptionalText(100);
        RuleFor(x => x.PersonalWebPage).OptionalText(500);
        RuleFor(x => x.SocialMediaLink).OptionalText(500);
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class RefreshTokenRequestValidator : AbstractValidator<RefreshTokenRequest>
{
    public RefreshTokenRequestValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(512);
}

public sealed class ConfirmEmailRequestValidator : AbstractValidator<ConfirmEmailRequest>
{
    public ConfirmEmailRequestValidator()
    {
        RuleFor(x => x.UserId).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Token).NotEmpty().MaximumLength(2048);
    }
}

public sealed class ResendConfirmationRequestValidator : AbstractValidator<ResendConfirmationRequest>
{
    public ResendConfirmationRequestValidator() => RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
}

public sealed class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator() => RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Token).NotEmpty().MaximumLength(2048);
        RuleFor(x => x.NewPassword).NotEmpty().MaximumLength(128);
    }
}
