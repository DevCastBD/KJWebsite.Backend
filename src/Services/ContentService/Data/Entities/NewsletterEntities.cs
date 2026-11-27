namespace ContentService.Data.Entities;

public static class SubscriberStatuses
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Unsubscribed = "unsubscribed";
    public const string Suppressed = "suppressed";
}

/// <summary>A newsletter address. It only receives campaigns once <see cref="Status"/> is `confirmed` (double opt-in); bounces and complaints move it to `suppressed`.</summary>
public sealed class NewsletterSubscriberEntity
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Lang { get; set; } = "bn";
    public string TopicsJson { get; set; } = "[]";
    public string Status { get; set; } = SubscriberStatuses.Pending;

    /// <summary>Where the address came from (`web`, `footer`, `event`, `import`...); a segment filter.</summary>
    public string Source { get; set; } = "web";

    public int SoftBounces { get; set; }
    public string? SuppressionReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ConfirmationSentAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? UnsubscribedAt { get; set; }
}

/// <summary>Append-only consent and lifecycle evidence for a subscriber: who agreed, to what, when.</summary>
public sealed class NewsletterEventEntity
{
    public long Id { get; set; }
    public string SubscriberId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>A message for a subscriber, written in the same commit as the change that caused it; delivery is the sending work of #81.</summary>
public sealed class NewsletterMessageEntity
{
    public long Id { get; set; }
    public string SubscriberId { get; set; } = string.Empty;
    public string ToEmail { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Lang { get; set; } = "bn";
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    /// <summary>The per-recipient one-click unsubscribe URL (RFC 8058), sent as `List-Unsubscribe` with `List-Unsubscribe-Post: List-Unsubscribe=One-Click`.</summary>
    public string? ListUnsubscribeUrl { get; set; }

    public string? BodyHtml { get; set; }
    public string? CampaignId { get; set; }

    /// <summary>Earliest delivery time, Unix seconds (a number so SQL can compare it). Spaced out per recipient to throttle a campaign; 0 sends at once.</summary>
    public long NotBeforeUnix { get; set; }

    /// <summary>`queued`, `sent`, `failed`, `skipped` (the subscriber stopped being confirmed before delivery) or `cancelled`.</summary>
    public string Status { get; set; } = "queued";

    public DateTimeOffset? SentAt { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A bilingual starting point for a campaign. A campaign copies it, so editing a template never rewrites a draft.</summary>
public sealed class NewsletterTemplateEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SubjectBn { get; set; } = string.Empty;
    public string BodyBn { get; set; } = string.Empty;
    public string SubjectEn { get; set; } = string.Empty;
    public string BodyEn { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class NewsletterCampaignEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? SegmentId { get; set; }
    public string SubjectBn { get; set; } = string.Empty;
    public string BodyBn { get; set; } = string.Empty;
    public string SubjectEn { get; set; } = string.Empty;
    public string BodyEn { get; set; } = string.Empty;

    /// <summary>`draft`, `sending` (queued for delivery, possibly in the future) or `cancelled`.</summary>
    public string Status { get; set; } = "draft";

    public int RecipientCount { get; set; }
    public DateTimeOffset? SendStartsAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class NewsletterSegmentEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FilterJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One immutable version of a transactional message in one language. The newest version of a (key, lang) is the one sent.</summary>
public sealed class NotificationTemplateEntity
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Lang { get; set; } = "bn";
    public int Version { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>What was actually produced for a recipient, with the template version it came from, so a past message can always be reproduced.</summary>
public sealed class NotificationSendEntity
{
    public long Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Lang { get; set; } = "bn";
    public int Version { get; set; }
    public string ToEmail { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string BodyHtml { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A member's channel choices. Email and in-app default on; SMS and web push are off until the member turns them on.</summary>
public sealed class NotificationPreferenceEntity
{
    public string Subject { get; set; } = string.Empty;
    public bool Email { get; set; } = true;
    public bool Sms { get; set; }
    public bool InApp { get; set; } = true;
    public bool Push { get; set; }

    /// <summary>Normalised Bangladeshi mobile number, `+8801XXXXXXXXX`.</summary>
    public string? Phone { get; set; }

    public string Lang { get; set; } = "bn";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A durable in-app notification. Read state lives here, so every device of the member sees the same unread count.</summary>
public sealed class InAppNotificationEntity
{
    public long Id { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public sealed class SmsMessageEntity
{
    public long Id { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string ToPhone { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Lang { get; set; } = "bn";
    public string Body { get; set; } = string.Empty;

    /// <summary>`gsm7` or `ucs2` (any Bengali text): decides the segment count and so the cost.</summary>
    public string Encoding { get; set; } = "gsm7";

    public int Segments { get; set; }
    public string Status { get; set; } = "queued";
    public string? Error { get; set; }

    /// <summary>Unix seconds, so rate limits can be counted in SQL.</summary>
    public long CreatedUnix { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A browser's push subscription, stored only after the member explicitly opts in. Sending needs a VAPID key pair and a push library, which are not wired yet.</summary>
public sealed class PushSubscriptionEntity
{
    public string Id { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public DateTimeOffset ConsentedAt { get; set; }
}
