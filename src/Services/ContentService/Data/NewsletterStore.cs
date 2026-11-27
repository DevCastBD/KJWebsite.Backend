using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ContentService.Data;

public sealed class NewsletterOptions
{
    public const string SectionName = "Newsletter";

    /// <summary>Key (at least 32 chars) that signs confirm and manage links. Required outside Development.</summary>
    public string LinkKey { get; set; } = string.Empty;

    /// <summary>Shared secret the email provider sends as `X-Webhook-Secret` with bounce and complaint events. Empty disables the webhook.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Public site origin, used for the confirmation link in the email.</summary>
    public string SiteUrl { get; set; } = "http://localhost:3000";

    /// <summary>Public API origin, used for the one-click unsubscribe URL.</summary>
    public string ApiUrl { get; set; } = "http://localhost:7002";

    /// <summary>Campaign delivery rate. Recipients are spaced evenly at this rate so a new domain does not send a thousand messages at once.</summary>
    public int SendPerMinute { get; set; } = 120;

    /// <summary>Whether the background dispatcher delivers queued messages.</summary>
    public bool DispatchEnabled { get; set; } = true;
}

public sealed record NewsletterSubscribeRequest(string? Email, string? Lang, string[]? Topics, string? Source, string? Website);
public sealed record NewsletterPreferencesRequest(string? Lang, string[]? Topics);
public sealed record NewsletterPreferencesView(string Email, string Lang, string[] Topics, string Status);
public sealed record NewsletterProviderEvent(string? Email, string? Type);
public sealed record NewsletterSubscriberView(string Id, string Email, string Lang, string[] Topics, string Status, string Source, string? SuppressionReason, DateTimeOffset CreatedAt, DateTimeOffset? ConfirmedAt, DateTimeOffset? UnsubscribedAt);
public sealed record NewsletterSegmentFilter(string[]? Langs, string[]? Sources, string[]? Topics, bool? AttendedEvent);
public sealed record NewsletterSegmentRequest(string? Name, NewsletterSegmentFilter? Filter);
public sealed record NewsletterSegmentView(string Id, string Name, NewsletterSegmentFilter Filter, DateTimeOffset CreatedAt);

/// <summary>
/// The newsletter list. Nothing is sent to an address until the owner confirms it from the link in the confirmation email (double opt-in);
/// confirm and manage links are HMAC-signed so they need no login and no stored secret; hard bounces and complaints suppress an address for good.
/// </summary>
public sealed class NewsletterStore(ContentDbContext db, TimeProvider time, IOptions<NewsletterOptions> options, IHostEnvironment environment, RegistrationPrivacy privacy)
{
    public const int SoftBounceLimit = 3;
    private static readonly TimeSpan ResendAfter = TimeSpan.FromMinutes(10);
    private static readonly string[] Languages = ["bn", "en"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly byte[] _key = ReadKey(options.Value, environment);
    private readonly NewsletterOptions _options = options.Value;

    private static byte[] ReadKey(NewsletterOptions options, IHostEnvironment environment)
    {
        var key = options.LinkKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException("Newsletter:LinkKey is required (at least 32 characters).");
            }

            key = "kj-newsletter-development-link-key-only-!";
        }

        return key.Length < 32 ? throw new InvalidOperationException("Newsletter:LinkKey must be at least 32 characters.") : Encoding.UTF8.GetBytes(key);
    }

    // ---- signed links

    public string Token(string purpose, string subscriberId) =>
        subscriberId + "." + Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(purpose + ":" + subscriberId)))[..32].ToLowerInvariant();

    private string? ReadToken(string purpose, string? token)
    {
        var dot = token?.IndexOf('.') ?? -1;
        if (token is null || dot <= 0)
        {
            return null;
        }

        var id = token[..dot];
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Token(purpose, id)), Encoding.UTF8.GetBytes(token)) ? id : null;
    }

    // ---- public flows

    /// <summary>Asks for a subscription. Always looks the same to the caller (no address enumeration); a confirmation email is queued only when one is useful.</summary>
    public async Task<(bool Valid, string? Error)> SubscribeAsync(NewsletterSubscribeRequest request)
    {
        if (!TryEmail(request.Email, out var email))
        {
            return (false, "Enter a valid email address.");
        }

        var lang = NormalizeLang(request.Lang);
        if (lang is null)
        {
            return (false, "lang must be bn or en.");
        }

        if (!string.IsNullOrWhiteSpace(request.Website))
        {
            return (true, null); // honeypot: a bot filled the hidden field
        }

        var now = time.GetUtcNow();
        var subscriber = await db.NewsletterSubscribers.FirstOrDefaultAsync(s => s.Email == email);
        if (subscriber is null)
        {
            subscriber = new NewsletterSubscriberEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Email = email,
                Lang = lang,
                TopicsJson = JsonSerializer.Serialize(CleanTopics(request.Topics), Json),
                Source = CleanSource(request.Source),
                CreatedAt = now,
                UpdatedAt = now
            };
            db.NewsletterSubscribers.Add(subscriber);
        }
        else if (subscriber.Status is SubscriberStatuses.Confirmed or SubscriberStatuses.Suppressed)
        {
            return (true, null); // already on the list, or barred from it: say nothing either way
        }
        else if (subscriber.Status == SubscriberStatuses.Pending && subscriber.ConfirmationSentAt > now - ResendAfter)
        {
            return (true, null);
        }
        else
        {
            subscriber.Status = SubscriberStatuses.Pending;
            subscriber.Lang = lang;
            subscriber.TopicsJson = JsonSerializer.Serialize(CleanTopics(request.Topics), Json);
            subscriber.UpdatedAt = now;
        }

        subscriber.ConfirmationSentAt = now;
        AddEvent(subscriber.Id, "subscribe-requested", $"source={subscriber.Source}", now);
        var link = $"{_options.SiteUrl.TrimEnd('/')}/newsletter/confirm?token={Token("confirm", subscriber.Id)}";
        var (subject, body) = lang == "bn"
            ? ("আপনার নিউজলেটার সাবস্ক্রিপশন নিশ্চিত করুন", $"নিউজলেটার পেতে নিচের লিংকে ক্লিক করে আপনার ঠিকানা নিশ্চিত করুন:\n{link}\n\nআপনি অনুরোধ না করলে এই ইমেইল উপেক্ষা করুন।")
            : ("Confirm your newsletter subscription", $"Confirm your address to start receiving the newsletter:\n{link}\n\nIf you did not ask for this, ignore this email.");
        db.NewsletterMessages.Add(new NewsletterMessageEntity { SubscriberId = subscriber.Id, ToEmail = email, Kind = "confirmation", Lang = lang, Subject = subject, Body = body, CreatedAt = now });
        await SaveAsync();
        return (true, null);
    }

    public async Task<bool> ConfirmAsync(string? token)
    {
        var subscriber = await Find("confirm", token);
        if (subscriber is null)
        {
            return false;
        }

        if (subscriber.Status == SubscriberStatuses.Pending)
        {
            var now = time.GetUtcNow();
            subscriber.Status = SubscriberStatuses.Confirmed;
            subscriber.ConfirmedAt = now;
            subscriber.UpdatedAt = now;
            AddEvent(subscriber.Id, "confirmed", $"source={subscriber.Source}; topics={subscriber.TopicsJson}", now);
            await SaveAsync();
        }

        return subscriber.Status == SubscriberStatuses.Confirmed;
    }

    /// <summary>One-click unsubscribe: no login, no confirmation step. Idempotent. A suppressed address stays suppressed.</summary>
    public async Task<bool> UnsubscribeAsync(string? token)
    {
        var subscriber = await Find("manage", token);
        if (subscriber is null)
        {
            return false;
        }

        if (subscriber.Status is SubscriberStatuses.Pending or SubscriberStatuses.Confirmed)
        {
            var now = time.GetUtcNow();
            subscriber.Status = SubscriberStatuses.Unsubscribed;
            subscriber.UnsubscribedAt = now;
            subscriber.UpdatedAt = now;
            AddEvent(subscriber.Id, "unsubscribed", null, now);
            await SaveAsync();
        }

        return true;
    }

    public async Task<NewsletterPreferencesView?> GetPreferencesAsync(string? token)
    {
        var subscriber = await Find("manage", token);
        return subscriber is null ? null : new NewsletterPreferencesView(Mask(subscriber.Email), subscriber.Lang, Topics(subscriber), subscriber.Status);
    }

    public async Task<(NewsletterPreferencesView? View, string? Error)> UpdatePreferencesAsync(string? token, NewsletterPreferencesRequest request)
    {
        var subscriber = await Find("manage", token);
        if (subscriber is null)
        {
            return (null, null);
        }

        var lang = request.Lang is null ? subscriber.Lang : NormalizeLang(request.Lang);
        if (lang is null)
        {
            return (null, "lang must be bn or en.");
        }

        subscriber.Lang = lang;
        if (request.Topics is not null)
        {
            subscriber.TopicsJson = JsonSerializer.Serialize(CleanTopics(request.Topics), Json);
        }

        var now = time.GetUtcNow();
        subscriber.UpdatedAt = now;
        AddEvent(subscriber.Id, "preferences", $"lang={lang}", now);
        await SaveAsync();
        return (new NewsletterPreferencesView(Mask(subscriber.Email), subscriber.Lang, Topics(subscriber), subscriber.Status), null);
    }

    // ---- bounces and complaints

    /// <summary>Applies provider events. `bounce` (permanent) and `complaint` suppress at once; `soft-bounce` suppresses on the third. Returns how many addresses changed.</summary>
    public async Task<(int Applied, string? Error)> ApplyProviderEventsAsync(IReadOnlyList<NewsletterProviderEvent> events)
    {
        var applied = 0;
        var now = time.GetUtcNow();
        foreach (var item in events)
        {
            if (item.Type is not ("bounce" or "soft-bounce" or "complaint"))
            {
                return (0, "type must be bounce, soft-bounce or complaint.");
            }

            if (!TryEmail(item.Email, out var email))
            {
                continue;
            }

            var subscriber = await db.NewsletterSubscribers.FirstOrDefaultAsync(s => s.Email == email);
            if (subscriber is null || subscriber.Status == SubscriberStatuses.Suppressed)
            {
                continue;
            }

            if (item.Type == "soft-bounce")
            {
                subscriber.SoftBounces++;
                AddEvent(subscriber.Id, "soft-bounce", $"count={subscriber.SoftBounces}", now);
                if (subscriber.SoftBounces < SoftBounceLimit)
                {
                    continue;
                }
            }
            else
            {
                AddEvent(subscriber.Id, item.Type, null, now);
            }

            subscriber.Status = SubscriberStatuses.Suppressed;
            subscriber.SuppressionReason = item.Type == "complaint" ? "complaint" : "bounce";
            subscriber.UpdatedAt = now;
            applied++;
        }

        await SaveAsync();
        return (applied, null);
    }

    // ---- sending gate

    /// <summary>Queues a campaign message. Refused unless the subscriber is confirmed, so nothing reaches an unconfirmed, unsubscribed or suppressed address.</summary>
    public async Task<bool> EnqueueCampaignAsync(string subscriberId, string subject, string body)
    {
        var subscriber = await db.NewsletterSubscribers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == subscriberId);
        if (subscriber is not { Status: SubscriberStatuses.Confirmed })
        {
            return false;
        }

        db.NewsletterMessages.Add(new NewsletterMessageEntity
        {
            SubscriberId = subscriber.Id,
            ToEmail = subscriber.Email,
            Kind = "campaign",
            Lang = subscriber.Lang,
            Subject = subject,
            Body = body,
            ListUnsubscribeUrl = $"{_options.ApiUrl.TrimEnd('/')}/api/v1/newsletter/unsubscribe/{Token("manage", subscriber.Id)}",
            CreatedAt = time.GetUtcNow()
        });
        await db.SaveChangesAsync();
        return true;
    }

    // ---- back office

    public async Task<(List<NewsletterSubscriberView> Rows, int Total)> ListAsync(string? status, string? query, int limit, int offset)
    {
        var rows = db.NewsletterSubscribers.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            rows = rows.Where(s => s.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim().ToLowerInvariant();
            rows = rows.Where(s => s.Email.Contains(q));
        }

        var total = await rows.CountAsync();
        var page = await rows.OrderBy(s => s.Email).Skip(offset).Take(limit).ToListAsync();
        return ([.. page.Select(View)], total);
    }

    public async Task<(NewsletterSegmentView? Segment, string? Error)> CreateSegmentAsync(NewsletterSegmentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120)
        {
            return (null, "name is required (max 120 characters).");
        }

        var filter = request.Filter ?? new NewsletterSegmentFilter(null, null, null, null);
        if (filter.Langs?.Any(l => !Languages.Contains(l)) == true)
        {
            return (null, "langs may only contain bn and en.");
        }

        var entity = new NewsletterSegmentEntity { Id = Guid.NewGuid().ToString("N"), Name = request.Name.Trim(), FilterJson = JsonSerializer.Serialize(filter, Json), CreatedAt = time.GetUtcNow() };
        db.NewsletterSegments.Add(entity);
        await db.SaveChangesAsync();
        return (ToView(entity), null);
    }

    public async Task<List<NewsletterSegmentView>> ListSegmentsAsync() =>
        [.. (await db.NewsletterSegments.AsNoTracking().OrderBy(s => s.Name).ThenBy(s => s.Id).ToListAsync()).Select(ToView)];

    public async Task<bool> DeleteSegmentAsync(string id)
    {
        var entity = await db.NewsletterSegments.FirstOrDefaultAsync(s => s.Id == id);
        if (entity is null)
        {
            return false;
        }

        db.NewsletterSegments.Remove(entity);
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// The audience of a segment: confirmed subscribers only (so consent is always respected), in a fixed order, so the same data always gives the same list.
    /// `attendedEvent` matches subscribers whose address checked in to any event (via the registration email hash, never the stored ciphertext).
    /// </summary>
    public async Task<(List<NewsletterSubscriberView> Rows, int Total)?> ResolveSegmentAsync(string id, int limit, int offset)
    {
        var list = await AudienceAsync(id);
        return list is null ? null : ([.. list.Skip(offset).Take(limit).Select(View)], list.Count);
    }

    /// <summary>The confirmed subscribers a campaign would reach: the segment's, or everyone confirmed when `segmentId` is null. Null when the segment does not exist.</summary>
    public async Task<List<NewsletterSubscriberEntity>?> AudienceAsync(string? segmentId)
    {
        var filter = new NewsletterSegmentFilter(null, null, null, null);
        if (segmentId is not null)
        {
            var segment = await db.NewsletterSegments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == segmentId);
            if (segment is null)
            {
                return null;
            }

            filter = JsonSerializer.Deserialize<NewsletterSegmentFilter>(segment.FilterJson, Json) ?? filter;
        }

        var all =await db.NewsletterSubscribers.AsNoTracking().Where(s => s.Status == SubscriberStatuses.Confirmed)
            .OrderBy(s => s.Email).ToListAsync();
        IEnumerable<NewsletterSubscriberEntity> rows = all;
        if (filter.Langs is { Length: > 0 })
        {
            rows = rows.Where(s => filter.Langs.Contains(s.Lang));
        }

        if (filter.Sources is { Length: > 0 })
        {
            rows = rows.Where(s => filter.Sources.Contains(s.Source));
        }

        if (filter.Topics is { Length: > 0 })
        {
            rows = rows.Where(s => Topics(s).Intersect(filter.Topics, StringComparer.OrdinalIgnoreCase).Any());
        }

        if (filter.AttendedEvent == true)
        {
            var attended = (await db.EventRegistrations.AsNoTracking().Where(r => r.CheckedInAt != null).Select(r => r.EmailKey).Distinct().ToListAsync()).ToHashSet();
            rows = rows.Where(s => attended.Contains(privacy.HashEmail(s.Email)));
        }

        return [.. rows];
    }

    /// <summary>The links a message to this subscriber carries: one-click unsubscribe (also the `List-Unsubscribe` header) and the preference centre.</summary>
    public (string UnsubscribeUrl, string PreferencesUrl) Links(string subscriberId)
    {
        var manage = Token("manage", subscriberId);
        return ($"{_options.ApiUrl.TrimEnd('/')}/api/v1/newsletter/unsubscribe/{manage}", $"{_options.SiteUrl.TrimEnd('/')}/newsletter/preferences?token={manage}");
    }

    public int SendPerMinute => Math.Max(1, _options.SendPerMinute);

    // ---- helpers

    private async Task<NewsletterSubscriberEntity?> Find(string purpose, string? token)
    {
        var id = ReadToken(purpose, token);
        return id is null ? null : await db.NewsletterSubscribers.FirstOrDefaultAsync(s => s.Id == id);
    }

    private void AddEvent(string subscriberId, string type, string? detail, DateTimeOffset at) =>
        db.NewsletterEvents.Add(new NewsletterEventEntity { SubscriberId = subscriberId, Type = type, Detail = detail, At = at });

    private async Task SaveAsync()
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // two concurrent sign-ups for one address: the unique index rejects the second, which is the intended outcome
            db.ChangeTracker.Clear();
        }
    }

    private static NewsletterSubscriberView View(NewsletterSubscriberEntity s) =>
        new(s.Id, s.Email, s.Lang, Topics(s), s.Status, s.Source, s.SuppressionReason, s.CreatedAt, s.ConfirmedAt, s.UnsubscribedAt);

    private static NewsletterSegmentView ToView(NewsletterSegmentEntity s) =>
        new(s.Id, s.Name, JsonSerializer.Deserialize<NewsletterSegmentFilter>(s.FilterJson, Json) ?? new NewsletterSegmentFilter(null, null, null, null), s.CreatedAt);

    private static string[] Topics(NewsletterSubscriberEntity s) => JsonSerializer.Deserialize<string[]>(s.TopicsJson, Json) ?? [];

    private static string[] CleanTopics(string[]? topics) =>
        [.. (topics ?? []).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length is > 0 and <= 40).Distinct().Take(20)];

    private static string CleanSource(string? source)
    {
        var clean = new string([.. (source ?? string.Empty).Trim().ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(40)]);
        return clean.Length == 0 ? "web" : clean;
    }

    private static string? NormalizeLang(string? lang)
    {
        var value = string.IsNullOrWhiteSpace(lang) ? "bn" : lang.Trim().ToLowerInvariant();
        return Languages.Contains(value) ? value : null;
    }

    private static bool TryEmail(string? raw, out string email)
    {
        email = string.Empty;
        var value = raw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value) || value.Length > 254 || !MailAddress.TryCreate(value, out var parsed) || parsed.Address != value || !value.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        email = value;
        return true;
    }

    private static string Mask(string email)
    {
        var at = email.IndexOf('@');
        return at <= 1 ? email : email[0] + new string('*', at - 1) + email[at..];
    }
}
