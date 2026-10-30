using System.Security.Cryptography;
using System.Text.Json;
using ContentService;
using ContentService.Data;
using ContentService.Data.Entities;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using static ContentService.ContentHttp;

var builder = WebApplication.CreateBuilder(args);
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
builder.Services.AddScoped<TaxonomyStore>();
builder.Services.AddScoped<RedirectStore>();
builder.Services.AddScoped<GenericContentStore>();
builder.Services.AddKjJwtAuthentication();
builder.Services.AddContentCaching(builder.Configuration);

var app = builder.Build();

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
app.UseOutputCache();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ContentService" }))
   .WithName("ContentHealth")
   .WithSummary("Content service health check")
   .WithTags("System")
.AllowAnonymous();

app.MapGet("/api/v1/content/projects", async (HttpRequest request, ContentStore store, string? lang, string? status, int? limit, int? offset) =>
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

    return Cached(request, Page(await store.ProjectsAsync(resolvedLanguage, normalizedStatus), safeLimit, safeOffset, resolvedLanguage));
})
.WithName("GetProjects")
.WithSummary("List projects (paginated)")
.WithDescription("Returns `{ items, total, limit, offset, lang }`. `lang`: en/bn (default en). `status`: active/all (default active). `limit`: 1–100 (default 20). `offset`: default 0. ETag + Cache-Control are set.")
.WithTags("Content")
.CachePublicContent()
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
.CachePublicContent()
.AllowAnonymous();

app.MapGet("/api/v1/content/news", async (HttpRequest request, ContentStore store, string? lang, int? limit, int? offset) =>
{
    var resolvedLanguage = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
    if (!TryReadPaging(limit, offset, out var safeLimit, out var safeOffset))
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid paging values.");
    }

    return Cached(request, Page(await store.NewsAsync(resolvedLanguage), safeLimit, safeOffset, resolvedLanguage));
})
.WithName("GetNews")
.WithSummary("List news articles (paginated)")
.WithDescription("Returns `{ items, total, limit, offset, lang }`. `lang`: en/bn (default en). `limit`: 1–100 (default 20). `offset`: default 0. ETag + Cache-Control are set.")
.WithTags("Content")
.CachePublicContent()
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
.CachePublicContent()
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
.CachePublicContent()
.AllowAnonymous();

app.MapGet("/api/v1/admin/content/projects", async (HttpRequest request, ContentStore store, int? limit, int? offset) =>
{

    if (!TryReadPaging(limit, offset, out var safeLimit, out var safeOffset))
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid paging values.");
    }

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
    SetETag(request, await store.ETagAsync(ContentTypes.Project, id));
    return view is null
        ? ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "Project not found.")
        : Results.Ok(view);
})
.WithName("GetAdminProject")
.WithSummary("Get a project with all translations")
.WithDescription("Requires the `content.view` permission. The `ETag` header is the version to send back as `If-Match` on update.")
.WithTags("Admin — Content")
.RequirePermission(Permissions.ContentView);

app.MapGet("/api/v1/admin/content/news", async (HttpRequest request, ContentStore store, int? limit, int? offset) =>
{

    if (!TryReadPaging(limit, offset, out var safeLimit, out var safeOffset))
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid paging values.");
    }

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
    SetETag(request, await store.ETagAsync(ContentTypes.News, id));
    return view is null
        ? ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "News item not found.")
        : Results.Ok(view);
})
.WithName("GetAdminNews")
.WithSummary("Get a news article with all translations")
.WithDescription("Requires the `content.view` permission. The `ETag` header is the version to send back as `If-Match` on update.")
.WithTags("Admin — Content")
.RequirePermission(Permissions.ContentView);

app.MapPost("/api/v1/admin/content/projects", async (HttpRequest request, ContentStore store, ProjectUpsertRequest payload) =>
{
    var result = await store.UpsertProjectAsync(payload.Slug, payload, mustExist: false);
    SetETag(request, result.ETag);
    return result.Outcome == UpsertOutcome.Invalid
        ? ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", result.Error!)
        : Results.Created($"/api/v1/content/projects/{payload.Slug}", result.Value);
})
.WithName("CreateProject")
.WithSummary("Create or replace a project")
.WithDescription("Creates a project with all provided language translations. Replaces (or restores) any existing project with the same slug. Requires the matching `content.*` permission.")
.WithTags("Admin — Content")
.EvictsContentCache()
.RequirePermission(Permissions.ContentCreate);

app.MapPut("/api/v1/admin/content/projects/{id}", async (HttpRequest request, ContentStore store, string id, ProjectUpsertRequest payload) =>
{
    var result = await store.UpsertProjectAsync(id, payload, mustExist: true, request.Headers.IfMatch.ToArray()!);
    SetETag(request, result.ETag);
    return result.Outcome switch
    {
        UpsertOutcome.Conflict => ConflictResponse(request),
        UpsertOutcome.NotFound => ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "Project not found."),
        UpsertOutcome.Invalid => ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", result.Error!),
        _ => Results.Ok(result.Value)
    };
})
.WithName("UpdateProject")
.WithSummary("Update a project")
.WithDescription("Replaces all translations for the given project. Requires the matching `content.*` permission. Send the `ETag` from the last read as `If-Match`: if the item changed since, the response is 409 CONFLICT and nothing is saved. Without `If-Match` the last write wins.")
.WithTags("Admin — Content")
.EvictsContentCache()
.RequirePermission(Permissions.ContentUpdate);

app.MapDelete("/api/v1/admin/content/projects/{id}", async (HttpRequest request, ContentStore store, string id) =>
    await store.DeleteAsync(ContentTypes.Project, id)
        ? Results.NoContent()
        : ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "Project not found."))
.WithName("DeleteProject")
.WithSummary("Delete a project")
.WithDescription("Soft-deletes the project (the slug stays reserved). Requires the matching `content.*` permission.")
.WithTags("Admin — Content")
.EvictsContentCache()
.RequirePermission(Permissions.ContentDelete);

app.MapPost("/api/v1/admin/content/news", async (HttpRequest request, ContentStore store, NewsUpsertRequest payload) =>
{
    var result = await store.UpsertNewsAsync(payload.Slug, payload, mustExist: false);
    SetETag(request, result.ETag);
    return result.Outcome == UpsertOutcome.Invalid
        ? ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", result.Error!)
        : Results.Created($"/api/v1/content/news/{payload.Slug}", result.Value);
})
.WithName("CreateNews")
.WithSummary("Create or replace a news article")
.WithDescription("Creates a news article with all provided language translations. Replaces (or restores) any existing article with the same slug. Requires the matching `content.*` permission.")
.WithTags("Admin — Content")
.EvictsContentCache()
.RequirePermission(Permissions.ContentCreate);

app.MapPut("/api/v1/admin/content/news/{id}", async (HttpRequest request, ContentStore store, string id, NewsUpsertRequest payload) =>
{
    var result = await store.UpsertNewsAsync(id, payload, mustExist: true, request.Headers.IfMatch.ToArray()!);
    SetETag(request, result.ETag);
    return result.Outcome switch
    {
        UpsertOutcome.Conflict => ConflictResponse(request),
        UpsertOutcome.NotFound => ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "News item not found."),
        UpsertOutcome.Invalid => ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", result.Error!),
        _ => Results.Ok(result.Value)
    };
})
.WithName("UpdateNews")
.WithSummary("Update a news article")
.WithDescription("Replaces all translations for the given news article. Requires the matching `content.*` permission. Send the `ETag` from the last read as `If-Match`: if the item changed since, the response is 409 CONFLICT and nothing is saved. Without `If-Match` the last write wins.")
.WithTags("Admin — Content")
.EvictsContentCache()
.RequirePermission(Permissions.ContentUpdate);

app.MapDelete("/api/v1/admin/content/news/{id}", async (HttpRequest request, ContentStore store, string id) =>
    await store.DeleteAsync(ContentTypes.News, id)
        ? Results.NoContent()
        : ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", "News item not found."))
.WithName("DeleteNews")
.WithSummary("Delete a news article")
.WithDescription("Soft-deletes the article (the slug stays reserved). Requires the matching `content.*` permission.")
.WithTags("Admin — Content")
.EvictsContentCache()
.RequirePermission(Permissions.ContentDelete);

app.MapGenericContent();

app.Run();

namespace ContentService
{
    public sealed class ContentServiceTestEntryPoint;
}
