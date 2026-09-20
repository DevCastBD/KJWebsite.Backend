using System.Globalization;

namespace KJWebsite.BuildingBlocks;

public static class LanguageResolver
{
    public const string Bengali = "bn";
    public const string English = "en";

    public static string Resolve(string? requestedLanguage, string? acceptLanguage)
    {
        var requested = Normalize(requestedLanguage);
        if (requested is not null)
        {
            return requested;
        }

        return ParseAcceptLanguage(acceptLanguage) ?? English;
    }

    private static string? ParseAcceptLanguage(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage))
        {
            return null;
        }

        return acceptLanguage
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((value, index) => new LanguagePreference(value, index))
            .OrderByDescending(preference => preference.Quality)
            .ThenBy(preference => preference.Order)
            .Select(preference => Normalize(preference.Tag))
            .FirstOrDefault(language => language is not null);
    }

    private static string? Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var tag = language.Trim();
        var separatorIndex = tag.IndexOf(';');
        if (separatorIndex >= 0)
        {
            tag = tag[..separatorIndex].Trim();
        }

        var cultureSeparatorIndex = tag.IndexOf('-');
        if (cultureSeparatorIndex >= 0)
        {
            tag = tag[..cultureSeparatorIndex];
        }

        return tag.ToLowerInvariant() switch
        {
            Bengali => Bengali,
            English => English,
            _ => null
        };
    }

    private sealed class LanguagePreference
    {
        public LanguagePreference(string value, int order)
        {
            var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Tag = parts[0];
            Order = order;
            Quality = parts.Skip(1)
                .FirstOrDefault(part => part.StartsWith("q=", StringComparison.OrdinalIgnoreCase)) is { } qualityPart &&
                double.TryParse(qualityPart[2..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var quality)
                ? quality
                : 1;
        }

        public string Tag { get; }

        public int Order { get; }

        public double Quality { get; }
    }
}
