namespace ContentService.Data.Entities;

/// <summary>A tag shared by every language and every content type. Its names per language are rows of <see cref="TagNameEntity"/>.</summary>
public sealed class TagEntity
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public List<TagNameEntity> Names { get; set; } = [];
}

/// <summary>`NormalizedName` is unique per language (see TagNormalizer), so "শিক্ষা " and "শিক্ষা" cannot become two tags.</summary>
public sealed class TagNameEntity
{
    public string TagId { get; set; } = string.Empty;
    public string Lang { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
}

public sealed class ContentItemTagEntity
{
    public string ContentItemId { get; set; } = string.Empty;
    public string TagId { get; set; } = string.Empty;
    public TagEntity? Tag { get; set; }
}

public sealed class ContentRelationEntity
{
    public string FromItemId { get; set; } = string.Empty;
    public string ToItemId { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public ContentItemEntity? To { get; set; }
}

/// <summary>Maps an old public path to its replacement. Flattened on write: `ToPath` is always a final target, never another redirect.</summary>
public sealed class RedirectEntity
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Normalised: lower case, leading slash, no query, no trailing slash.</summary>
    public string FromPath { get; set; } = string.Empty;

    public string ToPath { get; set; } = string.Empty;
    public int StatusCode { get; set; } = 301;

    /// <summary>`slug-change` for the automatic rows, `manual` for the rest (legacy URLs, language-prefix moves).</summary>
    public string Source { get; set; } = "manual";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A site setting. Catalog keys hold typed JSON in which every visible text is `{ "bn": "...", "en": "..." }`.</summary>
public sealed class SettingEntity
{
    public string Key { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "null";

    /// <summary>Secret settings are never returned by a public endpoint, and their value is hidden in admin lists.</summary>
    public bool IsSecret { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A runtime switch changed from the back office without a deploy. Not the frontend's build-time VITE_ENABLE_* flags.</summary>
public sealed class FeatureFlagEntity
{
    public string Key { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}
