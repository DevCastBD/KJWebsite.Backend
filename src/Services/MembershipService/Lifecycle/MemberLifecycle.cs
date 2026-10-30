using MembershipService.Data.Entities;

namespace MembershipService.Lifecycle;

public sealed record TenurePeriod(DateOnly From, DateOnly? To, int Days);

public sealed record Tenure(int TotalDays, int Years, int Months, bool CurrentlyActive, DateOnly? ActiveSince, IReadOnlyList<TenurePeriod> Periods);

/// <summary>
/// Which moves a member may make. Reactivation is possible from every non-active state (so every departure is reversible), but
/// from `terminated` it needs the terminate permission, which the endpoint checks.
/// </summary>
public static class MemberStatusMachine
{
    public const string InvalidTransition = "invalid_transition";
    public const string UnknownStatus = "unknown_status";

    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [MemberStatuses.Active] = [MemberStatuses.OnLeave, MemberStatuses.Inactive, MemberStatuses.Alumni, MemberStatuses.Terminated],
        [MemberStatuses.OnLeave] = [MemberStatuses.Active, MemberStatuses.Inactive, MemberStatuses.Alumni, MemberStatuses.Terminated],
        [MemberStatuses.Inactive] = [MemberStatuses.Active, MemberStatuses.Alumni, MemberStatuses.Terminated],
        [MemberStatuses.Alumni] = [MemberStatuses.Active, MemberStatuses.Terminated],
        [MemberStatuses.Terminated] = [MemberStatuses.Active]
    };

    public static IReadOnlyList<string> NextFrom(string status) => Allowed.TryGetValue(status, out var next) ? next : [];

    public static string? Check(string from, string to) =>
        !MemberStatuses.All.Contains(to) ? UnknownStatus : NextFrom(from).Contains(to) ? null : InvalidTransition;

    /// <summary>Terminating, or lifting a termination, is the sensitive pair.</summary>
    public static bool NeedsTerminatePermission(string from, string to) => to == MemberStatuses.Terminated || from == MemberStatuses.Terminated;
}

public static class TenureCalculator
{
    private const double DaysPerYear = 365.25;
    private const double DaysPerMonth = 30.44;

    /// <summary>
    /// Time spent `active`, summed over every stretch of service. Walks the events in effective order: a stretch opens when the
    /// member becomes active and closes at the next move away from it, so leave, lapses, alumni years and terminations never count,
    /// and a returning member's earlier service is kept. The last stretch, if still open, runs to <paramref name="asOf"/>.
    /// Years and months are derived from the day count (365.25 and 30.44 days) and are approximate; the day count is exact.
    /// </summary>
    public static Tenure Calculate(IEnumerable<MemberStatusEventEntity> events, DateOnly asOf)
    {
        var periods = new List<TenurePeriod>();
        DateOnly? since = null;
        foreach (var e in events.OrderBy(e => e.EffectiveOn).ThenBy(e => e.At).ThenBy(e => e.Id))
        {
            if (e.ToStatus == MemberStatuses.Active)
            {
                since ??= e.EffectiveOn;
            }
            else if (since is { } start)
            {
                periods.Add(new TenurePeriod(start, e.EffectiveOn, Math.Max(0, e.EffectiveOn.DayNumber - start.DayNumber)));
                since = null;
            }
        }

        if (since is { } open)
        {
            periods.Add(new TenurePeriod(open, null, Math.Max(0, asOf.DayNumber - open.DayNumber)));
        }

        var total = periods.Sum(p => p.Days);
        var years = (int)(total / DaysPerYear);
        var months = (int)((total - (years * DaysPerYear)) / DaysPerMonth);
        return new Tenure(total, years, months, since is not null, since, periods);
    }
}
