namespace ContentService.Data.Entities;

/// <summary>
/// The language-independent part of an `event` content item (plan §3.6). Instants are stored in UTC; the IANA zone the organisers
/// meant is stored beside them so calendar output can show the right wall-clock time. Translated text (venue name, address, notes)
/// lives in the translation attributes.
/// </summary>
public sealed class EventEntity
{
    public string ContentItemId { get; set; } = string.Empty;
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }

    /// <summary>IANA zone id, e.g. `Asia/Dhaka`. Recurrence is expanded in this zone so a weekly 10:00 stays 10:00 across DST.</summary>
    public string TimeZoneId { get; set; } = string.Empty;

    public string OnlineUrl { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>Seats per occurrence. Null means unlimited.</summary>
    public int? Capacity { get; set; }

    /// <summary>An RFC 5545 RRULE subset (FREQ, INTERVAL, COUNT, UNTIL, BYDAY). Empty for a single event; the series is this one row.</summary>
    public string RecurrenceRule { get; set; } = string.Empty;

    /// <summary>JSON array of ISO local start date-times removed from the series.</summary>
    public string ExceptionsJson { get; set; } = "[]";

    /// <summary>Groups related events (e.g. one programme's editions). Free text chosen by editors.</summary>
    public string SeriesKey { get; set; } = string.Empty;

    /// <summary>Whether registration is accepted. Closed events keep existing registrations.</summary>
    public bool RegistrationOpen { get; set; } = true;

    /// <summary>When true a registration waits for an organiser before it can take a seat.</summary>
    public bool RequiresApproval { get; set; }

    /// <summary>Ticket price in minor units (poisha, cents). Null or zero means free. Paid registrations hold a seat as `pending_payment` until payment is confirmed.</summary>
    public long? PriceMinor { get; set; }

    public string Currency { get; set; } = string.Empty;

    /// <summary>Bumped by every registration change that moves seats. It is a concurrency token, so two people taking the last seat cannot both succeed: the loser retries and is waitlisted.</summary>
    public int RegistrationVersion { get; set; }
}

/// <summary>One agenda row of an event, in position order. The title is per language.</summary>
public sealed class EventSessionEntity
{
    public string Id { get; set; } = string.Empty;
    public string ContentItemId { get; set; } = string.Empty;
    public int Position { get; set; }

    /// <summary>Offset from the occurrence start, so every occurrence of a series shares one agenda.</summary>
    public int StartOffsetMinutes { get; set; }
    public int DurationMinutes { get; set; }
    public string TitlesJson { get; set; } = "{}";
    public string Speaker { get; set; } = string.Empty;
    public string Room { get; set; } = string.Empty;
}

public static class RegistrationStatuses
{
    public const string PendingApproval = "pending_approval";
    public const string PendingPayment = "pending_payment";
    public const string Confirmed = "confirmed";
    public const string Waitlisted = "waitlisted";

    /// <summary>A waitlisted person was offered a seat and must accept before the deadline.</summary>
    public const string Offered = "offered";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";

    /// <summary>Confirmed registration whose person did not attend (set by attendance, #75). Distinct from cancelled.</summary>
    public const string NoShow = "no_show";

    public static readonly string[] HoldingSeat = [Confirmed, PendingPayment, Offered, NoShow];
    public static readonly string[] Active = [PendingApproval, PendingPayment, Confirmed, Waitlisted, Offered, NoShow];
}

/// <summary>One person's registration for one occurrence of an event. Guests have no account: name, email and phone are encrypted and the email is matched by keyed hash.</summary>
public sealed class EventRegistrationEntity
{
    public string Id { get; set; } = string.Empty;
    public string EventItemId { get; set; } = string.Empty;

    /// <summary>Start of the occurrence this registration is for (the event start for a single event).</summary>
    public DateTimeOffset OccurrenceStartUtc { get; set; }

    public string Status { get; set; } = string.Empty;

    /// <summary>Non-guessable ticket code (128 random bits). It is the credential to view, accept or cancel the registration.</summary>
    public string TicketCode { get; set; } = string.Empty;

    public string? MemberId { get; set; }
    public string NameProtected { get; set; } = string.Empty;
    public string EmailProtected { get; set; } = string.Empty;
    public string? PhoneProtected { get; set; }
    public string EmailKey { get; set; } = string.Empty;
    public string Language { get; set; } = "en";

    /// <summary>Deadline for the current wait: accepting a waitlist offer, or paying.</summary>
    public DateTimeOffset? DeadlineAt { get; set; }

    public string? DecisionReason { get; set; }
    public string? PaymentReference { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Guest personal data is kept until this date, then purged (retention, #58).</summary>
    public DateTimeOffset RetainUntil { get; set; }
}

/// <summary>A message for a registrant, written in the same commit as the change that caused it; delivery is the outbox/email work of #32/#33.</summary>
public sealed class EventNotificationEntity
{
    public long Id { get; set; }
    public string RegistrationId { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string RecipientProtected { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}
