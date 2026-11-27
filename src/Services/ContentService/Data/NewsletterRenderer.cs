using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ContentService.Data;

public sealed class RenderException(string message) : Exception(message);

public sealed record RenderedEmail(string Subject, string Text, string Html);

/// <summary>
/// Turns a campaign version (subject and light Markdown body) into the text and HTML that are sent. The test send and the real send both call
/// <see cref="Render"/>, so a test send is the final render. A `{{variable}}` that has no value fails the render, in the composer or a test send,
/// instead of reaching a subscriber as literal braces. No third-party images and no tracking pixels or rewritten links are added.
/// </summary>
public static partial class NewsletterRenderer
{
    public static readonly string[] Variables = ["email", "unsubscribeUrl", "preferencesUrl"];

    public static RenderedEmail Render(string lang, string subject, string body, IReadOnlyDictionary<string, string> values)
    {
        var filledSubject = Fill(subject, values).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        var filledBody = Fill(body, values).Replace("\r\n", "\n", StringComparison.Ordinal);
        var footer = lang == "bn"
            ? $"আপনি নিউজলেটার পাচ্ছেন কারণ আপনি সাবস্ক্রাইব করেছেন।\nসাবস্ক্রিপশন বাতিল: {values["unsubscribeUrl"]}\nপছন্দ পরিবর্তন: {values["preferencesUrl"]}"
            : $"You are receiving this because you subscribed.\nUnsubscribe: {values["unsubscribeUrl"]}\nChange preferences: {values["preferencesUrl"]}";

        var text = filledBody.Trim() + "\n\n--\n" + footer + "\n";
        var html = new StringBuilder($"<!doctype html><html lang=\"{lang}\"><body style=\"font-family:Arial,Helvetica,sans-serif;line-height:1.5;color:#222\">");
        foreach (var block in Blocks().Split(filledBody.Trim()))
        {
            var trimmed = block.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                html.Append("<h2>").Append(Inline(trimmed.TrimStart('#').Trim())).Append("</h2>");
            }
            else
            {
                html.Append("<p>").Append(Inline(trimmed).Replace("\n", "<br>", StringComparison.Ordinal)).Append("</p>");
            }
        }

        html.Append("<hr><p style=\"font-size:12px;color:#666\">")
            .Append(WebUtility.HtmlEncode(lang == "bn" ? "আপনি নিউজলেটার পাচ্ছেন কারণ আপনি সাবস্ক্রাইব করেছেন।" : "You are receiving this because you subscribed."))
            .Append(" <a href=\"").Append(WebUtility.HtmlEncode(values["unsubscribeUrl"])).Append("\">").Append(lang == "bn" ? "সাবস্ক্রিপশন বাতিল" : "Unsubscribe").Append("</a> · <a href=\"")
            .Append(WebUtility.HtmlEncode(values["preferencesUrl"])).Append("\">").Append(lang == "bn" ? "পছন্দ পরিবর্তন" : "Preferences").Append("</a></p></body></html>");
        return new RenderedEmail(filledSubject, text, html.ToString());
    }

    /// <summary>Checks a version renders with sample values; throws <see cref="RenderException"/> if it uses an unknown variable.</summary>
    public static void Validate(string lang, string subject, string body) =>
        Render(lang, subject, body, new Dictionary<string, string> { ["email"] = "sample@example.org", ["unsubscribeUrl"] = "https://example.org/u", ["preferencesUrl"] = "https://example.org/p" });

    private static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var value) ? value : throw new RenderException($"Unknown variable {{{{{m.Groups[1].Value}}}}}."));

    private static string Inline(string text)
    {
        var encoded = WebUtility.HtmlEncode(text);
        encoded = Bold().Replace(encoded, "<strong>$1</strong>");
        return Link().Replace(encoded, m => IsSafe(WebUtility.HtmlDecode(m.Groups[2].Value)) ? $"<a href=\"{m.Groups[2].Value}\">{m.Groups[1].Value}</a>" : m.Groups[1].Value);
    }

    private static bool IsSafe(string url) => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\{\{\s*(\w+)\s*\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex Blocks();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex Link();
}
