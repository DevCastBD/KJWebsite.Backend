using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Ledger;
using KJWebsite.BuildingBlocks.Finance;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Payments;

public enum SettlementOutcome
{
    Confirmed,
    AlreadyConfirmed,
    Failed,
    Cancelled,
    Pending,
    Mismatch,
    Ignored
}

/// <summary>
/// Turns a gateway's own answer about a payment into a decision on the donation. It is the one place that does so, shared by webhooks and by the
/// reconciliation job, so both apply the same rules: money is confirmed only when the gateway says it was paid, for the same reference and exactly the
/// charged amount in the charged currency. Anything else is recorded as a rejection and the donation is left alone.
/// </summary>
public sealed class PaymentSettlement(FinanceDbContext db, DonationConfirmation confirmation, TimeProvider clock, ILogger<PaymentSettlement> logger)
{
    private static readonly Action<ILogger, string, string, string, string, Exception?> Rejected = LoggerMessage.Define<string, string, string, string>(
        LogLevel.Warning, new EventId(10, nameof(Rejected)), "Payment call rejected: gateway {Gateway}, reason {Reason}, reference {Reference}, from {Source}");

    public async Task<SettlementOutcome> ApplyAsync(DonationEntity donation, string gateway, PaymentVerification verification, string source, CancellationToken cancellationToken)
    {
        switch (verification.Outcome)
        {
            case PaymentOutcome.Paid:
                if (verification.Reference is not null && !string.Equals(verification.Reference, donation.Reference, StringComparison.Ordinal))
                {
                    await RejectAsync(gateway, RejectionReasons.ReferenceMismatch, source, donation.Reference, cancellationToken);
                    return SettlementOutcome.Mismatch;
                }

                var charge = Money.Of(donation.Amount + donation.FeeAmount, donation.Currency);
                if (verification.Amount is not { } paid || paid != charge)
                {
                    await RejectAsync(gateway, RejectionReasons.AmountMismatch, source, donation.Reference, cancellationToken);
                    return SettlementOutcome.Mismatch;
                }

                var confirmed = await confirmation.ConfirmAsync(donation.Id, gateway, $"Confirmed by {gateway}", cancellationToken: cancellationToken);
                if (!confirmed.IsSuccess)
                {
                    // A save conflict is not an answer about the payment: leave it pending so the redelivery or the resolver settles it.
                    return confirmed.Error!.Code == "CONFLICT" ? SettlementOutcome.Pending : SettlementOutcome.Ignored;
                }

                await RecordSettlementAsync(donation.Id, gateway, verification.Settlement, cancellationToken);
                return confirmed.Value ? SettlementOutcome.Confirmed : SettlementOutcome.AlreadyConfirmed;

            case PaymentOutcome.Failed:
            case PaymentOutcome.Cancelled:
                var status = verification.Outcome == PaymentOutcome.Failed ? DonationStatuses.Failed : DonationStatuses.Cancelled;
                var marked = await confirmation.MarkAsync(donation.Id, status, $"{status} at {gateway}", cancellationToken: cancellationToken);
                return !marked.IsSuccess || !marked.Value ? SettlementOutcome.Ignored : verification.Outcome == PaymentOutcome.Failed ? SettlementOutcome.Failed : SettlementOutcome.Cancelled;

            default:
                return SettlementOutcome.Pending;
        }
    }

    // What the gateway settled is kept apart from the donor's designated amount; one record per donation, so repeating a confirmation adds nothing.
    private async Task RecordSettlementAsync(string donationId, string gateway, SettlementInfo? info, CancellationToken cancellationToken)
    {
        if (info is null || await db.PaymentSettlements.AnyAsync(p => p.DonationId == donationId, cancellationToken))
        {
            return;
        }

        db.PaymentSettlements.Add(new PaymentSettlementEntity
        {
            DonationId = donationId,
            Gateway = gateway,
            TransactionId = info.TransactionId,
            SettledAmount = info.Settled.Amount,
            SettledCurrency = info.Settled.Currency,
            Fee = info.Fee?.Amount,
            ExchangeRate = info.ExchangeRate,
            At = clock.GetUtcNow()
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent confirmation recorded it first.
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Records a refused call: a row for the alert list and a warning in the log. Only the gateway, a reason code, the caller's address and the reference are kept.</summary>
    public async Task RejectAsync(string gateway, string reason, string source, string? reference, CancellationToken cancellationToken)
    {
        Rejected(logger, Trim(gateway, 32)!, reason, Trim(reference, 64) ?? "-", source, null);
        db.WebhookRejections.Add(new WebhookRejectionEntity { Gateway = Trim(gateway, 32)!, ReasonCode = reason, SourceIp = Trim(source, 64)!, Reference = Trim(reference, 64), At = clock.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? Trim(string? value, int length) => value is null ? null : value.Length <= length ? value : value[..length];
}
