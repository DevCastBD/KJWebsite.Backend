using System.Text.RegularExpressions;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Self;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Volunteering;

public sealed record HourRequest(string? ProjectSlug, string? EventSlug, DateOnly? Date, int? Minutes, string? Description);

public sealed record ReasonBody(string? Reason);

public sealed record HourView(string Id, string ProjectSlug, string? EventSlug, DateOnly Date, int Minutes, string Description, string Status, DateTimeOffset SubmittedAt, DateTimeOffset? ReviewedAt, string? RejectionReason);

public sealed record AdminHourView(string Id, string MemberId, string MemberCode, string MemberName, string ProjectSlug, string? EventSlug, DateOnly Date, int Minutes, string Description, string Status, DateTimeOffset SubmittedAt, DateTimeOffset? ReviewedAt, string? ReviewedBy, string? RejectionReason);

public sealed record RollupRow(string Key, string? Name, int ApprovedMinutes, double ApprovedHours, int ApprovedEntries, int PendingEntries, string Fingerprint);

public sealed record ShiftInput(DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, int? Slots);

public sealed record OpportunityRequest(string? Slug, string? TitleEn, string? TitleBn, string? DescriptionEn, string? DescriptionBn, string? ProjectSlug, List<string>? Skills, bool? IsOpen, List<ShiftInput>? Shifts);

public sealed record ShiftView(string Id, DateTimeOffset StartsAt, DateTimeOffset EndsAt, int Slots, int SignedUp, int Free, bool? Mine);

public sealed record OpportunityView(string Id, string Slug, string TitleEn, string TitleBn, string DescriptionEn, string DescriptionBn, string? ProjectSlug, List<string> Skills, bool IsOpen, List<ShiftView> Shifts, int? MatchScore, List<string>? MatchedSkills);

public sealed record MatchView(string MemberId, string MemberCode, string Name, List<string> MatchedSkills);

/// <summary>
/// Volunteer operations. Hours need approval before they count anywhere, and never by the member who logged them. Rollups and totals are
/// sums over approved rows only, each with a fingerprint of the entries behind it so a published figure can be traced back (#89).
/// Skill matching uses `Expertise`: a member always sees their own match; the organiser-side suggestion list only includes members who
/// consented to publish expertise.
/// </summary>
public static partial class VolunteerEndpoints
{
    private const string Me = "/api/v1/members/me/volunteer";
    private const string Admin = "/api/v1/admin/membership/volunteer";
    private const string Tag = "Volunteer operations";
    private const int MaxMinutesPerEntry = 24 * 60;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    public static void MapVolunteering(this IEndpointRouteBuilder app)
    {
        MapMember(app);
        MapReview(app);
        MapOpportunities(app);
    }

    // ---- the volunteer

    private static void MapMember(IEndpointRouteBuilder app)
    {
        app.MapPost($"{Me}/hours", (HttpContext context, HourRequest payload, MembershipDbContext db, TimeProvider clock) =>
            SelfEndpoints.WithMember(context, db, async member =>
            {
                var entry = new VolunteerHourEntity { Id = $"vh_{Guid.NewGuid():N}", MemberId = member.Id, SubmittedAt = clock.GetUtcNow() };
                if (Apply(entry, payload, clock) is { } error)
                {
                    return Invalid(context, error);
                }

                entry.UpdatedAt = entry.SubmittedAt;
                db.VolunteerHours.Add(entry);
                await db.SaveChangesAsync();
                return Results.Created($"{Me}/hours/{entry.Id}", ToView(entry));
            }))
        .WithName("SubmitVolunteerHours").WithSummary("Log volunteer hours for approval")
        .WithDescription("Needs `projectSlug`, `date` (not in the future), `minutes` (1 to 1440) and a `description`. The entry is `submitted` and counts nowhere until an organiser approves it.")
        .WithTags(Tag).Own();

        app.MapGet($"{Me}/hours", (HttpContext context, MembershipDbContext db, string? status) =>
            SelfEndpoints.WithMember(context, db, async member =>
            {
                var rows = (await db.VolunteerHours.AsNoTracking().Where(h => h.MemberId == member.Id).ToListAsync())
                    .Where(h => status is null || h.Status == status).OrderByDescending(h => h.WorkDate).ThenByDescending(h => h.SubmittedAt).ToList();
                var all = await db.VolunteerHours.AsNoTracking().Where(h => h.MemberId == member.Id).ToListAsync();
                var approved = all.Where(h => h.Status == VolunteerHourStatuses.Approved).Sum(h => h.Minutes);
                return Results.Ok(new
                {
                    items = rows.Select(ToView),
                    summary = new { approvedMinutes = approved, approvedHours = Math.Round(approved / 60.0, 2), pendingEntries = all.Count(h => h.Status == VolunteerHourStatuses.Submitted), rejectedEntries = all.Count(h => h.Status == VolunteerHourStatuses.Rejected) }
                });
            }))
        .WithName("ListMyVolunteerHours").WithSummary("My hours with their status, rejection reasons and totals")
        .WithTags(Tag).Own();

        app.MapPut($"{Me}/hours/{{id}}", (HttpContext context, string id, HourRequest payload, MembershipDbContext db, TimeProvider clock) =>
            SelfEndpoints.WithMember(context, db, async member =>
            {
                var entry = await db.VolunteerHours.FirstOrDefaultAsync(h => h.Id == id && h.MemberId == member.Id);
                if (entry is null)
                {
                    return NotFound(context, "Entry not found.");
                }

                if (entry.Status == VolunteerHourStatuses.Approved)
                {
                    return Conflict(context, "APPROVED_LOCKED", "Approved hours cannot be changed.");
                }

                if (Apply(entry, payload, clock) is { } error)
                {
                    return Invalid(context, error);
                }

                // A corrected, rejected entry goes back to review.
                entry.Status = VolunteerHourStatuses.Submitted;
                entry.RejectionReason = null;
                entry.ReviewedAt = null;
                entry.ReviewedBy = null;
                entry.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync();
                return Results.Ok(ToView(entry));
            }))
        .WithName("UpdateMyVolunteerHours").WithSummary("Correct an entry that is waiting or was rejected; it goes back to review")
        .WithTags(Tag).Own();

        app.MapDelete($"{Me}/hours/{{id}}", (HttpContext context, string id, MembershipDbContext db) =>
            SelfEndpoints.WithMember(context, db, async member =>
            {
                var entry = await db.VolunteerHours.FirstOrDefaultAsync(h => h.Id == id && h.MemberId == member.Id);
                if (entry is null)
                {
                    return NotFound(context, "Entry not found.");
                }

                if (entry.Status == VolunteerHourStatuses.Approved)
                {
                    return Conflict(context, "APPROVED_LOCKED", "Approved hours cannot be withdrawn.");
                }

                db.VolunteerHours.Remove(entry);
                await db.SaveChangesAsync();
                return Results.NoContent();
            }))
        .WithName("WithdrawMyVolunteerHours").WithSummary("Withdraw an entry that is not approved")
        .WithTags(Tag).Own();

        app.MapGet($"{Me}/opportunities", (HttpContext context, MembershipDbContext db, TimeProvider clock) =>
            SelfEndpoints.WithMember(context, db, async member =>
            {
                var mine = await db.VolunteerSignups.AsNoTracking().Where(s => s.MemberId == member.Id && s.Status == "signed_up").Select(s => s.ShiftId).ToListAsync();
                var own = Skills(member.Expertise);
                var opportunities = await db.VolunteerOpportunities.AsNoTracking().Include(o => o.Shifts).Where(o => o.IsOpen).ToListAsync();
                var now = clock.GetUtcNow();
                var items = opportunities.Select(o => ToView(o, mine, own, now)).OrderByDescending(o => o.MatchScore).ThenBy(o => o.Slug, StringComparer.Ordinal).ToList();
                return Results.Ok(new { items });
            }))
        .WithName("ListVolunteerOpportunities").WithSummary("The opportunity board, with how well each matches my expertise")
        .WithDescription("Open opportunities with their upcoming shifts and free slots, best skill match first. The match uses the member's own expertise, whatever their publication consent.")
        .WithTags(Tag).Own();

        app.MapPost($"{Me}/shifts/{{shiftId}}/signup", (HttpContext context, string shiftId, MembershipDbContext db, TimeProvider clock) =>
            SelfEndpoints.WithMember(context, db, member => SignUp(context, db, member, shiftId, clock)))
        .WithName("SignUpForVolunteerShift").WithSummary("Take a place on a shift")
        .WithDescription("Decided on the server: the last slot goes to one person. A full shift is 409 SHIFT_FULL; signing up twice is idempotent.")
        .WithTags(Tag).Own();

        app.MapDelete($"{Me}/shifts/{{shiftId}}/signup", (HttpContext context, string shiftId, MembershipDbContext db) =>
            SelfEndpoints.WithMember(context, db, member => Cancel(context, db, member, shiftId)))
        .WithName("CancelVolunteerShift").WithSummary("Give up a shift")
        .WithTags(Tag).Own();
    }

    // ---- review and rollups

    private static void MapReview(IEndpointRouteBuilder app)
    {
        app.MapGet($"{Admin}/hours", async (MembershipDbContext db, string? status, string? project, string? memberId, int? limit, int? offset) =>
        {
            var members = await db.Members.AsNoTracking().ToDictionaryAsync(m => m.Id);
            var rows = (await db.VolunteerHours.AsNoTracking().ToListAsync())
                .Where(h => (status is null || h.Status == status) && (project is null || h.ProjectSlug == project) && (memberId is null || h.MemberId == memberId))
                .OrderBy(h => h.Status == VolunteerHourStatuses.Submitted ? 0 : 1).ThenBy(h => h.SubmittedAt).ThenBy(h => h.Id, StringComparer.Ordinal).ToList();
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var skip = Math.Max(offset ?? 0, 0);
            return Results.Ok(new { items = rows.Skip(skip).Take(take).Select(h => ToAdmin(h, members)), total = rows.Count, limit = take, offset = skip });
        })
        .WithName("ListVolunteerHoursAdmin").WithSummary("Hours to review, oldest waiting first")
        .WithTags(Tag).RequirePermission(Permissions.EventsManage);

        app.MapPost($"{Admin}/hours/{{id}}/approve", async (HttpContext context, string id, MembershipDbContext db, TimeProvider clock) =>
            await Decide(context, db, id, clock, null))
        .WithName("ApproveVolunteerHours").WithSummary("Approve hours so they count")
        .WithDescription("Never by the member who logged them (403 SELF_REVIEW).")
        .WithTags(Tag).RequirePermission(Permissions.EventsManage);

        app.MapPost($"{Admin}/hours/{{id}}/reject", async (HttpContext context, string id, ReasonBody payload, MembershipDbContext db, TimeProvider clock) =>
            string.IsNullOrWhiteSpace(payload.Reason)
                ? Invalid(context, "A reason is required to reject hours; the volunteer reads it.")
                : await Decide(context, db, id, clock, payload.Reason.Trim()))
        .WithName("RejectVolunteerHours").WithSummary("Reject hours with a reason the volunteer sees")
        .WithTags(Tag).RequirePermission(Permissions.EventsManage);

        app.MapGet($"{Admin}/rollups", async (HttpContext context, MembershipDbContext db, string? by, string? project) =>
        {
            if (by is not (null or "project" or "member"))
            {
                return Invalid(context, "`by` must be project or member.");
            }

            var hours = await db.VolunteerHours.AsNoTracking().ToListAsync();
            hours = [.. hours.Where(h => project is null || h.ProjectSlug == project)];
            var members = await db.Members.AsNoTracking().ToDictionaryAsync(m => m.Id);
            var rows = (by ?? "project") == "project"
                ? hours.GroupBy(h => h.ProjectSlug).Select(g => Rollup(g.Key, null, g)).OrderBy(r => r.Key, StringComparer.Ordinal)
                : hours.GroupBy(h => h.MemberId).Select(g => Rollup(members.TryGetValue(g.Key, out var m) ? m.MemberCode : g.Key, members.TryGetValue(g.Key, out var n) ? $"{n.FirstName} {n.LastName}" : null, g)).OrderBy(r => r.Key, StringComparer.Ordinal);
            var approved = hours.Where(h => h.Status == VolunteerHourStatuses.Approved).ToList();
            return Results.Ok(new { items = rows, total = Rollup("all", null, hours) with { Key = "all" }, approvedEntries = approved.Count });
        })
        .WithName("VolunteerRollups").WithSummary("Approved hours per project or per member")
        .WithDescription("Sums over approved entries only. `fingerprint` is a hash of the approved entry ids and minutes behind each figure, so a published total can be traced to its entries (impact metrics, BE-085).")
        .WithTags(Tag).RequirePermission(Permissions.EventsManage);
    }

    // ---- opportunity board administration

    private static void MapOpportunities(IEndpointRouteBuilder app)
    {
        app.MapGet($"{Admin}/opportunities", async (MembershipDbContext db, TimeProvider clock) =>
        {
            var items = (await db.VolunteerOpportunities.AsNoTracking().Include(o => o.Shifts).ToListAsync()).OrderBy(o => o.Slug, StringComparer.Ordinal).Select(o => ToView(o, [], [], clock.GetUtcNow(), includePast: true));
            return Results.Ok(new { items });
        })
        .WithName("ListVolunteerOpportunitiesAdmin").WithSummary("All opportunities, open or closed")
        .WithTags(Tag).RequirePermission(Permissions.EventsManage);

        app.MapPost($"{Admin}/opportunities", async (HttpContext context, OpportunityRequest payload, MembershipDbContext db, TimeProvider clock) =>
        {
            var opportunity = new VolunteerOpportunityEntity { Id = $"vop_{Guid.NewGuid():N}", CreatedAt = clock.GetUtcNow() };
            if (Apply(opportunity, payload, creating: true) is { } error)
            {
                return Invalid(context, error);
            }

            if (await db.VolunteerOpportunities.AnyAsync(o => o.Slug == opportunity.Slug))
            {
                return Conflict(context, "SLUG_TAKEN", $"The slug '{opportunity.Slug}' is already used.");
            }

            db.VolunteerOpportunities.Add(opportunity);
            await db.SaveChangesAsync();
            return Results.Created($"{Admin}/opportunities/{opportunity.Id}", ToView(opportunity, [], [], clock.GetUtcNow(), includePast: true));
        })
        .WithName("CreateVolunteerOpportunity").WithSummary("Post an opportunity with its shifts")
        .WithTags(Tag).RequirePermission(Permissions.EventsManage);

        app.MapPut($"{Admin}/opportunities/{{id}}", async (HttpContext context, string id, OpportunityRequest payload, MembershipDbContext db, TimeProvider clock) =>
        {
            var opportunity = await db.VolunteerOpportunities.Include(o => o.Shifts).FirstOrDefaultAsync(o => o.Id == id);
            if (opportunity is null)
            {
                return NotFound(context, "Opportunity not found.");
            }

            if (Apply(opportunity, payload, creating: false) is { } error)
            {
                return Invalid(context, error);
            }

            await db.SaveChangesAsync();
            return Results.Ok(ToView(opportunity, [], [], clock.GetUtcNow(), includePast: true));
        })
        .WithName("UpdateVolunteerOpportunity").WithSummary("Edit an opportunity, close it, or add shifts")
        .WithDescription("Fields left out stay as they are. `shifts` adds new shifts; existing shifts with sign-ups are never removed.")
        .WithTags(Tag).RequirePermission(Permissions.EventsManage);

        app.MapGet($"{Admin}/opportunities/{{id}}/matches", async (HttpContext context, string id, MembershipDbContext db) =>
        {
            var opportunity = await db.VolunteerOpportunities.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
            if (opportunity is null)
            {
                return NotFound(context, "Opportunity not found.");
            }

            var wanted = Skills(opportunity.Skills);
            // Expertise is only used for others with the member's consent to publish it.
            var members = (await db.Members.AsNoTracking().Where(m => m.Status == "active" && m.ConsentExpertise && m.Expertise != null).ToListAsync())
                .Where(m => !db.DeletionRequests.Any(d => d.MemberId == m.Id))
                .Select(m => new MatchView(m.Id, m.MemberCode, $"{m.FirstName} {m.LastName}", [.. wanted.Intersect(Skills(m.Expertise))]))
                .Where(m => m.MatchedSkills.Count > 0).OrderByDescending(m => m.MatchedSkills.Count).ThenBy(m => m.MemberCode, StringComparer.Ordinal).ToList();
            return Results.Ok(new { items = members });
        })
        .WithName("VolunteerOpportunityMatches").WithSummary("Members whose expertise matches an opportunity (consenting members only)")
        .WithTags(Tag).RequirePermission(Permissions.EventsManage);
    }

    // ---- logic

    private static async Task<IResult> Decide(HttpContext context, MembershipDbContext db, string id, TimeProvider clock, string? rejection)
    {
        var entry = await db.VolunteerHours.FirstOrDefaultAsync(h => h.Id == id);
        if (entry is null)
        {
            return NotFound(context, "Entry not found.");
        }

        var reviewer = SelfEndpoints.Subject(context) ?? "unknown";
        var owner = await db.Members.AsNoTracking().Where(m => m.Id == entry.MemberId).Select(m => m.UserId).FirstOrDefaultAsync();
        if (owner == reviewer)
        {
            return ApiError.Create(context, StatusCodes.Status403Forbidden, "SELF_REVIEW", "Hours cannot be reviewed by the member who logged them.");
        }

        if (entry.Status != VolunteerHourStatuses.Submitted)
        {
            return Conflict(context, "NOT_SUBMITTED", "Only submitted hours can be decided.");
        }

        var now = clock.GetUtcNow();
        entry.Status = rejection is null ? VolunteerHourStatuses.Approved : VolunteerHourStatuses.Rejected;
        entry.RejectionReason = rejection;
        entry.ReviewedAt = now;
        entry.ReviewedBy = reviewer;
        entry.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(context, "CONFLICT", "The entry changed while you were deciding. Reload it.");
        }

        var members = await db.Members.AsNoTracking().Where(m => m.Id == entry.MemberId).ToDictionaryAsync(m => m.Id);
        return Results.Ok(ToAdmin(entry, members));
    }

    private static async Task<IResult> SignUp(HttpContext context, MembershipDbContext db, MemberEntity member, string shiftId, TimeProvider clock)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            db.ChangeTracker.Clear();
            var shift = await db.VolunteerShifts.FirstOrDefaultAsync(s => s.Id == shiftId);
            var opportunity = shift is null ? null : await db.VolunteerOpportunities.AsNoTracking().FirstOrDefaultAsync(o => o.Id == shift.OpportunityId);
            if (shift is null || opportunity is null || !opportunity.IsOpen)
            {
                return NotFound(context, "Shift not found.");
            }

            if (shift.EndsAt <= clock.GetUtcNow())
            {
                return Conflict(context, "SHIFT_PAST", "That shift is over.");
            }

            var existing = await db.VolunteerSignups.FirstOrDefaultAsync(s => s.ShiftId == shift.Id && s.MemberId == member.Id);
            if (existing is { Status: "signed_up" })
            {
                return Results.Ok(new { shiftId, status = existing.Status });
            }

            if (shift.SignedUp >= shift.Slots)
            {
                return Conflict(context, "SHIFT_FULL", "That shift is full.");
            }

            if (existing is null)
            {
                db.VolunteerSignups.Add(new VolunteerSignupEntity { Id = $"vsu_{Guid.NewGuid():N}", ShiftId = shift.Id, MemberId = member.Id, CreatedAt = clock.GetUtcNow() });
            }
            else
            {
                existing.Status = "signed_up";
            }

            shift.SignedUp++;
            try
            {
                await db.SaveChangesAsync();
                return Results.Ok(new { shiftId, status = "signed_up" });
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone else took or freed a slot first: decide again against the new count.
            }
        }

        return Conflict(context, "BUSY", "The shift is very busy. Please try again.");
    }

    private static async Task<IResult> Cancel(HttpContext context, MembershipDbContext db, MemberEntity member, string shiftId)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            db.ChangeTracker.Clear();
            var signup = await db.VolunteerSignups.FirstOrDefaultAsync(s => s.ShiftId == shiftId && s.MemberId == member.Id && s.Status == "signed_up");
            if (signup is null)
            {
                return NotFound(context, "You are not signed up for that shift.");
            }

            var shift = await db.VolunteerShifts.FirstAsync(s => s.Id == shiftId);
            signup.Status = "cancelled";
            shift.SignedUp = Math.Max(0, shift.SignedUp - 1);
            try
            {
                await db.SaveChangesAsync();
                return Results.NoContent();
            }
            catch (DbUpdateConcurrencyException)
            {
                // Retry against the fresh count.
            }
        }

        return Conflict(context, "BUSY", "The shift is very busy. Please try again.");
    }

    private static string? Apply(VolunteerHourEntity entry, HourRequest request, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectSlug) || !SlugPattern().IsMatch(request.ProjectSlug.Trim()))
        {
            return "projectSlug is required (the project's slug).";
        }

        if (request.EventSlug is not null && !SlugPattern().IsMatch(request.EventSlug.Trim()))
        {
            return "eventSlug must be an event slug.";
        }

        if (request.Date is not { } date || date > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(1))
        {
            return "date is required and cannot be in the future.";
        }

        if (request.Minutes is null or < 1 or > MaxMinutesPerEntry)
        {
            return $"minutes must be between 1 and {MaxMinutesPerEntry}.";
        }

        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > 500)
        {
            return "description is required (up to 500 characters).";
        }

        entry.ProjectSlug = request.ProjectSlug.Trim();
        entry.EventSlug = string.IsNullOrWhiteSpace(request.EventSlug) ? null : request.EventSlug.Trim();
        entry.WorkDate = date;
        entry.Minutes = request.Minutes.Value;
        entry.Description = request.Description.Trim();
        return null;
    }

    private static string? Apply(VolunteerOpportunityEntity entity, OpportunityRequest request, bool creating)
    {
        if (creating && (string.IsNullOrWhiteSpace(request.Slug) || !SlugPattern().IsMatch(request.Slug.Trim())))
        {
            return "slug is required (lower-case letters, digits and hyphens).";
        }

        if (creating && (string.IsNullOrWhiteSpace(request.TitleEn) || string.IsNullOrWhiteSpace(request.TitleBn)))
        {
            return "titleEn and titleBn are required.";
        }

        if (request.ProjectSlug is not null && request.ProjectSlug.Length > 0 && !SlugPattern().IsMatch(request.ProjectSlug))
        {
            return "projectSlug must be a project slug.";
        }

        foreach (var (value, max, name) in new[] { (request.TitleEn, 200, "titleEn"), (request.TitleBn, 200, "titleBn"), (request.DescriptionEn, 2000, "descriptionEn"), (request.DescriptionBn, 2000, "descriptionBn") })
        {
            if (value is { } text && text.Length > max)
            {
                return $"{name} is too long.";
            }
        }

        var skills = request.Skills is null ? null : request.Skills.Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct().ToList();
        if (skills is not null && (skills.Count > 20 || skills.Any(s => s.Length > 40 || s.Contains(','))))
        {
            return "At most 20 skills of up to 40 characters, without commas.";
        }

        var shifts = request.Shifts ?? [];
        foreach (var shift in shifts)
        {
            if (shift.StartsAt is null || shift.EndsAt is null || shift.EndsAt <= shift.StartsAt || shift.Slots is null or < 1 or > 1000)
            {
                return "Each shift needs startsAt, a later endsAt and 1 to 1000 slots.";
            }
        }

        if (creating)
        {
            entity.Slug = request.Slug!.Trim();
        }

        entity.TitleEn = request.TitleEn?.Trim() ?? entity.TitleEn;
        entity.TitleBn = request.TitleBn?.Trim() ?? entity.TitleBn;
        entity.DescriptionEn = request.DescriptionEn?.Trim() ?? entity.DescriptionEn;
        entity.DescriptionBn = request.DescriptionBn?.Trim() ?? entity.DescriptionBn;
        entity.ProjectSlug = request.ProjectSlug is null ? entity.ProjectSlug : request.ProjectSlug.Length == 0 ? null : request.ProjectSlug;
        entity.Skills = skills is null ? entity.Skills : string.Join(',', skills);
        entity.IsOpen = request.IsOpen ?? entity.IsOpen;
        foreach (var shift in shifts)
        {
            entity.Shifts.Add(new VolunteerShiftEntity { Id = $"vsh_{Guid.NewGuid():N}", OpportunityId = entity.Id, StartsAt = shift.StartsAt!.Value.ToUniversalTime(), EndsAt = shift.EndsAt!.Value.ToUniversalTime(), Slots = shift.Slots!.Value });
        }

        return null;
    }

    private static List<string> Skills(string? text) =>
        [.. (text ?? string.Empty).Split([',', ';', '\n', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToLowerInvariant()).Where(s => s.Length > 0).Distinct()];

    private static RollupRow Rollup(string key, string? name, IEnumerable<VolunteerHourEntity> rows)
    {
        var list = rows.ToList();
        var approved = list.Where(h => h.Status == VolunteerHourStatuses.Approved).OrderBy(h => h.Id, StringComparer.Ordinal).ToList();
        var minutes = approved.Sum(h => h.Minutes);
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('|', approved.Select(h => $"{h.Id}:{h.Minutes}"))))).ToLowerInvariant()[..16];
        return new RollupRow(key, name, minutes, Math.Round(minutes / 60.0, 2), approved.Count, list.Count(h => h.Status == VolunteerHourStatuses.Submitted), fingerprint);
    }

    private static OpportunityView ToView(VolunteerOpportunityEntity o, List<string> mine, List<string> memberSkills, DateTimeOffset now, bool includePast = false)
    {
        var wanted = Skills(o.Skills);
        var matched = wanted.Intersect(memberSkills).ToList();
        var shifts = o.Shifts.Where(s => includePast || s.EndsAt > now).OrderBy(s => s.StartsAt).Select(s => new ShiftView(s.Id, s.StartsAt, s.EndsAt, s.Slots, s.SignedUp, Math.Max(0, s.Slots - s.SignedUp), includePast ? null : mine.Contains(s.Id))).ToList();
        return new OpportunityView(o.Id, o.Slug, o.TitleEn, o.TitleBn, o.DescriptionEn, o.DescriptionBn, o.ProjectSlug, wanted, o.IsOpen, shifts, memberSkills.Count == 0 && !includePast ? 0 : includePast ? null : matched.Count, includePast ? null : matched);
    }

    private static HourView ToView(VolunteerHourEntity h) => new(h.Id, h.ProjectSlug, h.EventSlug, h.WorkDate, h.Minutes, h.Description, h.Status, h.SubmittedAt, h.ReviewedAt, h.RejectionReason);

    private static AdminHourView ToAdmin(VolunteerHourEntity h, Dictionary<string, MemberEntity> members)
    {
        members.TryGetValue(h.MemberId, out var m);
        return new AdminHourView(h.Id, h.MemberId, m?.MemberCode ?? string.Empty, m is null ? string.Empty : $"{m.FirstName} {m.LastName}", h.ProjectSlug, h.EventSlug, h.WorkDate, h.Minutes, h.Description, h.Status, h.SubmittedAt, h.ReviewedAt, h.ReviewedBy, h.RejectionReason);
    }

    private static IResult Invalid(HttpContext context, string message) => ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", message);

    private static IResult NotFound(HttpContext context, string message) => ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", message);

    private static IResult Conflict(HttpContext context, string code, string message) => ApiError.Create(context, StatusCodes.Status409Conflict, code, message);
}
