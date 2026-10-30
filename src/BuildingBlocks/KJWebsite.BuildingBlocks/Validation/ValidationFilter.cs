using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.BuildingBlocks.Validation;

public static class ValidationProblem
{
    public const string Code = "VALIDATION_ERROR";

    /// <summary>400 Problem Details with a per-field <c>errors</c> object, localized to the request language.</summary>
    public static IResult Create(HttpContext context, IEnumerable<ValidationFailure> failures)
    {
        var language = LanguageResolver.Resolve(context.Request.Query["lang"], context.Request.Headers.AcceptLanguage);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var group in failures.GroupBy(f => FieldName(f.PropertyName), StringComparer.Ordinal))
        {
            errors[group.Key] = [.. group.Select(f => Message(f, group.Key, language)).Distinct(StringComparer.Ordinal)];
        }

        return ProblemDetailsFactory.Create(
            context,
            StatusCodes.Status400BadRequest,
            Code,
            ValidationMessages.Summary(language),
            new Dictionary<string, object?> { ["errors"] = errors });
    }

    public static IResult Create(HttpContext context, string field, string errorCode) =>
        Create(context, [new ValidationFailure(field, string.Empty) { ErrorCode = errorCode }]);

    private static string Message(ValidationFailure failure, string field, string language)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = field };
        if (failure.FormattedMessagePlaceholderValues is { } placeholders)
        {
            foreach (var (name, value) in placeholders)
            {
                arguments[name switch { "MaxLength" => "max", "MinLength" => "min", _ => name }] = value;
            }
        }

        return ValidationMessages.Render(failure.ErrorCode, language, arguments)
            ?? (string.IsNullOrEmpty(failure.ErrorMessage) ? ValidationMessages.Render(ValidationMessages.Invalid, language, arguments)! : failure.ErrorMessage);
    }

    /// <summary>Maps C# member paths (<c>Values[firstName]</c>) onto the camelCase JSON names clients send.</summary>
    internal static string FieldName(string propertyName) =>
        string.Join('.', propertyName.Split('.').Select(segment =>
            segment.Length > 0 && char.IsUpper(segment[0]) ? char.ToLowerInvariant(segment[0]) + segment[1..] : segment));
}

/// <summary>Runs the registered <see cref="IValidator{T}"/> for the handler's <typeparamref name="T"/> argument before the handler executes.</summary>
public sealed class ValidationFilter<T> : EndpointFilterBase
    where T : class
{
    public override async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var model = context.Arguments.OfType<T>().FirstOrDefault();
        var validator = context.HttpContext.RequestServices.GetService<IValidator<T>>();
        if (validator is null)
        {
            throw new InvalidOperationException($"No FluentValidation validator is registered for {typeof(T).Name}.");
        }

        // A missing body binds to null on optional parameters; treat it as a single "body required" failure.
        var result = model is null
            ? new ValidationResult([new ValidationFailure(typeof(T).Name, string.Empty) { ErrorCode = ValidationMessages.Required }])
            : await validator.ValidateAsync(model, context.HttpContext.RequestAborted);

        return result.IsValid
            ? await next(context)
            : ValidationProblem.Create(context.HttpContext, result.Errors);
    }
}

public static class ValidationExtensions
{
    /// <summary>Registers every validator in the assembly that contains <typeparamref name="TMarker"/>.</summary>
    public static IServiceCollection AddKjValidation<TMarker>(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<TMarker>(ServiceLifetime.Singleton);
        return services;
    }

    public static RouteHandlerBuilder WithValidation<T>(this RouteHandlerBuilder builder)
        where T : class =>
        builder.AddEndpointFilter(new ValidationFilter<T>());
}
