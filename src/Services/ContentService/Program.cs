using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

using KJWebsite.BuildingBlocks;
using Scalar.AspNetCore;

const int MaxPageSize = 100;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Info.Title = "Content Service API";
        document.Info.Description = "Serves projects and news items in multiple languages (en/bn). " +
            "Public read endpoints are unauthenticated. Admin write endpoints require `Authorization: Bearer dev-admin-token`.";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("Content Service API")
               .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient)
               .AddHttpAuthentication("Bearer", bearer => { bearer.Token = "dev-admin-token"; });
    });
}


var projects = new List<ProjectItem>
{
    new("pathshala", "pathshala", "en", "Pathshala", "Pathshala", "Training project", "Full details", "/img/causes/2.jpg", "Education", "2016", "active", "flaticon-circle", DateTimeOffset.UtcNow),
    new("pathshala", "pathshala", "bn", "পাঠশালা", "পাঠশালা", "প্রশিক্ষণ প্রকল্প", "বিস্তারিত", "/img/causes/2.jpg", "শিক্ষা", "২০১৬", "active", "flaticon-circle", DateTimeOffset.UtcNow)
};

var news = new List<NewsItem>
{
    new("bauniabadh-mou", "bauniabadh-mou", "en", "MoU signed", "Summary", ["Paragraph 1"], "/img/blog/1.jpg", "February 25, 2017", DateTimeOffset.Parse("2017-02-25", CultureInfo.InvariantCulture), "published"),
    new("bauniabadh-mou", "bauniabadh-mou", "bn", "সমঝোতা চুক্তি", "সারসংক্ষেপ", ["অনুচ্ছেদ ১"], "/img/blog/1.jpg", "ফেব্রুয়ারি ২৫, ২০১৭", DateTimeOffset.Parse("2017-02-25", CultureInfo.InvariantCulture), "published")
};

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ContentService" }))
   .WithName("ContentHealth")
   .WithSummary("Content service health check")
   .WithTags("System");

app.MapGet("/api/v1/content/projects", (HttpRequest request, string? lang, string? status, int? limit, int? offset) =>
{
    var resolvedLanguage = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
    if (!TryReadStatus(status, out var normalizedStatus))
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "status must be active or all.");
    }

    if (!TryReadPaging(limit, offset, out var safeLimit, out var safeOffset))
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid paging values.");
    }

    return Cached(request, Page(ProjectsFor(resolvedLanguage, normalizedStatus), safeLimit, safeOffset, resolvedLanguage));
})
.WithName("GetProjects")
.WithSummary("List projects (paginated)")
.WithDescription("Returns `{ items, total, limit, offset, lang }`. `lang`: en/bn (default en). `status`: active/all (default active). `limit`: 1–100 (default 20). `offset`: default 0. ETag + Cache-Control are set.")
.WithTags("Content");

app.MapGet("/api/v1/content/projects/{id}", (HttpRequest request, string id, string? lang) =>
{
    var resolvedLanguage = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
    var item = projects.FirstOrDefault(p => p.Id == id && p.Lang == resolvedLanguage);
    return item is null
        ? ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "Project not found.")
        : Cached(request, item);
})
.WithName("GetProjectById")
.WithSummary("Get project by ID")
.WithDescription("Returns a single project by slug ID in the requested language (`lang`: en/bn, default en).")
.WithTags("Content");

app.MapGet("/api/v1/content/news", (HttpRequest request, string? lang, int? limit, int? offset) =>
{
    var resolvedLanguage = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
    if (!TryReadPaging(limit, offset, out var safeLimit, out var safeOffset))
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid paging values.");
    }

    return Cached(request, Page(NewsFor(resolvedLanguage), safeLimit, safeOffset, resolvedLanguage));
})
.WithName("GetNews")
.WithSummary("List news articles (paginated)")
.WithDescription("Returns `{ items, total, limit, offset, lang }`. `lang`: en/bn (default en). `limit`: 1–100 (default 20). `offset`: default 0. ETag + Cache-Control are set.")
.WithTags("Content");

app.MapGet("/api/v1/content/news/{id}", (HttpRequest request, string id, string? lang) =>
{
    var resolvedLanguage = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
    var item = news.FirstOrDefault(n => n.Id == id && n.Lang == resolvedLanguage);
    return item is null
        ? ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "News item not found.")
        : Cached(request, item);
})
.WithName("GetNewsById")
.WithSummary("Get news article by ID")
.WithDescription("Returns a single news article by slug ID in the requested language (`lang`: en/bn, default en).")
.WithTags("Content");

app.MapGet("/api/v1/content/bootstrap", (HttpRequest request, string? lang) =>
{
    var resolvedLanguage = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
    return Cached(request, new
    {
        projects = Page(ProjectsFor(resolvedLanguage, "active"), MaxPageSize, 0, resolvedLanguage),
        news = Page(NewsFor(resolvedLanguage), MaxPageSize, 0, resolvedLanguage),
        settings = new Dictionary<string, string>(),
        lang = resolvedLanguage
    });
})
.WithName("GetContentBootstrap")
.WithSummary("Cold-start bundle")
.WithDescription("Returns `{ projects, news, settings, lang }` in one call for the SPA's first render (up to 100 active projects and 100 news items). ETag + Cache-Control are set.")
.WithTags("Content");

app.MapGet("/api/v1/admin/content/projects", (HttpRequest request, int? limit, int? offset) =>
{
    var authResult = EnsureAdmin(request);
    if (authResult is not null) return authResult;

    if (!TryReadPaging(limit, offset, out var safeLimit, out var safeOffset))
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid paging values.");
    }

    request.HttpContext.Response.Headers.CacheControl = "no-store";
    var all = projects.GroupBy(p => p.Id).Select(g => ToProjectAdminView(g.ToList())).ToList();
    return Results.Ok(PageAll(all, safeLimit, safeOffset));
})
.WithName("ListAdminProjects")
.WithSummary("List projects with all translations")
.WithDescription("Returns every translation of every project, grouped per slug. Requires `Authorization: Bearer dev-admin-token`.")
.WithTags("Admin — Content");

app.MapGet("/api/v1/admin/content/projects/{id}", (HttpRequest request, string id) =>
{
    var authResult = EnsureAdmin(request);
    if (authResult is not null) return authResult;

    var rows = projects.Where(p => p.Id == id).ToList();
    request.HttpContext.Response.Headers.CacheControl = "no-store";
    return rows.Count == 0
        ? ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "Project not found.")
        : Results.Ok(ToProjectAdminView(rows));
})
.WithName("GetAdminProject")
.WithSummary("Get a project with all translations")
.WithDescription("Requires `Authorization: Bearer dev-admin-token`.")
.WithTags("Admin — Content");

app.MapGet("/api/v1/admin/content/news", (HttpRequest request, int? limit, int? offset) =>
{
    var authResult = EnsureAdmin(request);
    if (authResult is not null) return authResult;

    if (!TryReadPaging(limit, offset, out var safeLimit, out var safeOffset))
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid paging values.");
    }

    request.HttpContext.Response.Headers.CacheControl = "no-store";
    var all = news.GroupBy(n => n.Id).Select(g => ToNewsAdminView(g.ToList())).ToList();
    return Results.Ok(PageAll(all, safeLimit, safeOffset));
})
.WithName("ListAdminNews")
.WithSummary("List news articles with all translations")
.WithDescription("Returns every translation of every article, grouped per slug. Requires `Authorization: Bearer dev-admin-token`.")
.WithTags("Admin — Content");

app.MapGet("/api/v1/admin/content/news/{id}", (HttpRequest request, string id) =>
{
    var authResult = EnsureAdmin(request);
    if (authResult is not null) return authResult;

    var rows = news.Where(n => n.Id == id).ToList();
    request.HttpContext.Response.Headers.CacheControl = "no-store";
    return rows.Count == 0
        ? ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "News item not found.")
        : Results.Ok(ToNewsAdminView(rows));
})
.WithName("GetAdminNews")
.WithSummary("Get a news article with all translations")
.WithDescription("Requires `Authorization: Bearer dev-admin-token`.")
.WithTags("Admin — Content");

app.MapPost("/api/v1/admin/content/projects", (HttpRequest request, ProjectUpsertRequest payload) =>
{
    var authResult = EnsureAdmin(request);
    if (authResult is not null) return authResult;

    if (payload.Translations.Count == 0)
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "At least one translation is required.");
    }

    var created = payload.Translations.Select(t => new ProjectItem(
        payload.Slug,
        payload.Slug,
        t.Lang,
        t.Title,
        t.ShortTitle ?? t.Title,
        t.ShortDescription ?? string.Empty,
        t.FullDescription ?? string.Empty,
        payload.ImageUrl ?? string.Empty,
        payload.Category ?? string.Empty,
        payload.Year ?? string.Empty,
        payload.Status,
        payload.Icon ?? string.Empty,
        DateTimeOffset.UtcNow)).ToList();

    projects.RemoveAll(p => p.Id == payload.Slug);
    projects.AddRange(created);

    return Results.Created($"/api/v1/content/projects/{payload.Slug}", created.First());
})
.WithName("CreateProject")
.WithSummary("Create or replace a project")
.WithDescription("Creates a project with all provided language translations. Replaces any existing project with the same slug. Requires `Authorization: Bearer dev-admin-token`.")
.WithTags("Admin — Content");

app.MapPut("/api/v1/admin/content/projects/{id}", (HttpRequest request, string id, ProjectUpsertRequest payload) =>
{
    var authResult = EnsureAdmin(request);
    if (authResult is not null) return authResult;

    if (!projects.Any(p => p.Id == id))
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "Project not found.");
    }

    projects.RemoveAll(p => p.Id == id);
    var updated = payload.Translations.Select(t => new ProjectItem(
        id, id, t.Lang, t.Title, t.ShortTitle ?? t.Title,
        t.ShortDescription ?? string.Empty, t.FullDescription ?? string.Empty,
        payload.ImageUrl ?? string.Empty, payload.Category ?? string.Empty,
        payload.Year ?? string.Empty, payload.Status, payload.Icon ?? string.Empty,
        DateTimeOffset.UtcNow)).ToList();
    projects.AddRange(updated);

    return Results.Ok(updated.First());
})
.WithName("UpdateProject")
.WithSummary("Update a project")
.WithDescription("Replaces all translations for the given project. Requires `Authorization: Bearer dev-admin-token`.")
.WithTags("Admin — Content");

app.MapDelete("/api/v1/admin/content/projects/{id}", (HttpRequest request, string id) =>
{
    var authResult = EnsureAdmin(request);
    if (authResult is not null) return authResult;

    var removed = projects.RemoveAll(p => p.Id == id);
    return removed == 0
        ? ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "Project not found.")
        : Results.NoContent();
})
.WithName("DeleteProject")
.WithSummary("Delete a project")
.WithDescription("Deletes all translations of the given project. Requires `Authorization: Bearer dev-admin-token`.")
.WithTags("Admin — Content");

app.MapPost("/api/v1/admin/content/news", (HttpRequest request, NewsUpsertRequest payload) =>
{
    var authResult = EnsureAdmin(request);
    if (authResult is not null) return authResult;

    if (payload.Translations.Count == 0)
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "At least one translation is required.");
    }

    var created = payload.Translations.Select(t => new NewsItem(
        payload.Slug,
        payload.Slug,
        t.Lang,
        t.Title,
        t.Summary ?? string.Empty,
        t.Content ?? [],
        payload.ImageUrl ?? string.Empty,
        payload.PublishedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
        payload.PublishedAt ?? DateTimeOffset.UtcNow,
        payload.Status)).ToList();

    news.RemoveAll(n => n.Id == payload.Slug);
    news.AddRange(created);

    return Results.Created($"/api/v1/content/news/{payload.Slug}", created.First());
})
.WithName("CreateNews")
.WithSummary("Create or replace a news article")
.WithDescription("Creates a news article with all provided language translations. Replaces any existing article with the same slug. Requires `Authorization: Bearer dev-admin-token`.")
.WithTags("Admin — Content");

app.MapPut("/api/v1/admin/content/news/{id}", (HttpRequest request, string id, NewsUpsertRequest payload) =>
{
    var authResult = EnsureAdmin(request);
    if (authResult is not null) return authResult;

    if (!news.Any(n => n.Id == id))
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "News item not found.");
    }

    news.RemoveAll(n => n.Id == id);
    var updated = payload.Translations.Select(t => new NewsItem(
        id,
        id,
        t.Lang,
        t.Title,
        t.Summary ?? string.Empty,
        t.Content ?? [],
        payload.ImageUrl ?? string.Empty,
        payload.PublishedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
        payload.PublishedAt ?? DateTimeOffset.UtcNow,
        payload.Status)).ToList();
    news.AddRange(updated);

    return Results.Ok(updated.First());
})
.WithName("UpdateNews")
.WithSummary("Update a news article")
.WithDescription("Replaces all translations for the given news article. Requires `Authorization: Bearer dev-admin-token`.")
.WithTags("Admin — Content");

app.MapDelete("/api/v1/admin/content/news/{id}", (HttpRequest request, string id) =>
{
    var authResult = EnsureAdmin(request);
    if (authResult is not null) return authResult;

    var removed = news.RemoveAll(n => n.Id == id);
    return removed == 0
        ? ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "News item not found.")
        : Results.NoContent();
})
.WithName("DeleteNews")
.WithSummary("Delete a news article")
.WithDescription("Deletes all translations of the given news article. Requires `Authorization: Bearer dev-admin-token`.")
.WithTags("Admin — Content");

app.Run();

List<ProjectItem> ProjectsFor(string language, string status) =>
    projects.Where(p => p.Lang == language && (status == "all" || p.Status == "active")).ToList();

List<NewsItem> NewsFor(string language) => news.Where(n => n.Lang == language).ToList();

static bool TryReadStatus(string? status, out string normalized)
{
    normalized = string.IsNullOrWhiteSpace(status) ? "active" : status.Trim().ToLowerInvariant();
    return normalized is "active" or "all";
}

static bool TryReadPaging(int? limit, int? offset, out int safeLimit, out int safeOffset)
{
    safeLimit = limit.GetValueOrDefault(20);
    safeOffset = offset.GetValueOrDefault(0);
    return safeLimit is >= 1 and <= MaxPageSize && safeOffset >= 0;
}

static object Page<T>(List<T> rows, int limit, int offset, string language)
{
    var paged = rows.Skip(offset).Take(limit).ToArray();
    return new { items = paged, total = rows.Count, limit, offset, lang = language };
}

static object PageAll<T>(List<T> rows, int limit, int offset) =>
    new PagingEnvelope<T>(rows.Skip(offset).Take(limit).ToArray(), rows.Count, null, limit, offset);

static IResult Cached(HttpRequest request, object body)
{
    var json = JsonSerializer.SerializeToUtf8Bytes(body, JsonSerializerOptions.Web);
    var etag = $"\"{Convert.ToHexString(SHA256.HashData(json))[..32].ToLowerInvariant()}\"";
    var headers = request.HttpContext.Response.Headers;
    headers.ETag = etag;
    headers.CacheControl = "public, max-age=60";
    headers.Vary = "Accept-Language";

    var ifNoneMatch = request.Headers.IfNoneMatch;
    return ifNoneMatch.Any(v => v is not null && (v == "*" || v.Split(',').Select(t => t.Trim()).Contains(etag)))
        ? Results.StatusCode(StatusCodes.Status304NotModified)
        : Results.Bytes(json, "application/json");
}

static ProjectAdminView ToProjectAdminView(List<ProjectItem> rows)
{
    var first = rows[0];
    return new ProjectAdminView(first.Slug, first.ImageUrl, first.Category, first.Year, first.Status, first.Icon,
        [.. rows.Select(r => new ProjectTranslationView(r.Lang, r.Title, r.ShortTitle, r.ShortDescription, r.FullDescription))]);
}

static NewsAdminView ToNewsAdminView(List<NewsItem> rows)
{
    var first = rows[0];
    return new NewsAdminView(first.Slug, first.ImageUrl, first.PublishedAt, first.Status,
        [.. rows.Select(r => new NewsTranslationView(r.Lang, r.Title, r.Summary, r.Content))]);
}

static IResult? EnsureAdmin(HttpRequest request)
{
    if (!request.Headers.TryGetValue("Authorization", out var authHeader))
    {
        return Results.Unauthorized();
    }

    return authHeader.ToString() == "Bearer dev-admin-token"
        ? null
        : ApiError.Create(request.HttpContext, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid token.");
}

sealed record ProjectItem(
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
    DateTimeOffset UpdatedAt);

sealed record NewsItem(
    string Id,
    string Slug,
    string Lang,
    string Title,
    string Summary,
    List<string> Content,
    string ImageUrl,
    string Date,
    DateTimeOffset PublishedAt,
    string Status);

sealed record ProjectTranslationView(string Lang, string Title, string ShortTitle, string ShortDescription, string FullDescription);

sealed record ProjectAdminView(string Slug, string ImageUrl, string Category, string Year, string Status, string Icon, List<ProjectTranslationView> Translations);

sealed record NewsTranslationView(string Lang, string Title, string Summary, List<string> Content);

sealed record NewsAdminView(string Slug, string ImageUrl, DateTimeOffset PublishedAt, string Status, List<NewsTranslationView> Translations);

sealed record ProjectUpsertRequest(
    string Slug,
    string? ImageUrl,
    string? Category,
    string? Year,
    string Status,
    string? Icon,
    List<ProjectTranslationInput> Translations);

sealed record ProjectTranslationInput(string Lang, string Title, string? ShortTitle, string? ShortDescription, string? FullDescription);

sealed record NewsUpsertRequest(
    string Slug,
    string? ImageUrl,
    DateTimeOffset? PublishedAt,
    string Status,
    List<NewsTranslationInput> Translations);

sealed record NewsTranslationInput(string Lang, string Title, string? Summary, List<string>? Content);

namespace ContentService
{
    public sealed class ContentServiceTestEntryPoint;
}
