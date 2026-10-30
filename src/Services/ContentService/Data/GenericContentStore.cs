using System.Text.Json;
using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

/// <summary>
/// Reads and writes every content type in the one generic shape: translations, tags, related items and slug changes.
/// Deleting is soft and a deleted slug stays reserved, as for projects and news.
/// </summary>
public sealed class GenericContentStore(ContentDbContext db, TimeProvider time, TaxonomyStore taxonomy, RedirectStore redirects, RevisionStore revisions, TranslationPolicy policy)
{
    private static readonly string[] Languages = ["en", "bn"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IQueryable<ContentItemEntity> Full() => db.Items.AsSplitQuery()
        .Include(i => i.Translations)
        .Include(i => i.TagLinks).ThenInclude(l => l.Tag).ThenInclude(t => t!.Names)
        .Include(i => i.RelatedLinks.OrderBy(r => r.SortOrder)).ThenInclude(r => r.To).ThenInclude(t => t!.Translations);

    // ---- public

    public async Task<List<ContentItemView>> PublicListAsync(string type, string lang, string? tagReference)
    {
        var query = Full().AsNoTracking().Where(i => i.Type == type && i.DeletedAt == null && (i.Status == "published" || i.Status == "active"));
        if (tagReference is not null)
        {
            var (tags, error) = await taxonomy.ResolveAsync([tagReference]);
            if (error is not null)
            {
                return [];
            }

            var tagId = tags[0].Id;
            query = query.Where(i => i.TagLinks.Any(l => l.TagId == tagId));
        }

        var items = await query.ToListAsync();
        // Sorted in memory: SQLite (the test provider) cannot order by DateTimeOffset.
        return [.. items.OrderBy(i => i.SortOrder).ThenByDescending(i => i.PublishedAt).ThenBy(i => i.Slug, StringComparer.Ordinal)
            .Select(i => ToView(i, lang)).OfType<ContentItemView>()];
    }

    public async Task<ContentItemView?> PublicItemAsync(string type, string slug, string lang)
    {
        var item = await Full().AsNoTracking().FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug && i.DeletedAt == null && (i.Status == "published" || i.Status == "active"));
        return item is null ? null : ToView(item, lang);
    }

    // ---- admin

    public async Task<List<ContentItemAdminView>> AdminListAsync(string type) =>
        [.. (await Full().AsNoTracking().Where(i => i.Type == type && i.DeletedAt == null).ToListAsync())
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Slug, StringComparer.Ordinal).Select(ToAdminView)];

    public async Task<ContentItemAdminView?> AdminItemAsync(string type, string slug)
    {
        var item = await Full().AsNoTracking().FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug && i.DeletedAt == null);
        return item is null ? null : ToAdminView(item);
    }

    public async Task<string?> ETagAsync(string type, string slug)
    {
        var updatedAt = await db.Items.AsNoTracking().Where(i => i.Type == type && i.Slug == slug && i.DeletedAt == null)
            .Select(i => (DateTimeOffset?)i.UpdatedAt).FirstOrDefaultAsync();
        return updatedAt is null ? null : ContentETag.For(updatedAt.Value);
    }

    public async Task<UpsertResult<ContentItemAdminView>> UpsertAsync(string type, string slug, ContentUpsertRequest request, bool mustExist, IReadOnlyCollection<string>? ifMatch, bool canPublish = true)
    {
        if (Validate(slug, request) is { } error)
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Invalid, Error: error);
        }

        var (tags, tagError) = await taxonomy.ResolveAsync(request.Tags ?? []);
        if (tagError is not null)
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Invalid, Error: tagError);
        }

        var item = await Full().FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug);
        var created = false;
        if (item is null || (item.DeletedAt is not null && mustExist))
        {
            if (mustExist)
            {
                return new UpsertResult<ContentItemAdminView>(UpsertOutcome.NotFound);
            }

            var maxOrder = (await db.Items.Where(i => i.Type == type).Select(i => (int?)i.SortOrder).MaxAsync()) ?? -1;
            item = new ContentItemEntity { Id = $"cnt_{Guid.NewGuid():N}", Type = type, Slug = slug, SortOrder = maxOrder + 1 };
            db.Items.Add(item);
            created = true;
        }
        else if (!created && ifMatch is not null && !ContentETag.Satisfies(ifMatch, item.UpdatedAt))
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Conflict);
        }

        if (StatusRules.GuardUpsert(created ? null : item.Status, request.Status, canPublish) is { } guard)
        {
            return new UpsertResult<ContentItemAdminView>(guard.Outcome, Error: guard.Error);
        }

        var related = await ResolveRelatedAsync(item, request.Related ?? []);
        if (related.Error is not null)
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Invalid, Error: related.Error);
        }

        item.DeletedAt = null;
        item.Status = request.Status;
        item.ScheduledPublishAt = request.Status == StatusRules.Scheduled ? item.ScheduledPublishAt : null;
        item.ImageUrl = request.ImageUrl ?? string.Empty;
        item.SortOrder = request.SortOrder ?? item.SortOrder;
        item.PublishedAt = request.Status == "published" ? request.PublishedAt ?? item.PublishedAt ?? time.GetUtcNow() : request.PublishedAt ?? item.PublishedAt;
        ReplaceTranslations(item, request.Translations);
        ReplaceTags(item, tags);
        ReplaceRelated(item, related.Items);
        if (!await SaveAsync(item, created))
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Conflict);
        }

        await revisions.RecordAsync(item.Id, created ? "create" : "update");
        return new UpsertResult<ContentItemAdminView>(created ? UpsertOutcome.Created : UpsertOutcome.Updated, ToAdminView(item), ETag: ContentETag.For(item.UpdatedAt));
    }

    public async Task<UpsertResult<ContentItemAdminView>> SetTagsAsync(string type, string slug, List<string> references)
    {
        var item = await Full().FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug && i.DeletedAt == null);
        if (item is null)
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.NotFound);
        }

        var (tags, error) = await taxonomy.ResolveAsync(references);
        if (error is not null)
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Invalid, Error: error);
        }

        ReplaceTags(item, tags);
        return await SaveTaxonomyAsync(item, "tags");
    }

    public async Task<UpsertResult<ContentItemAdminView>> SetRelatedAsync(string type, string slug, List<RelatedRef> references)
    {
        var item = await Full().FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug && i.DeletedAt == null);
        if (item is null)
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.NotFound);
        }

        var related = await ResolveRelatedAsync(item, references);
        if (related.Error is not null)
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Invalid, Error: related.Error);
        }

        ReplaceRelated(item, related.Items);
        return await SaveTaxonomyAsync(item, "related");
    }

    /// <summary>Changes the public slug and, in the same transaction, leaves a redirect from the old path so inbound links keep working.</summary>
    public async Task<UpsertResult<ContentItemAdminView>> RenameAsync(string type, string slug, string newSlug)
    {
        if (!SlugRules.IsValid(newSlug))
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Invalid, Error: "newSlug must be lowercase letters, digits and hyphens (at most 120 characters).");
        }

        var item = await Full().FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug && i.DeletedAt == null);
        if (item is null)
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.NotFound);
        }

        if (newSlug == slug)
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Updated, ToAdminView(item), ETag: ContentETag.For(item.UpdatedAt));
        }

        if (await db.Items.AnyAsync(i => i.Type == type && i.Slug == newSlug))
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Conflict, Error: $"The slug '{newSlug}' is already used (a deleted item keeps its slug reserved).");
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        var oldPath = ContentKinds.PublicPath(type, slug);
        var newPath = ContentKinds.PublicPath(type, newSlug);
        await redirects.RemoveFromAsync(newPath);
        if (await redirects.StageAsync(oldPath, newPath, 301, "slug-change") is { } loop)
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Invalid, Error: loop);
        }

        item.Slug = newSlug;
        if (!await SaveAsync(item, created: false))
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Conflict);
        }

        await transaction.CommitAsync();
        await revisions.RecordAsync(item.Id, "rename");
        return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Updated, ToAdminView(item), ETag: ContentETag.For(item.UpdatedAt));
    }

    public async Task<bool> DeleteAsync(string type, string slug)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug && i.DeletedAt == null);
        if (item is null)
        {
            return false;
        }

        item.DeletedAt = Now();
        item.UpdatedAt = item.DeletedAt.Value;
        await db.SaveChangesAsync();
        return true;
    }

    // ---- translation completeness

    public async Task<ItemCompleteness?> CompletenessAsync(string type, string slug)
    {
        var item = await db.Items.AsNoTracking().Include(i => i.Translations).FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug && i.DeletedAt == null);
        return item is null ? null : TranslationCompleteness.Evaluate(type, item.Translations);
    }

    /// <summary>
    /// The gaps across every content type: per item and language, whether the translation is `missing` (no row) or `incomplete`
    /// (a required field is blank). The summary counts cover the whole filtered set, not just the requested page.
    /// </summary>
    public async Task<TranslationReport> ReportAsync(string? type, string? lang, string? status, bool includeComplete, int limit, int offset)
    {
        var query = db.Items.AsNoTracking().Include(i => i.Translations).Where(i => i.DeletedAt == null);
        if (type is not null)
        {
            query = query.Where(i => i.Type == type);
        }

        var items = (await query.ToListAsync())
            .Where(i => status is null || StatusRules.Canonical(i.Status) == StatusRules.Canonical(status))
            .OrderBy(i => i.Type, StringComparer.Ordinal).ThenBy(i => i.Slug, StringComparer.Ordinal).ToList();

        var rows = new List<TranslationGapView>();
        var byType = new Dictionary<string, TypeGapCount>();
        var byLang = TranslationCompleteness.Languages.ToDictionary(l => l, _ => new GapCount(0, 0));
        foreach (var item in items)
        {
            var gaps = TranslationCompleteness.Evaluate(item.Type, item.Translations).Languages
                .Where(l => !l.Complete && (lang is null || l.Lang == lang))
                .Select(l => new LanguageGap(l.Lang, l.Present ? "incomplete" : "missing", l.Missing)).ToList();
            foreach (var gap in gaps)
            {
                byLang[gap.Lang] = gap.State == "missing" ? byLang[gap.Lang] with { Missing = byLang[gap.Lang].Missing + 1 } : byLang[gap.Lang] with { Incomplete = byLang[gap.Lang].Incomplete + 1 };
            }

            var counts = byType.GetValueOrDefault(item.Type, new TypeGapCount(0, 0));
            byType[item.Type] = new TypeGapCount(counts.Items + 1, counts.WithGaps + (gaps.Count > 0 ? 1 : 0));
            if (gaps.Count > 0 || includeComplete)
            {
                var title = item.Translations.OrderBy(t => t.Lang == "bn" ? 0 : 1).Select(t => t.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? item.Slug;
                rows.Add(new TranslationGapView(item.Type, item.Slug, item.Status, title, gaps));
            }
        }

        return new TranslationReport([.. rows.Skip(offset).Take(limit)], rows.Count, limit, offset, byType, byLang);
    }

    // ---- history, trash and preview

    /// <summary>Applies a stored revision to the item as a new revision. The slug, status and publication date are not rolled back.</summary>
    public async Task<UpsertResult<ContentItemAdminView>> RestoreRevisionAsync(string type, string slug, int number, IReadOnlyCollection<string>? ifMatch)
    {
        var detail = await revisions.GetAsync(type, slug, number);
        var item = await Full().Include(i => i.Project).Include(i => i.News).FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug && i.DeletedAt == null);
        if (detail is null || item is null)
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.NotFound);
        }

        if (ifMatch is not null && !ContentETag.Satisfies(ifMatch, item.UpdatedAt))
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Conflict);
        }

        var snapshot = detail.Snapshot;
        item.ImageUrl = snapshot.ImageUrl;
        item.SortOrder = snapshot.SortOrder;
        if (type == ContentTypes.Project)
        {
            item.Project ??= new ProjectEntity();
            item.Project.ImageUrl = snapshot.ProjectImageUrl ?? string.Empty;
            item.Project.Icon = snapshot.ProjectIcon ?? string.Empty;
        }
        else if (type == ContentTypes.News)
        {
            item.News ??= new NewsArticleEntity();
            item.News.ImageUrl = snapshot.NewsImageUrl ?? string.Empty;
        }

        foreach (var gone in item.Translations.Where(t => snapshot.Translations.All(n => n.Lang != t.Lang)).ToList())
        {
            item.Translations.Remove(gone);
            db.Translations.Remove(gone);
        }

        foreach (var old in snapshot.Translations)
        {
            var current = item.Translations.FirstOrDefault(t => t.Lang == old.Lang);
            if (current is null)
            {
                item.Translations.Add(new ContentTranslationEntity { ContentItemId = item.Id, Lang = old.Lang, Title = old.Title, Summary = old.Summary, Body = old.Body, AttributesJson = old.AttributesJson });
            }
            else
            {
                current.Title = old.Title;
                current.Summary = old.Summary;
                current.Body = old.Body;
                current.AttributesJson = old.AttributesJson;
            }
        }

        // Tags and related items that have since been deleted are skipped rather than failing the restore.
        var tags = new List<TagEntity>();
        foreach (var tag in snapshot.Tags)
        {
            var (found, error) = await taxonomy.ResolveAsync([tag]);
            if (error is null)
            {
                tags.AddRange(found);
            }
        }

        var related = new List<ContentItemEntity>();
        foreach (var reference in snapshot.Related)
        {
            var resolved = await ResolveRelatedAsync(item, [reference]);
            if (resolved.Error is null)
            {
                related.AddRange(resolved.Items);
            }
        }

        ReplaceTags(item, tags);
        ReplaceRelated(item, related);
        if (!await SaveAsync(item, created: false))
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Conflict);
        }

        await revisions.RecordAsync(item.Id, $"restore:{number}");
        return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Updated, ToAdminView(item), ETag: ContentETag.For(item.UpdatedAt));
    }

    public async Task<List<TrashItemView>> TrashAsync(string? type)
    {
        var query = db.Items.AsNoTracking().Include(i => i.Translations).Where(i => i.DeletedAt != null);
        if (type is not null)
        {
            query = query.Where(i => i.Type == type);
        }

        return [.. (await query.ToListAsync()).OrderByDescending(i => i.DeletedAt)
            .Select(i => new TrashItemView(i.Type, i.Slug, i.DeletedAt!.Value, i.Translations.OrderBy(t => t.Lang, StringComparer.Ordinal).Select(t => t.Title).FirstOrDefault() ?? i.Slug))];
    }

    /// <summary>Brings a soft-deleted item back as a draft: restoring must never publish anything by itself.</summary>
    public async Task<UpsertResult<ContentItemAdminView>> UndeleteAsync(string type, string slug)
    {
        var item = await Full().FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug && i.DeletedAt != null);
        if (item is null)
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.NotFound);
        }

        item.DeletedAt = null;
        item.Status = StatusRules.Draft;
        item.ScheduledPublishAt = null;
        if (!await SaveAsync(item, created: false))
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Conflict);
        }

        await revisions.RecordAsync(item.Id, "undelete");
        return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Updated, ToAdminView(item), ETag: ContentETag.For(item.UpdatedAt));
    }

    public async Task<string?> ItemIdAsync(string type, string slug) =>
        await db.Items.AsNoTracking().Where(i => i.Type == type && i.Slug == slug && i.DeletedAt == null).Select(i => i.Id).FirstOrDefaultAsync();

    /// <summary>The item in any status (for a preview link), in the requested language or else the first one it has.</summary>
    public async Task<ContentItemView?> PreviewAsync(string itemId, string lang)
    {
        var item = await Full().AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.DeletedAt == null);
        return item is null ? null : ToView(item, lang) ?? ToView(item, item.Translations.OrderBy(t => t.Lang, StringComparer.Ordinal).First().Lang);
    }

    // ---- internals

    private async Task<UpsertResult<ContentItemAdminView>> SaveTaxonomyAsync(ContentItemEntity item, string reason)
    {
        if (!await SaveAsync(item, created: false))
        {
            return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Conflict);
        }

        await revisions.RecordAsync(item.Id, reason);
        return new UpsertResult<ContentItemAdminView>(UpsertOutcome.Updated, ToAdminView(item), ETag: ContentETag.For(item.UpdatedAt));
    }

    private async Task<(List<ContentItemEntity> Items, string? Error)> ResolveRelatedAsync(ContentItemEntity item, List<RelatedRef> references)
    {
        var found = new List<ContentItemEntity>();
        foreach (var reference in references)
        {
            if (ContentKinds.ByType(reference.Type) is null)
            {
                return ([], $"Unknown related type '{reference.Type}'.");
            }

            var target = await db.Items.FirstOrDefaultAsync(i => i.Type == reference.Type && i.Slug == reference.Slug && i.DeletedAt == null);
            if (target is null)
            {
                return ([], $"Related item {reference.Type}/{reference.Slug} does not exist.");
            }

            if (target.Id == item.Id)
            {
                return ([], "An item cannot be related to itself.");
            }

            if (found.All(f => f.Id != target.Id))
            {
                found.Add(target);
            }
        }

        return (found, null);
    }

    private void ReplaceTags(ContentItemEntity item, List<TagEntity> tags)
    {
        foreach (var gone in item.TagLinks.Where(l => tags.All(t => t.Id != l.TagId)).ToList())
        {
            item.TagLinks.Remove(gone);
            db.ItemTags.Remove(gone);
        }

        foreach (var tag in tags.Where(t => item.TagLinks.All(l => l.TagId != t.Id)))
        {
            item.TagLinks.Add(new ContentItemTagEntity { ContentItemId = item.Id, TagId = tag.Id, Tag = tag });
        }
    }

    private void ReplaceRelated(ContentItemEntity item, List<ContentItemEntity> targets)
    {
        foreach (var gone in item.RelatedLinks.Where(l => targets.All(t => t.Id != l.ToItemId)).ToList())
        {
            item.RelatedLinks.Remove(gone);
            db.Relations.Remove(gone);
        }

        for (var order = 0; order < targets.Count; order++)
        {
            var link = item.RelatedLinks.FirstOrDefault(l => l.ToItemId == targets[order].Id);
            if (link is null)
            {
                item.RelatedLinks.Add(new ContentRelationEntity { FromItemId = item.Id, ToItemId = targets[order].Id, SortOrder = order, To = targets[order] });
            }
            else
            {
                link.SortOrder = order;
            }
        }
    }

    private void ReplaceTranslations(ContentItemEntity item, List<ContentTranslationInput> incoming)
    {
        foreach (var gone in item.Translations.Where(t => incoming.All(n => n.Lang != t.Lang)).ToList())
        {
            item.Translations.Remove(gone);
            db.Translations.Remove(gone);
        }

        foreach (var input in incoming)
        {
            var attributes = JsonSerializer.Serialize(input.Attributes ?? [], Json);
            var current = item.Translations.FirstOrDefault(t => t.Lang == input.Lang);
            if (current is null)
            {
                item.Translations.Add(new ContentTranslationEntity { ContentItemId = item.Id, Lang = input.Lang, Title = input.Title, Summary = input.Summary ?? string.Empty, Body = input.Body ?? string.Empty, AttributesJson = attributes });
            }
            else
            {
                current.Title = input.Title;
                current.Summary = input.Summary ?? string.Empty;
                current.Body = input.Body ?? string.Empty;
                current.AttributesJson = attributes;
            }
        }
    }

    private async Task<bool> SaveAsync(ContentItemEntity item, bool created)
    {
        item.UpdatedAt = Now();
        if (created)
        {
            item.CreatedAt = item.UpdatedAt;
        }

        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private DateTimeOffset Now() => new(time.GetUtcNow().UtcTicks / 10 * 10, TimeSpan.Zero);

    private static string? Validate(string slug, ContentUpsertRequest request)
    {
        if (!SlugRules.IsValid(slug))
        {
            return "slug must be lowercase letters, digits and hyphens (at most 120 characters).";
        }

        if (!ContentKinds.Statuses.Contains(request.Status))
        {
            return $"status must be one of: {string.Join(", ", ContentKinds.Statuses)}.";
        }

        var list = request.Translations ?? [];
        if (list.Count == 0)
        {
            return "At least one translation is required.";
        }

        if (list.Any(t => !Languages.Contains(t.Lang)) || list.Select(t => t.Lang).Distinct().Count() != list.Count)
        {
            return "Each translation needs a distinct lang of en or bn.";
        }

        return list.Any(t => string.IsNullOrWhiteSpace(t.Title)) ? "Every translation needs a title." : null;
    }

    /// <summary>The item as a reader of <paramref name="lang"/> sees it under the fallback rules, or null when it is hidden for them.</summary>
    private ContentItemView? ToView(ContentItemEntity item, string lang)
    {
        var choice = policy.Choose([.. item.Translations.Select(t => t.Lang)], lang);
        if (choice is null)
        {
            return null;
        }

        var translation = item.Translations.First(t => t.Lang == choice.ContentLang);
        var tags = item.TagLinks.Select(l => l.Tag).OfType<TagEntity>().OrderBy(t => t.Slug, StringComparer.Ordinal)
            .Select(t => new TagRef(t.Slug, (t.Names.FirstOrDefault(n => n.Lang == lang) ?? t.Names.FirstOrDefault())?.Name ?? t.Slug)).ToList();
        // Related items that are not live and published, or hidden for this reader by the fallback rules, are left out rather than shown half-empty.
        var related = item.RelatedLinks.OrderBy(r => r.SortOrder).Select(r => r.To)
            .Where(t => t is { DeletedAt: null, Status: "published" or "active" })
            .Select(t => (Target: t!, Choice: policy.Choose([.. t!.Translations.Select(x => x.Lang)], lang)))
            .Where(x => x.Choice is not null)
            .Select(x => new RelatedItemView(x.Target.Type, x.Target.Slug, x.Target.Translations.First(t => t.Lang == x.Choice!.ContentLang).Title)).ToList();
        var placeholder = choice is { Pending: true, Placeholder: true };
        return new ContentItemView(item.Id, item.Type, item.Slug, choice.ContentLang, translation.Title, placeholder ? string.Empty : translation.Summary, placeholder ? string.Empty : translation.Body,
            placeholder ? [] : ReadAttributes(translation), item.ImageUrl, item.PublishedAt, tags, related, item.UpdatedAt, choice.Pending, choice.Pending ? lang : null);
    }

    private static ContentItemAdminView ToAdminView(ContentItemEntity item) => new(
        item.Type, item.Slug, item.Status, item.ImageUrl, item.SortOrder, item.PublishedAt, item.ScheduledPublishAt,
        [.. item.Translations.OrderBy(t => t.Lang, StringComparer.Ordinal).Select(t => new ContentTranslationView(t.Lang, t.Title, t.Summary, t.Body, ReadAttributes(t)))],
        [.. item.TagLinks.Select(l => l.Tag?.Slug).OfType<string>().OrderBy(s => s, StringComparer.Ordinal)],
        [.. item.RelatedLinks.OrderBy(r => r.SortOrder).Where(r => r.To is not null).Select(r => new RelatedRef(r.To!.Type, r.To.Slug))],
        TranslationCompleteness.Evaluate(item.Type, item.Translations));

    private static Dictionary<string, JsonElement> ReadAttributes(ContentTranslationEntity translation)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(translation.AttributesJson, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
