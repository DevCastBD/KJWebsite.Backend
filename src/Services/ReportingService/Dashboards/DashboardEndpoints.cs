using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;

namespace ReportingService.Dashboards;

public static class DashboardEndpoints
{
    private const string Route = "/api/v1/admin/reporting/dashboards";

    public static void MapDashboards(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (HttpContext context, DashboardService dashboards, int? months) =>
        {
            if (months is < 1 or > DashboardService.MaxMonths)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"months must be 1-{DashboardService.MaxMonths}.");
            }

            return Results.Ok(await dashboards.BuildAsync(context.User, null, months, context.RequestAborted));
        })
        .WithName("GetDashboards")
        .WithSummary("The caller's dashboard: only the sections their permissions allow")
        .WithDescription("Sections are `donations` and `expenses` (`finance.view`), `membership` (`membership.manage`), `events` (`events.manage`), `content` (`content.view`), `inbox` (`cta.view`) and `programme` (`programme.manage`). A section the caller may not see is not in the response at all. Every figure and series carries a `trace` naming its warehouse metric and the call that returns its rows. All figures read the warehouse, never a transactional table; `asOf` and `stale` say how fresh they are. `months` is 1-36 (default 12). Requires `dashboard.view`.")
        .WithTags("Dashboards")
        .Produces<DashboardView>()
        .RequirePermission(Permissions.DashboardView);

        app.MapGet($"{Route}/{{section}}", async (HttpContext context, DashboardService dashboards, string section, int? months) =>
        {
            var needed = DashboardService.PermissionFor(section);
            if (needed is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "No such dashboard section.");
            }

            if (!context.User.HasClaim(KjClaims.Permission, needed))
            {
                return ApiError.Create(context, StatusCodes.Status403Forbidden, "FORBIDDEN", "You do not have access to this dashboard section.");
            }

            if (months is < 1 or > DashboardService.MaxMonths)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"months must be 1-{DashboardService.MaxMonths}.");
            }

            return Results.Ok(await dashboards.BuildAsync(context.User, section, months, context.RequestAborted));
        })
        .WithName("GetDashboardSection")
        .WithSummary("One dashboard section")
        .WithDescription("Same shape as the whole dashboard with a single section; 403 when the caller lacks that section's permission, 404 for an unknown section. Requires `dashboard.view`.")
        .WithTags("Dashboards")
        .Produces<DashboardView>()
        .RequirePermission(Permissions.DashboardView);
    }
}
