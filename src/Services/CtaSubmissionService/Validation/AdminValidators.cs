using CtaSubmissionService.Contracts;
using CtaSubmissionService.Data.Entities;
using FluentValidation;
using KJWebsite.BuildingBlocks.Validation;

namespace CtaSubmissionService.Validation;

public sealed class SubmissionListQueryValidator : AbstractValidator<SubmissionListQuery>
{
    public const int MaxPageSize = 100;

    public SubmissionListQueryValidator()
    {
        RuleFor(x => x.Status).Must(s => s is null || SubmissionStatuses.All.Contains(s)).WithErrorCode(ValidationMessages.OneOf);
        RuleFor(x => x.FormName).MaximumLength(64);
        RuleFor(x => x.CtaType).MaximumLength(64);
        RuleFor(x => x.Language).MaximumLength(8);
        RuleFor(x => x.AssigneeId).MaximumLength(64);
        RuleFor(x => x.Q).MaximumLength(100);
        RuleFor(x => x.Limit).InclusiveBetween(1, MaxPageSize).When(x => x.Limit.HasValue);
        RuleFor(x => x.Offset).GreaterThanOrEqualTo(0).When(x => x.Offset.HasValue);
        RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From!.Value).When(x => x.From.HasValue && x.To.HasValue).WithErrorCode(ValidationMessages.OutOfRange);
    }
}

public sealed class SubmissionPatchRequestValidator : AbstractValidator<SubmissionPatchRequest>
{
    public SubmissionPatchRequestValidator()
    {
        RuleFor(x => x).Must(x => x.Status is not null || x.AssigneeId is not null || x.Notes is not null)
            .WithName("body").WithErrorCode(ValidationMessages.Required);
        RuleFor(x => x.Status).Must(s => s is null || SubmissionStatuses.All.Contains(s)).WithErrorCode(ValidationMessages.OneOf);
        RuleFor(x => x.AssigneeId).MaximumLength(64);
        RuleFor(x => x.Notes).MaximumLength(4000);
    }
}
