using ContentService.Data.Entities;
using KJWebsite.BuildingBlocks.Auditing;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

public sealed record QueueItem(string Type, string Slug, string Title, DateTimeOffset UpdatedAt, DateTimeOffset? ScheduledPublishAt);

public sealed record ActivityItem(long Id, DateTimeOffset At, string ActorId, string Action, string Entity, string EntityId, string? Operation);

public sealed record DashboardSummary(
    DateTimeOffset GeneratedAt,
    Dictionary<string, Dictionary<string, int>> StatusByType,
    List<QueueItem> AwaitingReview,
    List<QueueItem> UpcomingScheduled,
    TranslationReport TranslationGaps,
    int TrashCount,
    List<ActivityItem>? RecentActivity);

public sealed record SearchHit(string Kind, string Id, string Title, string? Subtitle, string Path);

public sealed record SearchResults(string Q, List<string> Searched, List<SearchHit> Hits);

/// <summary>Read models for the back-office landing page and the global search.</summary>
public sealed class BackOfficeStore(ContentDbContext db, GenericContentStore content)
{
    private const int QueueSize = 20;

    public async Task<DashboardSummary> DashboardAsync(bool includeActivity, DateTimeOffset now)
    {
        var counts = await db.Items.AsNoTracking().Where(i => i.DeletedAt == null).GroupBy(i => new { i.Type, i.Status }).Select(g => new { g.Key.Type, g.Key.Status, Count = g.Count() }).ToListAsync();
        var byType = ContentKinds.All.ToDictionary(k => k.Type, k => counts.Where(c => c.Type == k.Type).ToDictionary(c => c.Status, c => c.Count));

        async Task<List<QueueItem>> Queue(string status) =>
            [.. (await db.Items.AsNoTracking().Include(i => i.Translations).Where(i => i.DeletedAt == null && i.Status == status).ToListAsync())
                .OrderBy(i => status == StatusRules.Scheduled ? i.ScheduledPublishAt : i.UpdatedAt).Take(QueueSize)
                .Select(i => new QueueItem(i.Type, i.Slug, Title(i), i.UpdatedAt, i.ScheduledPublishAt))];

        var review = await Queue(StatusRules.InReview);
        var scheduled = await Queue(StatusRules.Scheduled);
        var gaps = await content.ReportAsync(null, null, null, false, 1, 0);
        var trash = await db.Items.CountAsync(i => i.DeletedAt != null);
        List<ActivityItem>? activity = null;
        if (includeActivity)
        {
            activity = [.. (await db.Set<AuditEvent>().AsNoTracking().OrderByDescending(e => e.Id).Take(10).ToListAsync())
                .Select(e => new ActivityItem(e.Id, new DateTimeOffset(DateTime.SpecifyKind(e.At, DateTimeKind.Utc)), e.ActorId, e.Action, e.Entity, e.EntityId, e.Operation))];
        }

        return new DashboardSummary(now, byType, review, scheduled, gaps with { Items = [] }, trash, activity);
    }

    /// <summary>Case-insensitive substring search. Each kind is searched only when the caller may read it; the rest are left out, not reported empty.</summary>
    public async Task<SearchResults> SearchAsync(string q, bool canReadContent, bool canReadSettings, int limit)
    {
        var term = q.Trim().ToLowerInvariant();
        var searched = new List<string>();
        var hits = new List<(int Rank, SearchHit Hit)>();
        int Rank(string text) => string.Equals(text, term, StringComparison.OrdinalIgnoreCase) ? 0 : text.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 1 : 2;

        // The queries below use ToLower().Contains on purpose: EF translates it to portable SQL (LIKE/lower) on both PostgreSQL and SQLite,
        // which the StringComparison overloads the analyzers suggest cannot be translated.
#pragma warning disable CA1862, CA1304, CA1311
        if (canReadContent)
        {
            searched.AddRange(["item", "tag", "redirect"]);
            var items = await db.Items.AsNoTracking().Include(i => i.Translations).Where(i => i.DeletedAt == null &&
                (i.Slug.ToLower().Contains(term) || i.Translations.Any(t => t.Title.ToLower().Contains(term) || t.Summary.ToLower().Contains(term)))).Take(200).ToListAsync();
            hits.AddRange(items.Select(i => (Math.Min(Rank(i.Slug), Rank(Title(i))), new SearchHit("item", $"{i.Type}/{i.Slug}", Title(i), $"{i.Type} · {i.Status}", $"/api/v1/admin/content/items/{i.Type}/{i.Slug}"))));

            var tags = await db.Tags.AsNoTracking().Include(t => t.Names).Where(t => t.Slug.Contains(term) || t.Names.Any(n => n.NormalizedName.Contains(term))).Take(50).ToListAsync();
            hits.AddRange(tags.Select(t => (Rank(t.Slug), new SearchHit("tag", t.Slug, t.Names.OrderBy(n => n.Lang, StringComparer.Ordinal).Select(n => n.Name).FirstOrDefault() ?? t.Slug, "tag", "/api/v1/admin/content/tags"))));

            var redirects = await db.Redirects.AsNoTracking().Where(r => r.FromPath.Contains(term) || r.ToPath.Contains(term)).Take(50).ToListAsync();
            hits.AddRange(redirects.Select(r => (Rank(r.FromPath), new SearchHit("redirect", r.Id, r.FromPath, $"→ {r.ToPath}", "/api/v1/admin/content/redirects"))));
        }

        if (canReadSettings)
        {
            searched.Add("setting");
            var settings = await db.Settings.AsNoTracking().Where(s => s.Key.Contains(term)).Take(50).ToListAsync();
            hits.AddRange(settings.Select(s => (Rank(s.Key), new SearchHit("setting", s.Key, s.Key, s.IsSecret ? "secret setting" : "setting", $"/api/v1/admin/content/settings/{s.Key}"))));
        }

#pragma warning restore CA1862, CA1304, CA1311
        return new SearchResults(q.Trim(), searched, [.. hits.OrderBy(h => h.Rank).ThenBy(h => h.Hit.Kind, StringComparer.Ordinal).ThenBy(h => h.Hit.Title, StringComparer.Ordinal).Take(limit).Select(h => h.Hit)]);
    }

    private static string Title(ContentItemEntity item) =>
        item.Translations.OrderBy(t => t.Lang == "bn" ? 0 : 1).Select(t => t.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? item.Slug;
}
