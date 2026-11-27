using System.Security.Claims;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using ReportingService.Data;
using ReportingService.Data.Entities;

namespace ReportingService.Reports;

public static class ReportEndpoints
{
    private const string Route = "/api/v1/admin/reporting";

    public static void MapReports(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{Route}/report-templates", () => Results.Ok(new ReportTemplateListView([.. ReportTemplates.Catalog.Select(t => new ReportTemplateView(t.Key, t.Value, [.. ReportTemplates.Parameters.Order(StringComparer.Ordinal)]))])))
            .WithName("ListReportTemplates").WithSummary("The built-in report templates and the parameters a report may take")
            .WithDescription("Parameters: `month` (2026-03), or `from` and `to` (yyyy-MM-dd), or `months` (1-60, ending this month); `metric` for `aggregate-series`. Requires `reporting.view`.")
            .WithTags("Reports").Produces<ReportTemplateListView>().RequirePermission(Permissions.ReportingView);

        app.MapGet($"{Route}/reports", async (HttpContext context, ReportService reports, CancellationToken ct) =>
            Results.Ok(new ReportListView([.. (await reports.ListAsync(MayHandleFinance(context), ct)).Select(ReportService.View)])))
            .WithName("ListSavedReports").WithSummary("Saved report definitions")
            .WithDescription("Financial reports are listed only to callers who also hold `finance.view`. Requires `reporting.view`.")
            .WithTags("Reports").Produces<ReportListView>().RequirePermission(Permissions.ReportingView);

        app.MapPost($"{Route}/reports", async (HttpContext context, ReportService reports, ReportCreateRequest payload, CancellationToken ct) =>
        {
            var made = await reports.CreateAsync(payload, context.User.FindFirstValue(KjClaims.Subject), MayHandleFinance(context), ct);
            return made.IsSuccess ? Results.Created($"{Route}/reports/{made.Value!.Id}", ReportService.View(made.Value)) : Fail(context, made.Error!);
        })
        .WithName("SaveReport").WithSummary("Save a report definition")
        .WithDescription("`{name, template, parameters?}`. A financial template (or a series over a finance metric) also needs `finance.view`. Requires `reporting.manage`.")
        .WithTags("Reports").Produces<ReportDefinitionView>(StatusCodes.Status201Created).RequirePermission(Permissions.ReportingManage);

        app.MapGet($"{Route}/reports/{{id}}", async (HttpContext context, ReportService reports, string id, CancellationToken ct) =>
        {
            var found = await reports.GetAsync(id, MayHandleFinance(context), ct);
            return found.IsSuccess ? Results.Ok(ReportService.View(found.Value!)) : Fail(context, found.Error!);
        })
        .WithName("GetSavedReport").WithSummary("One saved report")
        .WithTags("Reports").Produces<ReportDefinitionView>().RequirePermission(Permissions.ReportingView);

        app.MapDelete($"{Route}/reports/{{id}}", async (HttpContext context, ReportService reports, string id, CancellationToken ct) =>
        {
            var archived = await reports.ArchiveAsync(id, MayHandleFinance(context), ct);
            return archived.IsSuccess ? Results.NoContent() : Fail(context, archived.Error!);
        })
        .WithName("ArchiveSavedReport").WithSummary("Archive a report and stop its schedules")
        .WithDescription("Runs and files are kept. Requires `reporting.manage`.")
        .WithTags("Reports").RequirePermission(Permissions.ReportingManage);

        app.MapPost($"{Route}/reports/{{id}}/runs", async (HttpContext context, ReportService reports, string id, ReportRunRequest? payload, CancellationToken ct) =>
        {
            var report = await reports.GetAsync(id, MayHandleFinance(context), ct);
            if (!report.IsSuccess)
            {
                return Fail(context, report.Error!);
            }

            var made = await reports.RunAsync(report.Value!, payload?.Parameters, payload?.Formats, RunTriggers.Manual, null, null, context.User.FindFirstValue(KjClaims.Subject), ct);
            return made.IsSuccess ? Results.Ok(await reports.ViewAsync(made.Value!, ct)) : Fail(context, made.Error!);
        })
        .WithName("RunSavedReport").WithSummary("Run a report now with parameters and formats")
        .WithDescription("`{parameters?, formats?}`: parameters override the saved ones, `formats` is any of `csv`, `xlsx`, `pdf` (default all). Returns the run with a download `url` per format. CSV has a byte-order mark so Excel reads Bengali; PDF embeds a Bengali font. Requires `reporting.view`.")
        .WithTags("Reports").Produces<ReportRunView>().RequirePermission(Permissions.ReportingView);

        app.MapGet($"{Route}/reports/{{id}}/runs", async (HttpContext context, ReportService reports, string id, int? limit, CancellationToken ct) =>
        {
            var report = await reports.GetAsync(id, MayHandleFinance(context), ct);
            if (!report.IsSuccess)
            {
                return Fail(context, report.Error!);
            }

            var runs = await reports.RunsAsync(id, Math.Clamp(limit ?? 20, 1, 100), ct);
            var views = new List<ReportRunView>();
            foreach (var run in runs)
            {
                views.Add(await reports.ViewAsync(run, ct));
            }

            return Results.Ok(new ReportRunListView(views));
        })
        .WithName("ListReportRuns").WithSummary("Recent runs of a report, newest first")
        .WithTags("Reports").Produces<ReportRunListView>().RequirePermission(Permissions.ReportingView);

        app.MapGet($"{Route}/report-runs/{{runId}}/files/{{format}}", async (HttpContext context, ReportingDbContext db, ReportService reports, string runId, string format, CancellationToken ct) =>
        {
            var run = await db.ReportRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
            var report = run is null ? null : await reports.GetAsync(run.ReportId, MayHandleFinance(context), ct);
            if (run is null || report is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "No such run.");
            }

            if (!report.IsSuccess)
            {
                return Fail(context, report.Error!);
            }

            var file = await db.ReportFiles.AsNoTracking().FirstOrDefaultAsync(f => f.RunId == runId && f.Format == format, ct);
            return file is null ? ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "No such file.") : Results.File(file.Content, file.ContentType, file.FileName);
        })
        .WithName("DownloadReportFile").WithSummary("Download one export of a run")
        .WithDescription("The stored bytes, the same every time. Financial reports need `finance.view` as well. Requires `reporting.view`.")
        .WithTags("Reports").Produces<byte[]>(contentType: "application/octet-stream").RequirePermission(Permissions.ReportingView);

        app.MapPost($"{Route}/reports/{{id}}/schedules", async (HttpContext context, ReportService reports, ReportScheduler scheduler, string id, ScheduleRequest payload, CancellationToken ct) =>
        {
            var report = await reports.GetAsync(id, MayHandleFinance(context), ct);
            if (!report.IsSuccess)
            {
                return Fail(context, report.Error!);
            }

            if (report.Value!.ArchivedAt is not null)
            {
                return ApiError.Create(context, StatusCodes.Status422UnprocessableEntity, "ARCHIVED", "An archived report cannot be scheduled.");
            }

            var made = await scheduler.CreateAsync(report.Value, payload, context.User.FindFirstValue(KjClaims.Subject), ct);
            return made.IsSuccess ? Results.Created($"{Route}/reports/{id}/schedules", await scheduler.ViewAsync(made.Value!, ct)) : Fail(context, made.Error!);
        })
        .WithName("ScheduleReport").WithSummary("Mail a report every month")
        .WithDescription("`{dayOfMonth (1-28), hourUtc, formats?, parameters?}`. With no window parameters the run covers the previous calendar month. It stays due until a run succeeds: failures are logged as alerts and retried, never skipped. Add recipients next. Requires `reporting.manage`.")
        .WithTags("Schedules").Produces<ScheduleView>(StatusCodes.Status201Created).RequirePermission(Permissions.ReportingManage);

        app.MapGet($"{Route}/reports/{{id}}/schedules", async (HttpContext context, ReportService reports, ReportScheduler scheduler, ReportingDbContext db, string id, CancellationToken ct) =>
        {
            var report = await reports.GetAsync(id, MayHandleFinance(context), ct);
            if (!report.IsSuccess)
            {
                return Fail(context, report.Error!);
            }

            var views = new List<ScheduleView>();
            foreach (var schedule in await db.ReportSchedules.AsNoTracking().Where(s => s.ReportId == id).ToListAsync(ct))
            {
                views.Add(await scheduler.ViewAsync(schedule, ct));
            }

            return Results.Ok(new ScheduleListView(views));
        })
        .WithName("ListReportSchedules").WithSummary("A report's schedules with their recipients")
        .WithTags("Schedules").Produces<ScheduleListView>().RequirePermission(Permissions.ReportingManage);

        app.MapDelete($"{Route}/report-schedules/{{id}}", async (HttpContext context, ReportingDbContext db, ReportService reports, string id, CancellationToken ct) =>
        {
            var schedule = await db.ReportSchedules.FirstOrDefaultAsync(s => s.Id == id, ct);
            var report = schedule is null ? null : await reports.GetAsync(schedule.ReportId, MayHandleFinance(context), ct);
            if (schedule is null || report is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "No such schedule.");
            }

            if (!report.IsSuccess)
            {
                return Fail(context, report.Error!);
            }

            schedule.Active = false;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithName("StopReportSchedule").WithSummary("Stop a schedule")
        .WithTags("Schedules").RequirePermission(Permissions.ReportingManage);

        app.MapPost($"{Route}/report-schedules/{{id}}/recipients", async (HttpContext context, ReportingDbContext db, ReportService reports, ReportScheduler scheduler, string id, RecipientRequest payload, CancellationToken ct) =>
        {
            var (schedule, report) = await Find(db, reports, context, id, ct);
            if (schedule is null || report is null || !report.IsSuccess)
            {
                return report is { IsSuccess: false } ? Fail(context, report.Error!) : ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "No such schedule.");
            }

            if (report.Value!.Sensitive && !context.User.HasClaim(KjClaims.Permission, Permissions.ReportingRecipients))
            {
                return ApiError.Create(context, StatusCodes.Status403Forbidden, "FORBIDDEN", "Recipients of a financial report are managed with the reporting.recipients permission.");
            }

            var added = await scheduler.AddRecipientAsync(schedule, payload.Email, context.User.FindFirstValue(KjClaims.Subject), ct);
            return added.IsSuccess ? Results.Created($"{Route}/report-schedules/{id}/recipients/{added.Value!.Id}", added.Value) : Fail(context, added.Error!);
        })
        .WithName("AddReportRecipient").WithSummary("Add a recipient to a schedule")
        .WithDescription("`{email}`. For a financial report the caller also needs `reporting.recipients`. The address is stored encrypted; who added it and when is in the audit log. Requires `reporting.manage`.")
        .WithTags("Schedules").Produces<RecipientView>(StatusCodes.Status201Created).RequirePermission(Permissions.ReportingManage);

        app.MapDelete($"{Route}/report-schedules/{{id}}/recipients/{{recipientId}}", async (HttpContext context, ReportingDbContext db, ReportService reports, ReportScheduler scheduler, string id, string recipientId, CancellationToken ct) =>
        {
            var (schedule, report) = await Find(db, reports, context, id, ct);
            if (schedule is null || report is null || !report.IsSuccess)
            {
                return report is { IsSuccess: false } ? Fail(context, report.Error!) : ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "No such schedule.");
            }

            if (report.Value!.Sensitive && !context.User.HasClaim(KjClaims.Permission, Permissions.ReportingRecipients))
            {
                return ApiError.Create(context, StatusCodes.Status403Forbidden, "FORBIDDEN", "Recipients of a financial report are managed with the reporting.recipients permission.");
            }

            var removed = await scheduler.RemoveRecipientAsync(id, recipientId, context.User.FindFirstValue(KjClaims.Subject), ct);
            return removed.IsSuccess ? Results.NoContent() : Fail(context, removed.Error!);
        })
        .WithName("RemoveReportRecipient").WithSummary("Remove a recipient from a schedule")
        .WithDescription("The recipient stops receiving the report; the row is kept and the removal is in the audit log. Requires `reporting.manage` (and `reporting.recipients` for a financial report).")
        .WithTags("Schedules").RequirePermission(Permissions.ReportingManage);
    }

    private static async Task<(ReportScheduleEntity? Schedule, Result<ReportDefinitionEntity>? Report)> Find(ReportingDbContext db, ReportService reports, HttpContext context, string scheduleId, CancellationToken ct)
    {
        var schedule = await db.ReportSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId, ct);
        return schedule is null ? (null, null) : (schedule, await reports.GetAsync(schedule.ReportId, MayHandleFinance(context), ct));
    }

    private static bool MayHandleFinance(HttpContext context) => context.User.HasClaim(KjClaims.Permission, Permissions.FinanceView);

    private static IResult Fail(HttpContext context, ResultError error) => ApiError.Create(context, error.Code switch
    {
        "VALIDATION_ERROR" => StatusCodes.Status400BadRequest,
        "NOT_FOUND" => StatusCodes.Status404NotFound,
        "FORBIDDEN" => StatusCodes.Status403Forbidden,
        "DUPLICATE" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status422UnprocessableEntity
    }, error.Code, error.Message);
}
