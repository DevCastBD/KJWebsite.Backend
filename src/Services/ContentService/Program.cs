using System.Security.Cryptography;
using System.Text.Json;
using ContentService;
using ContentService.Data;
using ContentService.Data.Entities;
using ContentService.Validation;
using FluentValidation;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using KJWebsite.BuildingBlocks.Validation;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

const int MaxPageSize = ListQueryValidator.MaxPageSize;

var builder = WebApplication.CreateBuilder(args);
var health = builder.AddKjObservability("ContentService");
builder.Services.AddKjValidation<ContentService.Validation.ListQueryValidator>();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Info.Title = "Content Service API";
        document.Info.Description = "Serves projects and news items in multiple languages (en/bn) from PostgreSQL. " +
            "Public read endpoints are unauthenticated. Admin endpoints require a bearer access token (from the Auth service) carrying the matching `content.*` permission.";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
});

builder.Services.AddDbContext<ContentDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("ContentDb")
        ?? (configuration.GetValue<bool>("OpenApi:Export") ? "Host=localhost;Database=content" : throw new InvalidOperationException("ConnectionStrings:ContentDb is required."));
    options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "content"));
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ContentStore>();
builder.Services.AddKjJwtAuthentication();

health.AddDbContextCheck<ContentDbContext>("database", tags: [KjObservability.ReadyTag, KjObservability.StartupTag]);

var app = builder.Build();
app.UseKjObservability();

if (!(app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("OpenApi:Export")))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
    // Tests swap in the Sqlite provider, which has no migrations to replay; Postgres keeps its migration history.
    if (db.Database.IsNpgsql())
    {
        db.Database.Migrate();
    }
    else
    {
        db.Database.EnsureCreated();
    }

    await ContentSeeder.SeedAsync(db, scope.ServiceProvider.GetRequiredService<TimeProvider>());
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("Content Service API")
               .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient)
               .AddHttpAuthentication("Bearer", bearer => { bearer.Token = string.Empty; });
    }).AllowAnonymous();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapKjObservability();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ContentService" }))
   .WithName("ContentHealth")
   .WithSummary("Content service health check")
   .WithTags("System")
.AllowAnonymous();

app.MapGet("/api/v1/content/projects", async (HttpRequest request, ContentStore store, string? lang, string? status, int? limit, int? offset, IValidator<ListQuery> listValidator) =>
{
    var resolvedLanguage = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
    if (InvalidList(request.HttpContext, listValidator, new ListQuery(limit, offset, status)) is { } invalid)
    {
        return invalid;
    }

    var normalizedStatus = NormalizeStatus(status);
    var (safeLimit, safeOffset) = (limit.GetValueOrDefault(20), offset.GetValueOrDefault(0));

    return Cached(request, Page(await store.ProjectsAsync(resolvedLanguage, normalizedStatus), safeLimit, safeOffset, resolvedLanguage));
})
.WithName("GetProjects")
.WithSummary("List projects (paginated)")
.WithDescription("Returns `{ items, total, limit, offset, lang }`. `lang`: en/bn (default en). `status`: active/all (default active). `limit`: 1–100 (default 20). `offset`: default 0. ETag + Cache-Control are set.")
.WithTags("Content")
.AllowAnonymous();

app.MapGet("/api/v1/content/projects/{id}", async (HttpRequest request, ContentStore store, string id, string? lang) =>
{
    var resolvedLanguage = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
    var item = await store.ProjectAsync(id, resolvedLanguage);
    return item is null
        ? ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "Project not found.")
        : Cached(request, item);
})
.WithName("GetProjectById")
.WithSummary("Get project by ID")
.WithDescription("Returns a single project by slug ID in the requested language (`lang`: en/bn, default en).")
.WithTags("Content")
.AllowAnonymous();

app.MapGet("/api/v1/content/news", async (HttpRequest request, ContentStore store, string? lang, int? limit, int? offset, IValidator<ListQuery> listValidator) =>
{
    var resolvedLanguage = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
    if (InvalidList(request.HttpContext, listValidator, new ListQuery(limit, offset)) is { } invalid)
    {
        return invalid;
    }

    var (safeLimit, safeOffset) = (limit.GetValueOrDefault(20), offset.GetValueOrDefault(0));

    return Cached(request, Page(await store.NewsAsync(resolvedLanguage), safeLimit, safeOffset, resolvedLanguage));
})
.WithName("GetNews")
.WithSummary("List news articles (paginated)")
.WithDescription("Returns `{ items, total, limit, offset, lang }`. `lang`: en/bn (default en). `limit`: 1–100 (default 20). `offset`: default 0. ETag + Cache-Control are set.")
.WithTags("Content")
.AllowAnonymous();

app.MapGet("/api/v1/content/news/{id}", async (HttpRequest request, ContentStore store, string id, string? lang) =>
{
    var resolvedLanguage = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
    var item = await store.NewsItemAsync(id, resolvedLanguage);
    return item is null
        ? ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "News item not found.")
        : Cached(request, item);
})
.WithName("GetNewsById")
.WithSummary("Get news article by ID")
.WithDescription("Returns a single news article by slug ID in the requested language (`lang`: en/bn, default en).")
.WithTags("Content")
.AllowAnonymous();

app.MapGet("/api/v1/content/bootstrap", async (HttpRequest request, ContentStore store, string? lang) =>
{
    var resolvedLanguage = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
    return Cached(request, new
    {
        projects = Page(await store.ProjectsAsync(resolvedLanguage, "active"), MaxPageSize, 0, resolvedLanguage),
        news = Page(await store.NewsAsync(resolvedLanguage), MaxPageSize, 0, resolvedLanguage),
        settings = new Dictionary<string, string>(),
        lang = resolvedLanguage
    });
})
.WithName("GetContentBootstrap")
.WithSummary("Cold-start bundle")
.WithDescription("Returns `{ projects, news, settings, lang }` in one call for the SPA's first render (up to 100 active projects and 100 news items). ETag + Cache-Control are set.")
.WithTags("Content")
.AllowAnonymous();

app.MapGet("/api/v1/admin/content/projects", async (HttpRequest request, ContentStore store, int? limit, int? offset, IValidator<ListQuery> listValidator) =>
{

    if (InvalidList(request.HttpContext, listValidator, new ListQuery(limit, offset)) is { } invalid)
    {
        return invalid;
    }

    var (safeLimit, safeOffset) = (limit.GetValueOrDefault(20), offset.GetValueOrDefault(0));

    request.HttpContext.Response.Headers.CacheControl = "no-store";
    return Results.Ok(PageAll(await store.AdminProjectsAsync(), safeLimit, safeOffset));
})
.WithName("ListAdminProjects")
.WithSummary("List projects with all translations")
.WithDescription("Returns every translation of every project, grouped per slug. Requires the matching `content.*` permission.")
.WithTags("Admin — Content")
.RequirePermission(Permissions.ContentView);

app.MapGet("/api/v1/admin/content/projects/{id}", async (HttpRequest request, ContentStore store, string id) =>
{

    var view = await store.AdminProjectAsync(id);
    request.HttpContext.Response.Headers.CacheControl = "no-store";
    return view is null
        ? ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "Project not found.")
        : Results.Ok(view);
})
.WithName("GetAdminProject")
.WithSummary("Get a project with all translations")
.WithDescription("Requires the `content.view` permission.")
.WithTags("Admin — Content")
.RequirePermission(Permissions.ContentView);

app.MapGet("/api/v1/admin/content/news", async (HttpRequest request, ContentStore store, int? limit, int? offset, IValidator<ListQuery> listValidator) =>
{

    if (InvalidList(request.HttpContext, listValidator, new ListQuery(limit, offset)) is { } invalid)
    {
        return invalid;
    }

    var (safeLimit, safeOffset) = (limit.GetValueOrDefault(20), offset.GetValueOrDefault(0));

    request.HttpContext.Response.Headers.CacheControl = "no-store";
    return Results.Ok(PageAll(await store.AdminNewsAsync(), safeLimit, safeOffset));
})
.WithName("ListAdminNews")
.WithSummary("List news articles with all translations")
.WithDescription("Returns every translation of every article, grouped per slug. Requires the matching `content.*` permission.")
.WithTags("Admin — Content")
.RequirePermission(Permissions.ContentView);

app.MapGet("/api/v1/admin/content/news/{id}", async (HttpRequest request, ContentStore store, string id) =>
{

    var view = await store.AdminNewsItemAsync(id);
    request.HttpContext.Response.Headers.CacheControl = "no-store";
    return view is null
        ? ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "News item not found.")
        : Results.Ok(view);
})
.WithName("GetAdminNews")
.WithSummary("Get a news article with all translations")
.WithDescription("Requires the `content.view` permission.")
.WithTags("Admin — Content")
.RequirePermission(Permissions.ContentView);

app.MapPost("/api/v1/admin/content/projects", async (HttpRequest request, ContentStore store, ProjectUpsertRequest payload) =>
{
    var result = await store.UpsertProjectAsync(payload.Slug, payload, mustExist: false);
    return result.Outcome == UpsertOutcome.Invalid
        ? ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", result.Error!)
        : Results.Created($"/api/v1/content/projects/{payload.Slug}", result.Value);
})
.WithName("CreateProject")
.WithSummary("Create or replace a project")
.WithDescription("Creates a project with all provided language translations. Replaces (or restores) any existing project with the same slug. Requires the matching `content.*` permission.")
.WithTags("Admin — Content")
.WithValidation<ProjectUpsertRequest>()
.RequirePermission(Permissions.ContentCreate);

app.MapPut("/api/v1/admin/content/projects/{id}", async (HttpRequest request, ContentStore store, string id, ProjectUpsertRequest payload) =>
{
    var result = await store.UpsertProjectAsync(id, payload, mustExist: true);
    return result.Outcome switch
    {
        UpsertOutcome.NotFound => ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "Project not found."),
        UpsertOutcome.Invalid => ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", result.Error!),
        _ => Results.Ok(result.Value)
    };
})
.WithName("UpdateProject")
.WithSummary("Update a project")
.WithDescription("Replaces all translations for the given project. Requires the matching `content.*` permission.")
.WithTags("Admin — Content")
.WithValidation<ProjectUpsertRequest>()
.RequirePermission(Permissions.ContentUpdate);

app.MapDelete("/api/v1/admin/content/projects/{id}", async (HttpRequest request, ContentStore store, string id) =>
    await store.DeleteAsync(ContentTypes.Project, id)
        ? Results.NoContent()
        : ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "Project not found."))
.WithName("DeleteProject")
.WithSummary("Delete a project")
.WithDescription("Soft-deletes the project (the slug stays reserved). Requires the matching `content.*` permission.")
.WithTags("Admin — Content")
.RequirePermission(Permissions.ContentDelete);

app.MapPost("/api/v1/admin/content/news", async (HttpRequest request, ContentStore store, NewsUpsertRequest payload) =>
{
    var result = await store.UpsertNewsAsync(payload.Slug, payload, mustExist: false);
    return result.Outcome == UpsertOutcome.Invalid
        ? ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", result.Error!)
        : Results.Created($"/api/v1/content/news/{payload.Slug}", result.Value);
})
.WithName("CreateNews")
.WithSummary("Create or replace a news article")
.WithDescription("Creates a news article with all provided language translations. Replaces (or restores) any existing article with the same slug. Requires the matching `content.*` permission.")
.WithTags("Admin — Content")
.WithValidation<NewsUpsertRequest>()
.RequirePermission(Permissions.ContentCreate);

app.MapPut("/api/v1/admin/content/news/{id}", async (HttpRequest request, ContentStore store, string id, NewsUpsertRequest payload) =>
{
    var result = await store.UpsertNewsAsync(id, payload, mustExist: true);
    return result.Outcome switch
    {
        UpsertOutcome.NotFound => ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "News item not found."),
        UpsertOutcome.Invalid => ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", result.Error!),
        _ => Results.Ok(result.Value)
    };
})
.WithName("UpdateNews")
.WithSummary("Update a news article")
.WithDescription("Replaces all translations for the given news article. Requires the matching `content.*` permission.")
.WithTags("Admin — Content")
.WithValidation<NewsUpsertRequest>()
.RequirePermission(Permissions.ContentUpdate);

app.MapDelete("/api/v1/admin/content/news/{id}", async (HttpRequest request, ContentStore store, string id) =>
    await store.DeleteAsync(ContentTypes.News, id)
        ? Results.NoContent()
        : ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "News item not found."))
.WithName("DeleteNews")
.WithSummary("Delete a news article")
.WithDescription("Soft-deletes the article (the slug stays reserved). Requires the matching `content.*` permission.")
.WithTags("Admin — Content")
.RequirePermission(Permissions.ContentDelete);

app.Run();

static string NormalizeStatus(string? status) => string.IsNullOrWhiteSpace(status) ? "active" : status.Trim().ToLowerInvariant();

static IResult? InvalidList(HttpContext context, IValidator<ListQuery> validator, ListQuery query)
{
    var result = validator.Validate(query);
    return result.IsValid ? null : ValidationProblem.Create(context, result.Errors);
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

namespace ContentService
{
    public sealed class ContentServiceTestEntryPoint;
}
