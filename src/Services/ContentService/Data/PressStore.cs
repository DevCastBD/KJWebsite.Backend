using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

public static class ReproductionPermissions
{
    public const string Unknown = "unknown";
    public const string LinkedOnly = "linked-only";
    public const string Pending = "pending";
    public const string Granted = "granted";

    public static readonly string[] All = [Unknown, LinkedOnly, Pending, Granted];
}

/// <summary>The non-text part of a `press` item: who published it, when, where to read it, whether we may host the clipping, and homepage ordering.</summary>
public sealed class PressCoverageEntity
{
    public string ContentItemId { get; set; } = string.Empty;
    public string Outlet { get; set; } = string.Empty;
    public string OutletLogoUrl { get; set; } = string.Empty;
    public DateOnly PublishedOn { get; set; }
    public string ExternalUrl { get; set; } = string.Empty;

    /// <summary>A hosted scan of the clipping (image or PDF). Only allowed, and only shown publicly, when reproduction is `granted`.</summary>
    public string ClippingUrl { get; set; } = string.Empty;

    /// <summary>Language the coverage itself is in (`bn`, `en`), independent of the languages of the item's own text.</summary>
    public string Language { get; set; } = "en";

    public string ReproductionPermission { get; set; } = ReproductionPermissions.Unknown;
    public string PermissionNote { get; set; } = string.Empty;
    public bool Featured { get; set; }
    public int FeaturedOrder { get; set; }
}

public sealed record PressCoverageRequest(string? Outlet, string? OutletLogoUrl, DateOnly? PublishedOn, string? ExternalUrl, string? ClippingUrl, string? Language,
    string? ReproductionPermission, string? PermissionNote, bool? Featured, int? FeaturedOrder);

public sealed record PressCoverageView(string Slug, string Outlet, string? OutletLogoUrl, DateOnly PublishedOn, string ExternalUrl, string? ClippingUrl, string Language,
    string ReproductionPermission, string? PermissionNote, bool Featured, int FeaturedOrder);

public sealed record PressListItem(string Slug, string Title, string Summary, string Outlet, string? OutletLogoUrl, DateOnly PublishedOn, string ExternalUrl, string? ClippingUrl, string Language, bool Featured, int FeaturedOrder);
public sealed record PressOrderRequest(string[]? Slugs);

public sealed class PressStore(ContentDbContext db, TimeProvider time, RevisionStore revisions)
{
    public async Task<PressCoverageView?> AdminAsync(string slug) => await LoadAsync(slug, publishedOnly: false);

    public async Task<PressCoverageView?> PublicAsync(string slug) => await LoadAsync(slug, publishedOnly: true);

    public async Task<string?> ETagAsync(string slug)
    {
        var updatedAt = await db.Items.AsNoTracking().Where(i => i.Type == ContentTypes.Press && i.Slug == slug && i.DeletedAt == null)
            .Select(i => (DateTimeOffset?)i.UpdatedAt).FirstOrDefaultAsync();
        return updatedAt is null ? null : ContentETag.For(updatedAt.Value);
    }

    public async Task<UpsertResult<PressCoverageView>> PutAsync(string slug, PressCoverageRequest request, IReadOnlyCollection<string>? ifMatch)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Type == ContentTypes.Press && i.Slug == slug && i.DeletedAt == null);
        if (item is null)
        {
            return new UpsertResult<PressCoverageView>(UpsertOutcome.NotFound);
        }

        if (ifMatch is not null && !ContentETag.Satisfies(ifMatch, item.UpdatedAt))
        {
            return new UpsertResult<PressCoverageView>(UpsertOutcome.Conflict);
        }

        var error = Validate(request);
        if (error is not null)
        {
            return new UpsertResult<PressCoverageView>(UpsertOutcome.Invalid, Error: error);
        }

        var detail = await db.Set<PressCoverageEntity>().FirstOrDefaultAsync(p => p.ContentItemId == item.Id);
        if (detail is null)
        {
            detail = new PressCoverageEntity { ContentItemId = item.Id };
            db.Set<PressCoverageEntity>().Add(detail);
        }

        detail.Outlet = request.Outlet!.Trim();
        detail.OutletLogoUrl = request.OutletLogoUrl?.Trim() ?? string.Empty;
        detail.PublishedOn = request.PublishedOn!.Value;
        detail.ExternalUrl = request.ExternalUrl!.Trim();
        detail.ClippingUrl = request.ClippingUrl?.Trim() ?? string.Empty;
        detail.Language = request.Language!.Trim().ToLowerInvariant();
        detail.ReproductionPermission = string.IsNullOrWhiteSpace(request.ReproductionPermission) ? ReproductionPermissions.Unknown : request.ReproductionPermission.Trim().ToLowerInvariant();
        detail.PermissionNote = request.PermissionNote?.Trim() ?? string.Empty;
        detail.Featured = request.Featured ?? false;
        detail.FeaturedOrder = detail.Featured ? request.FeaturedOrder ?? 0 : 0;

        item.UpdatedAt = new DateTimeOffset(time.GetUtcNow().UtcTicks / 10 * 10, TimeSpan.Zero);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return new UpsertResult<PressCoverageView>(UpsertOutcome.Conflict);
        }

        await revisions.RecordAsync(item.Id, "coverage");
        return new UpsertResult<PressCoverageView>(UpsertOutcome.Updated, await LoadAsync(slug, publishedOnly: false), ETag: ContentETag.For(item.UpdatedAt));
    }

    /// <summary>Sets the homepage press-strip order in one call: the listed items become featured, in that order, and every other item is un-featured.</summary>
    public async Task<(bool Ok, string? Error)> SetFeaturedOrderAsync(string[] slugs)
    {
        if (slugs.Length != slugs.Distinct(StringComparer.Ordinal).Count())
        {
            return (false, "slugs must not repeat.");
        }

        var rows = await db.Items.Where(i => i.Type == ContentTypes.Press && i.DeletedAt == null).ToListAsync();
        var coverage = await db.Set<PressCoverageEntity>().ToDictionaryAsync(p => p.ContentItemId);
        var missing = slugs.Where(s => !rows.Any(r => r.Slug == s && coverage.ContainsKey(r.Id))).ToArray();
        if (missing.Length > 0)
        {
            return (false, $"No coverage details for: {string.Join(", ", missing)}.");
        }

        foreach (var row in rows.Where(r => coverage.ContainsKey(r.Id)))
        {
            var position = Array.IndexOf(slugs, row.Slug);
            var detail = coverage[row.Id];
            var featured = position >= 0;
            if (detail.Featured != featured || (featured && detail.FeaturedOrder != position))
            {
                detail.Featured = featured;
                detail.FeaturedOrder = featured ? position : 0;
                row.UpdatedAt = new DateTimeOffset(time.GetUtcNow().UtcTicks / 10 * 10, TimeSpan.Zero);
            }
        }

        await db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>Published coverage, newest first, or in featured order. Items with no coverage details yet, or without a translation in `lang`, are left out.</summary>
    public async Task<List<PressListItem>> ListAsync(string lang, bool featuredOnly, string? coverageLanguage)
    {
        var items = await db.Items.AsNoTracking().Include(i => i.Translations)
            .Where(i => i.Type == ContentTypes.Press && i.DeletedAt == null && i.Status == StatusRules.Published).ToListAsync();
        var coverage = await db.Set<PressCoverageEntity>().AsNoTracking().ToDictionaryAsync(p => p.ContentItemId);
        var rows = items.Where(i => coverage.ContainsKey(i.Id))
            .Select(i => (Item: i, Detail: coverage[i.Id], Translation: i.Translations.FirstOrDefault(t => t.Lang == lang)))
            .Where(r => r.Translation is not null && (!featuredOnly || r.Detail.Featured) && (coverageLanguage is null || r.Detail.Language == coverageLanguage));
        rows = featuredOnly
            ? rows.OrderBy(r => r.Detail.FeaturedOrder).ThenByDescending(r => r.Detail.PublishedOn).ThenBy(r => r.Item.Slug, StringComparer.Ordinal)
            : rows.OrderByDescending(r => r.Detail.PublishedOn).ThenBy(r => r.Item.Slug, StringComparer.Ordinal);
        return [.. rows.Select(r => new PressListItem(r.Item.Slug, r.Translation!.Title, r.Translation.Summary, r.Detail.Outlet, Blank(r.Detail.OutletLogoUrl), r.Detail.PublishedOn, r.Detail.ExternalUrl,
            Hosted(r.Detail), r.Detail.Language, r.Detail.Featured, r.Detail.FeaturedOrder))];
    }

    private async Task<PressCoverageView?> LoadAsync(string slug, bool publishedOnly)
    {
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Type == ContentTypes.Press && i.Slug == slug && i.DeletedAt == null && (!publishedOnly || i.Status == StatusRules.Published));
        var detail = item is null ? null : await db.Set<PressCoverageEntity>().AsNoTracking().FirstOrDefaultAsync(p => p.ContentItemId == item.Id);
        if (item is null || detail is null)
        {
            return null;
        }

        return new PressCoverageView(item.Slug, detail.Outlet, Blank(detail.OutletLogoUrl), detail.PublishedOn, detail.ExternalUrl, publishedOnly ? Hosted(detail) : Blank(detail.ClippingUrl), detail.Language,
            detail.ReproductionPermission, publishedOnly ? null : Blank(detail.PermissionNote), detail.Featured, detail.FeaturedOrder);
    }

    /// <summary>The public site may show a hosted clipping only when reproduction is granted.</summary>
    private static string? Hosted(PressCoverageEntity detail) => detail.ReproductionPermission == ReproductionPermissions.Granted ? Blank(detail.ClippingUrl) : null;

    private static string? Blank(string value) => value.Length == 0 ? null : value;

    private static string? Validate(PressCoverageRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Outlet) || r.Outlet.Length > 200)
        {
            return "outlet is required (max 200 characters).";
        }

        if (r.PublishedOn is null)
        {
            return "publishedOn (a date) is required.";
        }

        if (!IsHttps(r.ExternalUrl))
        {
            return "externalUrl must be an absolute https URL.";
        }

        if (!string.IsNullOrWhiteSpace(r.OutletLogoUrl) && !IsHttps(r.OutletLogoUrl) && !r.OutletLogoUrl.StartsWith('/'))
        {
            return "outletLogoUrl must be an https URL or a site path.";
        }

        if (string.IsNullOrWhiteSpace(r.Language) || r.Language.Trim().Length is < 2 or > 8)
        {
            return "language is required (for example bn or en).";
        }

        var permission = string.IsNullOrWhiteSpace(r.ReproductionPermission) ? ReproductionPermissions.Unknown : r.ReproductionPermission.Trim().ToLowerInvariant();
        if (!ReproductionPermissions.All.Contains(permission))
        {
            return $"reproductionPermission must be one of: {string.Join(", ", ReproductionPermissions.All)}.";
        }

        if (!string.IsNullOrWhiteSpace(r.ClippingUrl))
        {
            if (!IsHttps(r.ClippingUrl) && !r.ClippingUrl.StartsWith('/'))
            {
                return "clippingUrl must be an https URL or a site path.";
            }

            if (permission != ReproductionPermissions.Granted)
            {
                return "A clipping can only be hosted when reproductionPermission is granted; record the permission first, or link to the original instead.";
            }

            if (string.IsNullOrWhiteSpace(r.PermissionNote))
            {
                return "permissionNote is required when reproduction is granted: who agreed, and how.";
            }
        }

        return r.FeaturedOrder is < 0 ? "featuredOrder cannot be negative." : null;
    }

    private static bool IsHttps(string? value) => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && value!.Length <= 1000;
}
