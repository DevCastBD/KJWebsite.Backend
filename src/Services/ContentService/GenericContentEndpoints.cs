using ContentService.Data;
using ContentService.Data.Entities;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using static ContentService.ContentHttp;

namespace ContentService;

/// <summary>
/// The generic content API: the six newer types, tags, related items, slug changes and redirects. Projects and news keep their own
/// endpoints; tags, related items and `items/{type}` work for all eight types.
/// </summary>
public static class GenericContentEndpoints
{
    private const string Public = "/api/v1/content";
    private const string Admin = "/api/v1/admin/content";

    public static void MapGenericContent(this WebApplication app)
    {
        foreach (var kind in ContentKinds.Generic)
        {
            MapKind(app, kind);
        }

        MapItems(app);
        MapTags(app);
        MapRedirects(app);
    }

    private static void MapKind(WebApplication app, ContentKind kind)
    {
        var type = kind.Type;
        var publicTag = $"Content — {kind.Pascal}";
        var adminTag = "Admin — Content";

        app.MapGet($"{Public}/{kind.Segment}", async (HttpRequest request, GenericContentStore store, string? lang, string? tag, int? limit, int? offset) =>
        {
            var language = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
            return !TryReadPaging(limit, offset, out var safeLimit, out var safeOffset)
                ? Invalid(request, "Invalid paging values.")
                : Cached(request, Page(await store.PublicListAsync(type, language, tag), safeLimit, safeOffset, language));
        })
        .WithName($"List{kind.Pascal}Items").WithSummary($"List {kind.Segment} (published)")
        .WithDescription("Returns `{ items, total, limit, offset, lang }`. `tag` filters by tag slug or by a tag name in any language. Items without a translation in `lang` are left out.")
        .WithTags(publicTag).CachePublicContent().AllowAnonymous();

        app.MapGet($"{Public}/{kind.Segment}/{{slug}}", async (HttpRequest request, GenericContentStore store, string slug, string? lang) =>
        {
            var language = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
            var item = await store.PublicItemAsync(type, slug, language);
            return item is null ? NotFound(request, "Item not found.") : Cached(request, item);
        })
        .WithName($"Get{kind.Pascal}Item").WithSummary($"Get one of {kind.Segment} by slug")
        .WithDescription("Includes the tags (named in the requested language) and the related items of any type.")
        .WithTags(publicTag).CachePublicContent().AllowAnonymous();

        app.MapGet($"{Admin}/{kind.Segment}", async (HttpRequest request, GenericContentStore store, int? limit, int? offset) =>
        {
            if (!TryReadPaging(limit, offset, out var safeLimit, out var safeOffset))
            {
                return Invalid(request, "Invalid paging values.");
            }

            request.HttpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(PageAll(await store.AdminListAsync(type), safeLimit, safeOffset));
        })
        .WithName($"ListAdmin{kind.Pascal}Items").WithSummary($"List {kind.Segment} with all translations")
        .WithTags(adminTag).RequirePermission(Permissions.ContentView);

        app.MapGet($"{Admin}/{kind.Segment}/{{slug}}", (HttpRequest request, GenericContentStore store, string slug) => AdminGet(request, store, type, slug))
        .WithName($"GetAdmin{kind.Pascal}Item").WithSummary($"Get one of {kind.Segment} with all translations")
        .WithDescription("The `ETag` header is the version to send back as `If-Match` on update.")
        .WithTags(adminTag).RequirePermission(Permissions.ContentView);

        app.MapPost($"{Admin}/{kind.Segment}", async (HttpRequest request, GenericContentStore store, ContentUpsertRequest payload) =>
        {
            var result = await store.UpsertAsync(type, payload.Slug, payload, mustExist: false, ifMatch: null);
            SetETag(request, result.ETag);
            return Respond(request, result, created: $"{Public}/{kind.Segment}/{payload.Slug}");
        })
        .WithName($"Create{kind.Pascal}Item").WithSummary($"Create or replace one of {kind.Segment}")
        .WithDescription("Replaces (or restores) any existing item with the same slug.")
        .WithTags(adminTag).EvictsContentCache().RequirePermission(Permissions.ContentCreate);

        app.MapPut($"{Admin}/{kind.Segment}/{{slug}}", async (HttpRequest request, GenericContentStore store, string slug, ContentUpsertRequest payload) =>
        {
            var result = await store.UpsertAsync(type, slug, payload, mustExist: true, request.Headers.IfMatch.ToArray()!);
            SetETag(request, result.ETag);
            return Respond(request, result, created: null);
        })
        .WithName($"Update{kind.Pascal}Item").WithSummary($"Update one of {kind.Segment}")
        .WithDescription("Replaces all translations, tags and related items. Send the `ETag` from the last read as `If-Match`: if the item changed since, the response is 409 CONFLICT.")
        .WithTags(adminTag).EvictsContentCache().RequirePermission(Permissions.ContentUpdate);

        app.MapDelete($"{Admin}/{kind.Segment}/{{slug}}", async (HttpRequest request, GenericContentStore store, string slug) =>
            await store.DeleteAsync(type, slug) ? Results.NoContent() : NotFound(request, "Item not found."))
        .WithName($"Delete{kind.Pascal}Item").WithSummary($"Delete one of {kind.Segment}")
        .WithDescription("Soft-deletes the item (the slug stays reserved).")
        .WithTags(adminTag).EvictsContentCache().RequirePermission(Permissions.ContentDelete);
    }

    private static void MapItems(WebApplication app)
    {
        app.MapGet($"{Public}/items/{{type}}/{{slug}}", async (HttpRequest request, GenericContentStore store, string type, string slug, string? lang) =>
        {
            if (ContentKinds.ByType(type) is null)
            {
                return NotFound(request, "Unknown content type.");
            }

            var language = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
            var item = await store.PublicItemAsync(type, slug, language);
            return item is null ? NotFound(request, "Item not found.") : Cached(request, item);
        })
        .WithName("GetContentItem").WithSummary("Get any content item in the generic shape")
        .WithDescription("Works for all eight types (project, news, page, faq, team-member, testimonial, partner, press), with tags and related items.")
        .WithTags("Content").CachePublicContent().AllowAnonymous();

        app.MapPut($"{Admin}/items/{{type}}/{{slug}}/tags", async (HttpRequest request, GenericContentStore store, string type, string slug, SetTagsRequest payload) =>
            ContentKinds.ByType(type) is null
                ? NotFound(request, "Unknown content type.")
                : Respond(request, await store.SetTagsAsync(type, slug, payload.Tags ?? []), created: null))
        .WithName("SetItemTags").WithSummary("Replace the tags of any content item")
        .WithDescription("Tags are given by slug or by a name in any language; unknown tags are rejected.")
        .WithTags("Admin — Content").EvictsContentCache().RequirePermission(Permissions.ContentUpdate);

        app.MapPut($"{Admin}/items/{{type}}/{{slug}}/related", async (HttpRequest request, GenericContentStore store, string type, string slug, SetRelatedRequest payload) =>
            ContentKinds.ByType(type) is null
                ? NotFound(request, "Unknown content type.")
                : Respond(request, await store.SetRelatedAsync(type, slug, payload.Related ?? []), created: null))
        .WithName("SetItemRelated").WithSummary("Replace the related items of any content item")
        .WithDescription("Related items may be of any type; order is kept.")
        .WithTags("Admin — Content").EvictsContentCache().RequirePermission(Permissions.ContentUpdate);

        app.MapPost($"{Admin}/items/{{type}}/{{slug}}/rename", async (HttpRequest request, GenericContentStore store, string type, string slug, RenameRequest payload) =>
            ContentKinds.ByType(type) is null
                ? NotFound(request, "Unknown content type.")
                : Respond(request, await store.RenameAsync(type, slug, payload.NewSlug), created: null))
        .WithName("RenameItem").WithSummary("Change the slug of any content item")
        .WithDescription("Creates a 301 redirect from the old public path (`/{segment}/{old}`) to the new one in the same transaction.")
        .WithTags("Admin — Content").EvictsContentCache().RequirePermission(Permissions.ContentUpdate);
    }

    private static void MapTags(WebApplication app)
    {
        app.MapGet($"{Public}/tags", async (HttpRequest request, TaxonomyStore store, string? lang) =>
        {
            var language = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
            var tags = (await store.ListAsync()).Select(t => new TagRef(t.Slug, t.Names.GetValueOrDefault(language) ?? t.Names.Values.First())).ToList();
            return Cached(request, new { items = tags, total = tags.Count, lang = language });
        })
        .WithName("ListTags").WithSummary("List tags, named in the requested language")
        .WithTags("Content").CachePublicContent().AllowAnonymous();

        app.MapGet($"{Admin}/tags", async (HttpRequest request, TaxonomyStore store) =>
        {
            request.HttpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await store.ListAsync());
        })
        .WithName("ListAdminTags").WithSummary("List tags with all names").WithTags("Admin — Content").RequirePermission(Permissions.ContentView);

        app.MapPost($"{Admin}/tags", async (HttpRequest request, TaxonomyStore store, TagUpsertRequest payload) =>
            Respond(request, await store.CreateAsync(payload), created: $"{Admin}/tags"))
        .WithName("CreateTag").WithSummary("Create a tag")
        .WithDescription("One tag carries a name per language. A name that matches an existing tag after Bengali-aware normalisation is a 409.")
        .WithTags("Admin — Content").EvictsContentCache().RequirePermission(Permissions.ContentCreate);

        app.MapPut($"{Admin}/tags/{{slug}}", async (HttpRequest request, TaxonomyStore store, string slug, TagUpsertRequest payload) =>
            Respond(request, await store.ReplaceAsync(slug, payload), created: null))
        .WithName("UpdateTag").WithSummary("Replace the names of a tag").WithTags("Admin — Content").EvictsContentCache().RequirePermission(Permissions.ContentUpdate);

        app.MapDelete($"{Admin}/tags/{{slug}}", async (HttpRequest request, TaxonomyStore store, string slug) =>
            await store.DeleteAsync(slug) ? Results.NoContent() : NotFound(request, "Tag not found."))
        .WithName("DeleteTag").WithSummary("Delete a tag and detach it from every item")
        .WithTags("Admin — Content").EvictsContentCache().RequirePermission(Permissions.ContentDelete);
    }

    private static void MapRedirects(WebApplication app)
    {
        app.MapGet($"{Public}/redirects/resolve", async (HttpRequest request, RedirectStore store, string? path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Invalid(request, "path is required.");
            }

            var row = await store.ResolveAsync(path);
            return row is null ? NotFound(request, "No redirect for this path.") : Cached(request, new RedirectResolution(row.FromPath, row.ToPath, row.StatusCode));
        })
        .WithName("ResolveRedirect").WithSummary("Resolve an old path (legacy `.html`/`.php`, renamed slug) to its replacement")
        .WithDescription("Returns `{ from, to, statusCode }` or 404. Paths are matched case-insensitively, without query string or trailing slash.")
        .WithTags("Content").CachePublicContent().AllowAnonymous();

        app.MapGet($"{Admin}/redirects", async (HttpRequest request, RedirectStore store, int? limit, int? offset) =>
        {
            if (!TryReadPaging(limit, offset, out var safeLimit, out var safeOffset))
            {
                return Invalid(request, "Invalid paging values.");
            }

            request.HttpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(PageAll([.. (await store.ListAsync()).Select(ToView)], safeLimit, safeOffset));
        })
        .WithName("ListRedirects").WithSummary("List redirects").WithTags("Admin — Content").RequirePermission(Permissions.ContentView);

        app.MapPost($"{Admin}/redirects", async (HttpRequest request, RedirectStore store, RedirectCreateRequest payload) =>
        {
            var result = await store.CreateAsync(payload.From, payload.To, payload.StatusCode);
            return result.Outcome == UpsertOutcome.Invalid
                ? Invalid(request, result.Error!)
                : Results.Created($"{Admin}/redirects", ToView(result.Value!));
        })
        .WithName("CreateRedirect").WithSummary("Add or replace a redirect (legacy URLs, language-prefix moves)")
        .WithDescription("Existing redirects that point at `from` are retargeted, and chains are flattened; a loop is rejected.")
        .WithTags("Admin — Content").EvictsContentCache().RequirePermission(Permissions.ContentCreate);

        app.MapDelete($"{Admin}/redirects/{{id}}", async (HttpRequest request, RedirectStore store, string id) =>
            await store.DeleteAsync(id) ? Results.NoContent() : NotFound(request, "Redirect not found."))
        .WithName("DeleteRedirect").WithSummary("Delete a redirect").WithTags("Admin — Content").EvictsContentCache().RequirePermission(Permissions.ContentDelete);
    }

    private static async Task<IResult> AdminGet(HttpRequest request, GenericContentStore store, string type, string slug)
    {
        var view = await store.AdminItemAsync(type, slug);
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        SetETag(request, await store.ETagAsync(type, slug));
        return view is null ? NotFound(request, "Item not found.") : Results.Ok(view);
    }

    private static IResult Respond<T>(HttpRequest request, UpsertResult<T> result, string? created) => result.Outcome switch
    {
        UpsertOutcome.NotFound => NotFound(request, "Item not found."),
        UpsertOutcome.Invalid => Invalid(request, result.Error!),
        UpsertOutcome.Conflict when result.Error is null => ConflictResponse(request),
        UpsertOutcome.Conflict => ApiError.Create(request.HttpContext, StatusCodes.Status409Conflict, "CONFLICT", result.Error!),
        // POST creates or restores (replacing), so both outcomes are a 201, as for projects and news.
        UpsertOutcome.Created or UpsertOutcome.Updated when created is not null => Results.Created(created, result.Value),
        _ => Results.Ok(result.Value)
    };

    private static RedirectView ToView(RedirectEntity row) => new(row.Id, row.FromPath, row.ToPath, row.StatusCode, row.Source, row.CreatedAt);
}
