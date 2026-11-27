using System.Globalization;
using System.Security.Claims;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;

namespace ReportingService.Metrics;

public sealed record MetricListView(IReadOnlyList<MetricVersionView> Items);

public static class MetricEndpoints
{
    private const string Route = "/api/v1/admin/reporting/metrics";

    public static void MapMetrics(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (MetricService metrics, CancellationToken ct) => Results.Ok(new MetricListView(await metrics.CurrentAsync(ct))))
            .WithName("ListImpactMetrics").WithSummary("Impact metric definitions in force now")
            .WithDescription("One entry per metric: its current version, with the plain-language `methodology` to show beside the figure. Requires `reporting.view`.")
            .WithTags("Metrics").Produces<MetricListView>().RequirePermission(Permissions.ReportingView);

        app.MapGet($"{Route}/{{key}}", async (HttpContext context, MetricService metrics, string key, CancellationToken ct) =>
        {
            var versions = await metrics.VersionsAsync(key, ct);
            return versions.Count == 0 ? ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "No such metric.") : Results.Ok(new MetricListView(versions));
        })
        .WithName("GetImpactMetricVersions").WithSummary("Every dated version of a metric definition, oldest first")
        .WithTags("Metrics").Produces<MetricListView>().RequirePermission(Permissions.ReportingView);

        app.MapGet($"{Route}/{{key}}/series", async (HttpContext context, MetricService metrics, string key, string? project, string? from, string? to, CancellationToken ct) =>
        {
            if (!TryDay(from, out var start) || !TryDay(to, out var end))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "from and to must be dates (yyyy-MM-dd).");
            }

            var series = await metrics.SeriesAsync(key, string.IsNullOrWhiteSpace(project) ? null : project.Trim(), start, end, ct);
            return series.IsSuccess ? Results.Ok(series.Value) : ApiError.Create(context, StatusCodes.Status404NotFound, series.Error!.Code, series.Error.Message);
        })
        .WithName("GetImpactMetricSeries").WithSummary("A metric monthly series, organisation-wide or for one project")
        .WithDescription("Without `project` the figure is the organisation-wide total; with it, that project. Each point carries the `definitionVersion` that produced it (a month keeps the definition in force for it), and the response carries the definition, every version, a `trace` to the underlying warehouse metric, `asOf` and `stale`. `total` is the sum for monthly metrics and absent for snapshots. Requires `reporting.view`.")
        .WithTags("Metrics").Produces<MetricSeriesView>().RequirePermission(Permissions.ReportingView);

        app.MapPost(Route, async (HttpContext context, MetricService metrics, MetricCreateRequest payload, CancellationToken ct) =>
        {
            var made = await metrics.CreateAsync(payload, context.User.FindFirstValue(KjClaims.Subject), ct);
            return made.IsSuccess ? Results.Created($"{Route}/{made.Value!.Key}", made.Value) : Fail(context, made.Error!);
        })
        .WithName("CreateImpactMetric").WithSummary("Define a new impact metric (version 1)")
        .WithDescription("`{key, name, description?, methodology, unit, metric, kind, scale, effectiveFrom?}`. `metric` is a warehouse metric from the catalogue, `kind` is `monthly` or `snapshot`, `scale` multiplies the stored figure (for example hours from minutes). Requires `reporting.manage`.")
        .WithTags("Metrics").Produces<MetricVersionView>(StatusCodes.Status201Created).RequirePermission(Permissions.ReportingManage);

        app.MapPost($"{Route}/{{key}}/versions", async (HttpContext context, MetricService metrics, string key, MetricVersionRequest payload, CancellationToken ct) =>
        {
            var made = await metrics.AddVersionAsync(key, payload, context.User.FindFirstValue(KjClaims.Subject), ct);
            return made.IsSuccess ? Results.Created($"{Route}/{key}", made.Value) : Fail(context, made.Error!);
        })
        .WithName("AddImpactMetricVersion").WithSummary("Change a metric definition with a new dated version")
        .WithDescription("Fields left out carry over from the current version, except `methodology`, which must be restated. `effectiveFrom` defaults to the current month and cannot be earlier: earlier months keep the definition they were published under (422 HISTORY_LOCKED). Requires `reporting.manage`.")
        .WithTags("Metrics").Produces<MetricVersionView>(StatusCodes.Status201Created).RequirePermission(Permissions.ReportingManage);
    }

    private static IResult Fail(HttpContext context, ResultError error) => ApiError.Create(context, error.Code switch
    {
        "VALIDATION_ERROR" => StatusCodes.Status400BadRequest,
        "NOT_FOUND" => StatusCodes.Status404NotFound,
        "DUPLICATE" or "CONFLICT" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status422UnprocessableEntity
    }, error.Code, error.Message);

    private static bool TryDay(string? text, out DateOnly? day)
    {
        day = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        day = parsed;
        return true;
    }
}
