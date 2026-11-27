using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

public static class ConsentBases
{
    public const string Written = "written";
    public const string RecordedVerbal = "recorded-verbal";
    public const string GuardianWritten = "guardian-written";
    public const string Agreement = "agreement";

    public static readonly string[] All = [Written, RecordedVerbal, GuardianWritten, Agreement];
}

/// <summary>Evidence that the person or organisation behind a testimonial, success story or partner listing agreed to appear. Rows are never edited except to withdraw.</summary>
public sealed class ConsentRecordEntity
{
    public string Id { get; set; } = string.Empty;
    public string ContentItemId { get; set; } = string.Empty;

    /// <summary>Who agreed: a person, or the organisation for a partner.</summary>
    public string SubjectName { get; set; } = string.Empty;

    public bool IsMinor { get; set; }
    public string GuardianName { get; set; } = string.Empty;
    public string Basis { get; set; } = string.Empty;

    /// <summary>What exactly was agreed: the words, the name display, a photo, where it may appear.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>Where the evidence lives (signed form reference, recording id). Never the evidence itself.</summary>
    public string Evidence { get; set; } = string.Empty;

    public DateTimeOffset? ExpiresAt { get; set; }
    public string RecordedBy { get; set; } = string.Empty;
    public DateTimeOffset RecordedAt { get; set; }
    public DateTimeOffset? WithdrawnAt { get; set; }
    public string WithdrawnBy { get; set; } = string.Empty;
    public string WithdrawalNote { get; set; } = string.Empty;
}

public sealed record ContentConsentRequest(string? SubjectName, bool? IsMinor, string? GuardianName, string? Basis, string? Scope, string? Evidence, DateTimeOffset? ExpiresAt);
public sealed record ContentConsentView(string Id, string SubjectName, bool IsMinor, string? GuardianName, string Basis, string Scope, string Evidence, DateTimeOffset? ExpiresAt, string RecordedBy, DateTimeOffset RecordedAt, DateTimeOffset? WithdrawnAt, string? WithdrawnBy, string? WithdrawalNote, bool Active);
public sealed record ContentWithdrawRequest(string? Note);
public sealed record ContentWithdrawResult(ContentConsentView Consent, bool Unpublished);
public sealed record ContentWithdrawnPath(string Path, DateTimeOffset WithdrawnAt);

/// <summary>
/// Consent for content that quotes or names real people and organisations. Nothing of these types goes live (publish, schedule, create-as-published) unless the item has an
/// active consent record; a withdrawal unpublishes the item at once and evicts the public cache. A minor's consent needs a guardian and, until the organisation has approved its
/// policy for beneficiary testimonials (`Consent:BeneficiaryPolicyApproved`), blocks publication altogether.
/// </summary>
public sealed class ConsentStore(ContentDbContext db, TimeProvider time, RevisionStore revisions, IConfiguration configuration)
{
    public static readonly string[] RequiredTypes = [ContentTypes.Testimonial, ContentTypes.SuccessStory, ContentTypes.Partner];

    public static bool Requires(string type) => RequiredTypes.Contains(type);

    /// <summary>The reason this item may not go live, or null. `itemId` is null for an item that does not exist yet, which can never have consent.</summary>
    public async Task<string?> PublishBlockAsync(string type, string? itemId)
    {
        if (!Requires(type))
        {
            return null;
        }

        var active = itemId is null ? [] : await ActiveAsync(itemId);
        if (active.Count == 0)
        {
            return "No active consent is recorded for this item. Create it as a draft, record consent (POST .../consents), then publish.";
        }

        if (active.All(c => c.IsMinor) && !configuration.GetValue<bool>("Consent:BeneficiaryPolicyApproved"))
        {
            return "The only consent on file is for a minor, and the beneficiary testimonial policy has not been approved (Consent:BeneficiaryPolicyApproved), so this cannot be published yet.";
        }

        return null;
    }

    public async Task<(ContentConsentView? View, string? Code, string? Error)> RecordAsync(string type, string slug, ContentConsentRequest request, string author)
    {
        if (!Requires(type))
        {
            return (null, "VALIDATION_ERROR", "Consent is only tracked for testimonials, success stories and partners.");
        }

        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug && i.DeletedAt == null);
        if (item is null)
        {
            return (null, "NOT_FOUND", "Item not found.");
        }

        var error = Validate(request);
        if (error is not null)
        {
            return (null, "VALIDATION_ERROR", error);
        }

        var entity = new ConsentRecordEntity
        {
            Id = $"con_{Guid.NewGuid():N}",
            ContentItemId = item.Id,
            SubjectName = request.SubjectName!.Trim(),
            IsMinor = request.IsMinor == true,
            GuardianName = request.GuardianName?.Trim() ?? string.Empty,
            Basis = request.Basis!.Trim().ToLowerInvariant(),
            Scope = request.Scope!.Trim(),
            Evidence = request.Evidence!.Trim(),
            ExpiresAt = request.ExpiresAt,
            RecordedBy = author,
            RecordedAt = time.GetUtcNow()
        };
        db.Set<ConsentRecordEntity>().Add(entity);
        await db.SaveChangesAsync();
        return (View(entity), null, null);
    }

    public async Task<List<ContentConsentView>?> ListAsync(string type, string slug)
    {
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Type == type && i.Slug == slug && i.DeletedAt == null);
        if (item is null)
        {
            return null;
        }

        return [.. (await db.Set<ConsentRecordEntity>().AsNoTracking().Where(c => c.ContentItemId == item.Id).ToListAsync()).OrderBy(c => c.RecordedAt).Select(View)];
    }

    /// <summary>Withdraws a consent. If it was the last active one and the item is live or scheduled, the item goes back to draft in the same save.</summary>
    public async Task<(ContentWithdrawResult? Result, string? Code, string? Error)> WithdrawAsync(string consentId, string? note, string author)
    {
        var consent = await db.Set<ConsentRecordEntity>().FirstOrDefaultAsync(c => c.Id == consentId);
        if (consent is null)
        {
            return (null, "NOT_FOUND", "Consent record not found.");
        }

        if (consent.WithdrawnAt is null)
        {
            consent.WithdrawnAt = time.GetUtcNow();
            consent.WithdrawnBy = author;
            consent.WithdrawalNote = note?.Trim() ?? string.Empty;
        }

        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == consent.ContentItemId);
        var unpublished = false;
        if (item is not null && item.DeletedAt is null && (StatusRules.IsLive(item.Status) || item.Status == StatusRules.Scheduled))
        {
            var others = await db.Set<ConsentRecordEntity>().Where(c => c.ContentItemId == item.Id && c.Id != consent.Id).ToListAsync();
            var now = time.GetUtcNow();
            if (!others.Any(c => IsActive(c, now)))
            {
                item.Status = StatusRules.Draft;
                item.ScheduledPublishAt = null;
                item.UpdatedAt = new DateTimeOffset(now.UtcTicks / 10 * 10, TimeSpan.Zero);
                unpublished = true;
            }
        }

        await db.SaveChangesAsync();
        if (unpublished && item is not null)
        {
            await revisions.RecordAsync(item.Id, "consent-withdrawn");
        }

        return (new ContentWithdrawResult(View(consent), unpublished), null, null);
    }

    /// <summary>Paths withdrawn since `since`, for the frontend to purge prerendered pages and CDN copies. No personal data.</summary>
    public async Task<List<ContentWithdrawnPath>> WithdrawnSinceAsync(DateTimeOffset since)
    {
        var rows = (await db.Set<ConsentRecordEntity>().AsNoTracking().Where(c => c.WithdrawnAt != null).ToListAsync()).Where(c => c.WithdrawnAt >= since).ToList();
        var ids = rows.Select(r => r.ContentItemId).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        return [.. rows.Where(r => items.ContainsKey(r.ContentItemId)).OrderBy(r => r.WithdrawnAt)
            .Select(r => new ContentWithdrawnPath(ContentKinds.PublicPath(items[r.ContentItemId].Type, items[r.ContentItemId].Slug), r.WithdrawnAt!.Value))];
    }

    private async Task<List<ConsentRecordEntity>> ActiveAsync(string itemId)
    {
        var now = time.GetUtcNow();
        return [.. (await db.Set<ConsentRecordEntity>().AsNoTracking().Where(c => c.ContentItemId == itemId).ToListAsync()).Where(c => IsActive(c, now))];
    }

    private static bool IsActive(ConsentRecordEntity c, DateTimeOffset now) => c.WithdrawnAt is null && (c.ExpiresAt is null || c.ExpiresAt > now);

    private static string? Validate(ContentConsentRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.SubjectName) || r.SubjectName.Length > 200)
        {
            return "subjectName is required (who agreed; max 200 characters).";
        }

        if (string.IsNullOrWhiteSpace(r.Basis) || !ConsentBases.All.Contains(r.Basis.Trim().ToLowerInvariant()))
        {
            return $"basis must be one of: {string.Join(", ", ConsentBases.All)}.";
        }

        if (string.IsNullOrWhiteSpace(r.Scope) || r.Scope.Length > 1000)
        {
            return "scope is required: what exactly was agreed (max 1000 characters).";
        }

        if (string.IsNullOrWhiteSpace(r.Evidence) || r.Evidence.Length > 500)
        {
            return "evidence is required: where the signed form or recording is kept (max 500 characters).";
        }

        if (r.IsMinor == true && (!string.Equals(r.Basis.Trim(), ConsentBases.GuardianWritten, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(r.GuardianName)))
        {
            return "A minor's consent needs basis guardian-written and the guardian's name.";
        }

        return null;
    }

    private static ContentConsentView View(ConsentRecordEntity c) =>
        new(c.Id, c.SubjectName, c.IsMinor, c.GuardianName.Length == 0 ? null : c.GuardianName, c.Basis, c.Scope, c.Evidence, c.ExpiresAt, c.RecordedBy, c.RecordedAt, c.WithdrawnAt,
            c.WithdrawnBy.Length == 0 ? null : c.WithdrawnBy, c.WithdrawalNote.Length == 0 ? null : c.WithdrawalNote, c.WithdrawnAt is null && (c.ExpiresAt is null || c.ExpiresAt > DateTimeOffset.UtcNow));
}
