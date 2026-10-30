using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Intake;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Self;

/// <summary>The reviewer's side of the moderation queue for sensitive profile edits.</summary>
public static class ModerationEndpoints
{
    private const string Route = "/api/v1/admin/membership/profile-changes";

    public static void MapProfileModeration(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (MembershipDbContext db, IFieldProtector protector, string? status) =>
        {
            var wanted = string.IsNullOrWhiteSpace(status) ? ProfileChangeStatuses.Pending : status;
            var changes = (await db.ProfileChanges.AsNoTracking().Where(c => c.Status == wanted).ToListAsync()).OrderBy(c => c.SubmittedAt).ToList();
            var ids = changes.Select(c => c.MemberId).Distinct().ToList();
            var members = await db.Members.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id);
            return Results.Ok(new
            {
                items = changes.Where(c => members.ContainsKey(c.MemberId)).Select(c =>
                {
                    var m = members[c.MemberId];
                    return new ProfileChangeView(c.Id, m.Id, m.MemberCode, $"{m.FirstName} {m.LastName}", c.Field, ProfileFields.Current(m, protector, c.Field), protector.Unprotect(c.ValueProtected), c.Status, c.Reason, c.SubmittedAt, c.DecidedAt);
                })
            });
        })
        .WithName("ListProfileChanges").WithSummary("Sensitive profile edits waiting for review (status=pending|approved|rejected)").WithTags("Membership admin")
        .RequirePermission(Permissions.MembershipManage);

        app.MapPost($"{Route}/{{id}}/approve", async (HttpContext context, string id, MembershipDbContext db, IFieldProtector protector, TimeProvider clock) =>
            await Decide(context, id, db, clock, null, (change, member) => ProfileFields.Apply(member, protector, change.Field, protector.Unprotect(change.ValueProtected)), ProfileChangeStatuses.Approved))
        .WithName("ApproveProfileChange").WithSummary("Apply a member's sensitive edit").WithTags("Membership admin")
        .RequirePermission(Permissions.MembershipManage);

        app.MapPost($"{Route}/{{id}}/reject", async (HttpContext context, string id, DecisionRequest payload, MembershipDbContext db, TimeProvider clock) =>
            string.IsNullOrWhiteSpace(payload.Reason) || payload.Reason.Trim().Length > 1000
                ? ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: reason.")
                : await Decide(context, id, db, clock, payload.Reason.Trim(), (_, _) => { }, ProfileChangeStatuses.Rejected))
        .WithName("RejectProfileChange").WithSummary("Refuse a member's sensitive edit, with a reason the member can read").WithTags("Membership admin")
        .RequirePermission(Permissions.MembershipManage);
    }

    private static async Task<IResult> Decide(HttpContext context, string id, MembershipDbContext db, TimeProvider clock, string? reason, Action<ProfileChangeEntity, MemberEntity> apply, string outcome)
    {
        var change = await db.ProfileChanges.FirstOrDefaultAsync(c => c.Id == id);
        if (change is null)
        {
            return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Change not found.");
        }

        if (change.Status != ProfileChangeStatuses.Pending)
        {
            return ApiError.Create(context, StatusCodes.Status409Conflict, "ALREADY_DECIDED", "This change has already been decided.");
        }

        var member = await db.Members.FirstAsync(m => m.Id == change.MemberId);
        apply(change, member);
        change.Status = outcome;
        change.Reason = reason;
        change.DecidedAt = clock.GetUtcNow();
        change.DecidedBy = context.User.FindFirst(KjClaims.Subject)?.Value ?? "unknown";
        await db.SaveChangesAsync();
        return Results.Ok(new { id = change.Id, status = change.Status });
    }
}
