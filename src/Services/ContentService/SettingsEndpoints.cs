using ContentService.Data;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using static ContentService.ContentHttp;

namespace ContentService;

/// <summary>Bilingual site settings and runtime flags (BE-037). See docs/SITE_SETTINGS.md.</summary>
public static class SettingsEndpoints
{
    private const string Admin = "/api/v1/admin/content";
    private const string Tag = "Admin — Site settings";

    public static void MapSettings(this WebApplication app)
    {
        app.MapGet("/api/v1/content/settings", async (HttpRequest request, SettingsStore store, string? lang) =>
        {
            var language = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
            var result = await store.PublicAsync(language);
            // Always 200, even in maintenance: the frontend reads the `maintenance` block and keeps its static fallback; a failure here must never empty the navigation.
            return Cached(request, new { lang = language, settings = result.Settings, flags = result.Flags });
        })
        .WithName("GetPublicSettings").WithSummary("Site settings and runtime flags in one language")
        .WithDescription("Navigation, contact, social, footer, homepage section order, maintenance and the custom non-secret settings, each text already resolved to `lang`. Secret settings are never included. `flags` are runtime feature flags, not the frontend's build-time VITE_ENABLE_* flags.")
        .WithTags("Content").CachePublicContent().AllowAnonymous();

        app.MapGet($"{Admin}/settings", async (HttpRequest request, SettingsStore store) =>
        {
            request.HttpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { items = await store.ListAsync(), knownKeys = SettingsStore.CatalogKeys });
        })
        .WithName("ListSettings").WithSummary("List settings (secret values are hidden)").WithTags(Tag).RequirePermission(Permissions.SettingsView);

        app.MapGet($"{Admin}/settings/{{key}}", async (HttpRequest request, SettingsStore store, string key) =>
        {
            request.HttpContext.Response.Headers.CacheControl = "no-store";
            SetETag(request, await store.ETagAsync(key));
            return await store.GetAsync(key) is { } view ? Results.Ok(view) : NotFound(request, "Setting not found.");
        })
        .WithName("GetSetting").WithSummary("Get one setting with both languages")
        .WithDescription("The `ETag` is the version to send back as `If-Match`. Secret values are returned here, to settings.view holders only.")
        .WithTags(Tag).RequirePermission(Permissions.SettingsView);

        app.MapPut($"{Admin}/settings/{{key}}", async (HttpRequest request, SettingsStore store, string key, SettingWrite payload) =>
        {
            var result = await store.SetAsync(key, payload, request.Headers.IfMatch.ToArray()!);
            SetETag(request, result.ETag);
            return result.Outcome is UpsertOutcome.Created or UpsertOutcome.Updated ? Results.Ok(result.Value) : Failure(request, result.Outcome, result.Error);
        })
        .WithName("SetSetting").WithSummary("Create or replace a setting")
        .WithDescription("Catalog keys: navigation.main, navigation.footer, contact, social, footer.text, homepage.sections, maintenance; plus custom.<name> (one bilingual text, optionally secret). Every visible text needs both bn and en or the save is a 400. Honours `If-Match`.")
        .WithTags(Tag).EvictsContentCache().RequirePermission(Permissions.SettingsManage);

        app.MapDelete($"{Admin}/settings/{{key}}", async (HttpRequest request, SettingsStore store, string key) =>
            await store.DeleteAsync(key) ? Results.NoContent() : NotFound(request, "Setting not found."))
        .WithName("DeleteSetting").WithSummary("Delete a setting (the frontend falls back to its static value)")
        .WithTags(Tag).EvictsContentCache().RequirePermission(Permissions.SettingsManage);

        app.MapGet($"{Admin}/flags", async (HttpRequest request, SettingsStore store) =>
        {
            request.HttpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { items = await store.FlagsAsync() });
        })
        .WithName("ListFlags").WithSummary("List runtime feature flags").WithTags(Tag).RequirePermission(Permissions.SettingsView);

        app.MapPut($"{Admin}/flags/{{key}}", async (HttpRequest request, SettingsStore store, string key, FlagWrite payload) =>
        {
            var result = await store.SetFlagAsync(key, payload);
            return result.Outcome is UpsertOutcome.Created or UpsertOutcome.Updated ? Results.Ok(result.Value) : Failure(request, result.Outcome, result.Error);
        })
        .WithName("SetFlag").WithSummary("Create or change a runtime feature flag")
        .WithDescription("Takes effect for the public site as soon as the cache is evicted (immediately). Runtime flags are separate from the frontend's build-time VITE_ENABLE_* capability flags.")
        .WithTags(Tag).EvictsContentCache().RequirePermission(Permissions.SettingsManage);

        app.MapDelete($"{Admin}/flags/{{key}}", async (HttpRequest request, SettingsStore store, string key) =>
            await store.DeleteFlagAsync(key) ? Results.NoContent() : NotFound(request, "Flag not found."))
        .WithName("DeleteFlag").WithSummary("Delete a runtime feature flag").WithTags(Tag).EvictsContentCache().RequirePermission(Permissions.SettingsManage);
    }
}
