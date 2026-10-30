using System.Text.Json;

namespace ContentService;

public sealed record TagRef(string Slug, string Name);

public sealed record RelatedItemView(string Type, string Slug, string Title);

public sealed record RelatedRef(string Type, string Slug);

/// <summary>Public shape of any content item, in one language. Fields are only ever added.</summary>
public sealed record ContentItemView(
    string Id,
    string Type,
    string Slug,
    string Lang,
    string Title,
    string Summary,
    string Body,
    Dictionary<string, JsonElement> Attributes,
    string ImageUrl,
    DateTimeOffset? PublishedAt,
    List<TagRef> Tags,
    List<RelatedItemView> Related,
    DateTimeOffset UpdatedAt);

public sealed record ContentTranslationInput(string Lang, string Title, string? Summary, string? Body, Dictionary<string, JsonElement>? Attributes);

public sealed record ContentUpsertRequest(
    string Slug,
    string Status,
    string? ImageUrl,
    DateTimeOffset? PublishedAt,
    int? SortOrder,
    List<ContentTranslationInput> Translations,
    List<string>? Tags,
    List<RelatedRef>? Related);

public sealed record ContentTranslationView(string Lang, string Title, string Summary, string Body, Dictionary<string, JsonElement> Attributes);

public sealed record ContentItemAdminView(
    string Type,
    string Slug,
    string Status,
    string ImageUrl,
    int SortOrder,
    DateTimeOffset? PublishedAt,
    List<ContentTranslationView> Translations,
    List<string> Tags,
    List<RelatedRef> Related);

public sealed record SetTagsRequest(List<string> Tags);

public sealed record SetRelatedRequest(List<RelatedRef> Related);

public sealed record RenameRequest(string NewSlug);

public sealed record TagUpsertRequest(string Slug, Dictionary<string, string> Names);

public sealed record TagView(string Slug, Dictionary<string, string> Names);

public sealed record RedirectCreateRequest(string From, string To, int? StatusCode);

public sealed record RedirectView(string Id, string From, string To, int StatusCode, string Source, DateTimeOffset CreatedAt);

public sealed record RedirectResolution(string From, string To, int StatusCode);
