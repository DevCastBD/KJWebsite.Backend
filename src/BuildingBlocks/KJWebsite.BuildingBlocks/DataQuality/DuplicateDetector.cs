using System.Globalization;
using System.Text;

namespace KJWebsite.BuildingBlocks.DataQuality;

/// <summary>What the detector sees of one person record: an id, a name, a hash of the email, a phone number. Nothing else, and none of it leaves the detector.</summary>
public sealed record PersonRecord(string Id, string? Name, string? EmailKey, string? Phone);

/// <summary>Why two records look alike: `kind` is `same_email`, `same_phone` or `same_name`. Values are never repeated, so the evidence itself holds no personal data; the ids lead staff to the records.</summary>
public sealed record DuplicateEvidence(string Kind, string Detail);

public sealed record DuplicateCandidate(string PairKey, string LeftId, string RightId, string Confidence, IReadOnlyList<DuplicateEvidence> Evidence);

/// <summary>
/// Finds record pairs that may describe one person. It only ever proposes: a shared email or phone is `high`, the same full name with nothing else in common is `low`. Merging is never
/// done here (a wrong merge is very hard to undo), and a pair a person has ruled on is kept out by the caller via its <see cref="DuplicateCandidate.PairKey"/>.
/// </summary>
public static class DuplicateDetector
{
    public const string High = "high";
    public const string Low = "low";
    private const int MaxBucket = 25;

    public static string PairKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

    /// <summary>Lower case, letters and Bengali marks only, single spaces. Two or more words are needed before a name counts as evidence at all.</summary>
    public static string? NormaliseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            var category = char.GetUnicodeCategory(c);
            if (char.IsLetter(c) || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != ' ')
            {
                sb.Append(' ');
            }
        }

        var text = sb.ToString().Trim();
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2 ? text : null;
    }

    /// <summary>The last ten digits, so `+8801712345678`, `01712345678` and `1712 345678` meet. Fewer than seven digits is not a number.</summary>
    public static string? NormalisePhone(string? phone)
    {
        if (phone is null)
        {
            return null;
        }

        var digits = new string([.. phone.Where(char.IsAsciiDigit)]);
        return digits.Length < 7 ? null : digits[^Math.Min(10, digits.Length)..];
    }

    public static IReadOnlyList<DuplicateCandidate> Detect(IEnumerable<PersonRecord> records)
    {
        var evidence = new Dictionary<string, (string Left, string Right, List<DuplicateEvidence> Items)>(StringComparer.Ordinal);
        var list = records.ToList();
        Bucket(list, r => string.IsNullOrEmpty(r.EmailKey) ? null : r.EmailKey, "same_email", "The two records share an email address.", evidence);
        Bucket(list, r => NormalisePhone(r.Phone), "same_phone", "The two records share a phone number.", evidence);
        Bucket(list, r => NormaliseName(r.Name), "same_name", "The two records have the same full name.", evidence);
        return [.. evidence.Select(e =>
        {
            var strong = e.Value.Items.Any(i => i.Kind is "same_email" or "same_phone");
            return new DuplicateCandidate(e.Key, e.Value.Left, e.Value.Right, strong ? High : Low, e.Value.Items);
        }).OrderByDescending(c => c.Confidence == High).ThenBy(c => c.PairKey, StringComparer.Ordinal)];
    }

    private static void Bucket(List<PersonRecord> records, Func<PersonRecord, string?> key, string kind, string detail, Dictionary<string, (string Left, string Right, List<DuplicateEvidence> Items)> into)
    {
        foreach (var group in records.Select(r => (Record: r, Key: key(r))).Where(x => x.Key is not null).GroupBy(x => x.Key, StringComparer.Ordinal))
        {
            var ids = group.Select(x => x.Record.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(MaxBucket).ToList();
            for (var i = 0; i < ids.Count; i++)
            {
                for (var j = i + 1; j < ids.Count; j++)
                {
                    var pair = PairKey(ids[i], ids[j]);
                    if (!into.TryGetValue(pair, out var entry))
                    {
                        entry = (ids[i], ids[j], []);
                        into[pair] = entry;
                    }

                    entry.Items.Add(new DuplicateEvidence(kind, detail));
                }
            }
        }
    }
}
