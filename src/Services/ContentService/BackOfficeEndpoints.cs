using System.Security.Claims;
using ContentService.Data;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.Extensions.Caching.Memory;
using static ContentService.ContentHttp;

namespace ContentService;

public sealed record BulkItem(string Type, string Slug);

public sealed record BulkRequest(string Action, List<BulkItem>? Items, string? Tag, DateTimeOffset? PublishAt);

public sealed record BulkItemResult(string Type, string Slug, string Status, string? Code, string? Message);

public sealed record BulkResponse(string Action, int Total, int Succeeded, int Failed, List<BulkItemResult> Results);

/// <summary>Dashboard, global search and bulk actions for the back office (BE-038). See docs/BACKOFFICE_APIS.md.</summary>
public static class BackOfficeEndpoints
{
    private const string Admin = "/api/v1/admin/content";
    private const string Tag = "Admin — Back office";
    public const int MaxBulkItems = 100;

    public static void MapBackOffice(this WebApplication app)
    {
        app.MapGet($"{Admin}/dashboard", async (HttpRequest request, ClaimsPrincipal user, BackOfficeStore store, IMemoryCache cache, IConfiguration configuration, TimeProvider time) =>
        {
            var activity = user.HasPermission(Permissions.AuditView);
            var seconds = configuration.GetValue("BackOffice:DashboardCacheSeconds", 30);
            // Activity is permission-gated, so the cache is kept per variant: a viewer without audit.view can never be handed a cached copy that has it.
            var summary = seconds <= 0
                ? await store.DashboardAsync(activity, time.GetUtcNow())
                : await cache.GetOrCreateAsync($"dashboard:{activity}", async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(seconds);
                    return await store.DashboardAsync(activity, time.GetUtcNow());
                });
            request.HttpContext.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(summary);
        })
        .WithName("GetDashboard").WithSummary("Back-office summary: what needs attention")
        .WithDescription("Item counts per type and status, the in-review queue, the next scheduled publications, translation gap totals, trash size and (with audit.view) the latest activity. Cached for 30 s (`BackOffice:DashboardCacheSeconds`), so it is cheap on every page view. Submissions and media widgets arrive with their modules.")
        .WithTags(Tag).RequirePermission(Permissions.DashboardView);

        app.MapGet($"{Admin}/search", async (HttpRequest request, ClaimsPrincipal user, BackOfficeStore store, string? q, int? limit) =>
        {
            var term = q?.Trim() ?? string.Empty;
            var size = limit.GetValueOrDefault(20);
            if (term.Length < 2 || term.Length > 100 || size is < 1 or > 50)
            {
                return Invalid(request, "q must be 2-100 characters and limit 1-50.");
            }

            request.HttpContext.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(await store.SearchAsync(term, user.HasPermission(Permissions.ContentView), user.HasPermission(Permissions.SettingsView), size));
        })
        .WithName("GlobalSearch").WithSummary("Search items, tags, redirects and settings")
        .WithDescription("Substring match on slugs, titles and summaries in any language. Only kinds the caller may read are searched (`searched` lists them); the rest are omitted, so a result never reveals a record the caller cannot open.")
        .WithTags(Tag).RequirePermission(Permissions.DashboardView);

        app.MapPost($"{Admin}/bulk", async (HttpRequest request, ClaimsPrincipal user, IServiceScopeFactory scopes, BulkRequest payload) =>
        {
            var items = payload.Items ?? [];
            if (items.Count is 0 or > MaxBulkItems)
            {
                return Invalid(request, $"Send 1-{MaxBulkItems} items.");
            }

            var canPublish = user.HasPermission(Permissions.ContentPublish);
            var ifMatch = Array.Empty<string>();
            var results = new List<BulkItemResult>();
            foreach (var item in items.DistinctBy(i => (i.Type, i.Slug)))
            {
                // A scope per item: a failed save cannot leave tracked changes that poison the next item.
                using var scope = scopes.CreateScope();
                var provider = scope.ServiceProvider;
                results.Add(await RunAsync(provider, user, payload, item, canPublish));
            }

            var failed = results.Count(r => r.Status == "failed");
            return Results.Ok(new BulkResponse(payload.Action, results.Count, results.Count - failed, failed, results));
        })
        .WithName("BulkAction").WithSummary("Apply one action to up to 100 items")
        .WithDescription("Actions: `workflow:draft|in_review|scheduled|published|archived` (scheduled needs `publishAt`), `delete`, `restore`, `tag-add`, `tag-remove` (with `tag`). Always 200: read `results` for each item's `ok` or `failed` with a code. Each item is checked against the caller's permissions and audited like a single change, and a failure never stops the rest.")
        .WithTags(Tag).EvictsContentCache().RequirePermission(Permissions.ContentView);
    }

    private static async Task<BulkItemResult> RunAsync(IServiceProvider services, ClaimsPrincipal user, BulkRequest request, BulkItem item, bool canPublish)
    {
        BulkItemResult Ok() => new(item.Type, item.Slug, "ok", null, null);
        BulkItemResult Fail(string code, string message) => new(item.Type, item.Slug, "failed", code, message);
        BulkItemResult FromOutcome(UpsertOutcome outcome, string? error) => outcome switch
        {
            UpsertOutcome.NotFound => Fail("NOT_FOUND", "Item not found."),
            UpsertOutcome.Forbidden => Fail("FORBIDDEN", error ?? "Not allowed."),
            UpsertOutcome.Conflict => Fail("CONFLICT", error ?? "The item was changed by someone else."),
            UpsertOutcome.Invalid => Fail("INVALID", error ?? "Invalid."),
            _ => Ok()
        };

        if (ContentKinds.ByType(item.Type) is null)
        {
            return Fail("INVALID", "Unknown content type.");
        }

        var action = request.Action ?? string.Empty;
        var required = action switch
        {
            "delete" => Permissions.ContentDelete,
            "restore" or "tag-add" or "tag-remove" => Permissions.ContentUpdate,
            _ when action.StartsWith("workflow:", StringComparison.Ordinal) => Permissions.ContentUpdate,
            _ => null
        };
        if (required is null)
        {
            return Fail("INVALID", $"Unknown action '{action}'.");
        }

        if (!user.HasPermission(required))
        {
            return Fail("FORBIDDEN", $"The {required} permission is needed for {action}.");
        }

        try
        {
            var store = services.GetRequiredService<GenericContentStore>();
            switch (action)
            {
                case "delete":
                    return await store.DeleteAsync(item.Type, item.Slug) ? Ok() : Fail("NOT_FOUND", "Item not found.");
                case "restore":
                    var restored = await store.UndeleteAsync(item.Type, item.Slug);
                    return FromOutcome(restored.Outcome == UpsertOutcome.Updated ? UpsertOutcome.Created : restored.Outcome, restored.Error);
                case "tag-add" or "tag-remove":
                    if (string.IsNullOrWhiteSpace(request.Tag))
                    {
                        return Fail("INVALID", "tag is required.");
                    }

                    var current = await store.AdminItemAsync(item.Type, item.Slug);
                    if (current is null)
                    {
                        return Fail("NOT_FOUND", "Item not found.");
                    }

                    var (resolved, tagError) = await services.GetRequiredService<TaxonomyStore>().ResolveAsync([request.Tag]);
                    if (tagError is not null)
                    {
                        return Fail("INVALID", tagError);
                    }

                    var slug = resolved[0].Slug;
                    var tags = action == "tag-add" ? [.. current.Tags.Append(slug).Distinct()] : current.Tags.Where(t => t != slug).ToList();
                    var tagged = await store.SetTagsAsync(item.Type, item.Slug, tags);
                    return FromOutcome(tagged.Outcome, tagged.Error);
                default:
                    var moved = await services.GetRequiredService<EditorialWorkflow>().TransitionAsync(item.Type, item.Slug, action["workflow:".Length..], request.PublishAt, canPublish, null);
                    return FromOutcome(moved.Outcome, moved.Error);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail("ERROR", "The item could not be changed; try it on its own.");
        }
    }
}
