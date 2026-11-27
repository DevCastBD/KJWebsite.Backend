using ContentService.Data;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using static ContentService.ContentHttp;

namespace ContentService;

/// <summary>
/// Press coverage details. The text (headline in `title`, excerpt in `summary`) uses the generic `press` content API with its editorial workflow;
/// this adds outlet, date, links, reproduction permission and homepage featured ordering.
/// </summary>
public static class PressEndpoints
{
    private const string Public = "/api/v1/content";
    private const string Admin = "/api/v1/admin/content";

    public static void MapPress(this WebApplication app)
    {
        app.MapGet($"{Admin}/press/{{slug}}/coverage", async (HttpRequest request, PressStore store, string slug) =>
        {
            var view = await store.AdminAsync(slug);
            if (view is null)
            {
                return NotFound(request, "The press item has no coverage details yet, or does not exist.");
            }

            SetETag(request, await store.ETagAsync(slug));
            request.HttpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(view);
        })
        .WithName("GetAdminPressCoverage").WithSummary("Get a press item's coverage details")
        .WithDescription("The `ETag` is the item's version: send it back as `If-Match` on PUT.")
        .WithTags("Admin — Content").RequirePermission(Permissions.ContentView);

        app.MapPut($"{Admin}/press/{{slug}}/coverage", async (HttpRequest request, PressStore store, string slug, PressCoverageRequest payload) =>
        {
            var result = await store.PutAsync(slug, payload, request.Headers.IfMatch.ToArray()!);
            if (result.Outcome is UpsertOutcome.Updated or UpsertOutcome.Created)
            {
                SetETag(request, result.ETag);
                return Results.Ok(result.Value);
            }

            return Failure(request, result);
        })
        .WithName("PutPressCoverage").WithSummary("Set a press item's outlet, date, links, permission and featured flag")
        .WithDescription("`externalUrl` (https) is always safe. A hosted `clippingUrl` is only accepted when `reproductionPermission` is `granted` (with a `permissionNote` saying who agreed); the permission values are unknown, linked-only, pending and granted. The public API never shows a clipping unless it is granted.")
        .WithTags("Admin — Content").EvictsContentCache().RequirePermission(Permissions.ContentUpdate);

        app.MapPut($"{Admin}/press-featured-order", async (HttpRequest request, PressStore store, ContentPressOrderRequest payload) =>
        {
            var (ok, error) = await store.SetFeaturedOrderAsync(payload.Slugs ?? []);
            return ok ? Results.NoContent() : Invalid(request, error!);
        })
        .WithName("PutPressFeaturedOrder").WithSummary("Set the homepage press strip: the listed items, in order")
        .WithDescription("Every listed item becomes featured at its position; every other item is un-featured. Takes effect on the next public read, with no deployment.")
        .WithTags("Admin — Content").EvictsContentCache().RequirePermission(Permissions.ContentPublish);

        app.MapGet($"{Public}/press/{{slug}}/coverage", async (HttpRequest request, PressStore store, string slug) =>
        {
            var view = await store.PublicAsync(slug);
            return view is null ? NotFound(request, "Press item not found.") : Cached(request, view);
        })
        .WithName("GetPressCoverage").WithSummary("Coverage details of a published press item")
        .WithTags("Content — Press").CachePublicContent().AllowAnonymous();

        app.MapGet($"{Public}/press-coverage", async (HttpRequest request, PressStore store, string? lang, bool? featured, string? coverageLanguage, int? limit, int? offset) =>
        {
            var language = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
            if (!TryReadPaging(limit, offset, out var safeLimit, out var safeOffset))
            {
                return Invalid(request, "Invalid paging values.");
            }

            return Cached(request, Page(await store.ListAsync(language, featured == true, coverageLanguage?.Trim().ToLowerInvariant()), safeLimit, safeOffset, language));
        })
        .WithName("ListPressCoverage").WithSummary("Published press coverage, newest first, or the featured strip in order")
        .WithDescription("`featured=true` returns the homepage strip in the editors' order. `coverageLanguage` filters by the language of the coverage itself (bn or en); `lang` picks the language of the headline and excerpt.")
        .WithTags("Content — Press").CachePublicContent().AllowAnonymous();
    }
}
