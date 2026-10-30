namespace ContentService;

// Public shapes. Fields are only ever added, never renamed: the static frontend keeps working against any version.
public sealed record ProjectItem(
    string Id,
    string Slug,
    string Lang,
    string Title,
    string ShortTitle,
    string ShortDescription,
    string FullDescription,
    string ImageUrl,
    string Category,
    string Year,
    string Status,
    string Icon,
    DateTimeOffset UpdatedAt,
    bool TranslationPending = false,
    string? RequestedLang = null);

public sealed record NewsItem(
    string Id,
    string Slug,
    string Lang,
    string Title,
    string Summary,
    List<string> Content,
    string ImageUrl,
    string Date,
    DateTimeOffset PublishedAt,
    string Status,
    List<string> Tags,
    bool TranslationPending = false,
    string? RequestedLang = null);

public sealed record ProjectTranslationView(string Lang, string Title, string ShortTitle, string ShortDescription, string FullDescription, string Category, string Year);

public sealed record ProjectAdminView(string Slug, string ImageUrl, string Category, string Year, string Status, string Icon, List<ProjectTranslationView> Translations, ContentService.Data.ItemCompleteness? Completeness = null);

public sealed record NewsTranslationView(string Lang, string Title, string Summary, List<string> Content, string Date, List<string> Tags);

public sealed record NewsAdminView(string Slug, string ImageUrl, DateTimeOffset PublishedAt, string Status, List<NewsTranslationView> Translations, ContentService.Data.ItemCompleteness? Completeness = null);

public sealed record ProjectUpsertRequest(
    string Slug,
    string? ImageUrl,
    string? Category,
    string? Year,
    string Status,
    string? Icon,
    List<ProjectTranslationInput> Translations);

public sealed record ProjectTranslationInput(string Lang, string Title, string? ShortTitle, string? ShortDescription, string? FullDescription, string? Category = null, string? Year = null);

public sealed record NewsUpsertRequest(
    string Slug,
    string? ImageUrl,
    DateTimeOffset? PublishedAt,
    string Status,
    List<NewsTranslationInput> Translations);

public sealed record NewsTranslationInput(string Lang, string Title, string? Summary, List<string>? Content, string? Date = null, List<string>? Tags = null);
