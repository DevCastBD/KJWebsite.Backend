using System.Globalization;

namespace KJWebsite.BuildingBlocks.DataQuality;

public sealed record FieldCompleteness(string Field, int Filled, int Missing, string FilledPercent, IReadOnlyList<string> SampleMissingIds);

public sealed record EntityCompleteness(string Entity, int Total, string CompletePercent, int CompleteRecords, IReadOnlyList<FieldCompleteness> Fields);

/// <summary>Counts, per entity and per field, how many records have the field filled. Samples are record ids only, so the report holds no personal data and each id leads to the record to fix.</summary>
public static class Completeness
{
    public const int SampleSize = 10;

    public static EntityCompleteness Build<T>(string entity, IReadOnlyCollection<T> rows, Func<T, string> id, params (string Field, Func<T, bool> Filled)[] checks)
    {
        var fields = new List<FieldCompleteness>();
        foreach (var (field, filled) in checks)
        {
            var missing = rows.Where(r => !filled(r)).Select(id).Order(StringComparer.Ordinal).ToList();
            fields.Add(new FieldCompleteness(field, rows.Count - missing.Count, missing.Count, Percent(rows.Count - missing.Count, rows.Count), [.. missing.Take(SampleSize)]));
        }

        var complete = rows.Count(r => checks.All(c => c.Filled(r)));
        return new EntityCompleteness(entity, rows.Count, Percent(complete, rows.Count), complete, fields);
    }

    private static string Percent(int part, int whole) => whole == 0 ? "100.0" : (part * 100m / whole).ToString("0.0", CultureInfo.InvariantCulture);
}
