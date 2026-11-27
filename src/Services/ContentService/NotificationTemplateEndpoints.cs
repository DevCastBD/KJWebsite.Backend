using ContentService.Data;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using static ContentService.ContentHttp;

namespace ContentService;

/// <summary>The transactional template catalogue: list, version, render (with an optional send record).</summary>
public static class NotificationTemplateEndpoints
{
    private const string Admin = "/api/v1/admin/notifications";
    private const string Tag = "Notification templates";

    public static void MapNotificationTemplates(this WebApplication app)
    {
        app.MapGet($"{Admin}/catalogue", () => Results.Ok(NotificationCatalogue.Specs))
        .WithName("GetNotificationCatalogue").WithSummary("Every transactional message and the variables it may use")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);

        app.MapGet($"{Admin}/templates", async (NotificationTemplateStore store) => Results.Ok(await store.LatestAsync()))
        .WithName("ListNotificationTemplates").WithSummary("The current (newest) version of each template in each language")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);

        app.MapGet($"{Admin}/templates/{{key}}/{{lang}}/versions", async (HttpContext context, NotificationTemplateStore store, string key, string lang) =>
            await store.VersionsAsync(key, lang) is { } versions ? Results.Ok(versions) : NotFound(context.Request, "Unknown template key."))
        .WithName("ListNotificationTemplateVersions").WithSummary("Every version of one template in one language, newest first")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);

        app.MapPost($"{Admin}/templates/{{key}}/{{lang}}", async (HttpContext context, NotificationTemplateStore store, string key, string lang, NotificationTemplateWrite payload) =>
            Respond(context, await store.AddVersionAsync(key, lang, payload, context.User.FindFirst(KjClaims.Subject)?.Value ?? "unknown"), t => Results.Created($"{Admin}/templates/{key}/{lang}/versions", t)))
        .WithName("AddNotificationTemplateVersion").WithSummary("Save an edit as the next version")
        .WithDescription("Versions are immutable. Only the variables declared for the key (see the catalogue) may be used; the new version is used for every send from now on, while past sends keep the version they used.")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);

        app.MapPost($"{Admin}/templates/{{key}}/render", async (HttpContext context, NotificationTemplateStore store, string key, NotificationRenderRequest payload) =>
            Respond(context, await store.RenderAsync(key, payload), Results.Ok))
        .WithName("RenderNotificationTemplate").WithSummary("Render a template with values (test render, or record a send)")
        .WithDescription("A variable the template uses but `values` lacks fails with 400 `RENDER_ERROR`: nothing renders `{{name}}` to a recipient. `version` pins an older version (default: newest). `record: true` with `toEmail` stores the rendered message and its version as a send record.")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);

        app.MapGet($"{Admin}/sends", async (HttpContext context, NotificationTemplateStore store, string? key, int? limit, int? offset) =>
            TryReadPaging(limit, offset, out var safeLimit, out var safeOffset) ? Results.Ok(await store.SendsAsync(key, safeLimit, safeOffset)) : Invalid(context.Request, "limit must be 1-100 and offset 0 or more."))
        .WithName("ListNotificationSends").WithSummary("Recorded sends with the version and exact text each used")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);
    }

    private static IResult Respond<T>(HttpContext context, NotificationResult<T> result, Func<T, IResult> ok)
    {
        if (result.Value is not null)
        {
            return ok(result.Value);
        }

        var status = result.Code switch
        {
            "NOT_FOUND" => StatusCodes.Status404NotFound,
            "CONFLICT" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return ApiError.Create(context, status, result.Code ?? "VALIDATION_ERROR", result.Error ?? "Invalid request.");
    }
}
