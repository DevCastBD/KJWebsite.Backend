using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using KJWebsite.BuildingBlocks.DataQuality;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.DataQuality;

public sealed class DataQualityOptions
{
    public const string SectionName = "Finance:DataQuality";

    /// <summary>A confirmed donation is flagged when it is at least this many times the median of the others in its currency (needs 10 donations to compare against).</summary>
    public decimal LargeFactor { get; set; } = 10m;

    /// <summary>The smallest amount ever flagged as large, per currency. Conservative on purpose: an alert that cries wolf gets muted. Currencies not listed use `DefaultLargeFloor`.</summary>
    public Dictionary<string, decimal> LargeFloor { get; set; } = new() { ["BDT"] = 100000m, ["USD"] = 1000m };

    public decimal DefaultLargeFloor { get; set; } = 1000m;
    public int RapidRepeatMinutes { get; set; } = 10;
    public int FailureSpikeMin { get; set; } = 10;
    public decimal FailureSpikePercent { get; set; } = 50m;

    /// <summary>Minutes between scans (0 turns the timer off; a scan can still be started by hand). Default daily.</summary>
    public int ScanIntervalMinutes { get; set; } = 1440;
}

public sealed record ScanResult(int Checked, int Flagged);

/// <summary>
/// Looks for donations that deserve a second look: unusually large, repeated within minutes, confirmed with no posting in the ledger, or a burst of failed payments (a gateway
/// problem or a test left running). It only writes flags. The donation, posting and donor rows are never touched, in keeping with the append-only ledger.
/// </summary>
public sealed class AnomalyDetector(FinanceDbContext db, IOptions<DataQualityOptions> options, TimeProvider clock)
{
    public async Task<ScanResult> ScanAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var latest = db.DonationEvents.GroupBy(e => e.DonationId).Select(g => new { DonationId = g.Key, EventId = g.Max(e => e.Id) });
        var rows = await (from d in db.Donations.AsNoTracking()
                          join l in latest on d.Id equals l.DonationId
                          join e in db.DonationEvents on l.EventId equals e.Id
                          select new Row(d.Id, d.Reference, d.Kind, d.Amount, d.Currency, d.DonorId, d.CreatedAt, e.Status)).ToListAsync(ct);
        var confirmed = rows.Where(r => r.Status == DonationStatuses.Confirmed && r.Kind != DonationKinds.Reversal && r.Amount > 0).ToList();
        var flags = new List<(string Key, string Rule, string? DonationId, string Detail)>();

        foreach (var currency in confirmed.GroupBy(r => r.Currency))
        {
            var amounts = currency.Select(r => r.Amount).Order().ToList();
            var median = amounts.Count >= 10 ? amounts[amounts.Count / 2] : 0m;
            var floor = o.LargeFloor.TryGetValue(currency.Key, out var f) ? f : o.DefaultLargeFloor;
            var threshold = Math.Max(floor, o.LargeFactor * median);
            flags.AddRange(currency.Where(r => r.Amount >= threshold).Select(r => ($"{AnomalyRules.LargeAmount}:{r.Id}", AnomalyRules.LargeAmount, (string?)r.Id,
                $"{r.Reference}: {r.Currency} {r.Amount:0.00} is at or above the {threshold:0.00} this currency flags (floor {floor:0.00}, {o.LargeFactor:0.#} times the median)." )));
        }

        foreach (var group in confirmed.Where(r => r.DonorId is not null).GroupBy(r => (r.DonorId, r.Amount, r.Currency)))
        {
            var ordered = group.OrderBy(r => r.CreatedAt).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].CreatedAt - ordered[i - 1].CreatedAt <= TimeSpan.FromMinutes(o.RapidRepeatMinutes))
                {
                    flags.Add(($"{AnomalyRules.RapidRepeat}:{ordered[i].Id}", AnomalyRules.RapidRepeat, ordered[i].Id,
                        $"{ordered[i].Reference}: the same donor gave {ordered[i].Currency} {ordered[i].Amount:0.00} twice within {o.RapidRepeatMinutes} minutes (earlier: {ordered[i - 1].Reference})."));
                }
            }
        }

        var posted = (await db.Postings.AsNoTracking().Where(p => p.SourceType == PostingSources.Donation && p.SourceId != null).Select(p => p.SourceId!).ToListAsync(ct)).ToHashSet();
        flags.AddRange(confirmed.Where(r => !posted.Contains(r.Id)).Select(r => ($"{AnomalyRules.ConfirmedWithoutPosting}:{r.Id}", AnomalyRules.ConfirmedWithoutPosting, (string?)r.Id,
            $"{r.Reference}: confirmed but the ledger has no posting for it.")));

        var recent = rows.Where(r => r.Kind != DonationKinds.Reversal && r.CreatedAt >= now.AddHours(-24)).ToList();
        var failed = recent.Count(r => r.Status is DonationStatuses.Failed or DonationStatuses.Cancelled);
        if (recent.Count >= o.FailureSpikeMin && failed * 100m / recent.Count >= o.FailureSpikePercent)
        {
            flags.Add(($"{AnomalyRules.GatewayFailureSpike}:{now:yyyy-MM-dd}", AnomalyRules.GatewayFailureSpike, null,
                $"{failed} of the last {recent.Count} payment attempts in 24 hours failed or were cancelled ({failed * 100m / recent.Count:0}%): a gateway problem, or a test left running."));
        }

        var known = (await db.DonationAnomalies.AsNoTracking().Select(a => a.DedupeKey).ToListAsync(ct)).ToHashSet();
        var fresh = flags.Where(f => known.Add(f.Key)).ToList();
        foreach (var f in fresh)
        {
            db.DonationAnomalies.Add(new DonationAnomalyEntity { Id = $"anm_{Guid.NewGuid():N}", DedupeKey = f.Key, Rule = f.Rule, DonationId = f.DonationId, Detail = f.Detail.Length <= 300 ? f.Detail : f.Detail[..300], DetectedAt = now });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A scan at the same moment raised some of these first; the unique key kept each to one.
            db.ChangeTracker.Clear();
            return new ScanResult(rows.Count, 0);
        }

        return new ScanResult(rows.Count, fresh.Count);
    }

    private sealed record Row(string Id, string Reference, string Kind, decimal Amount, string Currency, string? DonorId, DateTimeOffset CreatedAt, string Status);
}

/// <summary>Donor pairs that may be one person, found from the hash of the email and the decrypted phone and name. Pairs a person has already ruled on are left out.</summary>
public sealed class DonorDuplicates(FinanceDbContext db, IDonorProtector protector)
{
    public async Task<IReadOnlyList<DuplicateCandidate>> CandidatesAsync(CancellationToken ct)
    {
        var donors = await db.Donors.AsNoTracking().Select(d => new { d.Id, d.EmailKey, d.NameProtected, d.PhoneProtected }).ToListAsync(ct);
        var decided = (await db.DonorDuplicateDecisions.AsNoTracking().Select(d => d.PairKey).ToListAsync(ct)).ToHashSet();
        var people = donors.Select(d => new PersonRecord(d.Id, Safe(d.NameProtected), d.EmailKey, Safe(d.PhoneProtected))).ToList();
        return [.. DuplicateDetector.Detect(people).Where(c => !decided.Contains(c.PairKey))];
    }

    private string? Safe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(value);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}

public sealed class DataQualityWorker(IServiceScopeFactory scopes, IOptions<DataQualityOptions> options, TimeProvider clock, ILogger<DataQualityWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, Exception?> Flagged = LoggerMessage.Define<int>(LogLevel.Warning, new EventId(50, nameof(Flagged)), "Data quality scan flagged {Count} new donation anomalies for review");
    private static readonly Action<ILogger, Exception?> Failed = LoggerMessage.Define(LogLevel.Error, new EventId(51, nameof(Failed)), "Donation anomaly scan failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = options.Value.ScanIntervalMinutes;
        if (minutes <= 0)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), clock, stoppingToken);
                using var scope = scopes.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<AnomalyDetector>().ScanAsync(stoppingToken);
                if (result.Flagged > 0)
                {
                    Flagged(logger, result.Flagged, null);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                Failed(logger, ex);
            }
        }
    }
}
