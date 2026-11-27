using ContentService.Data;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using static ContentService.ContentHttp;

namespace ContentService;

/// <summary>Open Graph images (served from storage, drawn in the background) and share counts.</summary>
public static class SocialEndpoints
{
    private const string Public = "/api/v1/content";
    private const string Admin = "/api/v1/admin/content";
    private const string Tag = "Content — Social";

    public static void MapSocial(this WebApplication app)
    {
        app.MapGet($"{Public}/og-image/{{type}}/{{slug}}", async (HttpContext context, SocialStore store, string type, string slug, string? lang) =>
        {
            var language = LanguageResolver.Resolve(lang, context.Request.Headers.AcceptLanguage.ToString());
            var image = await store.GetAsync(type, slug, language);
            if (image is null)
            {
                context.Response.Headers.CacheControl = "no-store";
                return NotFound(context.Request, "No image yet.");
            }

            context.Response.Headers.ETag = image.ETag;
            context.Response.Headers.CacheControl = "public, max-age=3600";
            context.Response.Headers.Vary = "Accept-Language";
            return context.Request.Headers.IfNoneMatch.ToString() == image.ETag ? Results.StatusCode(StatusCodes.Status304NotModified) : Results.File(image.Png, "image/png");
        })
        .WithName("GetOpenGraphImage").WithSummary("The Open Graph image (1200x630 PNG) of a published item")
        .WithDescription("Served from storage only: a missing image is 404 and the page should fall back to the default image. Images are drawn by a background worker after publish (and redrawn when the title or summary changes), never on request. `lang` is bn or en.")
        .WithTags(Tag).AllowAnonymous();

        app.MapPost($"{Public}/shares/{{type}}/{{slug}}", async (HttpContext context, SocialStore store, string type, string slug, ContentShareRequest payload) =>
        {
            var (ok, error) = await store.CountShareAsync(type, slug, payload.Channel);
            if (ok)
            {
                return Results.NoContent();
            }

            return error is null ? NotFound(context.Request, "Item not found.") : Invalid(context.Request, error);
        })
        .WithName("CountShare").WithSummary("Count one share of a published item on a channel")
        .WithDescription("`channel`: facebook, x, linkedin, whatsapp, copy, other. A bare counter: no visitor, address or device is stored. Counts are indicative (anyone can call this), not an audit figure.")
        .WithTags(Tag).AllowAnonymous();

        app.MapGet($"{Public}/shares/{{type}}/{{slug}}", async (HttpContext context, SocialStore store, string type, string slug) =>
            await store.SharesAsync(type, slug) is { } counts ? Results.Ok(counts) : NotFound(context.Request, "Item not found."))
        .WithName("GetShareCounts").WithSummary("Share counts of a published item by channel")
        .WithTags(Tag).AllowAnonymous();

        app.MapGet($"{Admin}/social/top-shared", async (HttpContext context, SocialStore store, int? limit) =>
            Results.Ok(await store.TopAsync(Math.Clamp(limit ?? 20, 1, 100))))
        .WithName("ListTopSharedContent").WithSummary("The most-shared items")
        .WithTags(Tag).RequirePermission(Permissions.ContentView);

        app.MapPost($"{Admin}/social/og/generate", async (SocialStore store) => Results.Ok(new { drawn = await store.GenerateDueAsync(50) }))
        .WithName("GenerateOpenGraphImages").WithSummary("Draw any missing or outdated Open Graph images now (up to 50)")
        .WithDescription("The background worker does this on its own every 30 seconds; this only skips the wait after a publish.")
        .WithTags(Tag).RequirePermission(Permissions.ContentPublish);
    }
}

public sealed record ContentShareRequest(string? Channel);
