using System.Globalization;
using System.Security.Claims;
using System.Text;
using FinanceService.Data;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Reports;

public sealed record GenerateReportRequest(string? Month);

public static class ReportEndpoints
{
    private const string Route = "/api/v1/admin/finance/reports";

    /// <summary>Outside /admin on purpose: the link is opened by a browser that sends no bearer token, and nothing under /admin may be anonymous. The signed token is the credential.</summary>
    private const string DownloadRoute = "/api/v1/finance/report-downloads";
    private const int MaxRangeDays = 3700;

    public static void MapReports(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (HttpContext context, FinanceDbContext db, ReportService reports, IOptions<ReportOptions> options, int? limit, int? offset) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 100);
            var skip = Math.Max(0, offset ?? 0);
            // Packs are few (one or a handful a month) and SQLite cannot order a DateTimeOffset, so they are ordered and paged in memory.
            var all = await db.ReportPacks.AsNoTracking().ToListAsync(context.RequestAborted);
            var total = all.Count;
            var ordered = all.OrderByDescending(p => p.GeneratedAt).Skip(skip).Take(take).ToList();
            return Results.Ok(new PagingEnvelope<ReportPackView>([.. ordered.Select(p => View(context, options.Value, reports, p))], total, null, take, skip));
        })
        .WithName("ListReportPacks")
        .WithSummary("Generated monthly report packs, newest first")
        .WithDescription("Each item is `{id, month, generatedAt, url}` (plus who made it, its SHA-256 and size). `url` is an absolute link to the stored workbook that is valid for a few minutes (`Finance:Reports:LinkMinutes`); list again for a fresh one.")
        .WithTags("Reports")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost(Route, async (HttpContext context, GenerateReportRequest payload, ReportService reports, IOptions<ReportOptions> options) =>
        {
            var made = await reports.GeneratePackAsync(payload.Month, context.User.FindFirstValue(KjClaims.Subject), context.RequestAborted);
            if (!made.IsSuccess)
            {
                return ApiError.Create(context, made.Error!.Code == "VALIDATION_ERROR" ? StatusCodes.Status400BadRequest : StatusCodes.Status422UnprocessableEntity, made.Error.Code, made.Error.Message);
            }

            return Results.Created($"{Route}/{made.Value!.Id}", View(context, options.Value, reports, made.Value));
        })
        .WithName("GenerateReportPack")
        .WithSummary("Generate and store a month's financial report pack")
        .WithDescription("`{month: \"2026-03\"}`. Builds one workbook from the ledger (summary with the reconciliation checks, trial balance, income statement, balance sheet, cash flow, donations, project costs, donors, donation and expense registers, general ledger), stores it and returns `{id, month, generatedAt, url}`. The stored file never changes; generating again makes a new pack. Requires `finance.report.generate`.")
        .WithTags("Reports")
        .RequirePermission(Permissions.FinanceReportGenerate);

        app.MapGet($"{DownloadRoute}/{{id}}", async (HttpContext context, string id, string? token, ReportService reports) =>
        {
            if (!reports.TokenFits(id, token))
            {
                return ApiError.Create(context, StatusCodes.Status403Forbidden, "LINK_INVALID", "This download link is invalid or has expired; list the reports again for a new one.");
            }

            if (await reports.OpenAsync(id, context.RequestAborted) is not { } opened)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Report not found.");
            }

            if (!opened.Intact)
            {
                return ApiError.Create(context, StatusCodes.Status500InternalServerError, "REPORT_CORRUPT", "The stored report does not match its recorded hash and was not served.");
            }

            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.File(opened.Bytes, ReportService.XlsxType, $"report-{opened.Pack.Month}.xlsx");
        })
        .AllowAnonymous()
        .WithName("DownloadReportPack")
        .WithSummary("Download a stored report pack with its signed link")
        .WithDescription("Needs no bearer token (a browser link cannot send one); the short-lived `token` from the list or generate response is the credential. The file's hash is checked before it is served.")
        .WithTags("Reports");

        app.MapGet($"{Route}/{{name}}", async (HttpContext context, string name, ReportService reports, string? from, string? to, string? asOf, string? currency, string? format) =>
        {
            if (!ReportNames.All.Contains(name))
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", $"Unknown report '{name}'. Reports: {string.Join(", ", ReportNames.All)}.");
            }

            var today = reports.Today;
            var start = new DateOnly(today.Year, today.Month, 1);
            var end = start.AddMonths(1).AddDays(-1);
            var invalid = new List<string>();
            if (from is not null && !TryDay(from, out start))
            {
                invalid.Add("from");
            }

            if (to is not null && !TryDay(to, out end))
            {
                invalid.Add("to");
            }

            var balanceDate = end;
            if (asOf is not null && !TryDay(asOf, out balanceDate))
            {
                invalid.Add("asOf");
            }

            var wanted = (format ?? "json").ToLowerInvariant();
            if (wanted is not ("json" or "csv" or "xlsx"))
            {
                invalid.Add("format");
            }

            if (currency is not null && (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper)))
            {
                invalid.Add("currency");
            }

            if (invalid.Count == 0 && (start > end || end.DayNumber - start.DayNumber > MaxRangeDays))
            {
                invalid.Add("from/to");
            }

            if (invalid.Count > 0)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
            }

            if (asOf is null)
            {
                balanceDate = end;
            }

            var result = await reports.BuildAsync(name, start, end, balanceDate, currency, context.User.FindFirstValue(KjClaims.Subject), context.RequestAborted);
            var stamp = $"{name}-{start:yyyy-MM-dd}-{end:yyyy-MM-dd}";
            switch (wanted)
            {
                case "xlsx":
                    return Results.File(ReportService.Render(result, "xlsx"), ReportService.XlsxType, stamp + ".xlsx");
                case "csv":
                    return result.Tables.Count == 1
                        ? Results.File(ReportService.Render(result, "csv"), "text/csv; charset=utf-8", stamp + ".csv")
                        : Results.File(ReportService.Render(result, "csv"), "application/zip", stamp + ".zip");
                default:
                    return Results.Ok(new
                    {
                        report = name,
                        title = result.Title,
                        from = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        to = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        asOf = balanceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        checks = result.Checks,
                        tables = result.Tables.Select(t => new { name = t.Name, headers = t.Headers, rows = t.Rows.Select(r => r.Select(Cell).ToArray()) })
                    });
            }
        })
        .WithName("GetFinancialReport")
        .WithSummary("A financial report as JSON, CSV or XLSX")
        .WithDescription("`name`: income-statement, balance-sheet, cash-flow, donations, project-costs, donors, trial-balance, general-ledger, donation-register, expense-register or auditor-pack (every sheet). Query: `from`, `to` (default: this month), `asOf` (balances, default `to`), `currency`, `format` (json default; csv is UTF-8 with a byte-order mark so Excel reads Bengali, a zip of CSVs for the auditor pack; xlsx). Amounts are decimal strings in JSON and never converted between currencies. Statements are per currency and carry reconciliation `checks`. Requires `finance.view`.")
        .WithTags("Reports")
        .RequirePermission(Permissions.FinanceView);
    }

    private static object? Cell(object? value) => value switch
    {
        decimal d => d.ToString("0.00", CultureInfo.InvariantCulture),
        _ => value
    };

    private static bool TryDay(string text, out DateOnly day) => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private static ReportPackView View(HttpContext context, ReportOptions options, ReportService reports, FinanceService.Data.Entities.ReportPackEntity pack)
    {
        var baseUrl = string.IsNullOrWhiteSpace(options.PublicBaseUrl) ? $"{context.Request.Scheme}://{context.Request.Host}" : options.PublicBaseUrl.TrimEnd('/');
        return new ReportPackView(pack.Id, pack.Month, pack.GeneratedAt, baseUrl + reports.SignedPath(pack.Id), pack.GeneratedBy, pack.Sha256, pack.SizeBytes);
    }
}
