using System.Security.Cryptography;
using System.Text;
using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ContentService.Data;

public sealed class SocialOptions
{
    public const string SectionName = "Social";

    public string BrandNameEn { get; set; } = "Kolpojontro";
    public string BrandNameBn { get; set; } = "কল্পজন্ত্র";
    public int PollSeconds { get; set; } = 30;
    public bool OgEnabled { get; set; } = true;
}

public sealed record ContentOgImageData(byte[] Png, string ETag, DateTimeOffset GeneratedAt);
public sealed record ContentShareCounts(string Type, string Slug, Dictionary<string, long> Channels, long Total);
public sealed record ContentTopShared(string Type, string Slug, long Total);

/// <summary>
/// Open Graph images drawn from an item's title and summary and the brand, never fetched from the item's image URL (fetching arbitrary URLs from a server is an SSRF risk).
/// Drawing happens in a background worker, not on request: the public endpoint only ever serves a stored image, so a crawler cannot make the server render.
/// Bengali is shaped with the embedded Noto Sans Bengali through QuestPDF's text engine, the same one the certificates use.
/// </summary>
public sealed class SocialStore(ContentDbContext db, TimeProvider time, IOptions<SocialOptions> options)
{
    public const string TemplateVersion = "v1";
    public static readonly string[] OgTypes = [ContentTypes.News, ContentTypes.Project, ContentTypes.Event, ContentTypes.Press, ContentTypes.SuccessStory, ContentTypes.Page];
    public static readonly string[] Channels = ["facebook", "x", "linkedin", "whatsapp", "copy", "other"];

    private static readonly Lock Gate = new();
    private static bool _ready;

    /// <summary>Draws every missing or out-of-date image for published items, up to `max`. Returns how many were drawn.</summary>
    public async Task<int> GenerateDueAsync(int max)
    {
        var brand = options.Value;
        var items = await db.Items.AsNoTracking().Include(i => i.Translations)
            .Where(i => i.DeletedAt == null && i.Status == StatusRules.Published && OgTypes.Contains(i.Type)).ToListAsync();
        var stamps = (await db.Set<OgImageEntity>().Select(o => new { o.ContentItemId, o.Lang, o.SourceStamp }).ToListAsync()).ToDictionary(o => (o.ContentItemId, o.Lang), o => o.SourceStamp);
        var drawn = 0;
        foreach (var item in items.OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            foreach (var translation in item.Translations.Where(t => ContentLanguages.Contains(t.Lang)).OrderBy(t => t.Lang, StringComparer.Ordinal))
            {
                var stamp = Stamp(translation, brand);
                if (stamps.TryGetValue((item.Id, translation.Lang), out var current) && current == stamp)
                {
                    continue;
                }

                if (drawn >= max)
                {
                    return drawn;
                }

                var png = Render(translation.Title, translation.Summary, translation.Lang, brand);
                var existing = await db.Set<OgImageEntity>().FirstOrDefaultAsync(o => o.ContentItemId == item.Id && o.Lang == translation.Lang);
                if (existing is null)
                {
                    db.Set<OgImageEntity>().Add(new OgImageEntity { Id = $"og_{Guid.NewGuid():N}", ContentItemId = item.Id, Lang = translation.Lang, SourceStamp = stamp, Png = png, GeneratedAt = time.GetUtcNow() });
                }
                else
                {
                    existing.SourceStamp = stamp;
                    existing.Png = png;
                    existing.GeneratedAt = time.GetUtcNow();
                }

                await db.SaveChangesAsync();
                drawn++;
            }
        }

        return drawn;
    }

    /// <summary>The stored image, or null when the item is not published or its image has not been drawn yet (the caller falls back to a default; nothing is drawn here).</summary>
    public async Task<ContentOgImageData?> GetAsync(string type, string slug, string lang)
    {
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug && i.DeletedAt == null && i.Status == StatusRules.Published);
        var image = item is null ? null : await db.Set<OgImageEntity>().AsNoTracking().FirstOrDefaultAsync(o => o.ContentItemId == item.Id && o.Lang == lang);
        return image is null ? null : new ContentOgImageData(image.Png, $"\"{image.SourceStamp[..16]}\"", image.GeneratedAt);
    }

    public static byte[] Render(string title, string summary, string lang, SocialOptions brand)
    {
        EnsureFont();
        var name = lang == "bn" ? brand.BrandNameBn : brand.BrandNameEn;
        var document = Document.Create(container => container.Page(page =>
        {
            page.Size(new PageSize(1200, 630));
            page.Margin(0);
            page.DefaultTextStyle(style => style.FontFamily("Noto Sans Bengali").FontColor(Colors.White));
            page.Background().Background("#14532d");
            page.Content().Padding(72).Column(column =>
            {
                column.Spacing(24);
                column.Item().Text(name).FontSize(34).SemiBold().FontColor("#bbf7d0");
                column.Item().Text(title).FontSize(64).SemiBold().ClampLines(4);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    column.Item().Text(summary).FontSize(30).FontColor("#dcfce7").ClampLines(3);
                }
            });
        }));
        return document.GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = 72 }).First();
    }

    private static string Stamp(ContentTranslationEntity t, SocialOptions brand) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{TemplateVersion}\n{brand.BrandNameEn}\n{brand.BrandNameBn}\n{t.Lang}\n{t.Title}\n{t.Summary}"))).ToLowerInvariant();

    private static readonly string[] ContentLanguages = ["bn", "en"];

    private static void EnsureFont()
    {
        lock (Gate)
        {
            if (_ready)
            {
                return;
            }

            QuestPDF.Settings.License = LicenseType.Community;
            using var font = typeof(SocialStore).Assembly.GetManifestResourceStream("Fonts.NotoSansBengali.ttf")
                ?? throw new InvalidOperationException("The embedded Bengali font is missing.");
            QuestPDF.Drawing.FontManager.RegisterFontFromStream(font);
            _ready = true;
        }
    }

    // ---- share counts

    /// <summary>Counts one share of a published item on a channel. Returns false when the item is not published or the channel is unknown.</summary>
    public async Task<(bool Ok, string? Error)> CountShareAsync(string type, string slug, string? channel)
    {
        var chosen = channel?.Trim().ToLowerInvariant();
        if (chosen is null || !Channels.Contains(chosen))
        {
            return (false, $"channel must be one of: {string.Join(", ", Channels)}.");
        }

        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug && i.DeletedAt == null && i.Status == StatusRules.Published);
        if (item is null)
        {
            return (false, null);
        }

        var updated = await db.Set<ShareCountEntity>().Where(s => s.ContentItemId == item.Id && s.Channel == chosen).ExecuteUpdateAsync(s => s.SetProperty(x => x.Count, x => x.Count + 1));
        if (updated == 0)
        {
            db.Set<ShareCountEntity>().Add(new ShareCountEntity { ContentItemId = item.Id, Channel = chosen, Count = 1 });
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear(); // a concurrent first share created the row: add to it
                await db.Set<ShareCountEntity>().Where(s => s.ContentItemId == item.Id && s.Channel == chosen).ExecuteUpdateAsync(s => s.SetProperty(x => x.Count, x => x.Count + 1));
            }
        }

        return (true, null);
    }

    public async Task<ContentShareCounts?> SharesAsync(string type, string slug)
    {
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug && i.DeletedAt == null && i.Status == StatusRules.Published);
        if (item is null)
        {
            return null;
        }

        var rows = await db.Set<ShareCountEntity>().AsNoTracking().Where(s => s.ContentItemId == item.Id).ToListAsync();
        return new ContentShareCounts(type, slug, rows.OrderBy(r => r.Channel, StringComparer.Ordinal).ToDictionary(r => r.Channel, r => r.Count), rows.Sum(r => r.Count));
    }

    public async Task<List<ContentTopShared>> TopAsync(int limit)
    {
        var totals = (await db.Set<ShareCountEntity>().AsNoTracking().ToListAsync()).GroupBy(s => s.ContentItemId).Select(g => (Id: g.Key, Total: g.Sum(s => s.Count)))
            .OrderByDescending(t => t.Total).Take(limit).ToList();
        var ids = totals.Select(t => t.Id).ToList();
        var items = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        return [.. totals.Where(t => items.ContainsKey(t.Id)).Select(t => new ContentTopShared(items[t.Id].Type, items[t.Id].Slug, t.Total))];
    }
}

/// <summary>Draws due Open Graph images in the background (default every 30 s). Public so tests can drive a pass.</summary>
public sealed partial class OgImageWorker(IServiceScopeFactory scopes, TimeProvider time, IOptions<SocialOptions> options, ILogger<OgImageWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.OgEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)), time);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var drawn = await scope.ServiceProvider.GetRequiredService<SocialStore>().GenerateDueAsync(20);
                if (drawn > 0)
                {
                    Drawn(logger, drawn);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                CycleFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Drew {Count} Open Graph image(s).")]
    private static partial void Drawn(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Open Graph image cycle failed; retrying at the next poll.")]
    private static partial void CycleFailed(ILogger logger, Exception exception);
}
