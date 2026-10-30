using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Committees;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Intake;
using MembershipService.Privacy;
using MembershipService.Review;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Lifecycle;

/// <summary>Moves members through leave, alumni, termination and back. History is only ever added to.</summary>
public static class LifecycleEndpoints
{
    private const string Route = "/api/v1/admin/membership/members/{id}";

    public static bool CanTerminate(HttpContext context) =>
        context.User.FindAll(KjClaims.Permission).Any(c => c.Value == Permissions.MembershipTerminate);

    public static void MapLifecycle(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{Route}/lifecycle", async (HttpContext context, string id, MembershipDbContext db, IFieldProtector protector, TimeProvider clock) =>
        {
            var member = await db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
            return member is null
                ? ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Member not found.")
                : Results.Ok(await View(db, protector, member, CanTerminate(context), clock));
        })
        .WithName("GetMemberLifecycle")
        .WithSummary("A member's status history and tenure")
        .WithDescription("Termination reasons are returned only to callers with `membership.terminate`; others see `reasonRestricted: true`. Tenure is the time spent active, summed over every stretch of service.")
        .WithTags("Membership admin")
        .RequirePermission(Permissions.MembershipLifecycle);

        app.MapPost($"{Route}/status", async (HttpContext context, string id, StatusChangeRequest payload, MembershipDbContext db, IFieldProtector protector, IAuthProvisioningClient identity, Microsoft.Extensions.Options.IOptions<PrivacyOptions> privacy, TimeProvider clock) =>
        {
            var member = await db.Members.FirstOrDefaultAsync(m => m.Id == id);
            if (member is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Member not found.");
            }

            var now = clock.GetUtcNow();
            var today = CommitteeEndpoints.Today(clock);
            var to = payload.To ?? string.Empty;
            if (MemberStatusMachine.Check(member.Status, to) is { } refused)
            {
                return ApiError.Create(context, refused == MemberStatusMachine.UnknownStatus ? StatusCodes.Status400BadRequest : StatusCodes.Status409Conflict, refused.ToUpperInvariant(), refused == MemberStatusMachine.UnknownStatus ? "Invalid fields: to." : $"A {member.Status} member cannot become {to}.");
            }

            if (MemberStatusMachine.NeedsTerminatePermission(member.Status, to) && !CanTerminate(context))
            {
                return ApiError.Create(context, StatusCodes.Status403Forbidden, "FORBIDDEN", "Terminating or reinstating a member needs the terminate permission.");
            }

            var last = (await db.MemberStatusEvents.AsNoTracking().Where(e => e.MemberId == id).ToListAsync()).OrderByDescending(e => e.EffectiveOn).ThenByDescending(e => e.Id).FirstOrDefault();
            var effective = payload.EffectiveOn ?? today;
            var invalid = new List<string>();
            if (effective > today || (last is not null && effective < last.EffectiveOn))
            {
                invalid.Add("effectiveOn");
            }

            if (payload.Note is { Length: > 1000 })
            {
                invalid.Add("note");
            }

            var reason = payload.Reason?.Trim();
            if (to == MemberStatuses.Terminated && (string.IsNullOrEmpty(reason) || reason.Length > 2000))
            {
                invalid.Add("reason");
            }

            if (invalid.Count > 0)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
            }

            var from = member.Status;
            var actor = context.User.FindFirst(KjClaims.Subject)?.Value ?? "unknown";
            var token = context.Request.Headers.Authorization.ToString() is { Length: > 7 } h && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..] : null;

            // The record, its side effects and the Identity switch stand or fall together: Identity is called inside the open
            // transaction, so a refusal rolls everything back and the member keeps their status.
            await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
            member.Status = to;
            db.MemberStatusEvents.Add(new MemberStatusEventEntity
            {
                MemberId = id,
                FromStatus = from,
                ToStatus = to,
                EffectiveOn = effective,
                At = now,
                ActorId = actor,
                Note = string.IsNullOrWhiteSpace(payload.Note) ? null : payload.Note.Trim(),
                ReasonProtected = to == MemberStatuses.Terminated ? protector.Protect(reason!) : null
            });

            if (to is MemberStatuses.Alumni or MemberStatuses.Terminated or MemberStatuses.Inactive)
            {
                // Someone who has left no longer holds a seat; the term stays as history, ended on the day they stopped serving.
                var open = await db.CommitteeMembers.Where(t => t.MemberId == id).ToListAsync();
                foreach (var term in open.Where(t => t.EndsOn is null || t.EndsOn > effective))
                {
                    term.EndsOn = effective < term.StartsOn ? term.StartsOn : effective;
                }
            }

            if (to == MemberStatuses.Terminated)
            {
                WithdrawAllConsent(db, member, now, actor, privacy.Value.NoticeVersion);
            }

            await db.SaveChangesAsync(context.RequestAborted);

            if (to == MemberStatuses.Terminated || from == MemberStatuses.Terminated)
            {
                if (!await identity.SetAccountStatusAsync(member.UserId, enabled: to != MemberStatuses.Terminated, token, context.RequestAborted))
                {
                    await transaction.RollbackAsync(context.RequestAborted);
                    return ApiError.Create(context, StatusCodes.Status502BadGateway, "IDENTITY_UNAVAILABLE", "The login could not be updated, so nothing was changed. Try again.");
                }
            }

            await transaction.CommitAsync(context.RequestAborted);
            return Results.Ok(await View(db, protector, member, CanTerminate(context), clock));
        })
        .WithName("ChangeMemberStatus")
        .WithSummary("Move a member to on_leave, inactive, alumni, terminated or back to active")
        .WithDescription("Allowed moves: active to on_leave/inactive/alumni/terminated; on_leave and inactive back to active or on to alumni/terminated; alumni to active/terminated; terminated to active. `effectiveOn` defaults to today and may not be in the future or before the previous change. `reason` is required to terminate and is readable only with `membership.terminate`. Terminating or reinstating needs that permission and switches the Identity login off or on; ending service (alumni, inactive, terminated) ends the member's open committee terms; terminating withdraws all publication consent. Reactivation restores nothing automatically: the member opts in again.")
        .WithTags("Membership admin")
        .RequirePermission(Permissions.MembershipLifecycle);
    }

    private static void WithdrawAllConsent(MembershipDbContext db, MemberEntity member, DateTimeOffset now, string actor, string notice)
    {
        void Off(string field, bool was, Action clear)
        {
            if (was)
            {
                clear();
                ConsentLedger.Record(db, "member", member.Id, field, false, now, "staff", actor, notice);
            }
        }

        Off(ConsentPurposes.PublishName, member.ConsentName, () => member.ConsentName = false);
        Off(ConsentPurposes.PublishPhotograph, member.ConsentPhotograph, () => member.ConsentPhotograph = false);
        Off(ConsentPurposes.PublishExpertise, member.ConsentExpertise, () => member.ConsentExpertise = false);
        Off(ConsentPurposes.PublishAlumni, member.ConsentAlumni, () => member.ConsentAlumni = false);
    }

    public static async Task<LifecycleView> View(MembershipDbContext db, IFieldProtector protector, MemberEntity member, bool showReasons, TimeProvider clock)
    {
        var events = (await db.MemberStatusEvents.AsNoTracking().Where(e => e.MemberId == member.Id).ToListAsync()).OrderBy(e => e.EffectiveOn).ThenBy(e => e.At).ThenBy(e => e.Id).ToList();
        var history = events.Select(e => new LifecycleEvent(
            e.FromStatus, e.ToStatus, e.EffectiveOn, e.At, e.ActorId, e.Note,
            showReasons && e.ReasonProtected is not null ? protector.Unprotect(e.ReasonProtected) : null,
            !showReasons && e.ReasonProtected is not null)).ToList();
        return new LifecycleView(member.Id, member.MemberCode, member.Status, MemberStatusMachine.NextFrom(member.Status), history, TenureCalculator.Calculate(events, CommitteeEndpoints.Today(clock)));
    }
}
