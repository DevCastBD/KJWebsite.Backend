using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Exports;
using Microsoft.EntityFrameworkCore;
using ReportingService.Data;
using ReportingService.Data.Entities;

namespace ReportingService.Reports;

public sealed record ReportCreateRequest(string? Name, string? Template, Dictionary<string, string>? Parameters);

public sealed record ReportRunRequest(Dictionary<string, string>? Parameters, string[]? Formats);

public sealed record ReportDefinitionView(string Id, string Name, string Template, bool Sensitive, IReadOnlyDictionary<string, string> Parameters, DateTimeOffset CreatedAt, string? CreatedBy, bool Archived);

public sealed record ReportFileView(string Format, string FileName, string ContentType, long Bytes, string Sha256, string Url);

public sealed record ReportRunView(string Id, string ReportId, string Trigger, string Status, int Attempts, string? Error, int Rows, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    IReadOnlyDictionary<string, string> Parameters, IReadOnlyList<ReportFileView> Files);

public sealed record ReportListView(IReadOnlyList<ReportDefinitionView> Items);

public sealed record ReportRunListView(IReadOnlyList<ReportRunView> Items);

public sealed record ReportTemplateView(string Template, string Description, IReadOnlyList<string> Parameters);

public sealed record ReportTemplateListView(IReadOnlyList<ReportTemplateView> Items);

/// <summary>Saved report definitions and their runs. Every export of a run is generated once and kept, so a download or a delivery retry returns the same bytes.</summary>
public sealed class ReportService(ReportingDbContext db, ReportTemplates templates, TimeProvider clock, ILogger<ReportService> logger)
{
    public const string Csv = "csv";
    public const string Xlsx = "xlsx";
    public const string Pdf = "pdf";
    public static readonly IReadOnlyList<string> Formats = [Csv, Xlsx, Pdf];

    private static readonly Action<ILogger, string, string, Exception?> RunFailed = LoggerMessage.Define<string, string>(
        LogLevel.Error, new EventId(40, nameof(RunFailed)), "ALERT: report run {RunId} failed: {Reason}");

    public static ReportDefinitionView View(ReportDefinitionEntity e) =>
        new(e.Id, e.Name, e.Template, e.Sensitive, ReportTemplates.ReadParameters(e.ParametersJson), e.CreatedAt, e.CreatedBy, e.ArchivedAt is not null);

    public async Task<Result<ReportDefinitionEntity>> CreateAsync(ReportCreateRequest request, string? actor, bool mayHandleFinance, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 120)
        {
            return Result.Failure<ReportDefinitionEntity>("VALIDATION_ERROR", "name is required (up to 120 characters).");
        }

        if (request.Template is null || !ReportTemplates.Catalog.ContainsKey(request.Template))
        {
            return Result.Failure<ReportDefinitionEntity>("VALIDATION_ERROR", $"template must be one of: {string.Join(", ", ReportTemplates.Catalog.Keys)}.");
        }

        var parameters = request.Parameters ?? [];
        var window = templates.Resolve(parameters);
        if (!window.IsSuccess)
        {
            return Result.Failure<ReportDefinitionEntity>(window.Error!.Code, window.Error.Message);
        }

        if (request.Template == ReportTemplates.AggregateSeries && string.IsNullOrWhiteSpace(window.Value!.Metric))
        {
            return Result.Failure<ReportDefinitionEntity>("VALIDATION_ERROR", "The aggregate-series template needs a metric parameter.");
        }

        var sensitive = ReportTemplates.IsSensitive(request.Template, window.Value!.Metric);
        if (sensitive && !mayHandleFinance)
        {
            return Result.Failure<ReportDefinitionEntity>("FORBIDDEN", "Financial reports need finance.view as well.");
        }

        var entity = new ReportDefinitionEntity
        {
            Id = $"rpt_{Guid.NewGuid():N}", Name = name, Template = request.Template, ParametersJson = JsonSerializer.Serialize(parameters), Sensitive = sensitive, CreatedBy = actor, CreatedAt = clock.GetUtcNow()
        };
        db.ReportDefinitions.Add(entity);
        await db.SaveChangesAsync(ct);
        return Result.Success(entity);
    }

    public async Task<IReadOnlyList<ReportDefinitionEntity>> ListAsync(bool mayHandleFinance, CancellationToken ct) =>
        [.. (await db.ReportDefinitions.AsNoTracking().ToListAsync(ct)).Where(r => mayHandleFinance || !r.Sensitive).OrderBy(r => r.Name, StringComparer.Ordinal)];

    /// <summary>The report, or a failure: NOT_FOUND, or FORBIDDEN when it is financial and the caller may not see finance.</summary>
    public async Task<Result<ReportDefinitionEntity>> GetAsync(string id, bool mayHandleFinance, CancellationToken ct)
    {
        var report = await db.ReportDefinitions.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (report is null)
        {
            return Result.Failure<ReportDefinitionEntity>("NOT_FOUND", "No such report.");
        }

        return report.Sensitive && !mayHandleFinance ? Result.Failure<ReportDefinitionEntity>("FORBIDDEN", "Financial reports need finance.view as well.") : Result.Success(report);
    }

    public async Task<Result<ReportDefinitionEntity>> ArchiveAsync(string id, bool mayHandleFinance, CancellationToken ct)
    {
        var report = await GetAsync(id, mayHandleFinance, ct);
        if (!report.IsSuccess)
        {
            return report;
        }

        report.Value!.ArchivedAt ??= clock.GetUtcNow();
        foreach (var schedule in await db.ReportSchedules.Where(s => s.ReportId == id && s.Active).ToListAsync(ct))
        {
            schedule.Active = false;
        }

        await db.SaveChangesAsync(ct);
        return report;
    }

    /// <summary>Generates a run now: merged parameters, every requested export, stored. A failure is stored with its reason and logged as an alert for scheduled runs.</summary>
    public async Task<Result<ReportRunEntity>> RunAsync(ReportDefinitionEntity report, IReadOnlyDictionary<string, string>? overrides, IReadOnlyList<string>? formats, string trigger, string? scheduleId,
        IReadOnlyDictionary<string, string>? scheduleDefaults, string? actor, CancellationToken ct)
    {
        var parameters = ReportTemplates.ReadParameters(report.ParametersJson);
        foreach (var layer in new[] { scheduleDefaults, overrides })
        {
            foreach (var pair in layer ?? new Dictionary<string, string>())
            {
                parameters[pair.Key] = pair.Value;
            }
        }

        var chosen = (formats is { Count: > 0 } ? formats : Formats).Select(f => f.ToLowerInvariant()).Distinct().ToList();
        if (chosen.FirstOrDefault(f => !Formats.Contains(f)) is { } badFormat)
        {
            return Result.Failure<ReportRunEntity>("VALIDATION_ERROR", $"Unknown format '{badFormat}'. Use csv, xlsx or pdf.");
        }

        var window = templates.Resolve(parameters);
        if (!window.IsSuccess)
        {
            return Result.Failure<ReportRunEntity>(window.Error!.Code, window.Error.Message);
        }

        if (ReportTemplates.IsSensitive(report.Template, window.Value!.Metric) && !report.Sensitive)
        {
            return Result.Failure<ReportRunEntity>("FORBIDDEN", "These parameters turn a general report into a financial one; save it as its own report.");
        }

        var run = new ReportRunEntity
        {
            Id = $"run_{Guid.NewGuid():N}", ReportId = report.Id, ScheduleId = scheduleId, Trigger = trigger, ParametersJson = JsonSerializer.Serialize(parameters.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value)),
            GeneratedBy = actor, StartedAt = clock.GetUtcNow(), Attempts = 1
        };
        db.ReportRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            var data = await templates.BuildAsync(report.Template, window.Value, ct);
            if (!data.IsSuccess)
            {
                return await FailAsync(run, data.Error!.Message, ct);
            }

            foreach (var format in chosen)
            {
                var file = Render(report, window.Value, data.Value!, format);
                db.ReportFiles.Add(new ReportFileEntity { RunId = run.Id, Format = format, FileName = file.FileName, ContentType = file.ContentType, Content = file.Content, Sha256 = Convert.ToHexStringLower(SHA256.HashData(file.Content)) });
            }

            run.Rows = data.Value!.Rows;
            run.Status = RunStatuses.Succeeded;
            run.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Result.Success(run);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            run = await db.ReportRuns.FirstAsync(r => r.Id == run.Id, CancellationToken.None);
            return await FailAsync(run, ex.Message, CancellationToken.None);
        }
    }

    private async Task<Result<ReportRunEntity>> FailAsync(ReportRunEntity run, string reason, CancellationToken ct)
    {
        run.Status = RunStatuses.Failed;
        run.Error = reason.Length <= 2000 ? reason : reason[..2000];
        run.FinishedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        if (run.Trigger == RunTriggers.Schedule)
        {
            RunFailed(logger, run.Id, run.Error, null);
        }

        return Result.Failure<ReportRunEntity>("RUN_FAILED", run.Error);
    }

    private static (string FileName, string ContentType, byte[] Content) Render(ReportDefinitionEntity report, ReportWindow window, ReportData data, string format)
    {
        var stem = new string([.. report.Name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        stem = (stem.Length == 0 ? "report" : stem) + $"-{window.From:yyyy-MM-dd}-{window.To:yyyy-MM-dd}";
        return format switch
        {
            Csv when data.Tables.Count == 1 => ($"{stem}.csv", "text/csv; charset=utf-8", TableExport.Csv(data.Tables[0])),
            Csv => ($"{stem}.zip", "application/zip", TableExport.CsvZip(data.Tables)),
            Xlsx => ($"{stem}.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", TableExport.Xlsx(data.Tables)),
            _ => ($"{stem}.pdf", "application/pdf", ReportPdf.Render(data.Title, data.Tables))
        };
    }

    public async Task<ReportRunView> ViewAsync(ReportRunEntity run, CancellationToken ct)
    {
        var files = (await db.ReportFiles.AsNoTracking().Where(f => f.RunId == run.Id).Select(f => new { f.Format, f.FileName, f.ContentType, f.Content.Length, f.Sha256 }).ToListAsync(ct))
            .OrderBy(f => f.Format, StringComparer.Ordinal)
            .Select(f => new ReportFileView(f.Format, f.FileName, f.ContentType, f.Length, f.Sha256, $"/api/v1/admin/reporting/report-runs/{run.Id}/files/{f.Format}")).ToList();
        return new ReportRunView(run.Id, run.ReportId, run.Trigger, run.Status, run.Attempts, run.Error, run.Rows, run.StartedAt, run.FinishedAt, ReportTemplates.ReadParameters(run.ParametersJson), files);
    }

    public async Task<IReadOnlyList<ReportRunEntity>> RunsAsync(string reportId, int take, CancellationToken ct) =>
        [.. (await db.ReportRuns.AsNoTracking().Where(r => r.ReportId == reportId).ToListAsync(ct)).OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).Take(take)];

    public string Today() => clock.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
