using System.Globalization;

namespace ContentService.Data;

/// <summary>A parsed recurrence rule: the RFC 5545 subset events use.</summary>
public sealed record RecurrenceRule(string Frequency, int Interval, int? Count, DateTime? UntilLocal, DayOfWeek[] ByDay)
{
    public const int MaxOccurrences = 366;

    private static readonly string[] AllowedParts = ["FREQ", "INTERVAL", "COUNT", "UNTIL", "BYDAY"];

    /// <summary>Parses `FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=10`. Returns an error message when the rule is not usable.</summary>
    public static (RecurrenceRule? Rule, string? Error) Parse(string text)
    {
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in text.Trim().Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = segment.Split('=', 2);
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[1]) || !parts.TryAdd(pair[0].Trim(), pair[1].Trim()))
            {
                return (null, $"Bad recurrence part '{segment}'.");
            }
        }

        if (parts.Keys.FirstOrDefault(k => !AllowedParts.Contains(k.ToUpperInvariant())) is { } unsupported)
        {
            return (null, $"Recurrence part '{unsupported}' is not supported.");
        }

        if (!parts.TryGetValue("FREQ", out var freq) || (freq = freq.ToUpperInvariant()) is not ("DAILY" or "WEEKLY" or "MONTHLY"))
        {
            return (null, "Recurrence FREQ must be DAILY, WEEKLY or MONTHLY.");
        }

        var interval = 1;
        if (parts.TryGetValue("INTERVAL", out var i) && (!int.TryParse(i, out interval) || interval is < 1 or > 52))
        {
            return (null, "Recurrence INTERVAL must be 1 to 52.");
        }

        int? count = null;
        if (parts.TryGetValue("COUNT", out var c))
        {
            if (!int.TryParse(c, out var n) || n is < 1 or > MaxOccurrences)
            {
                return (null, $"Recurrence COUNT must be 1 to {MaxOccurrences}.");
            }

            count = n;
        }

        DateTime? until = null;
        if (parts.TryGetValue("UNTIL", out var u))
        {
            if (!DateTime.TryParseExact(u.TrimEnd('Z', 'z'), ["yyyyMMdd", "yyyyMMdd'T'HHmmss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return (null, "Recurrence UNTIL must be yyyyMMdd or yyyyMMddTHHmmss.");
            }

            // A date-only UNTIL includes that whole day.
            until = parsed.TimeOfDay == TimeSpan.Zero ? parsed.Date.AddDays(1).AddTicks(-1) : parsed;
        }

        if (count is null && until is null)
        {
            return (null, "A recurring event needs COUNT or UNTIL so the series is bounded.");
        }

        var days = new List<DayOfWeek>();
        if (parts.TryGetValue("BYDAY", out var by))
        {
            if (freq != "WEEKLY")
            {
                return (null, "BYDAY is only supported with FREQ=WEEKLY.");
            }

            foreach (var code in by.ToUpperInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                DayOfWeek? day = code switch
                {
                    "MO" => DayOfWeek.Monday,
                    "TU" => DayOfWeek.Tuesday,
                    "WE" => DayOfWeek.Wednesday,
                    "TH" => DayOfWeek.Thursday,
                    "FR" => DayOfWeek.Friday,
                    "SA" => DayOfWeek.Saturday,
                    "SU" => DayOfWeek.Sunday,
                    _ => null
                };
                if (day is null)
                {
                    return (null, $"Unknown BYDAY value '{code}'.");
                }

                if (!days.Contains(day.Value))
                {
                    days.Add(day.Value);
                }
            }
        }

        return (new RecurrenceRule(freq, interval, count, until, [.. days]), null);
    }

    /// <summary>Local start times of the series, first start included, expanded in wall-clock time (so DST never shifts the hour) and capped.</summary>
    public IEnumerable<DateTime> Expand(DateTime firstLocal)
    {
        var produced = 0;
        foreach (var local in Candidates(firstLocal))
        {
            if (UntilLocal is not null && local > UntilLocal)
            {
                yield break;
            }

            if (++produced > Math.Min(Count ?? MaxOccurrences, MaxOccurrences))
            {
                yield break;
            }

            yield return local;
        }
    }

    private IEnumerable<DateTime> Candidates(DateTime first)
    {
        if (Frequency == "DAILY")
        {
            for (var n = 0; ; n++)
            {
                yield return first.AddDays((long)n * Interval);
            }
        }

        if (Frequency == "MONTHLY")
        {
            // Anchored on the first date; a month too short for the day is skipped, as RFC 5545 does.
            for (var n = 0; n < 12 * 40; n++)
            {
                var month = new DateTime(first.Year, first.Month, 1).AddMonths(n * Interval);
                if (first.Day <= DateTime.DaysInMonth(month.Year, month.Month))
                {
                    yield return month.AddDays(first.Day - 1) + first.TimeOfDay;
                }
            }

            yield break;
        }

        var days = ByDay.Length == 0 ? [first.DayOfWeek] : ByDay;
        var weekStart = first.Date.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        for (var week = 0; week < 52 * 40; week++)
        {
            var monday = weekStart.AddDays(7L * week * Interval);
            foreach (var day in days.OrderBy(d => ((int)d + 6) % 7))
            {
                var local = monday.AddDays(((int)day + 6) % 7) + first.TimeOfDay;
                if (local >= first)
                {
                    yield return local;
                }
            }
        }
    }
}
