namespace ContentService.Data;

public sealed record NotificationTemplateSpec(string Key, string Description, string[] Variables);

/// <summary>
/// The transactional messages the organisation sends, each with the variables it may use. Every key has a Bengali and an English version from the
/// start (no exceptions: no hard-coded English), seeded as version 1. Wording is a starting point that the organisation edits as new versions.
/// </summary>
public static class NotificationCatalogue
{
    public static readonly IReadOnlyList<NotificationTemplateSpec> Specs =
    [
        new("application.received", "Membership application received", ["name", "reference"]),
        new("application.status-changed", "Membership application status changed", ["name", "reference", "status"]),
        new("application.approved", "Membership application approved", ["name", "memberNumber", "loginUrl"]),
        new("donation.receipt", "Donation received (receipt)", ["name", "amount", "receiptNumber", "receiptUrl"]),
        new("event.registration", "Event registration confirmed", ["name", "eventTitle", "startsAt", "ticketUrl"]),
        new("event.reminder", "Event reminder", ["name", "eventTitle", "startsAt", "ticketUrl"]),
        new("dues.reminder", "Membership dues reminder", ["name", "amount", "dueDate", "payUrl"]),
        new("auth.password-reset", "Password reset", ["name", "resetUrl", "expiresIn"]),
        new("admin.alert", "Alert to staff", ["title", "message", "link"])
    ];

    public static readonly IReadOnlyDictionary<(string Key, string Lang), (string Subject, string Body)> Seed = new Dictionary<(string, string), (string, string)>
    {
        [("application.received", "bn")] = ("আপনার সদস্যপদের আবেদন পেয়েছি ({{reference}})", "প্রিয় {{name}},\n\nআপনার সদস্যপদের আবেদন আমরা পেয়েছি। আপনার রেফারেন্স নম্বর: **{{reference}}**।\n\nআবেদন যাচাই শেষ হলে আমরা আপনাকে জানাব। ধন্যবাদ।"),
        [("application.received", "en")] = ("We received your membership application ({{reference}})", "Dear {{name}},\n\nWe have received your membership application. Your reference number is **{{reference}}**.\n\nWe will let you know when the review is done. Thank you."),
        [("application.status-changed", "bn")] = ("আপনার আবেদনের অবস্থা বদলেছে ({{reference}})", "প্রিয় {{name}},\n\nআপনার আবেদনের ({{reference}}) বর্তমান অবস্থা: **{{status}}**।"),
        [("application.status-changed", "en")] = ("Your application status has changed ({{reference}})", "Dear {{name}},\n\nThe status of your application ({{reference}}) is now: **{{status}}**."),
        [("application.approved", "bn")] = ("স্বাগতম, আপনার সদস্যপদ অনুমোদিত হয়েছে", "প্রিয় {{name}},\n\nআপনার সদস্যপদ অনুমোদিত হয়েছে। আপনার সদস্য নম্বর: **{{memberNumber}}**।\n\nসাইন ইন করুন: [{{loginUrl}}]({{loginUrl}})"),
        [("application.approved", "en")] = ("Welcome, your membership is approved", "Dear {{name}},\n\nYour membership has been approved. Your member number is **{{memberNumber}}**.\n\nSign in: [{{loginUrl}}]({{loginUrl}})"),
        [("donation.receipt", "bn")] = ("আপনার অনুদান পেয়েছি, ধন্যবাদ ({{receiptNumber}})", "প্রিয় {{name}},\n\nআপনার {{amount}} অনুদানের জন্য ধন্যবাদ। রসিদ নম্বর: **{{receiptNumber}}**।\n\nরসিদ ডাউনলোড করুন: [{{receiptUrl}}]({{receiptUrl}})"),
        [("donation.receipt", "en")] = ("Thank you for your donation ({{receiptNumber}})", "Dear {{name}},\n\nThank you for your donation of {{amount}}. Receipt number: **{{receiptNumber}}**.\n\nDownload your receipt: [{{receiptUrl}}]({{receiptUrl}})"),
        [("event.registration", "bn")] = ("নিবন্ধন নিশ্চিত: {{eventTitle}}", "প্রিয় {{name}},\n\n{{eventTitle}} অনুষ্ঠানে আপনার নিবন্ধন নিশ্চিত হয়েছে। সময়: **{{startsAt}}**।\n\nআপনার টিকিট: [{{ticketUrl}}]({{ticketUrl}})"),
        [("event.registration", "en")] = ("Registration confirmed: {{eventTitle}}", "Dear {{name}},\n\nYour registration for {{eventTitle}} is confirmed. Time: **{{startsAt}}**.\n\nYour ticket: [{{ticketUrl}}]({{ticketUrl}})"),
        [("event.reminder", "bn")] = ("অনুস্মারক: {{eventTitle}}", "প্রিয় {{name}},\n\n{{eventTitle}} শুরু হচ্ছে **{{startsAt}}** সময়ে।\n\nআপনার টিকিট: [{{ticketUrl}}]({{ticketUrl}})"),
        [("event.reminder", "en")] = ("Reminder: {{eventTitle}}", "Dear {{name}},\n\n{{eventTitle}} starts at **{{startsAt}}**.\n\nYour ticket: [{{ticketUrl}}]({{ticketUrl}})"),
        [("dues.reminder", "bn")] = ("সদস্য চাঁদার অনুস্মারক", "প্রিয় {{name}},\n\nআপনার সদস্য চাঁদা {{amount}} পরিশোধের শেষ তারিখ **{{dueDate}}**।\n\nপরিশোধ করুন: [{{payUrl}}]({{payUrl}})"),
        [("dues.reminder", "en")] = ("Membership dues reminder", "Dear {{name}},\n\nYour membership dues of {{amount}} are due on **{{dueDate}}**.\n\nPay here: [{{payUrl}}]({{payUrl}})"),
        [("auth.password-reset", "bn")] = ("পাসওয়ার্ড পুনরুদ্ধার", "প্রিয় {{name}},\n\nপাসওয়ার্ড বদলাতে নিচের লিংক ব্যবহার করুন। লিংকটি {{expiresIn}} পর্যন্ত কার্যকর।\n\n[{{resetUrl}}]({{resetUrl}})\n\nআপনি অনুরোধ না করে থাকলে এই ইমেইল উপেক্ষা করুন।"),
        [("auth.password-reset", "en")] = ("Password reset", "Dear {{name}},\n\nUse the link below to change your password. It is valid for {{expiresIn}}.\n\n[{{resetUrl}}]({{resetUrl}})\n\nIf you did not ask for this, ignore this email."),
        [("admin.alert", "bn")] = ("সতর্কবার্তা: {{title}}", "{{message}}\n\n[{{link}}]({{link}})"),
        [("admin.alert", "en")] = ("Alert: {{title}}", "{{message}}\n\n[{{link}}]({{link}})")
    };
}
