using System.Globalization;
using System.Text;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReportingService.Data;

namespace ReportingService.Analytics;

public sealed record FunnelStepView(string Step, int Entered, int Completed, int Abandoned, string DropOffPercent);

public sealed record FunnelView(string Funnel, string From, string To, int Visits, int Converted, int Abandoned, int InProgress, string ConversionPercent, IReadOnlyList<FunnelStepView> Steps,
    IReadOnlyDictionary<string, int> Events);

public static class AnalyticsEndpoints
{
    public const string CollectorRoute = "/api/v1/analytics/events";
    private const string FunnelRoute = "/api/v1/admin/reporting/funnels";
    private const int MaxDays = 366;

    public static void MapAnalytics(this IEndpointRouteBuilder app)
    {
        // Anonymous on purpose: the public site posts here. It takes a fixed set of event names and short tokens, stores no IP or cookie, and answers 202 with no body.
        app.MapPost(CollectorRoute, async (HttpContext context, AnalyticsStore store, IOptions<AnalyticsOptions> options, CancellationToken ct) =>
        {
            var limit = Math.Max(256, options.Value.MaxBodyBytes);
            using var buffer = new MemoryStream();
            var chunk = new byte[512];
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > limit)
                {
                    return ApiError.Create(context, StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE", "The event is too large.");
                }
            }

            // The frontend posts text/plain so the browser sends no preflight; the body is JSON either way.
            var optedOut = context.Request.Headers["DNT"] == "1" || context.Request.Headers["Sec-GPC"] == "1";
            var result = await store.IngestAsync(Encoding.UTF8.GetString(buffer.ToArray()), context.Connection.RemoteIpAddress?.ToString(), context.Request.Headers.UserAgent.ToString(), optedOut, ct);
            return result.Outcome switch
            {
                IngestOutcome.Accepted or IngestOutcome.Ignored => Results.Accepted(),
                IngestOutcome.TooMany => ApiError.Create(context, StatusCodes.Status429TooManyRequests, "RATE_LIMITED", "Too many events; try again shortly."),
                _ => ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", result.Message ?? "Invalid event.")
            };
        })
        .WithName("CollectAnalyticsEvent").WithSummary("Record one anonymous site event")
        .WithDescription("Body `{name, props?: {step?, field?, run?}, path?}` as JSON (text/plain is accepted). `name` is one of the documented event names (see docs/ANALYTICS_EVENTS.md); `props` may hold only `step`, `field` and `run`, each a token of up to 40 letters, digits, `.`, `-` or `_`. No cookie is set and the IP is hashed with the user agent and the day, never stored. Do Not Track and Global Privacy Control are honoured (202, nothing stored). 202 on success, 400 for an unknown name or a bad value, 429 when one visitor floods.")
        .WithTags("Analytics").AllowAnonymous();

        app.MapGet($"{FunnelRoute}/{{funnel}}", async (HttpContext context, ReportingDbContext db, TimeProvider clock, string funnel, string? from, string? to, CancellationToken ct) =>
        {
            var application = funnel == "application";
            if (!application && funnel != "donation")
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Funnels are `application` and `donation`.");
            }

            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            if (!TryDay(from, out var start) || !TryDay(to, out var end))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "from and to must be dates (yyyy-MM-dd).");
            }

            var last = end ?? today;
            var first = start ?? last.AddDays(-29);
            if (last < first || last.DayNumber - first.DayNumber >= MaxDays)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"The window must be 1-{MaxDays} days with to not before from.");
            }

            var low = first.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var high = last.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var prefix = application ? "application_" : "don";
            var events = await db.AnalyticsEvents.AsNoTracking().Where(e => e.At >= low && e.At < high).Select(e => new { e.Name, e.Step, e.Run }).ToListAsync(ct);
            events = [.. events.Where(e => application ? e.Name.StartsWith("application_", StringComparison.Ordinal) || e.Name == AnalyticsEvents.RegistrationSubmitSuccess : e.Name.StartsWith(prefix, StringComparison.Ordinal))];

            var steps = application ? AnalyticsEvents.ApplicationSteps : AnalyticsEvents.DonationSteps;
            var view = application ? AnalyticsEvents.ApplicationStepView : AnalyticsEvents.DonationStepView;
            var complete = application ? AnalyticsEvents.ApplicationStepComplete : AnalyticsEvents.DonationStepComplete;
            var abandon = application ? AnalyticsEvents.ApplicationAbandon : AnalyticsEvents.DonationAbandon;
            int Distinct(string name, string step) => events.Where(e => e.Name == name && e.Step == step && e.Run is not null).Select(e => e.Run).Distinct().Count();

            var stepViews = steps.Select(s =>
            {
                var entered = Distinct(view, s);
                var completed = Distinct(complete, s);
                return new FunnelStepView(s, entered, completed, Distinct(abandon, s), entered > 0 ? ((entered - completed) * 100m / entered).ToString("0.0", CultureInfo.InvariantCulture) : "0.0");
            }).ToList();

            var runs = events.Where(e => e.Run is not null).Select(e => e.Run).Distinct().Count();
            var converted = Distinct(complete, steps[^1]);
            var abandoned = events.Where(e => e.Name == abandon && e.Run is not null).Select(e => e.Run).Distinct().Count();
            var counts = events.GroupBy(e => e.Name).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
            return Results.Ok(new FunnelView(funnel, first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), runs, converted, abandoned,
                Math.Max(0, runs - converted - abandoned), runs > 0 ? (converted * 100m / runs).ToString("0.0", CultureInfo.InvariantCulture) : "0.0", stepViews, counts));
        })
        .WithName("GetFunnel").WithSummary("Per-step entry, completion and abandonment for the application or donation funnel")
        .WithDescription("`funnel` is `application` or `donation`; `from` and `to` default to the last 30 days (at most 366). A visit is one `run`. `entered`, `completed` and `abandoned` count distinct visits per step; `dropOffPercent` is entered-but-not-completed. `converted` is visits that completed the last step, `abandoned` those that left, `inProgress` the rest. `events` counts every event of the funnel, including those that carry no run. Requires `reporting.view`.")
        .WithTags("Analytics").Produces<FunnelView>().RequirePermission(Permissions.ReportingView);
    }

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
