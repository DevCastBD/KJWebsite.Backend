using System.Security.Claims;
using AuthIdentityService.Data;
using AuthIdentityService.Data.Entities;
using AuthIdentityService.Tokens;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthIdentityService;

/// <summary>
/// The member portal's slice of Identity: the signed-in member's own sessions and everything Identity holds about them.
/// Every route reads the subject from the token; none takes an id of a person, so another member's data cannot be asked for.
/// </summary>
public static class MemberSelfEndpoints
{
    public static void MapMemberSelf(this WebApplication app)
    {
        app.MapGet("/api/v1/members/me/sessions", async (ClaimsPrincipal principal, RefreshTokenService sessions) =>
        {
            var current = principal.FindFirstValue(KjClaims.SessionId);
            var active = await sessions.ActiveSessionsAsync(principal.FindFirstValue(KjClaims.Subject)!);
            return Results.Ok(new
            {
                items = active.Select(s => new { id = s.FamilyId, device = s.UserAgent ?? string.Empty, createdAt = s.SessionCreatedAt, lastSeenAt = s.CreatedAt, current = s.FamilyId == current })
            });
        })
        .WithName("ListMemberSessions").WithSummary("The member's signed-in devices").WithTags("Member portal")
        .RequirePermission(Permissions.MemberProfileEdit);

        app.MapDelete("/api/v1/members/me/sessions/{id}", async (HttpContext context, string id, ClaimsPrincipal principal, RefreshTokenService sessions) =>
            await sessions.RevokeSessionAsync(id, principal.FindFirstValue(KjClaims.Subject))
                ? Results.NoContent()
                : ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Session not found."))
        .WithName("RevokeMemberSession").WithSummary("Sign one device out").WithTags("Member portal")
        .RequirePermission(Permissions.MemberProfileEdit);

        app.MapGet("/api/v1/auth/me/export", async (ClaimsPrincipal principal, AuthDbContext db, UserManager<AuthUserEntity> users, RefreshTokenService sessions) =>
        {
            var userId = principal.FindFirstValue(KjClaims.Subject)!;
            var user = await db.Users.AsNoTracking().Include(u => u.Profile).FirstOrDefaultAsync(u => u.Id == userId);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var active = await sessions.ActiveSessionsAsync(userId);
            var audit = await db.Set<AuditEvent>().AsNoTracking().Where(a => a.EntityId == userId || a.ActorId == userId).OrderBy(a => a.Id).ToListAsync();
            return Results.Ok(new
            {
                account = new { user.Id, user.Email, user.Status, user.CreatedAt, user.EmailConfirmed, user.ProvisionedFor, roles = await users.GetRolesAsync(user) },
                profile = user.Profile,
                sessions = active.Select(s => new { id = s.FamilyId, device = s.UserAgent ?? string.Empty, createdAt = s.SessionCreatedAt, lastSeenAt = s.CreatedAt }),
                audit = audit.Select(a => new { a.At, a.Service, a.Action, a.Entity, a.EntityId, a.Before, a.After, actedBySelf = a.ActorId == userId })
            });
        })
        .WithName("ExportMyAccount").WithSummary("Everything Identity holds about the signed-in user")
        .WithDescription("Account, profile, active sessions and audit entries about or by the caller. Never includes password hashes or token values. Used by the member data export.")
        .WithTags("Auth")
        .RequireAuthorization();
    }
}
