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

    public string Status { get; set; } = "queued";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class NewsletterSegmentEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FilterJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
