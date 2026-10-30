using System.Globalization;
using System.Net;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Email;

namespace CtaSubmissionService.Email;

public enum CtaEmailKind
{
    /// <summary>Acknowledges a contact-style submission to the sender.</summary>
    ContactAck,

    /// <summary>Tells a volunteer applicant their registration arrived.</summary>
    RegistrationReceived,

    /// <summary>Tells the organisation's inbox that something new arrived.</summary>
    AdminNotification
}

public sealed record CtaEmailData(
    string SubmissionId,
    string FormName,
    string? Name,
    string? Email,
    string? Phone,
    string? Message,
    DateTimeOffset SubmittedAt);

/// <summary>
/// Bilingual (bn/en) message templates. Every value is HTML-escaped on the way in; markup is table-based with inline
/// styles and a 600px single column so it renders in Gmail, Outlook and mobile clients without media queries.
/// </summary>
public static class CtaEmailTemplates
{
    public static EmailMessage Render(CtaEmailKind kind, string language, string to, CtaEmailData data)
    {
        var bn = language == LanguageResolver.Bengali;
        var greetingName = string.IsNullOrWhiteSpace(data.Name) ? null : data.Name.Trim();
        var (subject, heading, paragraphs, tag) = kind switch
        {
            CtaEmailKind.ContactAck => bn
                ? ("আপনার বার্তা পেয়েছি — কল্পযন্ত্র", "বার্তার জন্য ধন্যবাদ",
                    new[] { "আপনার বার্তাটি আমরা পেয়েছি। আমাদের একজন সদস্য শীঘ্রই আপনার সাথে যোগাযোগ করবেন।", $"আপনার রেফারেন্স নম্বর: {data.SubmissionId}" }, "contact-ack")
                : ("We received your message — Kolpojontro", "Thank you for getting in touch",
                    new[] { "We have received your message. One of our team members will get back to you soon.", $"Your reference number: {data.SubmissionId}" }, "contact-ack"),
            CtaEmailKind.RegistrationReceived => bn
                ? ("আপনার স্বেচ্ছাসেবী আবেদন পেয়েছি — কল্পযন্ত্র", "আমাদের সাথে যুক্ত হতে চাওয়ার জন্য ধন্যবাদ",
                    new[] { "আপনার আবেদনটি আমরা পেয়েছি এবং আমাদের দল এটি পর্যালোচনা করবে। পরবর্তী ধাপ সম্পর্কে আমরা আপনাকে জানাব।", $"আপনার রেফারেন্স নম্বর: {data.SubmissionId}" }, "registration-received")
                : ("We received your volunteer application — Kolpojontro", "Thank you for wanting to join us",
                    new[] { "We have received your application and our team will review it. We will let you know about the next steps.", $"Your reference number: {data.SubmissionId}" }, "registration-received"),
            _ => bn
                ? ($"নতুন জমা: {data.FormName}", "নতুন ফর্ম জমা পড়েছে",
                    AdminLines(data, bn: true), "admin-notification")
                : ($"New submission: {data.FormName}", "A new form submission arrived",
                    AdminLines(data, bn: false), "admin-notification")
        };

        var greeting = kind == CtaEmailKind.AdminNotification
            ? string.Empty
            : bn ? (greetingName is null ? "প্রিয় বন্ধু," : $"প্রিয় {greetingName},") : (greetingName is null ? "Hello," : $"Hello {greetingName},");
        var signoff = kind == CtaEmailKind.AdminNotification ? string.Empty : bn ? "শুভেচ্ছান্তে,\nকল্পযন্ত্র দল" : "Warm regards,\nThe Kolpojontro team";
        var footer = bn
            ? "আপনি এই ইমেইলটি পেয়েছেন কারণ আমাদের ওয়েবসাইটে একটি ফর্ম জমা দেওয়া হয়েছে। এটি স্বয়ংক্রিয়ভাবে পাঠানো।"
            : "You received this email because a form was submitted on our website. It was sent automatically.";

        return new EmailMessage(to, subject, Html(bn ? "bn" : "en", heading, greeting, paragraphs, signoff, footer), Text(heading, greeting, paragraphs, signoff), tag);
    }

    private static string[] AdminLines(CtaEmailData data, bool bn)
    {
        var lines = new List<string>
        {
            $"{(bn ? "রেফারেন্স" : "Reference")}: {data.SubmissionId}",
            $"{(bn ? "ফর্ম" : "Form")}: {data.FormName}",
            $"{(bn ? "সময়" : "Submitted")}: {data.SubmittedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}"
        };
        AddIf(lines, bn ? "নাম" : "Name", data.Name);
        AddIf(lines, bn ? "ইমেইল" : "Email", data.Email);
        AddIf(lines, bn ? "ফোন" : "Phone", data.Phone);
        AddIf(lines, bn ? "বার্তা" : "Message", data.Message is { Length: > 2000 } text ? text[..2000] + "…" : data.Message);
        return [.. lines];
    }

    private static void AddIf(List<string> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"{label}: {value.Trim()}");
        }
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private static string Html(string lang, string heading, string greeting, string[] paragraphs, string signoff, string footer)
    {
        static string Para(string text) => $"<p style=\"margin:0 0 14px 0;font-size:16px;line-height:24px;color:#1f2933;\">{E(text).Replace("\n", "<br>", StringComparison.Ordinal)}</p>";
        var body = string.Concat(paragraphs.Select(Para));
        return $"""
            <!doctype html>
            <html lang="{lang}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{E(heading)}</title></head>
            <body style="margin:0;padding:0;background:#f3f5f7;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#f3f5f7;"><tr><td align="center" style="padding:24px 12px;">
            <table role="presentation" width="600" cellpadding="0" cellspacing="0" style="width:100%;max-width:600px;background:#ffffff;border-radius:8px;font-family:'Noto Sans Bengali','Segoe UI',Arial,sans-serif;">
            <tr><td style="padding:28px 28px 8px 28px;"><h1 style="margin:0;font-size:22px;line-height:30px;color:#0b5d3b;">{E(heading)}</h1></td></tr>
            <tr><td style="padding:12px 28px 24px 28px;">{(greeting.Length > 0 ? Para(greeting) : string.Empty)}{body}{(signoff.Length > 0 ? Para(signoff) : string.Empty)}</td></tr>
            <tr><td style="padding:16px 28px 24px 28px;border-top:1px solid #e4e7eb;font-size:12px;line-height:18px;color:#7b8794;">{E(footer)}</td></tr>
            </table></td></tr></table></body></html>
            """;
    }

    private static string Text(string heading, string greeting, string[] paragraphs, string signoff) =>
        string.Join("\n\n", new[] { heading, greeting }.Concat(paragraphs).Append(signoff).Where(s => s.Length > 0));
}
