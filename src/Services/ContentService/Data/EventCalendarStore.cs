using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

public sealed record FeedTokenView(string Token, string Path, DateTimeOffset CreatedAt);

/// <summary>
/// iCalendar output for events: a feed of public events, a single event, a registrant's invite, and a per-member subscription feed.
/// A member's feed URL is a credential (anyone holding it sees that member's schedule, and calendar apps resend it on every refresh), so
/// the token is 256 random bits, stored only as a hash, revocable, and never derived from the member id.
/// </summary>
public sealed class EventCalendarStore(ContentDbContext db, TimeProvider time)
{
    private const string ProductId = "-//Kolpojontro//Events//EN";
    private const string UidDomain = "events.kolpojontro";
    private static readonly TimeSpan PastWindow = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] VenueKeys = ["venueName", "venueAddress"];

    // ---- public feeds

    /// <summary>Every published event, or one (`slug`), as occurrences from 30 days ago onwards. Null when `slug` is not a published event.</summary>
    public async Task<byte[]?> PublicFeedAsync(string lang, string? slug)
    {
        var items = await db.Items.AsNoTracking().AsSplitQuery().Include(i => i.Event).Include(i => i.Translations)
            .Where(i => i.Type == ContentTypes.Event && i.DeletedAt == null && i.Status == StatusRules.Published && i.Event != null && (slug == null || i.Slug == slug))
            .ToListAsync();
        if (slug is not null && items.Count == 0)
        {
            return null;
        }

        var from = time.GetUtcNow() - PastWindow;
        var calendar = new IcsWriter(slug is null ? "Kolpojontro events" : items[0].Translations.FirstOrDefault(t => t.Lang == lang)?.Title ?? items[0].Slug, ProductId);
        foreach (var item in items.OrderBy(i => i.Event!.StartUtc).ThenBy(i => i.Slug, StringComparer.Ordinal))
        {
            AddEvents(calendar, item, lang, slug is null ? from : DateTimeOffset.MinValue);
        }

        return calendar.ToBytes();
    }

    // ---- registrant invite

    public async Task<(byte[]? Ics, string? Error)> InviteAsync(string code, string lang)
    {
        var wanted = code?.Trim().ToUpperInvariant() ?? string.Empty;
        var registration = wanted.Length == 0 ? null : await db.EventRegistrations.AsNoTracking().FirstOrDefaultAsync(r => r.TicketCode == wanted);
        if (registration is null)
        {
            return (null, "NOT_FOUND");
        }

        if (registration.Status != RegistrationStatuses.Confirmed)
        {
            return (null, "NOT_CONFIRMED");
        }

        var item = await LoadAsync(registration.EventItemId);
        if (item?.Event is null)
        {
            return (null, "NOT_FOUND");
        }

        var calendar = new IcsWriter(item.Translations.FirstOrDefault(t => t.Lang == lang)?.Title ?? item.Slug, ProductId);
        AddEvents(calendar, item, lang, DateTimeOffset.MinValue, only: registration.OccurrenceStartUtc);
        return (calendar.ToBytes(), null);
    }

    // ---- member subscription feed

    public async Task<FeedTokenView> CreateTokenAsync(string subject, string feedPathPrefix)
    {
        var now = time.GetUtcNow();
        foreach (var old in await db.EventFeedTokens.Where(t => t.Subject == subject && t.RevokedAt == null).ToListAsync())
        {
            old.RevokedAt = now;
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        db.EventFeedTokens.Add(new EventFeedTokenEntity { Id = $"eft_{Guid.NewGuid():N}", Subject = subject, TokenHash = Hash(token), CreatedAt = now });
        await db.SaveChangesAsync();
        return new FeedTokenView(token, $"{feedPathPrefix}/{token}.ics", now);
    }

    public async Task<bool> RevokeTokenAsync(string subject)
    {
        var active = await db.EventFeedTokens.Where(t => t.Subject == subject && t.RevokedAt == null).ToListAsync();
        foreach (var token in active)
        {
            token.RevokedAt = time.GetUtcNow();
        }

        await db.SaveChangesAsync();
        return active.Count > 0;
    }

    public async Task<bool> HasTokenAsync(string subject) => await db.EventFeedTokens.AnyAsync(t => t.Subject == subject && t.RevokedAt == null);

    /// <summary>The member's confirmed registrations as a calendar, or null for an unknown or revoked token.</summary>
    public async Task<byte[]?> MemberFeedAsync(string token, string lang)
    {
        var hash = Hash(token);
        var row = await db.EventFeedTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null);
        if (row is null)
        {
            return null;
        }

        row.LastUsedAt = time.GetUtcNow();
        await db.SaveChangesAsync();

        var from = time.GetUtcNow() - PastWindow;
        var mine = (await db.EventRegistrations.AsNoTracking().Where(r => r.MemberId == row.Subject && r.Status == RegistrationStatuses.Confirmed).ToListAsync()).ToList();
        var calendar = new IcsWriter("My Kolpojontro events", ProductId);
        foreach (var group in mine.GroupBy(r => r.EventItemId))
        {
            var item = await LoadAsync(group.Key);
            if (item?.Event is null)
            {
                continue;
            }

            foreach (var registration in group.OrderBy(r => r.OccurrenceStartUtc))
            {
                AddEvents(calendar, item, lang, from, only: registration.OccurrenceStartUtc);
            }
        }

        return calendar.ToBytes();
    }

    // ---- helpers

    private async Task<ContentItemEntity?> LoadAsync(string id) =>
        await db.Items.AsNoTracking().Include(i => i.Event).Include(i => i.Translations).FirstOrDefaultAsync(i => i.Id == id && i.DeletedAt == null);

    private void AddEvents(IcsWriter calendar, ContentItemEntity item, string lang, DateTimeOffset from, DateTimeOffset? only = null)
    {
        var detail = item.Event!;
        if (!TryZone(detail.TimeZoneId, out var zone))
        {
            return;
        }

        var translation = item.Translations.FirstOrDefault(t => t.Lang == lang) ?? item.Translations.FirstOrDefault(t => t.Lang == "en") ?? item.Translations.FirstOrDefault();
        if (translation is null)
        {
            return;
        }

        var location = Location(translation);
        var stamp = time.GetUtcNow();
        var sequence = (int)Math.Min(int.MaxValue, item.UpdatedAt.ToUnixTimeSeconds());
        foreach (var occurrence in EventStore.Expand(detail, zone).Where(o => (only is null || o.StartUtc == only) && o.EndUtc >= from))
        {
            calendar.Event($"{item.Id}-{IcsWriter.Utc(occurrence.StartUtc)}@{UidDomain}", stamp, item.UpdatedAt, occurrence.StartUtc, occurrence.EndUtc,
                translation.Title, Description(translation, detail.OnlineUrl), location, string.IsNullOrWhiteSpace(detail.OnlineUrl) ? null : detail.OnlineUrl, sequence);
        }
    }

    private static string Description(ContentTranslationEntity translation, string onlineUrl)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(translation.Summary))
        {
            parts.Add(translation.Summary);
        }

        if (!string.IsNullOrWhiteSpace(onlineUrl))
        {
            parts.Add(onlineUrl);
        }

        return string.Join("\n", parts);
    }

    private static string? Location(ContentTranslationEntity translation)
    {
        try
        {
            var attributes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(translation.AttributesJson, Json) ?? [];
            var parts = VenueKeys.Select(k => attributes.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null).Where(v => !string.IsNullOrWhiteSpace(v));
            var text = string.Join(", ", parts);
            return text.Length == 0 ? null : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

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
