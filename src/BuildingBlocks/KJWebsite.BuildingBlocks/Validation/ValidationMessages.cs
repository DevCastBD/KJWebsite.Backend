using System.Globalization;

namespace KJWebsite.BuildingBlocks.Validation;

/// <summary>
/// User-visible validation text in Bengali and English. Validators attach stable error codes and the
/// filter renders them in the caller's language, so no response carries hard-coded English.
/// </summary>
public static class ValidationMessages
{
    public const string Required = "required";
    public const string Invalid = "invalid";
    public const string TooLong = "too_long";
    public const string TooShort = "too_short";
    public const string OutOfRange = "out_of_range";
    public const string InvalidEmail = "invalid_email";
    public const string OneOf = "one_of";
    public const string Mismatch = "mismatch";
    public const string Taken = "taken";
    public const string WeakPassword = "weak_password";
    public const string TooLarge = "too_large";

    private static readonly Dictionary<string, (string En, string Bn)> Templates = new(StringComparer.Ordinal)
    {
        [Required] = ("{field} is required.", "{field} আবশ্যক।"),
        [Invalid] = ("{field} is invalid.", "{field} সঠিক নয়।"),
        [TooLong] = ("{field} must be at most {max} characters.", "{field} সর্বোচ্চ {max} অক্ষরের হতে পারে।"),
        [TooShort] = ("{field} must be at least {min} characters.", "{field} কমপক্ষে {min} অক্ষরের হতে হবে।"),
        [OutOfRange] = ("{field} is out of range.", "{field} সীমার বাইরে।"),
        [InvalidEmail] = ("{field} must be a valid email address.", "{field} একটি সঠিক ইমেইল ঠিকানা হতে হবে।"),
        [OneOf] = ("{field} must be one of the allowed values.", "{field} অনুমোদিত মানগুলোর একটি হতে হবে।"),
        [Mismatch] = ("{field} does not match.", "{field} মিলছে না।"),
        [Taken] = ("{field} is already in use.", "{field} ইতিমধ্যে ব্যবহৃত হয়েছে।"),
        [WeakPassword] = ("{field} does not meet the password requirements.", "{field} পাসওয়ার্ডের শর্ত পূরণ করছে না।"),
        [TooLarge] = ("{field} is too large.", "{field} অনেক বড়।"),
        ["summary"] = ("One or more fields are invalid.", "এক বা একাধিক ক্ষেত্র সঠিক নয়।")
    };

    // FluentValidation's built-in validators report their own codes; map them onto ours.
    private static readonly Dictionary<string, string> BuiltIn = new(StringComparer.Ordinal)
    {
        ["NotEmptyValidator"] = Required,
        ["NotNullValidator"] = Required,
        ["EmailValidator"] = InvalidEmail,
        ["MaximumLengthValidator"] = TooLong,
        ["MinimumLengthValidator"] = TooShort,
        ["InclusiveBetweenValidator"] = OutOfRange,
        ["ExclusiveBetweenValidator"] = OutOfRange,
        ["GreaterThanValidator"] = OutOfRange,
        ["GreaterThanOrEqualValidator"] = OutOfRange,
        ["LessThanValidator"] = OutOfRange,
        ["LessThanOrEqualValidator"] = OutOfRange,
        ["RegularExpressionValidator"] = Invalid,
        ["PredicateValidator"] = Invalid,
        ["AsyncPredicateValidator"] = Invalid
    };

    public static string Summary(string language) => Render("summary", language, null)!;

    /// <summary>Returns the localized text, or <c>null</c> when the code is not one of ours (caller keeps FluentValidation's text).</summary>
    public static string? Render(string? errorCode, string language, IReadOnlyDictionary<string, object?>? arguments)
    {
        if (errorCode is null)
        {
            return null;
        }

        var key = BuiltIn.GetValueOrDefault(errorCode, errorCode);
        if (!Templates.TryGetValue(key, out var template))
        {
            return null;
        }

        var text = language == LanguageResolver.Bengali ? template.Bn : template.En;
        if (arguments is null)
        {
            return text;
        }

        foreach (var (name, value) in arguments)
        {
            text = text.Replace("{" + name + "}", Convert.ToString(value, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        return text;
    }
}
