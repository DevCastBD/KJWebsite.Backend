using ContentService.Data;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using static ContentService.ContentHttp;

namespace ContentService;

/// <summary>Consent records for testimonials, success stories and partners, partner registry details, and the withdrawal feed.</summary>
public static class ConsentEndpoints
{
    private const string Public = "/api/v1/content";
    private const string Admin = "/api/v1/admin/content";
    private const string Tag = "Admin — Consent";

    public static void MapConsent(this WebApplication app)
    {
        app.MapGet($"{Admin}/items/{{type}}/{{slug}}/consents", async (HttpRequest request, ConsentStore store, string type, string slug) =>
            await store.ListAsync(type, slug) is { } rows ? Results.Ok(rows) : NotFound(request, "Item not found."))
        .WithName("ListItemConsents").WithSummary("Consent records of an item, oldest first (contains personal data)")
        .WithTags(Tag).RequirePermission(Permissions.ContentUpdate);

        app.MapPost($"{Admin}/items/{{type}}/{{slug}}/consents", async (HttpContext context, ConsentStore store, string type, string slug, ContentConsentRequest payload) =>
        {
            var (view, code, error) = await store.RecordAsync(type, slug, payload, context.User.FindFirst(KjClaims.Subject)?.Value ?? "unknown");
            return view is not null
                ? Results.Created($"{Admin}/items/{type}/{slug}/consents", view)
                : ApiError.Create(context, code == "NOT_FOUND" ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest, code ?? "VALIDATION_ERROR", error ?? "Invalid request.");
        })
        .WithName("RecordItemConsent").WithSummary("Record who agreed, to what, when and on what basis")
        .WithDescription("Required before a testimonial, success story or partner can be published. `basis`: written, recorded-verbal, guardian-written, agreement. `evidence` says where the signed form or recording is kept, never the file itself. A minor needs basis guardian-written and `guardianName`.")
        .WithTags(Tag).RequirePermission(Permissions.ContentUpdate);

        app.MapPost($"{Admin}/consents/{{id}}/withdraw", async (HttpContext context, ConsentStore store, string id, ContentWithdrawRequest payload) =>
        {
            var (result, code, error) = await store.WithdrawAsync(id, payload.Note, context.User.FindFirst(KjClaims.Subject)?.Value ?? "unknown");
            return result is not null ? Results.Ok(result) : ApiError.Create(context, StatusCodes.Status404NotFound, code ?? "NOT_FOUND", error ?? "Not found.");
        })
        .WithName("WithdrawConsent").WithSummary("Withdraw consent: unpublishes the item at once if it was the last active consent")
        .WithDescription("Needs only content.update, deliberately: honouring a withdrawal must not wait for someone with publish rights. The item returns to draft, the public cache is evicted, and its path appears in the withdrawal feed so prerendered copies can be purged.")
        .WithTags(Tag).EvictsContentCache().RequirePermission(Permissions.ContentUpdate);

        app.MapGet($"{Public}/consent-withdrawals", async (HttpContext context, ConsentStore store, DateTimeOffset? since) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await store.WithdrawnSinceAsync(since ?? DateTimeOffset.UtcNow.AddDays(-7)));
        })
        .WithName("ListConsentWithdrawals").WithSummary("Public paths withdrawn since a time (default 7 days), for purging prerendered pages and CDN copies")
        .WithDescription("Only paths and times; no names or reasons. Poll it from the frontend build or a purge job.")
        .WithTags("Content — Consent").AllowAnonymous();

        app.MapGet($"{Admin}/partners/{{slug}}/details", async (HttpRequest request, PartnerStore store, string slug) =>
        {
            var view = await store.AdminAsync(slug);
            if (view is null)
            {
                return NotFound(request, "The partner has no registry details yet, or does not exist.");
            }

            SetETag(request, await store.ETagAsync(slug));
            request.HttpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(view);
        })
        .WithName("GetAdminPartnerDetails").WithSummary("Partner tier, website, logo and logo rights")
        .WithTags("Admin — Content").RequirePermission(Permissions.ContentView);

        app.MapPut($"{Admin}/partners/{{slug}}/details", async (HttpRequest request, PartnerStore store, string slug, ContentPartnerDetailRequest payload) =>
        {
            var result = await store.PutAsync(slug, payload, request.Headers.IfMatch.ToArray()!);
            if (result.Outcome is UpsertOutcome.Updated or UpsertOutcome.Created)
            {
                SetETag(request, result.ETag);
                return Results.Ok(result.Value);
            }

            return Failure(request, result);
        })
        .WithName("PutPartnerDetails").WithSummary("Set a partner's tier, website, logo and how we may use the logo")
        .WithDescription("`logoRights`: unknown, granted, restricted (needs `logoConditions`) or withdrawn. Granted and restricted need `logoEvidence`. A logo URL is stored only while rights are granted or restricted, and the public API shows it only then.")
        .WithTags("Admin — Content").EvictsContentCache().RequirePermission(Permissions.ContentUpdate);

        app.MapGet($"{Public}/partners/{{slug}}/details", async (HttpRequest request, PartnerStore store, string slug) =>
        {
            var view = await store.PublicAsync(slug);
            return view is null ? NotFound(request, "Partner not found.") : Cached(request, view);
        })
        .WithName("GetPartnerDetails").WithSummary("Tier, website and (when rights allow) logo of a published partner")
        .WithTags("Content — Partners").CachePublicContent().AllowAnonymous();

        app.MapGet($"{Public}/partner-registry", async (HttpRequest request, PartnerStore store, string? lang, string? tier, int? limit, int? offset) =>
        {
            var language = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
            return TryReadPaging(limit, offset, out var safeLimit, out var safeOffset)
                ? Cached(request, Page(await store.ListAsync(language, tier), safeLimit, safeOffset, language))
                : Invalid(request, "Invalid paging values.");
        })
        .WithName("ListPartnerRegistry").WithSummary("Published partners with tier, website and logo where rights allow")
        .WithTags("Content — Partners").CachePublicContent().AllowAnonymous();
    }
}
