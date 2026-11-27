using System.Globalization;
using System.Text.Json;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Exports;
using ReportingService.Aggregates;
using ReportingService.Metrics;

namespace ReportingService.Reports;

public sealed record ReportData(string Title, IReadOnlyList<ExportTable> Tables)
{
    public int Rows => Tables.Sum(t => t.Rows.Count);
}

/// <summary>The window a report covers, resolved from its parameters.</summary>
public sealed record ReportWindow(DateOnly From, DateOnly To, string? Metric);

/// <summary>
/// The built-in report templates. Every table is read through the warehouse and the metric definitions (never another service's tables), so a report can never disagree with the
/// dashboards. Financial templates are marked sensitive.
/// </summary>
public sealed class ReportTemplates(AggregateQuery aggregates, MetricService metrics, TimeProvider clock)
{
    public const string ImpactSummary = "impact-summary";
    public const string FinancialSummary = "financial-summary";
    public const string AggregateSeries = "aggregate-series";

    public static readonly IReadOnlyDictionary<string, string> Catalog = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ImpactSummary] = "Every impact metric by month with its definition and version.",
        [FinancialSummary] = "Donations, ledger expenses and budget by month (financial: needs finance.view).",
        [AggregateSeries] = "One warehouse metric by month (parameter `metric`)."
    };

    public static readonly IReadOnlySet<string> Parameters = new HashSet<string>(StringComparer.Ordinal) { "from", "to", "month", "months", "metric" };
    private static readonly string[] FinancePrefixes = ["donations.", "expenses.", "budget.", "ledger."];

    public static bool IsSensitive(string template, string? metric) =>
        template == FinancialSummary || (template == AggregateSeries && metric is not null && FinancePrefixes.Any(p => metric.StartsWith(p, StringComparison.Ordinal)));

    public static Dictionary<string, string> ReadParameters(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];

    public Result<ReportWindow> Resolve(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.Keys.FirstOrDefault(k => !Parameters.Contains(k)) is { } unknown)
        {
            return Result.Failure<ReportWindow>("VALIDATION_ERROR", $"Unknown parameter '{unknown}'. Allowed: {string.Join(", ", Parameters.Order(StringComparer.Ordinal))}.");
        }

        var now = clock.GetUtcNow();
        var thisMonth = new DateOnly(now.Year, now.Month, 1);
        parameters.TryGetValue("metric", out var metric);
        if (parameters.TryGetValue("month", out var month))
        {
            if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
            {
                return Result.Failure<ReportWindow>("VALIDATION_ERROR", "month must look like 2026-03.");
            }

            return Result.Success(new ReportWindow(first, first.AddMonths(1).AddDays(-1), metric));
        }

        if (parameters.ContainsKey("from") || parameters.ContainsKey("to"))
        {
            DateOnly from = default;
            DateOnly to = default;
            if (!parameters.TryGetValue("from", out var f) || !DateOnly.TryParseExact(f, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out from)
                || !parameters.TryGetValue("to", out var t) || !DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out to) || to < from)
            {
                return Result.Failure<ReportWindow>("VALIDATION_ERROR", "from and to must both be dates (yyyy-MM-dd) with to not before from.");
            }

            return Result.Success(new ReportWindow(from, to, metric));
        }

        var months = 12;
        if (parameters.TryGetValue("months", out var m) && (!int.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture, out months) || months is < 1 or > 60))
        {
            return Result.Failure<ReportWindow>("VALIDATION_ERROR", "months must be 1-60.");
        }

        return Result.Success(new ReportWindow(thisMonth.AddMonths(-(months - 1)), thisMonth.AddMonths(1).AddDays(-1), metric));
    }

    public async Task<Result<ReportData>> BuildAsync(string template, ReportWindow window, CancellationToken ct)
    {
        var freshness = await aggregates.FreshnessAsync(ct);
        var note = new ExportTable("About this report", ["Period from", "Period to", "Figures as of", "Stale"],
            [[Day(window.From), Day(window.To), freshness.AsOf?.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "never refreshed", freshness.Stale ? "yes" : "no"]]);
        switch (template)
        {
            case ImpactSummary:
                return Result.Success(await ImpactAsync(window, note, ct));
            case FinancialSummary:
                return Result.Success(await FinancialAsync(window, note, ct));
            case AggregateSeries:
                if (string.IsNullOrWhiteSpace(window.Metric))
                {
                    return Result.Failure<ReportData>("VALIDATION_ERROR", "The metric parameter is required for this template.");
                }

                var rows = (await aggregates.SeriesAsync(window.Metric, window.From, window.To, null, null, ct))
                    .Select(r => new object?[] { r.Source, r.Metric, Day(r.Period), r.Dimension, r.Currency, r.Value }).ToList();
                return Result.Success(new ReportData(window.Metric, [new ExportTable("Series", ["Source", "Metric", "Period", "Dimension", "Currency", "Value"], rows), note]));
            default:
                return Result.Failure<ReportData>("VALIDATION_ERROR", "Unknown template.");
        }
    }

    private async Task<ReportData> ImpactAsync(ReportWindow window, ExportTable note, CancellationToken ct)
    {
        List<object?[]> figures = [];
        List<object?[]> definitions = [];
        foreach (var definition in await metrics.CurrentAsync(ct))
        {
            var series = await metrics.SeriesAsync(definition.Key, null, window.From, window.To, ct);
            if (!series.IsSuccess)
            {
                continue;
            }

            figures.AddRange(series.Value!.Points.Select(p => new object?[] { definition.Key, definition.Name, definition.Unit, p.Period, decimal.Parse(p.Value, CultureInfo.InvariantCulture), p.DefinitionVersion }));
            definitions.AddRange(series.Value.Versions.Select(v => new object?[] { v.Key, v.Version, v.Name, v.Methodology, v.EffectiveFrom }));
        }

        return new ReportData("Impact summary",
        [
            new ExportTable("Impact figures", ["Metric", "Name", "Unit", "Period", "Value", "Definition version"], figures),
            new ExportTable("Definitions", ["Metric", "Version", "Name", "Methodology", "Effective from"], definitions),
            note
        ]);
    }

    private async Task<ReportData> FinancialAsync(ReportWindow window, ExportTable note, CancellationToken ct)
    {
        var donations = await aggregates.SeriesAsync("donations.confirmed.amount", window.From, window.To, null, null, ct);
        var counts = (await aggregates.SeriesAsync("donations.confirmed.count", window.From, window.To, null, null, ct)).ToDictionary(r => (r.Period, r.Dimension, r.Currency), r => r.Value);
        var expenses = await aggregates.SeriesAsync("expenses.ledger.amount", window.From, window.To, null, null, ct);
        var budget = await aggregates.SeriesAsync("budget.planned.amount", null, null, null, null, ct);
        return new ReportData("Financial summary",
        [
            new ExportTable("Donations", ["Month", "Designation", "Currency", "Amount", "Count"],
                [.. donations.Select(r => new object?[] { Day(r.Period), r.Dimension, r.Currency, r.Value, (int)counts.GetValueOrDefault((r.Period, r.Dimension, r.Currency)) })]),
            new ExportTable("Expenses", ["Month", "Account", "Currency", "Amount"], [.. expenses.Select(r => new object?[] { Day(r.Period), r.Dimension, r.Currency, r.Value })]),
            new ExportTable("Budget", ["Fiscal year starts", "Currency", "Planned"], [.. budget.Select(r => new object?[] { Day(r.Period), r.Currency, r.Value })]),
            note
        ]);
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
