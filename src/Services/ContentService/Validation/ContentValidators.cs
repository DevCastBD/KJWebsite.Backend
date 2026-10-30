using System.Text.RegularExpressions;
using FluentValidation;
using KJWebsite.BuildingBlocks.Validation;

namespace ContentService.Validation;

internal static partial class ContentRules
{
    public static readonly string[] Languages = ["en", "bn"];

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    public static IRuleBuilderOptions<T, string> Slug<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(120).Must(v => v is not null && SlugPattern().IsMatch(v)).WithErrorCode(ValidationMessages.Invalid);

    public static IRuleBuilderOptions<T, string> OneOf<T>(this IRuleBuilder<T, string> rule, string[] allowed) =>
        rule.NotEmpty().Must(v => v is not null && allowed.Contains(v)).WithErrorCode(ValidationMessages.OneOf);
}

public sealed class ProjectUpsertRequestValidator : AbstractValidator<ProjectUpsertRequest>
{
    public ProjectUpsertRequestValidator()
    {
        RuleFor(x => x.Slug).Slug();
        RuleFor(x => x.Status).OneOf(["active", "draft", "archived"]);
        RuleFor(x => x.ImageUrl).MaximumLength(500);
        RuleFor(x => x.Translations).NotEmpty();
        RuleFor(x => x.Translations).Must(t => t is null || t.Select(i => i.Lang).Distinct().Count() == t.Count)
            .WithErrorCode(ValidationMessages.Invalid);
        RuleForEach(x => x.Translations).ChildRules(t =>
        {
            t.RuleFor(i => i.Lang).OneOf(ContentRules.Languages);
            t.RuleFor(i => i.Title).NotEmpty().MaximumLength(300);
        });
    }
}

public sealed class NewsUpsertRequestValidator : AbstractValidator<NewsUpsertRequest>
{
    public NewsUpsertRequestValidator()
    {
        RuleFor(x => x.Slug).Slug();
        RuleFor(x => x.Status).OneOf(["published", "draft", "archived"]);
        RuleFor(x => x.ImageUrl).MaximumLength(500);
        RuleFor(x => x.Translations).NotEmpty();
        RuleFor(x => x.Translations).Must(t => t is null || t.Select(i => i.Lang).Distinct().Count() == t.Count)
            .WithErrorCode(ValidationMessages.Invalid);
        RuleForEach(x => x.Translations).ChildRules(t =>
        {
            t.RuleFor(i => i.Lang).OneOf(ContentRules.Languages);
            t.RuleFor(i => i.Title).NotEmpty().MaximumLength(300);
        });
    }
}

/// <summary>Query-string values shared by the list endpoints.</summary>
public sealed record ListQuery(int? Limit, int? Offset, string? Status = null);

public sealed class ListQueryValidator : AbstractValidator<ListQuery>
{
    public const int MaxPageSize = 100;

    public ListQueryValidator()
    {
        RuleFor(x => x.Limit).InclusiveBetween(1, MaxPageSize).When(x => x.Limit.HasValue);
        RuleFor(x => x.Offset).GreaterThanOrEqualTo(0).When(x => x.Offset.HasValue);
        RuleFor(x => x.Status).Must(s => string.IsNullOrWhiteSpace(s) || s.Trim().ToLowerInvariant() is "active" or "all")
            .WithErrorCode(ValidationMessages.OneOf);
    }
}
