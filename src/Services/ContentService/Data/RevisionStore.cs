using System.Security.Claims;
using System.Text.Json;
using ContentService.Data.Entities;
using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

public sealed record SnapshotTranslation(string Lang, string Title, string Summary, string Body, string AttributesJson);

/// <summary>Everything editable about an item at one moment, for every content type.</summary>
public sealed record RevisionSnapshot(
    string Type,
    string Slug,
    string Status,
    string ImageUrl,
    int SortOrder,
    DateTimeOffset? PublishedAt,
    string? ProjectImageUrl,
    string? ProjectIcon,
    string? NewsImageUrl,
    List<SnapshotTranslation> Translations,
    List<string> Tags,
    List<RelatedRef> Related);

public sealed record RevisionSummary(int Number, DateTimeOffset CreatedAt, string ActorId, string Reason);

public sealed record RevisionDetail(int Number, DateTimeOffset CreatedAt, string ActorId, string Reason, RevisionSnapshot Snapshot);

public sealed record RevisionChange(string Path, string? Before, string? After);

public sealed record RevisionDiff(int From, int To, List<RevisionChange> Changes);

/// <summary>
/// Revision history. A snapshot is taken after each content change; an identical snapshot is not stored twice. Diffs are field-level
/// (per language for translations) so a bilingual item shows exactly which language changed.
/// </summary>
public sealed class RevisionStore(ContentDbContext db, TimeProvider time, IHttpContextAccessor accessor, SearchIndex search)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Records the item's current state. Call after the change is saved. Returns the revision number, or null when nothing changed.</summary>
    public async Task<int?> RecordAsync(string itemId, string reason)
    {
        var snapshot = await SnapshotAsync(itemId);
        if (snapshot is null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(snapshot, Json);
        var latest = await db.Revisions.AsNoTracking().Where(r => r.ContentItemId == itemId).OrderByDescending(r => r.Number).FirstOrDefaultAsync();
        if (latest?.SnapshotJson == json)
        {
            return latest.Number;
        }

        await search.ReindexAsync(itemId);

        var number = (latest?.Number ?? 0) + 1;
        db.Revisions.Add(new ContentRevisionEntity
        {
            Id = $"rev_{Guid.NewGuid():N}",
            ContentItemId = itemId,
            Number = number,
            CreatedAt = time.GetUtcNow(),
            ActorId = accessor.HttpContext?.User.FindFirstValue(KjClaims.Subject) ?? AuditActor.Current?.Id ?? "system",
            Reason = reason,
            SnapshotJson = json
        });
        await db.SaveChangesAsync();
        return number;
    }

    public async Task<List<RevisionSummary>?> ListAsync(string type, string slug)
    {
        var id = await ItemIdAsync(type, slug);
        if (id is null)
        {
            return null;
        }

        var rows = await db.Revisions.AsNoTracking().Where(r => r.ContentItemId == id).OrderByDescending(r => r.Number).ToListAsync();
        return [.. rows.Select(r => new RevisionSummary(r.Number, r.CreatedAt, r.ActorId, r.Reason))];
    }

    public async Task<RevisionDetail?> GetAsync(string type, string slug, int number)
    {
        var id = await ItemIdAsync(type, slug);
        var row = id is null ? null : await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.ContentItemId == id && r.Number == number);
        return row is null ? null : new RevisionDetail(row.Number, row.CreatedAt, row.ActorId, row.Reason, Parse(row));
    }

    public async Task<RevisionDiff?> DiffAsync(string type, string slug, int? from, int? to)
    {
        var id = await ItemIdAsync(type, slug);
        if (id is null)
        {
            return null;
        }

        var latest = await db.Revisions.Where(r => r.ContentItemId == id).MaxAsync(r => (int?)r.Number);
        if (latest is null)
        {
            return null;
        }

        var target = to ?? latest.Value;
        var source = from ?? target - 1;
        var after = await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.ContentItemId == id && r.Number == target);
        var before = source < 1 ? null : await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.ContentItemId == id && r.Number == source);
        if (after is null || (source >= 1 && before is null))
        {
            return null;
        }

        return new RevisionDiff(source, target, Compare(before is null ? null : Parse(before), Parse(after)));
    }

    public async Task<RevisionSnapshot?> SnapshotAsync(string itemId)
    {
        var item = await db.Items.AsNoTracking().AsSplitQuery()
            .Include(i => i.Translations).Include(i => i.Project).Include(i => i.News)
            .Include(i => i.TagLinks).ThenInclude(l => l.Tag)
            .Include(i => i.RelatedLinks).ThenInclude(r => r.To)
            .FirstOrDefaultAsync(i => i.Id == itemId);
        return item is null ? null : new RevisionSnapshot(
            item.Type, item.Slug, item.Status, item.ImageUrl, item.SortOrder, item.PublishedAt,
            item.Project?.ImageUrl, item.Project?.Icon, item.News?.ImageUrl,
            [.. item.Translations.OrderBy(t => t.Lang, StringComparer.Ordinal).Select(t => new SnapshotTranslation(t.Lang, t.Title, t.Summary, t.Body, t.AttributesJson))],
            [.. item.TagLinks.Select(l => l.Tag?.Slug).OfType<string>().OrderBy(s => s, StringComparer.Ordinal)],
            [.. item.RelatedLinks.OrderBy(r => r.SortOrder).Where(r => r.To is not null).Select(r => new RelatedRef(r.To!.Type, r.To.Slug))]);
    }

    private Task<string?> ItemIdAsync(string type, string slug) =>
        db.Items.AsNoTracking().Where(i => i.Type == type && i.Slug == slug && i.DeletedAt == null).Select(i => i.Id).FirstOrDefaultAsync();

    private static RevisionSnapshot Parse(ContentRevisionEntity row) => JsonSerializer.Deserialize<RevisionSnapshot>(row.SnapshotJson, Json)!;

    private static List<RevisionChange> Compare(RevisionSnapshot? before, RevisionSnapshot after)
    {
        var changes = new List<RevisionChange>();
        void Field(string path, string? was, string? now)
        {
            if (!string.Equals(was, now, StringComparison.Ordinal))
            {
                changes.Add(new RevisionChange(path, was, now));
            }
        }

        Field("slug", before?.Slug, after.Slug);
        Field("status", before?.Status, after.Status);
        Field("imageUrl", before?.ImageUrl, after.ImageUrl);
        Field("sortOrder", before?.SortOrder.ToString(System.Globalization.CultureInfo.InvariantCulture), after.SortOrder.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Field("publishedAt", before?.PublishedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture), after.PublishedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        Field("project.imageUrl", before?.ProjectImageUrl, after.ProjectImageUrl);
        Field("project.icon", before?.ProjectIcon, after.ProjectIcon);
        Field("news.imageUrl", before?.NewsImageUrl, after.NewsImageUrl);

        foreach (var lang in (before?.Translations.Select(t => t.Lang) ?? []).Union(after.Translations.Select(t => t.Lang)).Order(StringComparer.Ordinal))
        {
            var was = before?.Translations.FirstOrDefault(t => t.Lang == lang);
            var now = after.Translations.FirstOrDefault(t => t.Lang == lang);
            if (was is null || now is null)
            {
                changes.Add(new RevisionChange($"translations.{lang}", was is null ? null : "present", now is null ? null : "present"));
                continue;
            }

            Field($"translations.{lang}.title", was.Title, now.Title);
            Field($"translations.{lang}.summary", was.Summary, now.Summary);
            Field($"translations.{lang}.body", was.Body, now.Body);
            Field($"translations.{lang}.attributes", was.AttributesJson, now.AttributesJson);
        }

        Field("tags", before is null ? null : string.Join(",", before.Tags), string.Join(",", after.Tags));
        Field("related", before is null ? null : string.Join(",", before.Related.Select(r => $"{r.Type}/{r.Slug}")), string.Join(",", after.Related.Select(r => $"{r.Type}/{r.Slug}")));
        return changes;
    }
}
