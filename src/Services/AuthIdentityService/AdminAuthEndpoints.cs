using System.Security.Claims;
using AuthIdentityService.Identity;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;

namespace AuthIdentityService;

public sealed record CreateRoleRequest(string? Name, List<string>? Permissions);

public sealed record SetPermissionsRequest(List<string>? Permissions);

public sealed record SetUserRolesRequest(List<string>? Roles);

/// <summary>Back-office role and user-role management (plan §3.1). Guards live in <see cref="RoleAdministration"/>.</summary>
public static class AdminAuthEndpoints
{
    private const string Base = "/api/v1/admin/auth";
    private const string Tag = "Admin — Roles and users";

    public static void MapAdminAuth(this WebApplication app)
    {
        app.MapGet($"{Base}/permissions", () => Results.Ok(new { items = RoleAdministration.Catalog }))
            .WithName("ListPermissions").WithSummary("List every permission key, grouped by module")
            .WithTags(Tag).RequirePermission(Permissions.RolesManage);

        app.MapGet($"{Base}/roles", async (RoleAdministration admin) =>
        {
            var items = await admin.ListRolesAsync();
            return Results.Ok(new PagingEnvelope<RoleView>([.. items], items.Count, null, items.Count, 0));
        })
        .WithName("ListRoles").WithSummary("List roles with their permissions and member counts")
        .WithTags(Tag).RequirePermission(Permissions.RolesManage);

        app.MapGet($"{Base}/roles/{{name}}", async (HttpContext context, RoleAdministration admin, string name) =>
            Respond(context, await admin.GetRoleAsync(name)))
        .WithName("GetRole").WithSummary("Get a role").WithTags(Tag).RequirePermission(Permissions.RolesManage);

        app.MapPost($"{Base}/roles", async (HttpContext context, ClaimsPrincipal user, RoleAdministration admin, CreateRoleRequest payload) =>
            Respond(context, await admin.CreateRoleAsync(user, payload.Name, payload.Permissions)))
        .WithName("CreateRole").WithSummary("Create a role")
        .WithDescription("You can only grant permissions you hold yourself. Changes are logged with before/after.")
        .WithTags(Tag).RequirePermission(Permissions.RolesManage);

        app.MapPut($"{Base}/roles/{{name}}/permissions", async (HttpContext context, ClaimsPrincipal user, RoleAdministration admin, string name, SetPermissionsRequest payload) =>
            Respond(context, await admin.SetPermissionsAsync(user, name, payload.Permissions)))
        .WithName("SetRolePermissions").WithSummary("Replace the permissions of a role")
        .WithDescription("`SuperAdmin` cannot be changed (409 SUPERADMIN_PROTECTED). Members' sessions end so the new permissions apply at their next sign-in.")
        .WithTags(Tag).RequirePermission(Permissions.RolesManage);

        app.MapDelete($"{Base}/roles/{{name}}", async (HttpContext context, ClaimsPrincipal user, RoleAdministration admin, string name) =>
        {
            var result = await admin.DeleteRoleAsync(user, name);
            return result.Ok ? Results.NoContent() : Problem(context, result);
        })
        .WithName("DeleteRole").WithSummary("Delete a custom role")
        .WithDescription("Built-in roles and `SuperAdmin` cannot be deleted, nor can a role that still has members.")
        .WithTags(Tag).RequirePermission(Permissions.RolesManage);

        app.MapGet($"{Base}/users", async (HttpContext context, RoleAdministration admin, string? role, int? limit, int? offset) =>
        {
            var safeLimit = limit.GetValueOrDefault(20);
            var safeOffset = offset.GetValueOrDefault(0);
            if (safeLimit is < 1 or > 100 || safeOffset < 0)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid paging values.");
            }

            var (items, total) = await admin.ListUsersAsync(role, safeLimit, safeOffset);
            return Results.Ok(new PagingEnvelope<UserRolesView>([.. items], total, null, safeLimit, safeOffset));
        })
        .WithName("ListUsers").WithSummary("List users with their roles").WithTags(Tag).RequirePermission(Permissions.UsersView);

        app.MapPut($"{Base}/users/{{id}}/roles", async (HttpContext context, ClaimsPrincipal user, RoleAdministration admin, string id, SetUserRolesRequest payload) =>
            Respond(context, await admin.SetUserRolesAsync(user, id, payload.Roles)))
        .WithName("SetUserRoles").WithSummary("Replace the roles of a user")
        .WithDescription("Granting a role needs every permission the role holds. Only a SuperAdmin can grant or remove `SuperAdmin`, and the last active SuperAdmin cannot be removed (409 LAST_SUPERADMIN). The user's sessions end.")
        .WithTags(Tag).RequirePermission(Permissions.UsersManage);
    }

    private static IResult Respond<T>(HttpContext context, AdminResult<T> result) => result.Ok
        ? Results.Json(result.Value, statusCode: result.Status)
        : Problem(context, result);

    private static IResult Problem<T>(HttpContext context, AdminResult<T> result) => ApiError.Create(context, result.Status, result.Code!, result.Message!);
}
