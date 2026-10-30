using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace KJWebsite.BuildingBlocks.Observability;

/// <summary>
/// Last line of defence against personal data reaching a log aggregator. Request bodies are never logged on
/// purpose; this catches the accidental cases: a property whose name says it holds PII, an object logged with
/// <c>{@Applicant}</c>, or an email address / token embedded in a string.
/// </summary>
public sealed partial class PiiRedactionEnricher : ILogEventEnricher
{
    public const string Redacted = "[REDACTED]";

    // Matched against the property name with separators removed, lowercase.
    private static readonly string[] SensitiveNames =
    [
        "password", "secret", "token", "authorization", "apikey", "cookie", "privatekey", "credential",
        "email", "phone", "mobile", "firstname", "lastname", "fullname", "displayname", "address", "bloodgroup", "dateofbirth",
        "gender", "nationality", "reasonforjoining", "disabilit", "values", "notes", "otp"
    ];

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]*")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=\-]{8,}")]
    private static partial Regex BearerPattern();

    // International (+...) or national (leading 0) numbers only: bare digit runs are usually ids and must stay queryable.
    [GeneratedRegex(@"(?<![\w.\-])(?:\+\d|0\d)[\d\s\-()]{8,}\d(?![\w])")]
    private static partial Regex PhonePattern();

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var property in logEvent.Properties.ToArray())
        {
            var cleaned = Clean(property.Key, property.Value);
            if (!ReferenceEquals(cleaned, property.Value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, cleaned));
            }
        }
    }

    /// <summary>Redacts a string in isolation (exposed for exception messages and tests).</summary>
    public static string Scrub(string text) =>
        PhonePattern().Replace(
            BearerPattern().Replace(JwtPattern().Replace(EmailPattern().Replace(text, Redacted), Redacted), "Bearer " + Redacted),
            Redacted);

    internal static bool IsSensitive(string name)
    {
        var normalized = new string([.. name.Where(char.IsLetterOrDigit)]).ToLowerInvariant();
        return SensitiveNames.Any(normalized.Contains);
    }

    private static LogEventPropertyValue Clean(string? name, LogEventPropertyValue value)
    {
        if (name is not null && IsSensitive(name) && value is not (ScalarValue { Value: null }))
        {
            return new ScalarValue(Redacted);
        }

        switch (value)
        {
            case ScalarValue { Value: string text }:
                var scrubbed = Scrub(text);
                return ReferenceEquals(scrubbed, text) || scrubbed == text ? value : new ScalarValue(scrubbed);
            case StructureValue structure:
                return new StructureValue(structure.Properties.Select(p => new LogEventProperty(p.Name, Clean(p.Name, p.Value))), structure.TypeTag);
            case DictionaryValue dictionary:
                return new DictionaryValue(dictionary.Elements.Select(e =>
                    new KeyValuePair<ScalarValue, LogEventPropertyValue>(e.Key, Clean(e.Key.Value?.ToString(), e.Value))));
            case SequenceValue sequence:
                return new SequenceValue(sequence.Elements.Select(e => Clean(null, e)));
            default:
                return value;
        }
    }
}
