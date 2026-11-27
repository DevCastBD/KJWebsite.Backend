using System.Globalization;
using System.Security.Claims;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReportingService.Aggregates;
using ReportingService.Data;

namespace ReportingService.Dashboards;

public sealed class DashboardOptions
{
    public const string SectionName = "Reporting:Dashboards";

    /// <summary>The month a fiscal year starts (7 = July, matching Finance's default). Used for budget burn.</summary>
    public int FiscalYearStartMonth { get; set; } = 7;

    /// <summary>An optional monthly donation target per currency, for the trend-versus-target line. No entry, no target line.</summary>
    public Dictionary<string, decimal> DonationTargetMonthly { get; set; } = [];
}

/// <summary>Where a figure comes from: the warehouse metric and the call that returns its rows, so a surprising number can be investigated.</summary>
public sealed record DashboardTrace(string Metric, string Source, string Drilldown);

public sealed record DashboardPoint(string Period, string Value);

public sealed record DashboardSeries(string Key, string Label, string? Currency, IReadOnlyList<DashboardPoint> Points, DashboardTrace Trace);

public sealed record DashboardFigure(string Key, string Label, string Value, string? Unit, DashboardTrace Trace);

public sealed record DashboardSection(string Id, string Title, IReadOnlyList<DashboardFigure> Figures, IReadOnlyList<DashboardSeries> Series);

public sealed record DashboardView(DateTimeOffset? AsOf, bool Stale, int Months, IReadOnlyList<DashboardSection> Sections);

/// <summary>
/// Builds the dashboards from the warehouse only. A section is built only for a caller holding its permission: a content editor's dashboard has no financial section at all, not a hidden one.
/// </summary>
public sealed class DashboardService(ReportingDbContext db, AggregateQuery aggregates, IOptions<DashboardOptions> options, TimeProvider clock)
{
    public const int DefaultMonths = 12;
    public const int MaxMonths = 36;

    private static readonly (string Id, string Title, string Permission)[] Catalog =
    [
        ("donations", "Donation trend", Permissions.FinanceView),
        ("expenses", "Expense burn against budget", Permissions.FinanceView),
        ("membership", "Membership funnel", Permissions.MembershipManage),
        ("events", "Event attendance", Permissions.EventsManage),
        ("content", "Content pipeline", Permissions.ContentView),
        ("inbox", "Engagement inbox", Permissions.CtaView),
        ("programme", "Programme delivery", Permissions.ProgrammeManage)
    ];

    public static IReadOnlyList<string> SectionIds => [.. Catalog.Select(c => c.Id)];

    public static string? PermissionFor(string section) => Catalog.FirstOrDefault(c => c.Id == section).Permission;

    public async Task<DashboardView> BuildAsync(ClaimsPrincipal user, string? only, int? months, CancellationToken ct)
    {
        var window = Math.Clamp(months ?? DefaultMonths, 1, MaxMonths);
        var allowed = Catalog.Where(c => (only is null || c.Id == only) && user.HasClaim(KjClaims.Permission, c.Permission)).ToList();
        var freshness = await aggregates.FreshnessAsync(ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var from = new DateOnly(today.Year, today.Month, 1).AddMonths(-(window - 1));
        var sections = new List<DashboardSection>();
        foreach (var (id, title, _) in allowed)
        {
            sections.Add(id switch
            {
                "donations" => await DonationsAsync(title, from),
                "expenses" => await ExpensesAsync(title, today),
                "membership" => await MembershipAsync(title, from),
                "events" => await EventsAsync(title, from),
                "content" => await ContentAsync(title, from),
                "inbox" => await InboxAsync(title, from),
                _ => await ProgrammeAsync(title, from)
            });
        }

        return new DashboardView(freshness.AsOf, freshness.Stale, window, sections);
    }

    private async Task<List<AggregateRowLite>> LoadAsync(DateOnly? from, params string[] metrics)
    {
        var query = db.AggregatePoints.AsNoTracking().Where(p => metrics.Contains(p.Metric));
        if (from is { } f)
        {
            query = query.Where(p => p.Period >= f);
        }

        return await query.Select(p => new AggregateRowLite(p.Source, p.Metric, p.Period, p.Dimension, p.Currency, p.Value)).ToListAsync();
    }

    private static DashboardTrace Trace(string metric, string source) => new(metric, source, $"/api/v1/admin/reporting/aggregates?metric={metric}&source={source}");

    private static string Fmt(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Month(DateOnly period) => period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Every month of the window, zero where nothing happened, so a chart has no gaps.</summary>
    private static List<DashboardPoint> Monthly(IEnumerable<AggregateRowLite> rows, DateOnly from, DateOnly to, decimal scale = 1m)
    {
        var byMonth = rows.GroupBy(r => r.Period).ToDictionary(g => g.Key, g => g.Sum(r => r.Value) * scale);
        var points = new List<DashboardPoint>();
        for (var month = from; month <= to; month = month.AddMonths(1))
        {
            points.Add(new DashboardPoint(Month(month), Fmt(byMonth.GetValueOrDefault(month))));
        }

        return points;
    }

    private DateOnly ThisMonth() { var t = clock.GetUtcNow(); return new DateOnly(t.Year, t.Month, 1); }

    private async Task<DashboardSection> DonationsAsync(string title, DateOnly from)
    {
        var rows = await LoadAsync(from, "donations.confirmed.amount", "donations.confirmed.count");
        var amounts = rows.Where(r => r.Metric == "donations.confirmed.amount").ToList();
        var series = new List<DashboardSeries>();
        var figures = new List<DashboardFigure>();
        foreach (var currency in amounts.Select(r => r.Currency).Distinct().Order(StringComparer.Ordinal))
        {
            var mine = amounts.Where(r => r.Currency == currency).ToList();
            series.Add(new($"donations.{currency}", $"Confirmed donations ({currency})", currency, Monthly(mine, from, ThisMonth()), Trace("donations.confirmed.amount", "finance")));
            if (options.Value.DonationTargetMonthly.TryGetValue(currency, out var target))
            {
                series.Add(new($"target.{currency}", $"Monthly target ({currency})", currency, Monthly([], from, ThisMonth()).Select(p => p with { Value = Fmt(target) }).ToList(), Trace("donations.confirmed.amount", "finance")));
            }

            figures.Add(new($"total.{currency}", $"Confirmed in the period ({currency})", Fmt(mine.Sum(r => r.Value)), currency, Trace("donations.confirmed.amount", "finance")));
        }

        figures.Add(new("count", "Confirmed donations in the period", rows.Where(r => r.Metric == "donations.confirmed.count").Sum(r => r.Value).ToString("0", CultureInfo.InvariantCulture), "donations", Trace("donations.confirmed.count", "finance")));
        return new("donations", title, figures, series);
    }

    private async Task<DashboardSection> ExpensesAsync(string title, DateOnly today)
    {
        var startMonth = Math.Clamp(options.Value.FiscalYearStartMonth, 1, 12);
        var fyStart = new DateOnly(today.Month >= startMonth ? today.Year : today.Year - 1, startMonth, 1);
        var fyEnd = fyStart.AddMonths(12).AddDays(-1);
        var rows = await LoadAsync(fyStart, "expenses.ledger.amount", "budget.planned.amount");
        rows = [.. rows.Where(r => r.Period <= fyEnd)];
        var series = new List<DashboardSeries>();
        var figures = new List<DashboardFigure>();
        foreach (var currency in rows.Select(r => r.Currency).Distinct().Order(StringComparer.Ordinal))
        {
            var spent = rows.Where(r => r.Metric == "expenses.ledger.amount" && r.Currency == currency).ToList();
            var planned = rows.Where(r => r.Metric == "budget.planned.amount" && r.Currency == currency).Sum(r => r.Value);
            var spentTotal = spent.Sum(r => r.Value);
            series.Add(new($"expenses.{currency}", $"Expenses by month ({currency})", currency, Monthly(spent, fyStart, ThisMonth()), Trace("expenses.ledger.amount", "finance")));
            figures.Add(new($"planned.{currency}", $"Budget for the fiscal year ({currency})", Fmt(planned), currency, Trace("budget.planned.amount", "finance")));
            figures.Add(new($"spent.{currency}", $"Spent so far ({currency})", Fmt(spentTotal), currency, Trace("expenses.ledger.amount", "finance")));
            figures.Add(new($"burn.{currency}", $"Budget used ({currency})", planned > 0 ? (spentTotal * 100m / planned).ToString("0.0", CultureInfo.InvariantCulture) : "0.0", "percent", Trace("expenses.ledger.amount", "finance")));
        }

        return new("expenses", title, figures, series);
    }

    private async Task<DashboardSection> MembershipAsync(string title, DateOnly from)
    {
        var rows = await LoadAsync(from, "applications.received", "members.joined");
        var applications = rows.Where(r => r.Metric == "applications.received").ToList();
        var joined = rows.Where(r => r.Metric == "members.joined").ToList();
        var received = applications.Sum(r => r.Value);
        return new("membership", title,
        [
            new("applications", "Applications received", received.ToString("0", CultureInfo.InvariantCulture), "applications", Trace("applications.received", "membership")),
            new("joined", "Members joined", joined.Sum(r => r.Value).ToString("0", CultureInfo.InvariantCulture), "members", Trace("members.joined", "membership")),
            new("conversion", "Applications that became members", received > 0 ? (joined.Sum(r => r.Value) * 100m / received).ToString("0.0", CultureInfo.InvariantCulture) : "0.0", "percent", Trace("members.joined", "membership"))
        ],
        [
            new("applications", "Applications received", null, Monthly(applications, from, ThisMonth()), Trace("applications.received", "membership")),
            new("joined", "Members joined", null, Monthly(joined, from, ThisMonth()), Trace("members.joined", "membership"))
        ]);
    }

    private async Task<DashboardSection> EventsAsync(string title, DateOnly from)
    {
        var rows = await LoadAsync(from, "events.registrations", "events.attended");
        var registrations = rows.Where(r => r.Metric == "events.registrations").ToList();
        var attended = rows.Where(r => r.Metric == "events.attended").ToList();
        var registered = registrations.Sum(r => r.Value);
        return new("events", title,
        [
            new("registrations", "Registrations", registered.ToString("0", CultureInfo.InvariantCulture), "registrations", Trace("events.registrations", "content")),
            new("attended", "Checked in", attended.Sum(r => r.Value).ToString("0", CultureInfo.InvariantCulture), "people", Trace("events.attended", "content")),
            new("rate", "Checked in against registered", registered > 0 ? (attended.Sum(r => r.Value) * 100m / registered).ToString("0.0", CultureInfo.InvariantCulture) : "0.0", "percent", Trace("events.attended", "content"))
        ],
        [
            new("registrations", "Registrations", null, Monthly(registrations, from, ThisMonth()), Trace("events.registrations", "content")),
            new("attended", "Checked in", null, Monthly(attended, from, ThisMonth()), Trace("events.attended", "content"))
        ]);
    }

    private async Task<DashboardSection> ContentAsync(string title, DateOnly from)
    {
        var published = await LoadAsync(from, "content.published");
        var pipeline = await LoadAsync(null, "content.pipeline.snapshot");
        var newest = pipeline.Select(r => r.Period).DefaultIfEmpty().Max();
        var figures = pipeline.Where(r => r.Period == newest).OrderBy(r => r.Dimension, StringComparer.Ordinal)
            .Select(r => new DashboardFigure($"status.{r.Dimension}", $"Items {r.Dimension}", r.Value.ToString("0", CultureInfo.InvariantCulture), "items", Trace("content.pipeline.snapshot", "content"))).ToList();
        return new("content", title, figures, [new("published", "Published per month", null, Monthly(published, from, ThisMonth()), Trace("content.published", "content"))]);
    }

    private async Task<DashboardSection> InboxAsync(string title, DateOnly from)
    {
        var rows = await LoadAsync(from, "inbox.received", "inbox.first_responded", "inbox.closed");
        var open = await LoadAsync(null, "inbox.open.snapshot");
        var newest = open.Select(r => r.Period).DefaultIfEmpty().Max();
        var received = rows.Where(r => r.Metric == "inbox.received").ToList();
        var responded = rows.Where(r => r.Metric == "inbox.first_responded").ToList();
        var closed = rows.Where(r => r.Metric == "inbox.closed").ToList();
        var figures = new List<DashboardFigure>
        {
            new("received", "Received", received.Sum(r => r.Value).ToString("0", CultureInfo.InvariantCulture), "enquiries", Trace("inbox.received", "cta")),
            new("responded", "First reply sent", responded.Sum(r => r.Value).ToString("0", CultureInfo.InvariantCulture), "enquiries", Trace("inbox.first_responded", "cta")),
            new("response-rate", "Answered against received", received.Sum(r => r.Value) > 0 ? (responded.Sum(r => r.Value) * 100m / received.Sum(r => r.Value)).ToString("0.0", CultureInfo.InvariantCulture) : "0.0", "percent", Trace("inbox.first_responded", "cta"))
        };
        figures.AddRange(open.Where(r => r.Period == newest).OrderBy(r => r.Dimension, StringComparer.Ordinal).Select(r => new DashboardFigure($"open.{r.Dimension}", $"Open, {r.Dimension} priority", r.Value.ToString("0", CultureInfo.InvariantCulture), "enquiries", Trace("inbox.open.snapshot", "cta"))));
        return new("inbox", title, figures,
        [
            new("received", "Received", null, Monthly(received, from, ThisMonth()), Trace("inbox.received", "cta")),
            new("responded", "First reply sent", null, Monthly(responded, from, ThisMonth()), Trace("inbox.first_responded", "cta")),
            new("closed", "Closed", null, Monthly(closed, from, ThisMonth()), Trace("inbox.closed", "cta"))
        ]);
    }

    private async Task<DashboardSection> ProgrammeAsync(string title, DateOnly from)
    {
        var rows = await LoadAsync(from, "sessions.delivered", "sessions.attendance", "volunteer.minutes.approved");
        var snapshots = await LoadAsync(null, "students.enrolled.snapshot", "institutions.engaged.snapshot");
        var sessions = rows.Where(r => r.Metric == "sessions.delivered").ToList();
        var minutes = rows.Where(r => r.Metric == "volunteer.minutes.approved").ToList();
        decimal Latest(string metric)
        {
            var mine = snapshots.Where(r => r.Metric == metric).ToList();
            var newest = mine.Select(r => r.Period).DefaultIfEmpty().Max();
            return mine.Where(r => r.Period == newest).Sum(r => r.Value);
        }

        return new("programme", title,
        [
            new("sessions", "Sessions delivered", sessions.Sum(r => r.Value).ToString("0", CultureInfo.InvariantCulture), "sessions", Trace("sessions.delivered", "membership")),
            new("hours", "Volunteer hours approved", Fmt(minutes.Sum(r => r.Value) / 60m), "hours", Trace("volunteer.minutes.approved", "membership")),
            new("students", "Students enrolled now", Latest("students.enrolled.snapshot").ToString("0", CultureInfo.InvariantCulture), "students", Trace("students.enrolled.snapshot", "membership")),
            new("institutions", "Institutions engaged now", Latest("institutions.engaged.snapshot").ToString("0", CultureInfo.InvariantCulture), "institutions", Trace("institutions.engaged.snapshot", "membership"))
        ],
        [
            new("sessions", "Sessions delivered", null, Monthly(sessions, from, ThisMonth()), Trace("sessions.delivered", "membership")),
            new("hours", "Volunteer hours approved", null, Monthly(minutes, from, ThisMonth(), 1m / 60m), Trace("volunteer.minutes.approved", "membership"))
        ]);
    }

    private sealed record AggregateRowLite(string Source, string Metric, DateOnly Period, string Dimension, string Currency, decimal Value);
}
