using System.Text.Json;
using CtaSubmissionService.Contracts;
using FluentValidation;
using KJWebsite.BuildingBlocks.Validation;

namespace CtaSubmissionService.Validation;

public sealed class CtaSubmissionRequestValidator : AbstractValidator<CtaSubmissionRequest>
{
    public const int MaxValuesBytes = 32_000;

    public static readonly string[] Forms = ["registration", "contact", "footer-contact", "project-involved"];
    public static readonly string[] Types = ["join", "donate", "partnership", "get-involved", "contact"];
    public static readonly string[] Languages = ["bn", "en"];

    public CtaSubmissionRequestValidator()
    {
        RuleFor(x => x.FormName).NotEmpty().Must(v => IsOneOf(Forms, v)).WithErrorCode(ValidationMessages.OneOf);
        RuleFor(x => x.CtaType).NotEmpty().Must(v => IsOneOf(Types, v)).WithErrorCode(ValidationMessages.OneOf);
        RuleFor(x => x.Language).NotEmpty().Must(v => IsOneOf(Languages, v)).WithErrorCode(ValidationMessages.OneOf);
        RuleFor(x => x.SourcePath).MaximumLength(500);
        RuleFor(x => x.Values).NotNull()
            .Must(v => v is null || JsonSerializer.Serialize(v).Length <= MaxValuesBytes)
            .WithErrorCode(ValidationMessages.TooLarge);
    }

    private static bool IsOneOf(string[] allowed, string? value) =>
        value is not null && allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
}
