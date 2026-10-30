namespace ContentService.Data.Entities;

public static class ContentTypes
{
    public const string Project = "project";
    public const string News = "news";
}

/// <summary>
/// The generalised content shape (plan §3.2): every kind of content is a row here with a type discriminator, so P2 can add
/// pages, events and the rest without migrating live content. Type-specific, language-independent fields live in a one-to-one table.
/// </summary>
public sealed class ContentItemEntity
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;

    /// <summary>The public identifier. Unique per type and never changed; a soft-deleted slug stays reserved.</summary>
    public string Slug { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public List<ContentTranslationEntity> Translations { get; set; } = [];
    public ProjectEntity? Project { get; set; }
    public NewsArticleEntity? News { get; set; }
}

/// <summary>One language of one item. `AttributesJson` holds the per-language, type-specific extras (project category and year, news date label and tags).</summary>
public sealed class ContentTranslationEntity
{
    public string ContentItemId { get; set; } = string.Empty;
    public string Lang { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;

    /// <summary>Long text. News paragraphs are stored separated by a blank line.</summary>
    public string Body { get; set; } = string.Empty;

    public string AttributesJson { get; set; } = "{}";
}

public sealed class ProjectEntity
{
    public string ContentItemId { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
}

public sealed class NewsArticleEntity
{
    public string ContentItemId { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
}
