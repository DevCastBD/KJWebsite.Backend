using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

/// <summary>
/// The matching form of a tag name. Bengali text arrives in several equivalent encodings (precomposed vs. decomposed য়/ড়/ঢ়, stray
/// joiners, Bengali vs. ASCII digits), so names are compared after this normalisation or the bn and en taxonomies drift apart.
/// </summary>
public static partial class TagNormalizer
{
    public static string Normalize(string name)
    {
        var composed = name.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(composed.Length);
        foreach (var ch in composed)
        {
            if (ch is '​' or '‌' or '‍' or '﻿')
            {
                continue;
            }

            sb.Append(ch is >= '০' and <= '৯' ? (char)('0' + (ch - '০')) : char.ToLower(ch, CultureInfo.InvariantCulture));
        }

        return Whitespace().Replace(sb.ToString(), " ").Trim().TrimStart('#').Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public static partial class SlugRules
{
    public static bool IsValid(string? slug) => !string.IsNullOrWhiteSpace(slug) && slug.Length <= 120 && Pattern().IsMatch(slug);

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex Pattern();
}

/// <summary>Tags: one row per concept, with a name per language. Items refer to tags by slug or by any language's name.</summary>
public sealed class TaxonomyStore(ContentDbContext db, TimeProvider time)
{
    private static readonly string[] Languages = ["en", "bn"];

    public async Task<List<TagView>> ListAsync() =>
        [.. (await db.Tags.AsNoTracking().Include(t => t.Names).ToListAsync()).OrderBy(t => t.Slug, StringComparer.Ordinal).Select(ToView)];

    public async Task<UpsertResult<TagView>> CreateAsync(TagUpsertRequest request)
    {
        if (Validate(request) is { } error)
        {
            return new UpsertResult<TagView>(UpsertOutcome.Invalid, Error: error);
        }

        if (await db.Tags.AnyAsync(t => t.Slug == request.Slug))
        {
            return new UpsertResult<TagView>(UpsertOutcome.Conflict, Error: $"Tag '{request.Slug}' already exists.");
        }

        var tag = new TagEntity { Id = $"tag_{Guid.NewGuid():N}", Slug = request.Slug, CreatedAt = time.GetUtcNow() };
        return await SaveNamesAsync(tag, request.Names, isNew: true);
    }

    public async Task<UpsertResult<TagView>> ReplaceAsync(string slug, TagUpsertRequest request)
    {
        if (Validate(request with { Slug = slug }) is { } error)
        {
            return new UpsertResult<TagView>(UpsertOutcome.Invalid, Error: error);
        }

        var tag = await db.Tags.Include(t => t.Names).FirstOrDefaultAsync(t => t.Slug == slug);
        return tag is null ? new UpsertResult<TagView>(UpsertOutcome.NotFound) : await SaveNamesAsync(tag, request.Names, isNew: false);
    }

    public async Task<bool> DeleteAsync(string slug)
    {
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Slug == slug);
        if (tag is null)
        {
            return false;
        }

        db.Tags.Remove(tag);
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Finds each reference by slug, then by normalised name in any language. Unknown references are an error, never auto-created.</summary>
    public async Task<(List<TagEntity> Tags, string? Error)> ResolveAsync(IEnumerable<string> references)
    {
        var found = new List<TagEntity>();
        foreach (var reference in references.Select(r => r?.Trim() ?? string.Empty).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal))
        {
            var tag = await db.Tags.FirstOrDefaultAsync(t => t.Slug == reference);
            if (tag is null)
            {
                var normalized = TagNormalizer.Normalize(reference);
                var tagId = await db.TagNames.Where(n => n.NormalizedName == normalized).Select(n => n.TagId).FirstOrDefaultAsync();
                tag = tagId is null ? null : await db.Tags.FirstOrDefaultAsync(t => t.Id == tagId);
            }

            if (tag is null)
            {
                return ([], $"Unknown tag '{reference}'. Create it first under /api/v1/admin/content/tags.");
            }

            if (found.All(f => f.Id != tag.Id))
            {
                found.Add(tag);
            }
        }

        return (found, null);
    }

    private async Task<UpsertResult<TagView>> SaveNamesAsync(TagEntity tag, Dictionary<string, string> names, bool isNew)
    {
        var desired = names.Select(n => (Lang: n.Key, Name: n.Value.Trim(), Normalized: TagNormalizer.Normalize(n.Value))).ToList();
        foreach (var (lang, _, normalized) in desired)
        {
            var clash = await db.TagNames.Where(n => n.Lang == lang && n.NormalizedName == normalized && n.TagId != tag.Id).Select(n => n.TagId).FirstOrDefaultAsync();
            if (clash is not null)
            {
                var existing = await db.Tags.Where(t => t.Id == clash).Select(t => t.Slug).FirstAsync();
                return new UpsertResult<TagView>(UpsertOutcome.Conflict, Error: $"The {lang} name already belongs to tag '{existing}'.");
            }
        }

        if (isNew)
        {
            db.Tags.Add(tag);
        }

        foreach (var gone in tag.Names.Where(n => desired.All(d => d.Lang != n.Lang)).ToList())
        {
            tag.Names.Remove(gone);
            db.TagNames.Remove(gone);
        }

        foreach (var (lang, name, normalized) in desired)
        {
            var current = tag.Names.FirstOrDefault(n => n.Lang == lang);
            if (current is null)
            {
                tag.Names.Add(new TagNameEntity { TagId = tag.Id, Lang = lang, Name = name, NormalizedName = normalized });
            }
            else
            {
                current.Name = name;
                current.NormalizedName = normalized;
            }
        }

        await db.SaveChangesAsync();
        return new UpsertResult<TagView>(isNew ? UpsertOutcome.Created : UpsertOutcome.Updated, ToView(tag));
    }

    private static string? Validate(TagUpsertRequest request)
    {
        if (!SlugRules.IsValid(request.Slug))
        {
            return "slug must be lowercase letters, digits and hyphens (at most 120 characters).";
        }

        if (request.Names is null || request.Names.Count == 0 || request.Names.Any(n => !Languages.Contains(n.Key)))
        {
            return "names needs at least one entry, keyed by en or bn.";
        }

        return request.Names.Values.Any(v => TagNormalizer.Normalize(v ?? string.Empty).Length is 0 or > 200) ? "Every tag name needs 1-200 characters." : null;
    }

    private static TagView ToView(TagEntity tag) => new(tag.Slug, tag.Names.OrderBy(n => n.Lang, StringComparer.Ordinal).ToDictionary(n => n.Lang, n => n.Name));
}
