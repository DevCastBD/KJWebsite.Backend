using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ContentService.Data;

public sealed class RenderException(string message) : Exception(message);

public sealed record RenderedEmail(string Subject, string Text, string Html);

/// <summary>
/// Turns a subject and light Markdown body into the text and HTML that are sent, for campaigns and transactional templates alike. A `{{variable}}`
/// that has no value fails the render (in the composer or a test send), never reaching a recipient as literal braces. The HTML is deliberately plain
/// for old mail clients (Outlook renders with Word): one centred table, inline styles, no style block, flex, grid or web fonts, no remote images and
/// no tracking. A test send and the real send call the same code, so a test send is the final render.
/// </summary>
public static partial class NewsletterRenderer
{
    public static readonly string[] Variables = ["email", "unsubscribeUrl", "preferencesUrl"];

    /// <summary>A campaign message: the body plus the unsubscribe and preferences footer.</summary>
    public static RenderedEmail Render(string lang, string subject, string body, IReadOnlyDictionary<string, string> values)
    {
        var footer = lang == "bn"
            ? ["আপনি নিউজলেটার পাচ্ছেন কারণ আপনি সাবস্ক্রাইব করেছেন।", "সাবস্ক্রিপশন বাতিল", "পছন্দ পরিবর্তন"]
            : new[] { "You are receiving this because you subscribed.", "Unsubscribe", "Change preferences" };
        var unsubscribe = values["unsubscribeUrl"];
        var preferences = values["preferencesUrl"];
        var text = $"{footer[0]}\n{footer[1]}: {unsubscribe}\n{footer[2]}: {preferences}";
        var html = $"{WebUtility.HtmlEncode(footer[0])} <a href=\"{WebUtility.HtmlEncode(unsubscribe)}\">{footer[1]}</a> · <a href=\"{WebUtility.HtmlEncode(preferences)}\">{footer[2]}</a>";
        return RenderMessage(lang, subject, body, values, text, html);
    }

    /// <summary>A message with a plain-text footer (empty for transactional mail) and its HTML twin.</summary>
    public static RenderedEmail RenderMessage(string lang, string subject, string body, IReadOnlyDictionary<string, string> values, string footerText, string footerHtml)
    {
        var filledSubject = Fill(subject, values).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        var filledBody = Fill(body, values).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var text = footerText.Length == 0 ? filledBody + "\n" : filledBody + "\n\n--\n" + footerText + "\n";

        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"").Append(lang).Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>")
            .Append(WebUtility.HtmlEncode(filledSubject)).Append("</title></head><body style=\"margin:0;padding:0;background:#f4f4f4\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"background:#f4f4f4\"><tr><td align=\"center\" style=\"padding:16px\">")
            .Append("<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"width:100%;max-width:600px;background:#ffffff\"><tr><td style=\"padding:24px;font-family:Arial,Helvetica,sans-serif;font-size:16px;line-height:24px;color:#222222\">");
        foreach (var block in Blocks().Split(filledBody))
        {
            var trimmed = block.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                html.Append("<h2 style=\"margin:0 0 12px 0;font-size:20px;line-height:28px\">").Append(Inline(trimmed.TrimStart('#').Trim())).Append("</h2>");
            }
            else
            {
                html.Append("<p style=\"margin:0 0 16px 0\">").Append(Inline(trimmed).Replace("\n", "<br>", StringComparison.Ordinal)).Append("</p>");
            }
        }

        if (footerHtml.Length > 0)
        {
            html.Append("<hr style=\"border:0;border-top:1px solid #dddddd;margin:24px 0 12px 0\"><p style=\"margin:0;font-size:12px;line-height:18px;color:#666666\">").Append(footerHtml).Append("</p>");
        }

        html.Append("</td></tr></table></td></tr></table></body></html>");
        return new RenderedEmail(filledSubject, text, html.ToString());
    }

    /// <summary>Checks a campaign version renders with sample values; throws <see cref="RenderException"/> if it uses an unknown variable.</summary>
    public static void Validate(string lang, string subject, string body) =>
        Render(lang, subject, body, new Dictionary<string, string> { ["email"] = "sample@example.org", ["unsubscribeUrl"] = "https://example.org/u", ["preferencesUrl"] = "https://example.org/p" });

    /// <summary>The variable names a template uses.</summary>
    public static IReadOnlyCollection<string> UsedVariables(string template) =>
        [.. Placeholder().Matches(template).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    public static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var value) ? value : throw new RenderException($"Missing variable {{{{{m.Groups[1].Value}}}}}."));

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
