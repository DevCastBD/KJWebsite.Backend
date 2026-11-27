using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

public static class LogoRights
{
    public const string Unknown = "unknown";
    public const string Granted = "granted";
    public const string Restricted = "restricted";
    public const string Withdrawn = "withdrawn";

    public static readonly string[] All = [Unknown, Granted, Restricted, Withdrawn];
}

/// <summary>A partner's registry details. The logo is another organisation's trademark, so how we may use it is recorded per partner and gates whether it is shown.</summary>
public sealed class PartnerDetailEntity
{
    public string ContentItemId { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
    public string WebsiteUrl { get; set; } = string.Empty;
    public string LogoUrl { get; set; } = string.Empty;
    public string LogoRights { get; set; } = Data.LogoRights.Unknown;

    /// <summary>Placement or tier conditions the partner attached to the logo (internal).</summary>
    public string LogoConditions { get; set; } = string.Empty;

    public string LogoEvidence { get; set; } = string.Empty;
}

public sealed record ContentPartnerDetailRequest(string? Tier, string? WebsiteUrl, string? LogoUrl, string? LogoRights, string? LogoConditions, string? LogoEvidence);
public sealed record ContentPartnerDetailView(string Slug, string? Tier, string? WebsiteUrl, string? LogoUrl, string LogoRights, string? LogoConditions, string? LogoEvidence);
public sealed record ContentPartnerListItem(string Slug, string Name, string? Tier, string? WebsiteUrl, string? LogoUrl);

public sealed class PartnerStore(ContentDbContext db, TimeProvider time, RevisionStore revisions)
{
    public async Task<ContentPartnerDetailView?> AdminAsync(string slug) => await LoadAsync(slug, publishedOnly: false);

    public async Task<ContentPartnerDetailView?> PublicAsync(string slug) => await LoadAsync(slug, publishedOnly: true);

    public async Task<string?> ETagAsync(string slug)
    {
        var updatedAt = await db.Items.AsNoTracking().Where(i => i.Type == ContentTypes.Partner && i.Slug == slug && i.DeletedAt == null)
            .Select(i => (DateTimeOffset?)i.UpdatedAt).FirstOrDefaultAsync();
        return updatedAt is null ? null : ContentETag.For(updatedAt.Value);
    }

    public async Task<UpsertResult<ContentPartnerDetailView>> PutAsync(string slug, ContentPartnerDetailRequest request, IReadOnlyCollection<string>? ifMatch)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Type == ContentTypes.Partner && i.Slug == slug && i.DeletedAt == null);
        if (item is null)
        {
            return new UpsertResult<ContentPartnerDetailView>(UpsertOutcome.NotFound);
        }

        if (ifMatch is not null && !ContentETag.Satisfies(ifMatch, item.UpdatedAt))
        {
            return new UpsertResult<ContentPartnerDetailView>(UpsertOutcome.Conflict);
        }

        var rights = string.IsNullOrWhiteSpace(request.LogoRights) ? LogoRights.Unknown : request.LogoRights.Trim().ToLowerInvariant();
        var error = Validate(request, rights);
        if (error is not null)
        {
            return new UpsertResult<ContentPartnerDetailView>(UpsertOutcome.Invalid, Error: error);
        }

        var detail = await db.Set<PartnerDetailEntity>().FirstOrDefaultAsync(p => p.ContentItemId == item.Id);
        if (detail is null)
        {
            detail = new PartnerDetailEntity { ContentItemId = item.Id };
            db.Set<PartnerDetailEntity>().Add(detail);
        }

        detail.Tier = request.Tier?.Trim() ?? string.Empty;
        detail.WebsiteUrl = request.WebsiteUrl?.Trim() ?? string.Empty;
        detail.LogoUrl = request.LogoUrl?.Trim() ?? string.Empty;
        detail.LogoRights = rights;
        detail.LogoConditions = request.LogoConditions?.Trim() ?? string.Empty;
        detail.LogoEvidence = request.LogoEvidence?.Trim() ?? string.Empty;
        item.UpdatedAt = new DateTimeOffset(time.GetUtcNow().UtcTicks / 10 * 10, TimeSpan.Zero);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return new UpsertResult<ContentPartnerDetailView>(UpsertOutcome.Conflict);
        }

        await revisions.RecordAsync(item.Id, "partner-details");
        return new UpsertResult<ContentPartnerDetailView>(UpsertOutcome.Updated, await LoadAsync(slug, publishedOnly: false), ETag: ContentETag.For(item.UpdatedAt));
    }

    /// <summary>Published partners (which already have consent) in registry order, grouped by tier by the caller if wanted.</summary>
    public async Task<List<ContentPartnerListItem>> ListAsync(string lang, string? tier)
    {
        var items = await db.Items.AsNoTracking().Include(i => i.Translations)
            .Where(i => i.Type == ContentTypes.Partner && i.DeletedAt == null && i.Status == StatusRules.Published).ToListAsync();
        var details = await db.Set<PartnerDetailEntity>().AsNoTracking().ToDictionaryAsync(d => d.ContentItemId);
        return [.. items.OrderBy(i => i.SortOrder).ThenBy(i => i.Slug, StringComparer.Ordinal)
            .Select(i => (Item: i, Detail: details.GetValueOrDefault(i.Id), Translation: i.Translations.FirstOrDefault(t => t.Lang == lang)))
            .Where(r => r.Translation is not null && (tier is null || string.Equals(r.Detail?.Tier, tier, StringComparison.OrdinalIgnoreCase)))
            .Select(r => new ContentPartnerListItem(r.Item.Slug, r.Translation!.Title, Blank(r.Detail?.Tier), Blank(r.Detail?.WebsiteUrl), r.Detail is null ? null : ShownLogo(r.Detail)))];
    }

    private async Task<ContentPartnerDetailView?> LoadAsync(string slug, bool publishedOnly)
    {
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Type == ContentTypes.Partner && i.Slug == slug && i.DeletedAt == null && (!publishedOnly || i.Status == StatusRules.Published));
        var detail = item is null ? null : await db.Set<PartnerDetailEntity>().AsNoTracking().FirstOrDefaultAsync(p => p.ContentItemId == item.Id);
        if (item is null || detail is null)
        {
            return null;
        }

        return publishedOnly
            ? new ContentPartnerDetailView(item.Slug, Blank(detail.Tier), Blank(detail.WebsiteUrl), ShownLogo(detail), detail.LogoRights, null, null)
            : new ContentPartnerDetailView(item.Slug, Blank(detail.Tier), Blank(detail.WebsiteUrl), Blank(detail.LogoUrl), detail.LogoRights, Blank(detail.LogoConditions), Blank(detail.LogoEvidence));
    }

    /// <summary>A logo is shown only while rights are granted or restricted (restricted: the conditions apply, enforced by the frontend placement).</summary>
    private static string? ShownLogo(PartnerDetailEntity d) => d.LogoRights is Data.LogoRights.Granted or Data.LogoRights.Restricted ? Blank(d.LogoUrl) : null;

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? Validate(ContentPartnerDetailRequest r, string rights)
    {
        if (!LogoRights.All.Contains(rights))
        {
            return $"logoRights must be one of: {string.Join(", ", LogoRights.All)}.";
        }

        if (!string.IsNullOrWhiteSpace(r.WebsiteUrl) && !(Uri.TryCreate(r.WebsiteUrl.Trim(), UriKind.Absolute, out var site) && site.Scheme == Uri.UriSchemeHttps))
        {
            return "websiteUrl must be an absolute https URL.";
        }

        if (!string.IsNullOrWhiteSpace(r.LogoUrl) && !r.LogoUrl.StartsWith('/') && !r.LogoUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "logoUrl must be an https URL or a site path.";
        }

        if (!string.IsNullOrWhiteSpace(r.LogoUrl) && rights is LogoRights.Unknown or LogoRights.Withdrawn)
        {
            return "A logo can only be stored once logoRights is granted or restricted.";
        }

        if (rights is LogoRights.Granted or LogoRights.Restricted && string.IsNullOrWhiteSpace(r.LogoEvidence))
        {
            return "logoEvidence is required when logo rights are granted or restricted: where the permission is recorded.";
        }

        return rights == LogoRights.Restricted && string.IsNullOrWhiteSpace(r.LogoConditions) ? "logoConditions is required when logo rights are restricted." : null;
    }
}
