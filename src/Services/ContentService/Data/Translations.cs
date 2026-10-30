using ContentService.Data.Entities;

namespace ContentService.Data;

public sealed record LanguageCompleteness(string Lang, bool Present, bool Complete, int Percent, List<string> Missing);

public sealed record ItemCompleteness(bool Complete, List<LanguageCompleteness> Languages);

/// <summary>
/// What "translated" means per content type: the text fields that must be filled in every language (`title`, `summary`, `body`; for
/// projects they are the title, short and full description). A language is complete when all of them are non-blank.
/// </summary>
public static class TranslationCompleteness
{
    public static readonly string[] Languages = ["bn", "en"];

    private static readonly Dictionary<string, string[]> Required = new()
    {
        [ContentTypes.Project] = ["title", "summary", "body"],
        [ContentTypes.News] = ["title", "summary", "body"],
        [ContentTypes.Page] = ["title", "body"],
        [ContentTypes.Faq] = ["title", "body"],
        [ContentTypes.TeamMember] = ["title", "summary"],
        [ContentTypes.Testimonial] = ["title", "body"],
        [ContentTypes.Partner] = ["title"],
        [ContentTypes.Press] = ["title", "summary"]
    };

    public static string[] RequiredFields(string type) => Required.TryGetValue(type, out var fields) ? fields : ["title"];

    public static ItemCompleteness Evaluate(string type, IEnumerable<ContentTranslationEntity> translations)
    {
        var required = RequiredFields(type);
        var byLang = translations.ToDictionary(t => t.Lang, StringComparer.Ordinal);
        var languages = new List<LanguageCompleteness>();
        foreach (var lang in Languages)
        {
            if (!byLang.TryGetValue(lang, out var translation))
            {
                languages.Add(new LanguageCompleteness(lang, false, false, 0, [.. required]));
                continue;
            }

            var missing = required.Where(field => string.IsNullOrWhiteSpace(Value(translation, field))).ToList();
            languages.Add(new LanguageCompleteness(lang, true, missing.Count == 0, (required.Length - missing.Count) * 100 / required.Length, missing));
        }

        return new ItemCompleteness(languages.All(l => l.Complete), languages);
    }

    private static string Value(ContentTranslationEntity translation, string field) => field switch
    {
        "title" => translation.Title,
        "summary" => translation.Summary,
        _ => translation.Body
    };
}

public enum FallbackMode
{
    /// <summary>The item is not served in a language it has no translation for.</summary>
    Hide,

    /// <summary>The other language's content is served, flagged `translationPending`.</summary>
    Fallback,

    /// <summary>The item is served with the other language's title only and empty summary and body, flagged `translationPending`.</summary>
    Placeholder
}

/// <summary>What to serve for one item in the requested language.</summary>
public sealed record TranslationChoice(string ContentLang, bool Pending, bool Placeholder);

/// <summary>
/// The fallback rules for a missing translation, per requested language, from `Content:Translations:Fallback:{bn|en}` (hide, fallback or
/// placeholder). Default, as decided for the Bengali-first site: Bengali readers get a placeholder that says the translation is pending
/// (never silent English); English readers get the Bengali content, flagged. Documented in docs/TRANSLATIONS.md.
/// </summary>
public sealed class TranslationPolicy
{
    private readonly Dictionary<string, FallbackMode> _modes;

    public TranslationPolicy(IConfiguration configuration)
    {
        _modes = new Dictionary<string, FallbackMode>(StringComparer.Ordinal)
        {
            ["bn"] = Read(configuration, "bn", FallbackMode.Placeholder),
            ["en"] = Read(configuration, "en", FallbackMode.Fallback)
        };
    }

    public FallbackMode ModeFor(string requested) => _modes.GetValueOrDefault(requested, FallbackMode.Hide);

    /// <summary>The language of the text to serve, or null when the item must not be served in <paramref name="requested"/>.</summary>
    public TranslationChoice? Choose(IReadOnlyCollection<string> available, string requested)
    {
        if (available.Contains(requested))
        {
            return new TranslationChoice(requested, false, false);
        }

        var mode = ModeFor(requested);
        if (mode == FallbackMode.Hide || available.Count == 0)
        {
            return null;
        }

        var other = requested == "bn" ? "en" : "bn";
        var content = available.Contains(other) ? other : available.OrderBy(l => l, StringComparer.Ordinal).First();
        return new TranslationChoice(content, true, mode == FallbackMode.Placeholder);
    }

    private static FallbackMode Read(IConfiguration configuration, string lang, FallbackMode fallback)
    {
        var raw = configuration[$"Content:Translations:Fallback:{lang}"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return Enum.TryParse<FallbackMode>(raw, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? mode
            : throw new InvalidOperationException($"Content:Translations:Fallback:{lang} must be hide, fallback or placeholder (was '{raw}').");
    }
}
