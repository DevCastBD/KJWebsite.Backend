using System.Security.Cryptography;
using System.Text.Json;
using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

public enum RegistrationOutcome
{
    Ok,
    Created,
    NotFound,
    Invalid,
    Closed,
    Conflict
}

public sealed record RegistrationResult(RegistrationOutcome Outcome, TicketView? Ticket = null, string? Error = null, string? Code = null);

public sealed record RegisterRequest(string? Name, string? Email, string? Phone, DateTimeOffset? Occurrence, string? Lang);

public sealed record TicketView(
    string TicketCode,
    string Status,
    string EventSlug,
    DateTimeOffset OccurrenceStartUtc,
    string TimeZoneId,
    string StartLocal,
    string Name,
    DateTimeOffset? DeadlineAt,
    int? WaitlistPosition,
    long? PriceMinor,
    string Currency,
    string? Reason,
    DateTimeOffset? CheckedInAt = null);

public sealed record AdminRegistrationView(
    string Id,
    string Status,
    string TicketCode,
    DateTimeOffset OccurrenceStartUtc,
    string Name,
    string Email,
    string? Phone,
    string? MemberId,
    DateTimeOffset? DeadlineAt,
    string? Reason,
    string? PaymentReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CheckedInAt = null);

public sealed record RegistrationSummary(Dictionary<string, int> ByStatus, int? Capacity, int SeatsHeld, int CheckedIn = 0);

public sealed record AdminRegistrationList(List<AdminRegistrationView> Items, int Total, int Limit, int Offset, RegistrationSummary Summary);

/// <summary>
/// Registration, capacity and waitlist for events. Seats are decided on the server: every change that moves seats bumps the event's
/// `RegistrationVersion` (a concurrency token), so two people taking the last seat at once cannot both win. The loser's save fails, the
/// work is redone from fresh data, and they land on the waitlist. Holds that lapse (an unpaid seat, an unanswered waitlist offer) are
/// released on the next touch of the event and passed to the next person in line.
/// </summary>
public sealed class EventRegistrationStore(ContentDbContext db, TimeProvider time, RegistrationPrivacy privacy)
{
    public static readonly TimeSpan OfferWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan PaymentWindow = TimeSpan.FromHours(2);
    private const int MaxAttempts = 6;
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- public (guest or member)

    public Task<RegistrationResult> RegisterAsync(string slug, RegisterRequest request, string? memberId) => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
        {
            return Fail(RegistrationOutcome.Invalid, "Name is required (up to 200 characters).");
        }

        if (!IsEmail(request.Email))
        {
            return Fail(RegistrationOutcome.Invalid, "A valid email address is required.");
        }

        if (request.Phone is { Length: > 40 })
        {
            return Fail(RegistrationOutcome.Invalid, "Phone is too long.");
        }

        var item = await LoadEventAsync(slug, publishedOnly: true);
        if (item?.Event is null || !TryZone(item.Event.TimeZoneId, out var zone))
        {
            return Fail(RegistrationOutcome.NotFound, "Event not found.");
        }

        var now = time.GetUtcNow();
        var all = await LoadActiveAsync(item.Id);
        var notes = new List<EventNotificationEntity>();
        var touched = ReleaseExpired(item, all, now, notes);

        var occurrences = EventStore.Expand(item.Event, zone).ToList();
        var occurrence = request.Occurrence is { } wanted
            ? occurrences.FirstOrDefault(o => o.StartUtc == wanted.ToUniversalTime())
            : occurrences.FirstOrDefault(o => o.EndUtc > now);
        if (occurrence is null)
        {
            return Fail(RegistrationOutcome.Invalid, request.Occurrence is null ? "This event has no upcoming occurrence." : "No such occurrence of this event.");
        }

        if (!item.Event.RegistrationOpen || occurrence.EndUtc <= now)
        {
            return Fail(RegistrationOutcome.Closed, "Registration for this event is closed.", "REGISTRATION_CLOSED");
        }

        var key = privacy.HashEmail(request.Email!);
        var existing = all.FirstOrDefault(r => r.OccurrenceStartUtc == occurrence.StartUtc && r.EmailKey == key && RegistrationStatuses.Active.Contains(r.Status));
        if (existing is not null)
        {
            await SaveIfChangedAsync(item.Event, touched, notes);
            return new RegistrationResult(RegistrationOutcome.Ok, ToTicket(existing, item, zone, all));
        }

        var language = request.Lang is "bn" ? "bn" : "en";
        var registration = new EventRegistrationEntity
        {
            Id = $"reg_{Guid.NewGuid():N}",
            EventItemId = item.Id,
            OccurrenceStartUtc = occurrence.StartUtc,
            TicketCode = NewTicketCode(),
            MemberId = string.IsNullOrWhiteSpace(memberId) ? null : memberId,
            NameProtected = privacy.Protect(request.Name.Trim()),
            EmailProtected = privacy.Protect(request.Email!.Trim()),
            PhoneProtected = string.IsNullOrWhiteSpace(request.Phone) ? null : privacy.Protect(request.Phone.Trim()),
            EmailKey = key,
            Language = language,
            CreatedAt = now,
            UpdatedAt = now,
            RetainUntil = occurrence.EndUtc.AddDays(365)
        };
        db.EventRegistrations.Add(registration);
        all.Add(registration);

        if (item.Event.RequiresApproval)
        {
            registration.Status = RegistrationStatuses.PendingApproval;
        }
        else
        {
            Place(item.Event, all, registration, now);
        }

        notes.Add(Note(registration, item, request.Email!.Trim(), registration.Status == RegistrationStatuses.PendingApproval ? "event.registration.received" : $"event.registration.{registration.Status}"));
        await SaveIfChangedAsync(item.Event, true, notes);
        return new RegistrationResult(RegistrationOutcome.Created, ToTicket(registration, item, zone, all));
    });

    public Task<RegistrationResult> GetTicketAsync(string code) => WithTicket(code, async (ctx) =>
    {
        await SaveIfChangedAsync(ctx.Item.Event!, ctx.Touched, ctx.Notes);
        return new RegistrationResult(RegistrationOutcome.Ok, ToTicket(ctx.Registration, ctx.Item, ctx.Zone, ctx.All));
    });

    public Task<RegistrationResult> CancelAsync(string code) => WithTicket(code, async (ctx) => await CancelCore(ctx));

    /// <summary>Accepts a waitlist offer before its deadline.</summary>
    public Task<RegistrationResult> AcceptAsync(string code) => WithTicket(code, async (ctx) =>
    {
        var r = ctx.Registration;
        if (r.Status != RegistrationStatuses.Offered)
        {
            await SaveIfChangedAsync(ctx.Item.Event!, ctx.Touched, ctx.Notes);
            return r.Status == RegistrationStatuses.Cancelled && r.DecisionReason == "expired"
                ? Fail(RegistrationOutcome.Conflict, "The offer expired and the seat went to the next person.", "OFFER_EXPIRED")
                : Fail(RegistrationOutcome.Conflict, "There is no waitlist offer to accept on this ticket.", "NOT_OFFERED");
        }

        var now = time.GetUtcNow();
        MoveToSeat(ctx.Item.Event!, r, now);
        ctx.Notes.Add(Note(r, ctx.Item, Unprotect(r), $"event.registration.{r.Status}"));
        await SaveIfChangedAsync(ctx.Item.Event!, true, ctx.Notes);
        return new RegistrationResult(RegistrationOutcome.Ok, ToTicket(r, ctx.Item, ctx.Zone, ctx.All));
    });

    // ---- admin

    public async Task<AdminRegistrationList?> ListAsync(string slug, string? status, int limit, int offset)
    {
        var item = await LoadEventAsync(slug, publishedOnly: false);
        if (item?.Event is null)
        {
            return null;
        }

        var all = await db.EventRegistrations.AsNoTracking().Where(r => r.EventItemId == item.Id).ToListAsync();
        var filtered = all.Where(r => status is null || r.Status == status).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        var held = all.Count(r => RegistrationStatuses.HoldingSeat.Contains(r.Status));
        var summary = new RegistrationSummary(all.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count()), item.Event.Capacity, held, all.Count(r => r.CheckedInAt is not null));
        return new AdminRegistrationList([.. filtered.Skip(offset).Take(limit).Select(ToAdmin)], filtered.Count, limit, offset, summary);
    }

    public Task<RegistrationResult> ApproveAsync(string slug, string id) => WithAdmin(slug, id, async (ctx) =>
    {
        if (ctx.Registration.Status != RegistrationStatuses.PendingApproval)
        {
            return Fail(RegistrationOutcome.Conflict, "Only a registration waiting for approval can be approved.", "NOT_PENDING_APPROVAL");
        }

        Place(ctx.Item.Event!, ctx.All, ctx.Registration, time.GetUtcNow());
        ctx.Notes.Add(Note(ctx.Registration, ctx.Item, Unprotect(ctx.Registration), $"event.registration.{ctx.Registration.Status}"));
        await SaveIfChangedAsync(ctx.Item.Event!, true, ctx.Notes);
        return new RegistrationResult(RegistrationOutcome.Ok, ToTicket(ctx.Registration, ctx.Item, ctx.Zone, ctx.All));
    });

    public Task<RegistrationResult> RejectAsync(string slug, string id, string? reason) => WithAdmin(slug, id, async (ctx) =>
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Fail(RegistrationOutcome.Invalid, "A reason is required to reject a registration.");
        }

        if (ctx.Registration.Status != RegistrationStatuses.PendingApproval)
        {
            return Fail(RegistrationOutcome.Conflict, "Only a registration waiting for approval can be rejected.", "NOT_PENDING_APPROVAL");
        }

        ctx.Registration.Status = RegistrationStatuses.Rejected;
        ctx.Registration.DecisionReason = reason.Trim();
        ctx.Registration.UpdatedAt = time.GetUtcNow();
        ctx.Notes.Add(Note(ctx.Registration, ctx.Item, Unprotect(ctx.Registration), "event.registration.rejected"));
        await SaveIfChangedAsync(ctx.Item.Event!, true, ctx.Notes);
        return new RegistrationResult(RegistrationOutcome.Ok, ToTicket(ctx.Registration, ctx.Item, ctx.Zone, ctx.All));
    });

    /// <summary>Marks a paid registration as paid. Nothing else ever moves `pending_payment` to `confirmed`.</summary>
    public Task<RegistrationResult> ConfirmPaymentAsync(string slug, string id, string? paymentReference) => WithAdmin(slug, id, async (ctx) =>
    {
        if (string.IsNullOrWhiteSpace(paymentReference))
        {
            return Fail(RegistrationOutcome.Invalid, "A payment reference is required.");
        }

        if (ctx.Registration.Status != RegistrationStatuses.PendingPayment)
        {
            await SaveIfChangedAsync(ctx.Item.Event!, ctx.Touched, ctx.Notes);
            return Fail(RegistrationOutcome.Conflict, "The registration is not waiting for payment (it may have expired).", "NOT_PENDING_PAYMENT");
        }

        ctx.Registration.Status = RegistrationStatuses.Confirmed;
        ctx.Registration.PaymentReference = paymentReference.Trim();
        ctx.Registration.DeadlineAt = null;
        ctx.Registration.UpdatedAt = time.GetUtcNow();
        ctx.Notes.Add(Note(ctx.Registration, ctx.Item, Unprotect(ctx.Registration), "event.registration.confirmed"));
        await SaveIfChangedAsync(ctx.Item.Event!, true, ctx.Notes);
        return new RegistrationResult(RegistrationOutcome.Ok, ToTicket(ctx.Registration, ctx.Item, ctx.Zone, ctx.All));
    });

    public Task<RegistrationResult> AdminCancelAsync(string slug, string id) => WithAdmin(slug, id, async (ctx) => await CancelCore(ctx));

    // ---- core

    private async Task<RegistrationResult> CancelCore(Ctx ctx)
    {
        var r = ctx.Registration;
        if (!RegistrationStatuses.Active.Contains(r.Status) || r.Status == RegistrationStatuses.NoShow)
        {
            await SaveIfChangedAsync(ctx.Item.Event!, ctx.Touched, ctx.Notes);
            return Fail(RegistrationOutcome.Conflict, "This registration is already closed.", "ALREADY_CLOSED");
        }

        var now = time.GetUtcNow();
        if (r.OccurrenceStartUtc <= now)
        {
            return Fail(RegistrationOutcome.Closed, "The event has started; it can no longer be cancelled.", "EVENT_STARTED");
        }

        var heldSeat = RegistrationStatuses.HoldingSeat.Contains(r.Status);
        r.Status = RegistrationStatuses.Cancelled;
        r.DecisionReason = "cancelled";
        r.DeadlineAt = null;
        r.UpdatedAt = now;
        if (heldSeat)
        {
            Promote(ctx.Item, ctx.All, r.OccurrenceStartUtc, now, ctx.Notes);
        }

        await SaveIfChangedAsync(ctx.Item.Event!, true, ctx.Notes);
        return new RegistrationResult(RegistrationOutcome.Ok, ToTicket(r, ctx.Item, ctx.Zone, ctx.All));
    }

    private sealed record Ctx(ContentItemEntity Item, TimeZoneInfo Zone, List<EventRegistrationEntity> All, EventRegistrationEntity Registration, List<EventNotificationEntity> Notes, bool Touched);

    private Task<RegistrationResult> WithTicket(string code, Func<Ctx, Task<RegistrationResult>> work) => RunAsync(async () =>
    {
        var wanted = code?.Trim().ToUpperInvariant() ?? string.Empty;
        var registration = wanted.Length == 0 ? null : await db.EventRegistrations.FirstOrDefaultAsync(r => r.TicketCode == wanted);
        return registration is null ? Fail(RegistrationOutcome.NotFound, "Ticket not found.") : await Prepare(registration.EventItemId, registration.Id, work);
    });

    private Task<RegistrationResult> WithAdmin(string slug, string id, Func<Ctx, Task<RegistrationResult>> work) => RunAsync(async () =>
    {
        var item = await LoadEventAsync(slug, publishedOnly: false);
        return item is null ? Fail(RegistrationOutcome.NotFound, "Event not found.") : await Prepare(item.Id, id, work);
    });

    private async Task<RegistrationResult> Prepare(string itemId, string registrationId, Func<Ctx, Task<RegistrationResult>> work)
    {
        var item = await db.Items.Include(i => i.Event).FirstOrDefaultAsync(i => i.Id == itemId && i.Type == ContentTypes.Event && i.DeletedAt == null);
        if (item?.Event is null || !TryZone(item.Event.TimeZoneId, out var zone))
        {
            return Fail(RegistrationOutcome.NotFound, "Event not found.");
        }

        var all = await LoadActiveAsync(itemId);
        var registration = all.FirstOrDefault(r => r.Id == registrationId) ?? await db.EventRegistrations.FirstOrDefaultAsync(r => r.Id == registrationId && r.EventItemId == itemId);
        if (registration is null)
        {
            return Fail(RegistrationOutcome.NotFound, "Registration not found.");
        }

        var notes = new List<EventNotificationEntity>();
        var touched = ReleaseExpired(item, all, time.GetUtcNow(), notes);
        return await work(new Ctx(item, zone, all, registration, notes, touched));
    }

    /// <summary>Runs `work`, redoing it from fresh data when another request moved seats in between.</summary>
    private async Task<RegistrationResult> RunAsync(Func<Task<RegistrationResult>> work)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            db.ChangeTracker.Clear();
            try
            {
                return await work();
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another registration took or freed a seat first: start over and decide again against the new count.
            }
            catch (DbUpdateException) when (attempt < MaxAttempts - 1)
            {
                // A unique ticket-code or duplicate race: retry; the duplicate check then returns the winner's ticket.
            }
        }

        db.ChangeTracker.Clear();
        return Fail(RegistrationOutcome.Conflict, "The event is very busy. Please try again.", "BUSY");
    }

    private async Task SaveIfChangedAsync(EventEntity detail, bool changed, List<EventNotificationEntity> notes)
    {
        if (!changed && notes.Count == 0)
        {
            return;
        }

        db.EventNotifications.AddRange(notes);
        detail.RegistrationVersion++;
        await db.SaveChangesAsync();
    }

    /// <summary>Gives the registration a seat if one is free, else the waitlist. A paid event holds the seat as `pending_payment`.</summary>
    private static void Place(EventEntity detail, List<EventRegistrationEntity> all, EventRegistrationEntity registration, DateTimeOffset now)
    {
        var held = all.Count(r => r != registration && r.OccurrenceStartUtc == registration.OccurrenceStartUtc && RegistrationStatuses.HoldingSeat.Contains(r.Status));
        var waiting = all.Any(r => r != registration && r.OccurrenceStartUtc == registration.OccurrenceStartUtc && r.Status == RegistrationStatuses.Waitlisted);
        if (detail.Capacity is { } capacity && (held >= capacity || waiting))
        {
            registration.Status = RegistrationStatuses.Waitlisted;
            registration.DeadlineAt = null;
        }
        else
        {
            MoveToSeat(detail, registration, now);
        }

        registration.UpdatedAt = now;
    }

    private static void MoveToSeat(EventEntity detail, EventRegistrationEntity registration, DateTimeOffset now)
    {
        if (detail.PriceMinor is > 0)
        {
            registration.Status = RegistrationStatuses.PendingPayment;
            registration.DeadlineAt = now + PaymentWindow;
        }
        else
        {
            registration.Status = RegistrationStatuses.Confirmed;
            registration.DeadlineAt = null;
        }

        registration.UpdatedAt = now;
    }

    /// <summary>Offers freed seats to the waitlist in order. The seat is held for the offer window.</summary>
    private void Promote(ContentItemEntity item, List<EventRegistrationEntity> all, DateTimeOffset occurrence, DateTimeOffset now, List<EventNotificationEntity> notes)
    {
        var detail = item.Event!;
        if (detail.Capacity is not { } capacity)
        {
            return;
        }

        var held = all.Count(r => r.OccurrenceStartUtc == occurrence && RegistrationStatuses.HoldingSeat.Contains(r.Status));
        foreach (var next in all.Where(r => r.OccurrenceStartUtc == occurrence && r.Status == RegistrationStatuses.Waitlisted).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal).ToList())
        {
            if (held >= capacity)
            {
                break;
            }

            next.Status = RegistrationStatuses.Offered;
            next.DeadlineAt = now + OfferWindow;
            next.UpdatedAt = now;
            held++;
            notes.Add(Note(next, item, Unprotect(next), "event.waitlist.offer"));
        }
    }

    private bool ReleaseExpired(ContentItemEntity item, List<EventRegistrationEntity> all, DateTimeOffset now, List<EventNotificationEntity> notes)
    {
        var lapsed = all.Where(r => r.Status is RegistrationStatuses.PendingPayment or RegistrationStatuses.Offered && r.DeadlineAt is { } d && d < now).ToList();
        if (lapsed.Count == 0)
        {
            return false;
        }

        foreach (var registration in lapsed)
        {
            registration.Status = RegistrationStatuses.Cancelled;
            registration.DecisionReason = "expired";
            registration.DeadlineAt = null;
            registration.UpdatedAt = now;
            notes.Add(Note(registration, item, Unprotect(registration), "event.registration.expired"));
        }

        foreach (var occurrence in lapsed.Select(r => r.OccurrenceStartUtc).Distinct())
        {
            Promote(item, all, occurrence, now, notes);
        }

        return true;
    }

    // ---- helpers

    private async Task<ContentItemEntity?> LoadEventAsync(string slug, bool publishedOnly) =>
        await db.Items.Include(i => i.Event).FirstOrDefaultAsync(i => i.Type == ContentTypes.Event && i.Slug == slug && i.DeletedAt == null && (!publishedOnly || i.Status == StatusRules.Published));

    private Task<List<EventRegistrationEntity>> LoadActiveAsync(string itemId) =>
        db.EventRegistrations.Where(r => r.EventItemId == itemId && RegistrationStatuses.Active.Contains(r.Status)).ToListAsync();

    private string Unprotect(EventRegistrationEntity r) => privacy.Unprotect(r.EmailProtected);

    private EventNotificationEntity Note(EventRegistrationEntity r, ContentItemEntity item, string email, string template) => new()
    {
        RegistrationId = r.Id,
        Template = template,
        Language = r.Language,
        RecipientProtected = privacy.Protect(email),
        PayloadJson = privacy.Protect(JsonSerializer.Serialize(new
        {
            eventSlug = item.Slug,
            ticketCode = r.TicketCode,
            calendarPath = $"/api/v1/content/event-tickets/{r.TicketCode}/calendar.ics",
            occurrenceStartUtc = r.OccurrenceStartUtc,
            status = r.Status,
            deadlineAt = r.DeadlineAt
        }, Json)),
        CreatedAt = time.GetUtcNow()
    };

    private TicketView ToTicket(EventRegistrationEntity r, ContentItemEntity item, TimeZoneInfo zone, List<EventRegistrationEntity> all)
    {
        int? position = r.Status == RegistrationStatuses.Waitlisted
            ? 1 + all.Count(o => o.OccurrenceStartUtc == r.OccurrenceStartUtc && o.Status == RegistrationStatuses.Waitlisted && (o.CreatedAt < r.CreatedAt || o.CreatedAt == r.CreatedAt && string.CompareOrdinal(o.Id, r.Id) < 0))
            : null;
        var detail = item.Event!;
        return new TicketView(r.TicketCode, r.Status, item.Slug, r.OccurrenceStartUtc, detail.TimeZoneId,
            TimeZoneInfo.ConvertTime(r.OccurrenceStartUtc, zone).ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture),
            privacy.Unprotect(r.NameProtected), r.DeadlineAt, position, detail.PriceMinor, detail.Currency,
            r.Status is RegistrationStatuses.Rejected or RegistrationStatuses.Cancelled ? r.DecisionReason : null, r.CheckedInAt);
    }

    private AdminRegistrationView ToAdmin(EventRegistrationEntity r) => new(r.Id, r.Status, r.TicketCode, r.OccurrenceStartUtc, privacy.Unprotect(r.NameProtected),
        privacy.Unprotect(r.EmailProtected), r.PhoneProtected is null ? null : privacy.Unprotect(r.PhoneProtected), r.MemberId, r.DeadlineAt, r.DecisionReason, r.PaymentReference, r.CreatedAt, r.CheckedInAt);

    private static RegistrationResult Fail(RegistrationOutcome outcome, string error, string? code = null) => new(outcome, Error: error, Code: code);

    private static string NewTicketCode()
    {
        // 128 random bits as 26 Crockford base32 characters: unguessable, and unambiguous when read aloud or typed.
        var bytes = RandomNumberGenerator.GetBytes(16);
        var chars = new char[26];
        var bits = 0;
        var buffer = 0;
        var index = 0;
        foreach (var b in bytes)
        {
            buffer = buffer << 8 | b;
            bits += 8;
            while (bits >= 5 && index < 26)
            {
                chars[index++] = Crockford[buffer >> (bits - 5) & 31];
                bits -= 5;
            }
        }

        if (index < 26)
        {
            chars[index] = Crockford[buffer << (5 - bits) & 31];
        }

        return new string(chars);
    }

    private static bool IsEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 254 && System.Net.Mail.MailAddress.TryCreate(value.Trim(), out var address) && address.Address == value.Trim() && address.Host.Contains('.');

    private static bool TryZone(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }
}
