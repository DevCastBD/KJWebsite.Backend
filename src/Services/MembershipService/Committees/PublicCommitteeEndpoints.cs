using MembershipService.Data;
using MembershipService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Committees;

/// <summary>
/// The public org chart. A seat is shown only when the member consented to publish their name (the same per-field consent as the
/// directory), is active and has no erasure request; the filter is in the SQL, which selects nothing about anyone else. A member
/// who has not consented simply leaves no trace: the seat is not listed.
/// </summary>
public static class PublicCommitteeEndpoints
{
    private sealed record SeatRow(string CommitteeId, string MemberId, string FirstName, string LastName, string? NameBn, string PositionKey, string PositionEn, string PositionBn, DateOnly StartsOn, DateOnly? EndsOn);

    public static void MapPublicCommittees(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/committees", async (HttpContext context, MembershipDbContext db, TimeProvider clock) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var today = CommitteeEndpoints.Today(clock);
            var committees = await db.Committees.AsNoTracking().Where(c => c.ArchivedAt == null).ToListAsync();
            var seats = (await Seats(db, today, current: true, committeeId: null)).ToLookup(s => s.CommitteeId);
            var ids = committees.Select(c => c.Id).ToHashSet();

            OrgNode Node(CommitteeEntity c) => new(
                c.Id, c.Slug, new PublicName(c.NameEn, c.NameBn), c.Kind,
                [.. seats[c.Id].Select(ToSeat)],
                [.. committees.Where(k => k.ParentId == c.Id).OrderBy(k => k.NameEn).Select(Node)]);

            // A committee whose parent is archived (or gone) is shown at the top rather than lost with it.
            var roots = committees.Where(c => c.ParentId is null || !ids.Contains(c.ParentId)).OrderBy(c => c.Kind == CommitteeKinds.Executive ? 0 : 1).ThenBy(c => c.NameEn);
            return Results.Ok(new OrgChart(today, [.. roots.Select(Node)]));
        })
        .WithName("GetOrgChart")
        .WithSummary("The organisation's committees as a tree, with current members who agreed to be named")
        .WithDescription("Current terms only (dates inclusive), members who consented to publish their name. Never cached: withdrawing consent shows on the next request.")
        .WithTags("Committees")
        .AllowAnonymous();

        app.MapGet("/api/v1/committees/{slug}", async (HttpContext context, string slug, MembershipDbContext db, TimeProvider clock) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var committee = await db.Committees.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == slug && c.ArchivedAt == null);
            if (committee is null)
            {
                return KJWebsite.BuildingBlocks.ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Committee not found.");
            }

            var today = CommitteeEndpoints.Today(clock);
            var parentSlug = committee.ParentId is null ? null : await db.Committees.AsNoTracking().Where(c => c.Id == committee.ParentId && c.ArchivedAt == null).Select(c => c.Slug).FirstOrDefaultAsync();
            var current = await Seats(db, today, current: true, committee.Id);
            var past = await Seats(db, today, current: false, committee.Id);
            return Results.Ok(new CommitteeDetail(committee.Id, committee.Slug, new PublicName(committee.NameEn, committee.NameBn), committee.Kind, parentSlug,
                [.. current.Select(ToSeat)], [.. past.OrderByDescending(s => s.EndsOn).Select(ToSeat)]));
        })
        .WithName("GetCommittee")
        .WithSummary("One committee with its current and past members (consenting members only)")
        .WithTags("Committees")
        .AllowAnonymous();
    }

    private static async Task<List<SeatRow>> Seats(MembershipDbContext db, DateOnly today, bool current, string? committeeId)
    {
        var rows = from t in db.CommitteeMembers.AsNoTracking()
                   join m in db.Members.AsNoTracking() on t.MemberId equals m.Id
                   where m.Status == "active" && m.ConsentName && !db.DeletionRequests.Any(d => d.MemberId == m.Id)
                   where committeeId == null || t.CommitteeId == committeeId
                   where current
                       ? t.StartsOn <= today && (t.EndsOn == null || t.EndsOn >= today)
                       : t.EndsOn != null && t.EndsOn < today
                   select new SeatRow(t.CommitteeId, m.Id, m.FirstName, m.LastName, m.NameBn, t.PositionKey, t.PositionEn, t.PositionBn, t.StartsOn, t.EndsOn);
        return (await rows.ToListAsync()).OrderBy(r => r.PositionKey == "chair" ? 0 : 1).ThenBy(r => r.StartsOn).ThenBy(r => r.MemberId, StringComparer.Ordinal).ToList();
    }

    private static PublicSeat ToSeat(SeatRow r) => new(
        r.MemberId, $"{r.FirstName} {r.LastName}".Trim(), string.IsNullOrWhiteSpace(r.NameBn) ? null : r.NameBn, new PublicPosition(r.PositionKey, r.PositionEn, r.PositionBn), r.StartsOn, r.EndsOn);
}
