using System.Globalization;
using System.Text;
using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

/// <summary>
/// Splits text into search terms. PostgreSQL's built-in text-search configurations cannot tokenise Bengali, so terms are cut here: text is
/// normalised like tag names (NFC, zero-width joiners removed, Bengali digits to ASCII, lower case), then split on anything that is not a
/// letter, digit or combining mark. Vowel signs and the virama are combining marks, so a conjunct such as "ক্ষ" stays one term, and Bengali
/// and English words in the same text are both kept.
/// </summary>
public static class SearchTokenizer
{
    public const int MaxTermLength = 64;

    public static List<string> Tokens(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var current = new StringBuilder();
        foreach (var ch in TagNormalizer.Normalize(text))
        {
            var category = char.GetUnicodeCategory(ch);
            if (char.IsLetterOrDigit(ch) || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                current.Append(ch);
            }
            else
            {
                Flush(current, tokens);
            }
        }

        Flush(current, tokens);
        return tokens;
    }

    private static void Flush(StringBuilder current, List<string> tokens)
    {
        if (current.Length > 0)
        {
            tokens.Add(current.Length > MaxTermLength ? current.ToString(0, MaxTermLength) : current.ToString());
            current.Clear();
        }
    }
}

public sealed record SearchItem(string Type, string Slug, string Lang, string Title, string Snippet, int Score, string Path);

public sealed record SearchPage(string Q, string Lang, List<SearchItem> Items, int Total, int Limit, int Offset);

/// <summary>The term index behind public search. Rebuilt for an item whenever its content changes; visibility is checked at query time.</summary>
public sealed class SearchIndex(ContentDbContext db)
{
    private const int MaxTermsPerLanguage = 2000;
    public const int MaxQueryTerms = 8;

    public async Task ReindexAsync(string itemId)
    {
        var translations = await db.Translations.AsNoTracking().Where(t => t.ContentItemId == itemId).ToListAsync();
        await db.SearchTerms.Where(t => t.ContentItemId == itemId).ExecuteDeleteAsync();
        var rows = new List<SearchTermEntity>();
        foreach (var translation in translations)
        {
            var weights = new Dictionary<string, int>(StringComparer.Ordinal);
            void Add(string? text, int weight)
            {
                foreach (var token in SearchTokenizer.Tokens(text))
                {
                    if (weights.Count < MaxTermsPerLanguage || weights.ContainsKey(token))
                    {
                        weights[token] = Math.Max(weights.GetValueOrDefault(token), weight);
                    }
                }
            }

            Add(translation.Title, 3);
            Add(translation.Summary, 2);
            Add(translation.Body, 1);
            rows.AddRange(weights.Select(w => new SearchTermEntity { ContentItemId = itemId, Lang = translation.Lang, Term = w.Key, Weight = w.Value }));
        }

        db.SearchTerms.AddRange(rows);
        await db.SaveChangesAsync();
        foreach (var row in rows)
        {
            db.Entry(row).State = EntityState.Detached;
        }
    }

    /// <summary>Indexes every item that has no terms yet (first start after this feature, or after seeding).</summary>
    public async Task EnsureBuiltAsync()
    {
        var indexed = db.SearchTerms.Select(t => t.ContentItemId).Distinct();
        foreach (var id in await db.Items.AsNoTracking().Where(i => !indexed.Contains(i.Id)).Select(i => i.Id).ToListAsync())
        {
            await ReindexAsync(id);
        }
    }

    /// <summary>Published, live items in <paramref name="lang"/> whose terms start with every query term, best title/summary matches first.</summary>
    public async Task<SearchPage> SearchAsync(string q, string lang, string? type, int limit, int offset, CancellationToken ct)
    {
        var tokens = SearchTokenizer.Tokens(q).Distinct(StringComparer.Ordinal).Take(MaxQueryTerms).ToList();
        if (tokens.Count == 0)
        {
            return new SearchPage(q, lang, [], 0, limit, offset);
        }

        Dictionary<string, int>? scores = null;
        foreach (var token in tokens)
        {
            var matches = await db.SearchTerms.AsNoTracking().Where(t => t.Lang == lang && t.Term.StartsWith(token)).Select(t => new { t.ContentItemId, t.Weight }).ToListAsync(ct);
            var best = matches.GroupBy(m => m.ContentItemId).ToDictionary(g => g.Key, g => g.Max(m => m.Weight));
            scores = scores is null ? best : scores.Where(s => best.ContainsKey(s.Key)).ToDictionary(s => s.Key, s => s.Value + best[s.Key]);
            if (scores.Count == 0)
            {
                break;
            }
        }

        var ids = scores!.Keys.ToList();
        var items = ids.Count == 0 ? [] : await db.Items.AsNoTracking().Include(i => i.Translations)
            .Where(i => ids.Contains(i.Id) && i.DeletedAt == null && (i.Status == "published" || i.Status == "active") && (type == null || i.Type == type)).ToListAsync(ct);
        var ranked = items.Select(i => (Item: i, Translation: i.Translations.First(t => t.Lang == lang), Score: scores![i.Id]))
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Item.PublishedAt).ThenBy(x => x.Item.Slug, StringComparer.Ordinal).ToList();
        var page = ranked.Skip(offset).Take(limit).Select(x => new SearchItem(x.Item.Type, x.Item.Slug, lang, x.Translation.Title, Snippet(x.Translation),
            x.Score, ContentKinds.PublicPath(x.Item.Type, x.Item.Slug))).ToList();
        return new SearchPage(q, lang, page, ranked.Count, limit, offset);
    }

    public static string Snippet(ContentTranslationEntity translation)
    {
        var text = string.IsNullOrWhiteSpace(translation.Summary) ? translation.Body : translation.Summary;
        text = text.Replace('\n', ' ').Trim();
        return text.Length <= 160 ? text : text[..160] + "…";
    }
}
