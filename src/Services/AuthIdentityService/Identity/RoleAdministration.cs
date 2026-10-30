using System.Data;
using System.Security.Claims;
using System.Text.RegularExpressions;
using AuthIdentityService.Data;
using AuthIdentityService.Data.Entities;
using AuthIdentityService.Tokens;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthIdentityService.Identity;

public sealed record RoleView(string Name, bool IsSystem, List<string> Permissions, int MemberCount);

public sealed record UserRolesView(string Id, string? Email, string Status, List<string> Roles);

/// <summary>Outcome of an administrative change: a value, or a status/code/message the endpoint turns into a problem response.</summary>
public sealed record AdminResult<T>(T? Value, int Status = 200, string? Code = null, string? Message = null)
{
    public bool Ok => Code is null;
}

public static class AdminFail
{
    public static AdminResult<T> Of<T>(int status, string code, string message) => new(default, status, code, message);
}

/// <summary>
/// Role and permission management. Every rule that keeps the organisation from locking itself out or escalating privilege lives here:
/// SuperAdmin is fixed and cannot be deleted, the last active SuperAdmin cannot be removed, built-in roles cannot be deleted, a role in use
/// cannot be deleted, and nobody can hand out a permission they do not hold. Each change ends the affected users' sessions so a stale
/// token cannot keep a revoked permission, and is logged with before/after (persisted by the audit log, BE-033).
/// </summary>
public sealed partial class RoleAdministration(
    RoleManager<AuthRoleEntity> roles, UserManager<AuthUserEntity> users, AuthDbContext db, RefreshTokenService sessions, ILogger<RoleAdministration> logger)
{
    // One SuperAdmin change at a time per process; the serializable transaction below covers other processes.
    private static readonly SemaphoreSlim SuperAdminGate = new(1, 1);

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9 _-]{1,48}$")]
    private static partial Regex RoleNamePattern();

    public static IReadOnlyList<PermissionInfo> Catalog => PermissionCatalog.Entries;

    // ---- roles

    public async Task<List<RoleView>> ListRolesAsync()
    {
        var counts = await db.UserRoles.GroupBy(r => r.RoleId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count);
        var result = new List<RoleView>();
        foreach (var role in await roles.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync())
        {
            result.Add(new RoleView(role.Name!, IsSystem(role.Name!), await PermissionsOfAsync(role), counts.GetValueOrDefault(role.Id)));
        }

        return result;
    }

    public async Task<AdminResult<RoleView>> GetRoleAsync(string name)
    {
        var role = await roles.FindByNameAsync(name);
        return role is null ? NotFound<RoleView>("Role not found.") : new(await ViewAsync(role));
    }

    public async Task<AdminResult<RoleView>> CreateRoleAsync(ClaimsPrincipal actor, string? name, List<string>? permissions)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (!RoleNamePattern().IsMatch(trimmed))
        {
            return Invalid<RoleView>("name must start with a letter and use 2-49 letters, digits, spaces, hyphens or underscores.");
        }

        var granted = Distinct(permissions);
        if (Unknown(granted) is { } unknown)
        {
            return Invalid<RoleView>($"Unknown permission '{unknown}'.");
        }

        if (Escalation(actor, granted) is { } blocked)
        {
            return Forbidden<RoleView>(blocked);
        }

        if (await roles.FindByNameAsync(trimmed) is not null)
        {
            return AdminFail.Of<RoleView>(StatusCodes.Status409Conflict, "ROLE_EXISTS", $"Role '{trimmed}' already exists.");
        }

        var created = await roles.CreateAsync(new AuthRoleEntity(trimmed));
        if (!created.Succeeded)
        {
            return Invalid<RoleView>(string.Join(" ", created.Errors.Select(e => e.Description)));
        }

        var role = (await roles.FindByNameAsync(trimmed))!;
        foreach (var permission in granted)
        {
            await roles.AddClaimAsync(role, new Claim(KjClaims.Permission, permission));
        }

        LogChange(actor, "role.create", trimmed, "[]", string.Join(",", granted));
        return new(await ViewAsync(role), StatusCodes.Status201Created);
    }

    public async Task<AdminResult<RoleView>> SetPermissionsAsync(ClaimsPrincipal actor, string name, List<string>? permissions)
    {
        var role = await roles.FindByNameAsync(name);
        if (role is null)
        {
            return NotFound<RoleView>("Role not found.");
        }

        if (role.Name == KjRoles.SuperAdmin)
        {
            return SuperAdminProtected<RoleView>("SuperAdmin always holds every permission and cannot be changed.");
        }

        var next = Distinct(permissions);
        if (Unknown(next) is { } unknown)
        {
            return Invalid<RoleView>($"Unknown permission '{unknown}'.");
        }

        var before = await PermissionsOfAsync(role);
        // Editing a role needs every permission it holds before or after, so a lesser admin cannot strip or extend a stronger role.
        if (Escalation(actor, [.. before.Union(next)]) is { } blocked)
        {
            return Forbidden<RoleView>(blocked);
        }

        var held = (await roles.GetClaimsAsync(role)).Where(c => c.Type == KjClaims.Permission).ToList();
        foreach (var claim in held.Where(c => !next.Contains(c.Value)))
        {
            await roles.RemoveClaimAsync(role, claim);
        }

        foreach (var permission in next.Where(p => held.All(c => c.Value != p)))
        {
            await roles.AddClaimAsync(role, new Claim(KjClaims.Permission, permission));
        }

        await RevokeSessionsOfRoleAsync(role);
        LogChange(actor, "role.permissions", role.Name!, string.Join(",", before), string.Join(",", next));
        return new(await ViewAsync(role));
    }

    public async Task<AdminResult<bool>> DeleteRoleAsync(ClaimsPrincipal actor, string name)
    {
        var role = await roles.FindByNameAsync(name);
        if (role is null)
        {
            return NotFound<bool>("Role not found.");
        }

        if (role.Name == KjRoles.SuperAdmin)
        {
            return SuperAdminProtected<bool>("SuperAdmin cannot be deleted.");
        }

        if (IsSystem(role.Name!))
        {
            return AdminFail.Of<bool>(StatusCodes.Status409Conflict, "SYSTEM_ROLE", "Built-in roles cannot be deleted; remove their permissions instead.");
        }

        if (await db.UserRoles.AnyAsync(r => r.RoleId == role.Id))
        {
            return AdminFail.Of<bool>(StatusCodes.Status409Conflict, "ROLE_IN_USE", "Move the role's members to another role first.");
        }

        var before = await PermissionsOfAsync(role);
        if (Escalation(actor, before) is { } blocked)
        {
            return Forbidden<bool>(blocked);
        }

        await roles.DeleteAsync(role);
        LogChange(actor, "role.delete", name, string.Join(",", before), "[]");
        return new(true);
    }

    // ---- users

    public async Task<(List<UserRolesView> Items, int Total)> ListUsersAsync(string? role, int limit, int offset)
    {
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(role))
        {
            var roleId = await roles.Roles.Where(r => r.NormalizedName == roles.NormalizeKey(role)).Select(r => r.Id).FirstOrDefaultAsync();
            query = query.Where(u => db.UserRoles.Any(r => r.UserId == u.Id && r.RoleId == roleId));
        }

        var total = await query.CountAsync();
        var page = await query.OrderBy(u => u.Email).Skip(offset).Take(limit).Select(u => new { u.Id, u.Email, u.Status }).ToListAsync();
        var ids = page.Select(u => u.Id).ToList();
        var assignments = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ids.Contains(ur.UserId) select new { ur.UserId, r.Name }).ToListAsync();
        return ([.. page.Select(u => new UserRolesView(u.Id, u.Email, u.Status, [.. assignments.Where(a => a.UserId == u.Id).Select(a => a.Name!).OrderBy(n => n, StringComparer.Ordinal)]))], total);
    }

    public async Task<AdminResult<UserRolesView>> SetUserRolesAsync(ClaimsPrincipal actor, string userId, List<string>? roleNames)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            return NotFound<UserRolesView>("User not found.");
        }

        var wanted = (roleNames ?? []).Select(r => r?.Trim() ?? string.Empty).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var resolved = new List<AuthRoleEntity>();
        foreach (var name in wanted)
        {
            var role = await roles.FindByNameAsync(name);
            if (role is null)
            {
                return Invalid<UserRolesView>($"Unknown role '{name}'.");
            }

            resolved.Add(role);
        }

        await SuperAdminGate.WaitAsync();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var current = (await users.GetRolesAsync(user)).ToList();
            var next = resolved.Select(r => r.Name!).ToList();
            var added = next.Except(current, StringComparer.Ordinal).ToList();
            var removed = current.Except(next, StringComparer.Ordinal).ToList();
            if (added.Count == 0 && removed.Count == 0)
            {
                return new(await ViewUserAsync(user));
            }

            var touchesSuperAdmin = added.Concat(removed).Contains(KjRoles.SuperAdmin);
            if (touchesSuperAdmin && !actor.HasClaim(KjClaims.Role, KjRoles.SuperAdmin))
            {
                return Forbidden<UserRolesView>("Only a SuperAdmin can grant or remove the SuperAdmin role.");
            }

            if (removed.Contains(KjRoles.SuperAdmin) && !await AnotherActiveSuperAdminExistsAsync(user.Id))
            {
                return AdminFail.Of<UserRolesView>(StatusCodes.Status409Conflict, "LAST_SUPERADMIN", "The last active SuperAdmin cannot be removed. Promote someone else first.");
            }

            foreach (var name in added)
            {
                var role = resolved.First(r => r.Name == name);
                if (Escalation(actor, await PermissionsOfAsync(role)) is { } blocked)
                {
                    return Forbidden<UserRolesView>(blocked);
                }
            }

            if (removed.Count > 0)
            {
                await users.RemoveFromRolesAsync(user, removed);
            }

            if (added.Count > 0)
            {
                await users.AddToRolesAsync(user, added);
            }

            await transaction.CommitAsync();
            await sessions.RevokeAllAsync(user.Id);
            LogChange(actor, "user.roles", user.Id, string.Join(",", current), string.Join(",", next));
            return new(await ViewUserAsync(user));
        }
        finally
        {
            SuperAdminGate.Release();
        }
    }

    // ---- internals

    private async Task<bool> AnotherActiveSuperAdminExistsAsync(string excludingUserId)
    {
        var superAdmins = await users.GetUsersInRoleAsync(KjRoles.SuperAdmin);
        return superAdmins.Any(u => u.Id != excludingUserId && u.Status == "active");
    }

    private async Task RevokeSessionsOfRoleAsync(AuthRoleEntity role)
    {
        foreach (var userId in await db.UserRoles.Where(r => r.RoleId == role.Id).Select(r => r.UserId).ToListAsync())
        {
            await sessions.RevokeAllAsync(userId);
        }
    }

    private async Task<RoleView> ViewAsync(AuthRoleEntity role) =>
        new(role.Name!, IsSystem(role.Name!), await PermissionsOfAsync(role), await db.UserRoles.CountAsync(r => r.RoleId == role.Id));

    private async Task<UserRolesView> ViewUserAsync(AuthUserEntity user) =>
        new(user.Id, user.Email, user.Status, [.. (await users.GetRolesAsync(user)).OrderBy(n => n, StringComparer.Ordinal)]);

    private async Task<List<string>> PermissionsOfAsync(AuthRoleEntity role) =>
        [.. (await roles.GetClaimsAsync(role)).Where(c => c.Type == KjClaims.Permission).Select(c => c.Value).OrderBy(v => v, StringComparer.Ordinal)];

    private static bool IsSystem(string name) => KjRoles.Seed.ContainsKey(name);

    private static List<string> Distinct(List<string>? permissions) =>
        [.. (permissions ?? []).Select(p => p?.Trim() ?? string.Empty).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal)];

    private static string? Unknown(IEnumerable<string> permissions) => permissions.FirstOrDefault(p => !Permissions.All.Contains(p));

    /// <summary>A caller may only hand out, or edit, what they hold themselves. Returns the message when they would exceed their own permissions.</summary>
    private static string? Escalation(ClaimsPrincipal actor, IEnumerable<string> permissions) =>
        permissions.FirstOrDefault(p => !actor.HasPermission(p)) is { } missing
            ? $"You cannot grant or change '{missing}' because you do not hold it yourself."
            : null;

    private static AdminResult<T> NotFound<T>(string message) => AdminFail.Of<T>(StatusCodes.Status404NotFound, "NOT_FOUND", message);

    private static AdminResult<T> Invalid<T>(string message) => AdminFail.Of<T>(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", message);

    private static AdminResult<T> Forbidden<T>(string message) => AdminFail.Of<T>(StatusCodes.Status403Forbidden, "FORBIDDEN", message);

    private static AdminResult<T> SuperAdminProtected<T>(string message) => AdminFail.Of<T>(StatusCodes.Status409Conflict, "SUPERADMIN_PROTECTED", message);

    private void LogChange(ClaimsPrincipal actor, string action, string target, string before, string after)
    {
        var actorId = actor.FindFirstValue(KjClaims.Subject) ?? "unknown";
        LogRbacChange(logger, action, actorId, target, before, after);
    }

    [LoggerMessage(Level = LogLevel.Information, EventId = 4101, Message = "RBAC {Action} by {Actor} on {Target}: before=[{Before}] after=[{After}]")]
    private static partial void LogRbacChange(ILogger logger, string action, string actor, string target, string before, string after);
}
