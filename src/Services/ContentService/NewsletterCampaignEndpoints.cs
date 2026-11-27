using ContentService.Data;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;

namespace ContentService;

/// <summary>Back-office campaign email: bilingual templates and drafts, preview, test send, the confirmed send, cancel and the delivery report.</summary>
public static class NewsletterCampaignEndpoints
{
    private const string Admin = "/api/v1/admin/newsletter";
    private const string Tag = "Newsletter — campaigns";

    public static void MapNewsletterCampaigns(this WebApplication app)
    {
        app.MapGet($"{Admin}/templates", async (NewsletterCampaignStore store) => Results.Ok(await store.ListTemplatesAsync()))
        .WithName("ListNewsletterTemplates").WithSummary("Bilingual campaign templates")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);

        app.MapPost($"{Admin}/templates", async (HttpContext context, NewsletterCampaignStore store, NewsletterTemplateRequest payload) =>
            Respond(context, await store.CreateTemplateAsync(payload), t => Results.Created($"{Admin}/templates/{t.Id}", t)))
        .WithName("CreateNewsletterTemplate").WithSummary("Save a template (bn and en subject and body)")
        .WithDescription("Bodies are light Markdown (paragraphs, `# heading`, `**bold**`, `[text](https://link)`). Variables: `{{email}}`, `{{unsubscribeUrl}}`, `{{preferencesUrl}}`. An unknown variable is rejected here, not rendered to a subscriber. An unsubscribe and preferences footer is always added.")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);

        app.MapGet($"{Admin}/campaigns", async (NewsletterCampaignStore store) => Results.Ok(await store.ListAsync()))
        .WithName("ListNewsletterCampaigns").WithSummary("Campaigns, newest first")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);

        app.MapPost($"{Admin}/campaigns", async (HttpContext context, NewsletterCampaignStore store, NewsletterCampaignRequest payload) =>
            Respond(context, await store.CreateAsync(payload), c => Results.Created($"{Admin}/campaigns/{c.Id}", c)))
        .WithName("CreateNewsletterCampaign").WithSummary("Create a draft, optionally from a template and aimed at a segment")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);

        app.MapGet($"{Admin}/campaigns/{{id}}", async (HttpContext context, NewsletterCampaignStore store, string id) =>
            await store.GetAsync(id) is { } campaign ? Results.Ok(campaign) : ContentHttp.NotFound(context.Request, "Campaign not found."))
        .WithName("GetNewsletterCampaign").WithSummary("One campaign")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);

        app.MapPut($"{Admin}/campaigns/{{id}}", async (HttpContext context, NewsletterCampaignStore store, string id, NewsletterCampaignRequest payload) =>
            Respond(context, await store.UpdateAsync(id, payload), Results.Ok))
        .WithName("UpdateNewsletterCampaign").WithSummary("Edit a draft")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);

        app.MapGet($"{Admin}/campaigns/{{id}}/preview", async (HttpContext context, NewsletterCampaignStore store, string id, string? lang) =>
            Respond(context, await store.PreviewAsync(id, lang), Results.Ok))
        .WithName("PreviewNewsletterCampaign").WithSummary("The exact subject, text and HTML for one language")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);

        app.MapPost($"{Admin}/campaigns/{{id}}/test", async (HttpContext context, NewsletterCampaignStore store, string id, NewsletterTestSendRequest payload) =>
            Respond(context, await store.TestSendAsync(id, payload), r => Results.Accepted(null, r)))
        .WithName("TestSendNewsletterCampaign").WithSummary("Send a test of one language to any address")
        .WithDescription("Needs no confirmation and works on drafts. Rendered by the same code as the real send, so it is the final render.")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);

        app.MapPost($"{Admin}/campaigns/{{id}}/send", async (HttpContext context, NewsletterCampaignStore store, string id, NewsletterSendRequest payload) =>
            Respond(context, await store.SendAsync(id, payload), c => Results.Accepted(null, c)))
        .WithName("SendNewsletterCampaign").WithSummary("Send (or schedule) a campaign: irreversible")
        .WithDescription("`confirmRecipientCount` must equal the audience right now, else 409 `RECIPIENT_COUNT_MISMATCH` with the actual count. Both language versions must be complete, else 400 `MISSING_LANGUAGE`. Each subscriber gets their own language; deliveries are spaced at `Newsletter:SendPerMinute`.")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);

        app.MapPost($"{Admin}/campaigns/{{id}}/cancel", async (HttpContext context, NewsletterCampaignStore store, string id) =>
            Respond(context, await store.CancelAsync(id), Results.Ok))
        .WithName("CancelNewsletterCampaign").WithSummary("Cancel everything not yet delivered")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);

        app.MapGet($"{Admin}/campaigns/{{id}}/report", async (HttpContext context, NewsletterCampaignStore store, string id) =>
            await store.ReportAsync(id) is { } report ? Results.Ok(report) : ContentHttp.NotFound(context.Request, "Campaign not found."))
        .WithName("GetNewsletterCampaignReport").WithSummary("Delivery report")
        .WithDescription("Counts come from the outbox, so they add up to `recipients`. `bounced`, `complaints` and `unsubscribed` describe the recipients afterwards. No open or click tracking is collected.")
        .WithTags(Tag).RequirePermission(Permissions.CommsManage);
    }

    private static IResult Respond<T>(HttpContext context, CampaignResult<T> result, Func<T, IResult> ok)
    {
        if (result.Value is not null)
        {
            return ok(result.Value);
        }

        var status = result.Code switch
        {
            "NOT_FOUND" => StatusCodes.Status404NotFound,
            "CONFLICT" or "RECIPIENT_COUNT_MISMATCH" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return ApiError.Create(context, status, result.Code ?? "VALIDATION_ERROR", result.Error ?? "Invalid request.");
    }
}
