using System.Security.Cryptography;
using System.Text.Json;
using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

public sealed record CheckInItem(string? TicketCode, string? RegistrationId, DateTimeOffset? CheckedInAt);

public sealed record CheckInRequest(List<CheckInItem>? Items);

/// <summary>`result` is `checked_in`, `already_checked_in`, `not_found`, `wrong_event` or `not_eligible` (with the registration's `status`).</summary>
public sealed record CheckInResultItem(string? TicketCode, string? RegistrationId, string Result, string? Status, DateTimeOffset? CheckedInAt);

public sealed record AttendanceCloseRequest(DateTimeOffset? Occurrence);

public sealed record AttendanceCloseResult(DateTimeOffset Occurrence, int NoShows);

public sealed record IssueCertificateRequest(string? Name);

public sealed record CertificateView(string Number, string Name, string Language, DateTimeOffset IssuedAt);

public sealed record FeedbackQuestion(string? Id, string? Type, Dictionary<string, string>? Prompt, bool Required, List<string>? Options);

public sealed record FeedbackFormRequest(List<FeedbackQuestion>? Questions, bool? IsOpen);

public sealed record FeedbackFormView(List<FeedbackQuestion> Questions, bool IsOpen);

public sealed record FeedbackSubmission(Dictionary<string, JsonElement>? Answers);

public sealed record FeedbackQuestionResult(string Id, string Type, int Answered, double? Average, Dictionary<string, int>? Counts, List<string>? Texts);

public sealed record FeedbackResults(int Responses, int Attended, List<FeedbackQuestionResult> Questions);

public enum AttendanceOutcome
{
    Ok,
    Created,
    NotFound,
    Invalid,
    Conflict
}

public sealed record AttendanceResult<T>(AttendanceOutcome Outcome, T? Value = default, string? Error = null, string? Code = null);

/// <summary>
/// Check-in, no-show tracking, certificates and feedback. Check-in is idempotent and order-independent: the frontend queues scans while
/// offline and replays them late and out of order, so repeating a ticket is a success, the earliest reported time wins, and a late scan
/// rescues someone already marked `no_show`.
/// </summary>
public sealed class EventAttendanceStore(ContentDbContext db, TimeProvider time, RegistrationPrivacy privacy)
{
    public const int MaxBatch = 500;
    private const int MaxQuestions = 30;
    private static readonly string[] QuestionTypes = ["rating", "text", "choice"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- check-in and no-shows

    public async Task<AttendanceResult<List<CheckInResultItem>>> CheckInAsync(string slug, List<CheckInItem>? items)
    {
        if (items is null or { Count: 0 } || items.Count > MaxBatch)
        {
            return new(AttendanceOutcome.Invalid, Error: $"Send between 1 and {MaxBatch} check-ins.");
        }

        var item = await LoadEventAsync(slug);
        if (item is null)
        {
            return new(AttendanceOutcome.NotFound, Error: "Event not found.");
        }

        var now = time.GetUtcNow();
        var codes = items.Select(i => Normalize(i.TicketCode)).Where(c => c.Length > 0).Distinct().ToList();
        var ids = items.Select(i => i.RegistrationId?.Trim() ?? string.Empty).Where(i => i.Length > 0).Distinct().ToList();
        var found = await db.EventRegistrations.Where(r => codes.Contains(r.TicketCode) || ids.Contains(r.Id)).ToListAsync();

        var results = new List<CheckInResultItem>();
        foreach (var entry in items)
        {
            var code = Normalize(entry.TicketCode);
            var registration = code.Length > 0 ? found.FirstOrDefault(r => r.TicketCode == code) : found.FirstOrDefault(r => r.Id == entry.RegistrationId?.Trim());
            if (registration is null)
            {
                results.Add(new(entry.TicketCode, entry.RegistrationId, "not_found", null, null));
                continue;
            }

            if (registration.EventItemId != item.Id)
            {
                results.Add(new(entry.TicketCode, entry.RegistrationId, "wrong_event", null, null));
                continue;
            }

            if (registration.Status is not (RegistrationStatuses.Confirmed or RegistrationStatuses.NoShow))
            {
                results.Add(new(entry.TicketCode, entry.RegistrationId, "not_eligible", registration.Status, registration.CheckedInAt));
                continue;
            }

            // A scan cannot come from the future; clamp a skewed phone clock rather than reject a real arrival.
            var at = entry.CheckedInAt is { } reported ? reported.ToUniversalTime() : now;
            if (at > now.AddMinutes(5))
            {
                at = now;
            }

            var source = code.Length > 0 ? "qr" : "manual";
            var first = registration.CheckedInAt is null;
            if (first || at < registration.CheckedInAt)
            {
                registration.CheckedInAt = at;
                registration.CheckInSource = first ? source : registration.CheckInSource;
                registration.UpdatedAt = now;
            }

            if (registration.Status == RegistrationStatuses.NoShow)
            {
                registration.Status = RegistrationStatuses.Confirmed;
                registration.UpdatedAt = now;
            }

            results.Add(new(entry.TicketCode, entry.RegistrationId, first ? "checked_in" : "already_checked_in", registration.Status, registration.CheckedInAt));
        }

        await db.SaveChangesAsync();
        return new(AttendanceOutcome.Ok, results);
    }

    /// <summary>Marks every confirmed registration of an ended occurrence that never checked in as `no_show`. Cancelled registrations are left alone, so the two stay distinguishable.</summary>
    public async Task<AttendanceResult<AttendanceCloseResult>> CloseAttendanceAsync(string slug, DateTimeOffset? occurrence)
    {
        var item = await LoadEventAsync(slug);
        if (item?.Event is null || !TryZone(item.Event.TimeZoneId, out var zone))
        {
            return new(AttendanceOutcome.NotFound, Error: "Event not found.");
        }

        var now = time.GetUtcNow();
        var occurrences = EventStore.Expand(item.Event, zone).ToList();
        var target = occurrence is { } wanted ? occurrences.FirstOrDefault(o => o.StartUtc == wanted.ToUniversalTime()) : occurrences.LastOrDefault(o => o.EndUtc <= now);
        if (target is null)
        {
            return new(AttendanceOutcome.Invalid, Error: occurrence is null ? "No occurrence of this event has ended yet." : "No such occurrence of this event.");
        }

        if (target.EndUtc > now)
        {
            return new(AttendanceOutcome.Conflict, Error: "That occurrence has not ended yet.", Code: "NOT_ENDED");
        }

        var open = (await db.EventRegistrations.Where(r => r.EventItemId == item.Id && r.Status == RegistrationStatuses.Confirmed && r.CheckedInAt == null).ToListAsync())
            .Where(r => r.OccurrenceStartUtc == target.StartUtc).ToList();
        foreach (var registration in open)
        {
            registration.Status = RegistrationStatuses.NoShow;
            registration.UpdatedAt = now;
        }

        await db.SaveChangesAsync();
        return new(AttendanceOutcome.Ok, new AttendanceCloseResult(target.StartUtc, open.Count));
    }

    // ---- certificates

    public async Task<AttendanceResult<CertificateView>> IssueCertificateAsync(string slug, string registrationId, string? name, string actorId)
    {
        var item = await LoadEventAsync(slug);
        var registration = item is null ? null : await db.EventRegistrations.FirstOrDefaultAsync(r => r.Id == registrationId && r.EventItemId == item.Id);
        if (item is null || registration is null)
        {
            return new(AttendanceOutcome.NotFound, Error: "Registration not found.");
        }

        if (registration.CheckedInAt is null || registration.Status != RegistrationStatuses.Confirmed)
        {
            return new(AttendanceOutcome.Conflict, Error: "A certificate is only issued to someone who checked in.", Code: "NOT_ATTENDED");
        }

        if (await db.EventCertificates.AnyAsync(c => c.RegistrationId == registration.Id))
        {
            return new(AttendanceOutcome.Conflict, Error: "A certificate was already issued. Correct names before issuing.", Code: "ALREADY_ISSUED");
        }

        var printed = string.IsNullOrWhiteSpace(name) ? privacy.Unprotect(registration.NameProtected) : name.Trim();
        if (printed.Length > 200)
        {
            return new(AttendanceOutcome.Invalid, Error: "The name is too long.");
        }

        var now = time.GetUtcNow();
        var certificate = new EventCertificateEntity
        {
            Id = $"crt_{Guid.NewGuid():N}",
            RegistrationId = registration.Id,
            EventItemId = item.Id,
            Number = $"KJC-{now.Year}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(5))}",
            NameProtected = privacy.Protect(printed),
            Language = registration.Language,
            IssuedAt = now,
            IssuedBy = actorId
        };
        db.EventCertificates.Add(certificate);
        await db.SaveChangesAsync();
        return new(AttendanceOutcome.Created, new CertificateView(certificate.Number, printed, certificate.Language, certificate.IssuedAt));
    }

    /// <summary>The PDF of an issued certificate, found by ticket code (the holder) or by registration id (an organiser).</summary>
    public async Task<(byte[] Pdf, string FileName)?> CertificatePdfAsync(string? ticketCode, string? slug, string? registrationId)
    {
        var code = Normalize(ticketCode);
        var registration = code.Length > 0
            ? await db.EventRegistrations.AsNoTracking().FirstOrDefaultAsync(r => r.TicketCode == code)
            : await db.EventRegistrations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == registrationId);
        var certificate = registration is null ? null : await db.EventCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.RegistrationId == registration.Id);
        if (registration is null || certificate is null)
        {
            return null;
        }

        var item = await db.Items.AsNoTracking().Include(i => i.Event).Include(i => i.Translations)
            .FirstOrDefaultAsync(i => i.Id == registration.EventItemId && (slug == null || i.Slug == slug));
        if (item?.Event is null || !TryZone(item.Event.TimeZoneId, out var zone))
        {
            return null;
        }

        var translation = item.Translations.FirstOrDefault(t => t.Lang == certificate.Language) ?? item.Translations.FirstOrDefault(t => t.Lang == "en") ?? item.Translations.FirstOrDefault();
        var date = TimeZoneInfo.ConvertTime(registration.OccurrenceStartUtc, zone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var pdf = EventCertificatePdf.Render(certificate.Number, privacy.Unprotect(certificate.NameProtected), translation?.Title ?? item.Slug, date, "Kolpojontro", "কল্পজন্ত্র");
        return (pdf, $"certificate-{certificate.Number}.pdf");
    }

    // ---- feedback

    public async Task<AttendanceResult<FeedbackFormView>> PutFeedbackFormAsync(string slug, FeedbackFormRequest request)
    {
        var item = await LoadEventAsync(slug);
        if (item is null)
        {
            return new(AttendanceOutcome.NotFound, Error: "Event not found.");
        }

        var form = await db.EventFeedbackForms.FirstOrDefaultAsync(f => f.EventItemId == item.Id);
        var questions = request.Questions ?? (form is null ? [] : JsonSerializer.Deserialize<List<FeedbackQuestion>>(form.QuestionsJson, Json) ?? []);
        if (ValidateQuestions(questions) is { } error)
        {
            return new(AttendanceOutcome.Invalid, Error: error);
        }

        if (form is null)
        {
            form = new EventFeedbackFormEntity { EventItemId = item.Id };
            db.EventFeedbackForms.Add(form);
        }
        else if (request.Questions is not null && await db.EventFeedbackResponses.AnyAsync(r => r.EventItemId == item.Id))
        {
            return new(AttendanceOutcome.Conflict, Error: "Responses exist; the questions can no longer change. Close the form instead.", Code: "HAS_RESPONSES");
        }

        form.QuestionsJson = JsonSerializer.Serialize(questions, Json);
        form.IsOpen = request.IsOpen ?? form.IsOpen;
        form.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
        return new(AttendanceOutcome.Ok, new FeedbackFormView(questions, form.IsOpen));
    }

    public async Task<AttendanceResult<FeedbackFormView>> AdminFeedbackFormAsync(string slug)
    {
        var item = await LoadEventAsync(slug);
        var form = item is null ? null : await db.EventFeedbackForms.AsNoTracking().FirstOrDefaultAsync(f => f.EventItemId == item.Id);
        return form is null ? new(AttendanceOutcome.NotFound, Error: "No feedback form for this event.") : new(AttendanceOutcome.Ok, ToView(form));
    }

    /// <summary>The form for someone who attended, found by their ticket code. Others get nothing: it is not a public form.</summary>
    public async Task<AttendanceResult<FeedbackFormView>> TicketFormAsync(string code)
    {
        var (registration, form) = await Attendee(code);
        if (registration is null || form is null)
        {
            return new(AttendanceOutcome.NotFound, Error: "There is no feedback form for this ticket.");
        }

        return new(AttendanceOutcome.Ok, ToView(form));
    }

    public async Task<AttendanceResult<FeedbackFormView>> SubmitFeedbackAsync(string code, FeedbackSubmission submission)
    {
        var (registration, form) = await Attendee(code);
        if (registration is null || form is null)
        {
            return new(AttendanceOutcome.NotFound, Error: "There is no feedback form for this ticket.");
        }

        if (!form.IsOpen)
        {
            return new(AttendanceOutcome.Conflict, Error: "Feedback is closed.", Code: "FEEDBACK_CLOSED");
        }

        if (await db.EventFeedbackResponses.AnyAsync(r => r.RegistrationId == registration.Id))
        {
            return new(AttendanceOutcome.Conflict, Error: "Feedback was already submitted for this ticket.", Code: "ALREADY_SUBMITTED");
        }

        var questions = JsonSerializer.Deserialize<List<FeedbackQuestion>>(form.QuestionsJson, Json) ?? [];
        var answers = new Dictionary<string, object>();
        var given = submission.Answers ?? [];
        if (given.Keys.FirstOrDefault(k => questions.All(q => q.Id != k)) is { } unknown)
        {
            return new(AttendanceOutcome.Invalid, Error: $"Unknown question '{unknown}'.");
        }

        foreach (var question in questions)
        {
            if (!given.TryGetValue(question.Id!, out var value) || value.ValueKind is JsonValueKind.Null || value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
            {
                if (question.Required)
                {
                    return new(AttendanceOutcome.Invalid, Error: $"Question '{question.Id}' is required.");
                }

                continue;
            }

            switch (question.Type)
            {
                case "rating" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var rating) && rating is >= 1 and <= 5:
                    answers[question.Id!] = rating;
                    break;
                case "choice" when value.ValueKind == JsonValueKind.String && question.Options!.Contains(value.GetString()!):
                    answers[question.Id!] = value.GetString()!;
                    break;
                case "text" when value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 2000:
                    answers[question.Id!] = value.GetString()!.Trim();
                    break;
                default:
                    return new(AttendanceOutcome.Invalid, Error: $"Answer to '{question.Id}' is not valid for a {question.Type} question.");
            }
        }

        db.EventFeedbackResponses.Add(new EventFeedbackResponseEntity
        {
            Id = $"fbk_{Guid.NewGuid():N}",
            EventItemId = registration.EventItemId,
            RegistrationId = registration.Id,
            AnswersJson = JsonSerializer.Serialize(answers, Json),
            CreatedAt = time.GetUtcNow()
        });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return new(AttendanceOutcome.Conflict, Error: "Feedback was already submitted for this ticket.", Code: "ALREADY_SUBMITTED");
        }

        return new(AttendanceOutcome.Created, ToView(form));
    }

    /// <summary>Aggregates per question. Text answers are listed without any identity. Organisers with `events.manage` only.</summary>
    public async Task<AttendanceResult<FeedbackResults>> ResultsAsync(string slug)
    {
        var item = await LoadEventAsync(slug);
        var form = item is null ? null : await db.EventFeedbackForms.AsNoTracking().FirstOrDefaultAsync(f => f.EventItemId == item.Id);
        if (item is null || form is null)
        {
            return new(AttendanceOutcome.NotFound, Error: "No feedback form for this event.");
        }

        var responses = (await db.EventFeedbackResponses.AsNoTracking().Where(r => r.EventItemId == item.Id).ToListAsync())
            .Select(r => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.AnswersJson, Json) ?? []).ToList();
        var attended = await db.EventRegistrations.CountAsync(r => r.EventItemId == item.Id && r.CheckedInAt != null);
        var results = new List<FeedbackQuestionResult>();
        foreach (var question in JsonSerializer.Deserialize<List<FeedbackQuestion>>(form.QuestionsJson, Json) ?? [])
        {
            var answers = responses.Where(r => r.ContainsKey(question.Id!)).Select(r => r[question.Id!]).ToList();
            results.Add(question.Type switch
            {
                "rating" => new(question.Id!, "rating", answers.Count, answers.Count == 0 ? null : Math.Round(answers.Average(a => a.GetInt32()), 2),
                    Enumerable.Range(1, 5).ToDictionary(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture), n => answers.Count(a => a.GetInt32() == n)), null),
                "choice" => new(question.Id!, "choice", answers.Count, null, question.Options!.ToDictionary(o => o, o => answers.Count(a => a.GetString() == o)), null),
                _ => new(question.Id!, "text", answers.Count, null, null, [.. answers.Select(a => a.GetString()!).Order(StringComparer.Ordinal)])
            });
        }

        return new(AttendanceOutcome.Ok, new FeedbackResults(responses.Count, attended, results));
    }

    // ---- helpers

    private async Task<(EventRegistrationEntity? Registration, EventFeedbackFormEntity? Form)> Attendee(string code)
    {
        var normalized = Normalize(code);
        var registration = normalized.Length == 0 ? null : await db.EventRegistrations.AsNoTracking().FirstOrDefaultAsync(r => r.TicketCode == normalized);
        if (registration?.CheckedInAt is null)
        {
            return (null, null);
        }

        return (registration, await db.EventFeedbackForms.AsNoTracking().FirstOrDefaultAsync(f => f.EventItemId == registration.EventItemId));
    }

    private static FeedbackFormView ToView(EventFeedbackFormEntity form) =>
        new(JsonSerializer.Deserialize<List<FeedbackQuestion>>(form.QuestionsJson, Json) ?? [], form.IsOpen);

    private static string? ValidateQuestions(List<FeedbackQuestion> questions)
    {
        if (questions.Count > MaxQuestions)
        {
            return $"At most {MaxQuestions} questions.";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in questions)
        {
            if (string.IsNullOrWhiteSpace(q.Id) || q.Id.Length > 40 || !seen.Add(q.Id))
            {
                return "Every question needs a unique id of up to 40 characters.";
            }

            if (q.Type is null || !QuestionTypes.Contains(q.Type))
            {
                return $"Question '{q.Id}' type must be rating, text or choice.";
            }

            if (q.Prompt is null || !q.Prompt.Any(p => p.Key is "en" or "bn" && !string.IsNullOrWhiteSpace(p.Value)))
            {
                return $"Question '{q.Id}' needs a prompt in en and/or bn.";
            }

            if (q.Type == "choice" && (q.Options is null || q.Options.Count is < 2 or > 20 || q.Options.Any(string.IsNullOrWhiteSpace) || q.Options.Distinct().Count() != q.Options.Count))
            {
                return $"Choice question '{q.Id}' needs 2 to 20 distinct options.";
            }
        }

        return null;
    }

    private async Task<ContentItemEntity?> LoadEventAsync(string slug) =>
        await db.Items.Include(i => i.Event).FirstOrDefaultAsync(i => i.Type == ContentTypes.Event && i.Slug == slug && i.DeletedAt == null);

    private static string Normalize(string? code) => code?.Trim().ToUpperInvariant() ?? string.Empty;

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
