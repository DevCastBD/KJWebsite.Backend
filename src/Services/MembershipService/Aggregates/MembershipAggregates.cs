using KJWebsite.BuildingBlocks.Aggregates;
using MembershipService.Data;
using MembershipService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Aggregates;

/// <summary>
/// Membership's monthly figures for the reporting warehouse. Volunteer time counts only after an organiser approved it; sessions count once however late they were logged.
/// `.snapshot` metrics describe the state when the feed was read (period = the current month).
/// </summary>
public static class MembershipAggregates
{
    public const string Source = "membership";

    public static async Task<AggregateBatch> CollectAsync(MembershipDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var points = new List<AggregatePoint>();
        var today = AggregateFeedExtensions.MonthOf(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));

        foreach (var g in (await db.Members.AsNoTracking().Select(m => m.JoinedAt).ToListAsync(ct)).GroupBy(AggregateFeedExtensions.MonthOf))
        {
            points.Add(new("members.joined", g.Key, string.Empty, g.Count()));
        }

        foreach (var g in (await db.Applications.AsNoTracking().Select(a => a.SubmittedAt).ToListAsync(ct)).GroupBy(AggregateFeedExtensions.MonthOf))
        {
            points.Add(new("applications.received", g.Key, string.Empty, g.Count()));
        }

        var hours = await db.VolunteerHours.AsNoTracking().Where(h => h.Status == VolunteerHourStatuses.Approved).Select(h => new { h.WorkDate, h.ProjectSlug, h.Minutes }).ToListAsync(ct);
        foreach (var g in hours.GroupBy(h => (Month: AggregateFeedExtensions.MonthOf(h.WorkDate), h.ProjectSlug)))
        {
            points.Add(new("volunteer.minutes.approved", g.Key.Month, g.Key.ProjectSlug, g.Sum(h => h.Minutes)));
        }

        var sessions = await db.SessionLogs.AsNoTracking().Select(s => new { s.HeldOn, s.ProjectSlug, s.Attendance }).ToListAsync(ct);
        foreach (var g in sessions.GroupBy(s => (Month: AggregateFeedExtensions.MonthOf(s.HeldOn), s.ProjectSlug)))
        {
            points.Add(new("sessions.delivered", g.Key.Month, g.Key.ProjectSlug, g.Count()));
            points.Add(new("sessions.attendance", g.Key.Month, g.Key.ProjectSlug, g.Sum(s => s.Attendance)));
        }

        var enrolments = await db.ProgrammeEnrolments.AsNoTracking().Select(e => new { e.ProjectSlug, e.BeneficiaryId, e.Students }).ToListAsync(ct);
        foreach (var g in enrolments.GroupBy(e => e.ProjectSlug))
        {
            points.Add(new("students.enrolled.snapshot", today, g.Key, g.Sum(e => e.Students)));
            points.Add(new("institutions.engaged.snapshot", today, g.Key, g.Select(e => e.BeneficiaryId).Distinct().Count()));
        }

        return new AggregateBatch(points);
    }
}
