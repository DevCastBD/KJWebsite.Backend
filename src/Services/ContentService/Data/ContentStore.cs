using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

public enum UpsertOutcome
{
    Created,
    Updated,
    NotFound,
    Invalid
}

public sealed record UpsertResult<T>(UpsertOutcome Outcome, T? Value = default, string? Error = null);

/// <summary>Reads and writes content, and maps it to the public and admin shapes. Deleting is soft: the row stays and its slug stays reserved.</summary>
public sealed partial class ContentStore(ContentDbContext db, TimeProvider time)
{
    private static readonly string[] ProjectStatuses = ["active", "draft", "archived"];
    private static readonly string[] NewsStatuses = ["published", "draft", "archived"];
    private static readonly string[] Languages = ["en", "bn"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    // ---- projects

    public async Task<List<ProjectItem>> ProjectsAsync(string lang, string status)
    {
        var items = await LiveAsync(ContentTypes.Project);
        return [.. items.Where(i => status == "all" || i.Status == "active").SelectMany(i => ToProjectItems(i).Where(p => p.Lang == lang))];
    }

    public async Task<ProjectItem?> ProjectAsync(string slug, string lang) =>
        (await LiveAsync(ContentTypes.Project, slug)).SelectMany(i => ToProjectItems(i)).FirstOrDefault(p => p.Lang == lang);

    public async Task<List<ProjectAdminView>> AdminProjectsAsync() => [.. (await LiveAsync(ContentTypes.Project)).Select(ToProjectAdminView)];

    public async Task<ProjectAdminView?> AdminProjectAsync(string slug) =>
        (await LiveAsync(ContentTypes.Project, slug)).Select(ToProjectAdminView).FirstOrDefault();

    public async Task<UpsertResult<ProjectItem>> UpsertProjectAsync(string slug, ProjectUpsertRequest request, bool mustExist)
    {
        if (Validate(slug, request.Status, ProjectStatuses, request.Translations.Select(t => (t.Lang, t.Title))) is { } error)
        {
            return new UpsertResult<ProjectItem>(UpsertOutcome.Invalid, Error: error);
        }

        var (item, created) = await LoadForUpsertAsync(ContentTypes.Project, slug, mustExist);
        if (item is null)
        {
            return new UpsertResult<ProjectItem>(UpsertOutcome.NotFound);
        }

        item.Status = request.Status;
        item.Project ??= new ProjectEntity();
        item.Project.ImageUrl = request.ImageUrl ?? string.Empty;
        item.Project.Icon = request.Icon ?? string.Empty;
        ReplaceTranslations(item, request.Translations.Select(t => new ContentTranslationEntity
        {
            ContentItemId = item.Id,
            Lang = t.Lang,
            Title = t.Title,
            Summary = t.ShortDescription ?? string.Empty,
            Body = t.FullDescription ?? string.Empty,
            AttributesJson = JsonSerializer.Serialize(new ProjectAttributes(t.ShortTitle ?? t.Title, t.Category ?? request.Category ?? string.Empty, t.Year ?? request.Year ?? string.Empty), Json)
        }));
        await SaveAsync(item, created);
        return new UpsertResult<ProjectItem>(created ? UpsertOutcome.Created : UpsertOutcome.Updated, ToProjectItems(item).First());
    }

    // ---- news

    public async Task<List<NewsItem>> NewsAsync(string lang) =>
        [.. (await LiveAsync(ContentTypes.News)).Where(i => i.Status == "published").SelectMany(i => ToNewsItems(i).Where(n => n.Lang == lang))];

    public async Task<NewsItem?> NewsItemAsync(string slug, string lang) =>
        (await LiveAsync(ContentTypes.News, slug)).Where(i => i.Status == "published").SelectMany(i => ToNewsItems(i)).FirstOrDefault(n => n.Lang == lang);

    public async Task<List<NewsAdminView>> AdminNewsAsync() => [.. (await LiveAsync(ContentTypes.News)).Select(ToNewsAdminView)];

    public async Task<NewsAdminView?> AdminNewsItemAsync(string slug) =>
        (await LiveAsync(ContentTypes.News, slug)).Select(ToNewsAdminView).FirstOrDefault();

    public async Task<UpsertResult<NewsItem>> UpsertNewsAsync(string slug, NewsUpsertRequest request, bool mustExist)
    {
        if (Validate(slug, request.Status, NewsStatuses, request.Translations.Select(t => (t.Lang, t.Title))) is { } error)
        {
            return new UpsertResult<NewsItem>(UpsertOutcome.Invalid, Error: error);
        }

        var (item, created) = await LoadForUpsertAsync(ContentTypes.News, slug, mustExist);
        if (item is null)
        {
            return new UpsertResult<NewsItem>(UpsertOutcome.NotFound);
        }

        var publishedAt = request.PublishedAt ?? item.PublishedAt ?? time.GetUtcNow();
        item.Status = request.Status;
        item.PublishedAt = publishedAt;
        item.News ??= new NewsArticleEntity();
        item.News.ImageUrl = request.ImageUrl ?? string.Empty;
        ReplaceTranslations(item, request.Translations.Select(t => new ContentTranslationEntity
        {
            ContentItemId = item.Id,
            Lang = t.Lang,
            Title = t.Title,
            Summary = t.Summary ?? string.Empty,
            Body = string.Join("\n\n", t.Content ?? []),
            AttributesJson = JsonSerializer.Serialize(new NewsAttributes(t.Date ?? publishedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), t.Tags ?? []), Json)
        }));
        await SaveAsync(item, created);
        return new UpsertResult<NewsItem>(created ? UpsertOutcome.Created : UpsertOutcome.Updated, ToNewsItems(item).First());
    }

    // ---- shared

    public async Task<bool> DeleteAsync(string type, string slug)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug && i.DeletedAt == null);
        if (item is null)
        {
            return false;
        }

        item.DeletedAt = time.GetUtcNow();
        item.UpdatedAt = item.DeletedAt.Value;
        await db.SaveChangesAsync();
        return true;
    }

    public Task<bool> AnyAsync() => db.Items.AnyAsync();

    private async Task<List<ContentItemEntity>> LiveAsync(string type, string? slug = null)
    {
        var query = db.Items.AsNoTracking().AsSplitQuery()
            .Include(i => i.Translations).Include(i => i.Project).Include(i => i.News)
            .Where(i => i.Type == type && i.DeletedAt == null);
        if (slug is not null)
        {
            query = query.Where(i => i.Slug == slug);
        }

        var items = await query.ToListAsync();
        // Sorted in memory: SQLite (the test provider) cannot order by DateTimeOffset. Newest news first, projects in curated order.
        return type == ContentTypes.News
            ? [.. items.OrderByDescending(i => i.PublishedAt).ThenBy(i => i.SortOrder)]
            : [.. items.OrderBy(i => i.SortOrder).ThenBy(i => i.Slug, StringComparer.Ordinal)];
    }

    private async Task<(ContentItemEntity? Item, bool Created)> LoadForUpsertAsync(string type, string slug, bool mustExist)
    {
        var existing = await db.Items.Include(i => i.Translations).Include(i => i.Project).Include(i => i.News)
            .FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug);
        if (existing is not null && (existing.DeletedAt is null || !mustExist))
        {
            existing.DeletedAt = null;
            return (existing, false);
        }

        if (mustExist)
        {
            return (null, false);
        }

        var now = time.GetUtcNow();
        var maxOrder = (await db.Items.Where(i => i.Type == type).Select(i => (int?)i.SortOrder).MaxAsync()) ?? -1;
        var item = new ContentItemEntity
        {
            Id = $"cnt_{Guid.NewGuid():N}",
            Type = type,
            Slug = slug,
            SortOrder = maxOrder + 1,
            CreatedAt = now
        };
        db.Items.Add(item);
        return (item, true);
    }

    private void ReplaceTranslations(ContentItemEntity item, IEnumerable<ContentTranslationEntity> incoming)
    {
        var next = incoming.ToList();
        foreach (var gone in item.Translations.Where(t => next.All(n => n.Lang != t.Lang)).ToList())
        {
            item.Translations.Remove(gone);
            db.Translations.Remove(gone);
        }

        foreach (var translation in next)
        {
            var current = item.Translations.FirstOrDefault(t => t.Lang == translation.Lang);
            if (current is null)
            {
                item.Translations.Add(translation);
            }
            else
            {
                current.Title = translation.Title;
                current.Summary = translation.Summary;
                current.Body = translation.Body;
                current.AttributesJson = translation.AttributesJson;
            }
        }
    }

    private async Task SaveAsync(ContentItemEntity item, bool created)
    {
        item.UpdatedAt = time.GetUtcNow();
        if (created)
        {
            item.CreatedAt = item.UpdatedAt;
        }

        await db.SaveChangesAsync();
    }

    private static string? Validate(string slug, string status, string[] allowedStatuses, IEnumerable<(string Lang, string Title)> translations)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Length > 120 || !SlugPattern().IsMatch(slug))
        {
            return "slug must be lowercase letters, digits and hyphens (at most 120 characters).";
        }

        if (!allowedStatuses.Contains(status))
        {
            return $"status must be one of: {string.Join(", ", allowedStatuses)}.";
        }

        var list = translations.ToList();
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

    // ---- mapping

    private static List<ProjectItem> ToProjectItems(ContentItemEntity item) =>
    [
        .. item.Translations.OrderBy(t => t.Lang, StringComparer.Ordinal).Select(t =>
        {
            var attributes = ReadAttributes<ProjectAttributes>(t) ?? new ProjectAttributes(t.Title, string.Empty, string.Empty);
            return new ProjectItem(item.Slug, item.Slug, t.Lang, t.Title, attributes.ShortTitle ?? t.Title, t.Summary, t.Body,
                item.Project?.ImageUrl ?? string.Empty, attributes.Category ?? string.Empty, attributes.Year ?? string.Empty,
                item.Status, item.Project?.Icon ?? string.Empty, item.UpdatedAt);
        })
    ];

    private static List<NewsItem> ToNewsItems(ContentItemEntity item) =>
    [
        .. item.Translations.OrderBy(t => t.Lang, StringComparer.Ordinal).Select(t =>
        {
            var attributes = ReadAttributes<NewsAttributes>(t) ?? new NewsAttributes(string.Empty, []);
            return new NewsItem(item.Slug, item.Slug, t.Lang, t.Title, t.Summary, Paragraphs(t.Body), item.News?.ImageUrl ?? string.Empty,
                attributes.Date ?? string.Empty, item.PublishedAt ?? item.CreatedAt, item.Status, attributes.Tags ?? []);
        })
    ];

    private static ProjectAdminView ToProjectAdminView(ContentItemEntity item)
    {
        var translations = ToProjectItems(item);
        var lead = translations.FirstOrDefault(t => t.Lang == "en") ?? translations.FirstOrDefault();
        return new ProjectAdminView(item.Slug, item.Project?.ImageUrl ?? string.Empty, lead?.Category ?? string.Empty, lead?.Year ?? string.Empty,
            item.Status, item.Project?.Icon ?? string.Empty,
            [.. translations.Select(t => new ProjectTranslationView(t.Lang, t.Title, t.ShortTitle, t.ShortDescription, t.FullDescription, t.Category, t.Year))]);
    }

    private static NewsAdminView ToNewsAdminView(ContentItemEntity item) =>
        new(item.Slug, item.News?.ImageUrl ?? string.Empty, item.PublishedAt ?? item.CreatedAt, item.Status,
            [.. ToNewsItems(item).Select(n => new NewsTranslationView(n.Lang, n.Title, n.Summary, n.Content, n.Date, n.Tags))]);

    private static List<string> Paragraphs(string body) =>
        [.. body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static T? ReadAttributes<T>(ContentTranslationEntity translation) where T : class =>
        JsonSerializer.Deserialize<T>(translation.AttributesJson, Json);

    private sealed record ProjectAttributes(string? ShortTitle, string? Category, string? Year);

    private sealed record NewsAttributes(string? Date, List<string>? Tags);
}
