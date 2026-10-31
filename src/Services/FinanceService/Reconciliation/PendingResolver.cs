using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Reconciliation;

public sealed record ResolveSummary(int Checked, int Confirmed, int Failed, int Cancelled, int StillPending, int Mismatched, int Errors);

/// <summary>
/// Settles checkouts whose webhook never came or could not be verified: it asks the gateway about every donation still `pending` after a grace period and applies the
/// answer exactly as a webhook would. A gateway that does not answer leaves the donation pending, never in a wrong final state, to be asked again next time.
/// </summary>
public sealed class PendingResolver(FinanceDbContext db, PaymentGatewayRegistry registry, PaymentSettlement settlement, IOptions<PaymentOptions> options, TimeProvider clock, ILogger<PendingResolver> logger)
{
    private const int BatchSize = 200;

    private static readonly Action<ILogger, string, string, string, Exception?> AskFailed = LoggerMessage.Define<string, string, string>(
        LogLevel.Warning, new EventId(12, nameof(AskFailed)), "Reconciliation could not ask {Gateway} about {Reference}: {Code}");

    public async Task<ResolveSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetUtcNow() - TimeSpan.FromMinutes(Math.Max(0, options.Value.ReconcileAfterMinutes));
        var rows = await (from a in db.PaymentAttempts.AsNoTracking()
                          join d in db.Donations.AsNoTracking() on a.DonationId equals d.Id
                          where a.Status == PaymentAttemptStatuses.Created && d.Kind != DonationKinds.Reversal
                              && db.DonationEvents.Where(e => e.DonationId == d.Id).OrderByDescending(e => e.Id).Select(e => e.Status).FirstOrDefault() == DonationStatuses.Pending
                          orderby a.Id
                          select new { Attempt = a, Donation = d }).Take(BatchSize * 5).ToListAsync(cancellationToken);

        // Only the newest started checkout of each donation counts; the age test runs here because SQLite cannot compare DateTimeOffset.
        var due = rows.GroupBy(r => r.Donation.Id).Select(g => g.OrderByDescending(r => r.Attempt.Id).First()).Where(r => r.Attempt.At <= cutoff).Take(BatchSize).ToList();

        int confirmed = 0, failed = 0, cancelled = 0, pending = 0, mismatched = 0, errors = 0;
        foreach (var row in due)
        {
            var gateway = registry.Find(row.Attempt.Gateway);
            if (gateway is not { IsEnabled: true })
            {
                pending++;
                continue;
            }

            try
            {
                var verification = await gateway.VerifyAsync(new VerifyRequest(row.Donation.Reference, row.Attempt.ProviderPaymentId), cancellationToken);
                switch (await settlement.ApplyAsync(row.Donation, gateway.Id, verification, "reconciliation", cancellationToken))
                {
                    case SettlementOutcome.Confirmed:
                        confirmed++;
                        break;
                    case SettlementOutcome.Failed:
                        failed++;
                        break;
                    case SettlementOutcome.Cancelled:
                        cancelled++;
                        break;
                    case SettlementOutcome.Mismatch:
                        mismatched++;
                        break;
                    default:
                        pending++;
                        break;
                }
            }
            catch (GatewayException exception)
            {
                errors++;
                AskFailed(logger, gateway.Id, row.Donation.Reference, exception.Code, null);
            }
        }

        return new ResolveSummary(due.Count, confirmed, failed, cancelled, pending, mismatched, errors);
    }
}

/// <summary>Runs the pending resolver on a timer (`Finance:Payments:ReconcileIntervalMinutes`, 0 = off) so stuck checkouts settle without anyone asking.</summary>
public sealed class ReconciliationWorker(IServiceScopeFactory scopes, IOptions<PaymentOptions> options, ILogger<ReconciliationWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> RunFailed = LoggerMessage.Define(LogLevel.Error, new EventId(13, nameof(RunFailed)), "Pending-payment reconciliation run failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = options.Value.ReconcileIntervalMinutes;
        if (minutes <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PendingResolver>().RunAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RunFailed(logger, exception);
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
