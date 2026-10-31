using FinanceService.Data;
using FinanceService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Payments;

public enum WebhookDisposition
{
    /// <summary>Handled (or already handled): answer 200.</summary>
    Accepted,

    /// <summary>Refused and recorded: answer 400.</summary>
    Rejected,

    /// <summary>Could not be decided right now (the gateway did not answer): ask for a redelivery with 503.</summary>
    Retry
}

public sealed record WebhookResult(WebhookDisposition Disposition, string? Reference);

/// <summary>
/// Handles one call from a gateway. Order matters: it checks the signature first and refuses anything unsigned or badly signed, then finds the donation, then
/// asks the gateway itself what happened and acts on that answer alone (via <see cref="PaymentSettlement"/>). The call is only ever a prompt to look, so a forged
/// one can at worst make us ask the gateway about a real payment. A delivery that was handled is remembered by its key, so a replay changes nothing.
/// </summary>
public sealed class WebhookProcessor(FinanceDbContext db, PaymentGatewayRegistry registry, PaymentSettlement settlement, TimeProvider clock, ILogger<WebhookProcessor> logger)
{
    private static readonly Action<ILogger, string, string, Exception?> VerifyFailed = LoggerMessage.Define<string, string>(
        LogLevel.Warning, new EventId(11, nameof(VerifyFailed)), "Could not verify a payment with {Gateway}: {Code}; asking for redelivery");

    private static readonly Action<ILogger, string, string, string, Exception?> NoticeRecorded = LoggerMessage.Define<string, string, string>(
        LogLevel.Warning, new EventId(14, nameof(NoticeRecorded)), "Gateway {Gateway} reported a {Kind} for donation {Reference}: finance should review it");

    public async Task<WebhookResult> ProcessAsync(string gatewayId, IncomingWebhook webhook, CancellationToken cancellationToken)
    {
        var gateway = registry.Find(gatewayId);
        if (gateway is null)
        {
            await settlement.RejectAsync(gatewayId, RejectionReasons.UnknownGateway, webhook.SourceIp, null, cancellationToken);
            return new WebhookResult(WebhookDisposition.Rejected, null);
        }

        if (!gateway.IsEnabled)
        {
            await settlement.RejectAsync(gateway.Id, RejectionReasons.GatewayDisabled, webhook.SourceIp, null, cancellationToken);
            return new WebhookResult(WebhookDisposition.Rejected, null);
        }

        WebhookParse parsed;
        try
        {
            parsed = await gateway.ParseWebhookAsync(webhook, cancellationToken);
        }
        catch (GatewayException exception)
        {
            // Checking a signature can itself need the gateway (PayPal); if it cannot be done now, nothing is decided and the delivery is retried.
            VerifyFailed(logger, gateway.Id, exception.Code, null);
            return new WebhookResult(WebhookDisposition.Retry, null);
        }

        if (parsed.Authenticity == WebhookAuthenticity.Invalid)
        {
            await settlement.RejectAsync(gateway.Id, RejectionReasons.BadSignature, webhook.SourceIp, parsed.Reference, cancellationToken);
            return new WebhookResult(WebhookDisposition.Rejected, null);
        }

        if (parsed.Irrelevant)
        {
            return new WebhookResult(WebhookDisposition.Accepted, null);
        }

        if (parsed.Notice is not null)
        {
            return await ProcessNoticeAsync(gateway.Id, parsed, webhook, cancellationToken);
        }

        var (donation, providerPaymentId) = await FindAsync(gateway.Id, parsed, cancellationToken);
        if (donation is null)
        {
            await settlement.RejectAsync(gateway.Id, RejectionReasons.UnknownPayment, webhook.SourceIp, parsed.Reference ?? parsed.ProviderPaymentId, cancellationToken);
            return new WebhookResult(WebhookDisposition.Rejected, null);
        }

        if (parsed.EventKey is not null && await db.PaymentEvents.AnyAsync(e => e.Gateway == gateway.Id && e.EventKey == parsed.EventKey, cancellationToken))
        {
            return new WebhookResult(WebhookDisposition.Accepted, donation.Reference);
        }

        PaymentVerification verification;
        try
        {
            verification = await gateway.VerifyAsync(new VerifyRequest(donation.Reference, providerPaymentId), cancellationToken);
        }
        catch (GatewayException exception)
        {
            VerifyFailed(logger, gateway.Id, exception.Code, null);
            return new WebhookResult(WebhookDisposition.Retry, donation.Reference);
        }

        var outcome = await settlement.ApplyAsync(donation, gateway.Id, verification, webhook.SourceIp, cancellationToken);
        if (outcome == SettlementOutcome.Mismatch)
        {
            return new WebhookResult(WebhookDisposition.Rejected, donation.Reference);
        }

        // Pending is not final: leave no record so the gateway's redelivery (or the resolver) is looked at again.
        if (outcome != SettlementOutcome.Pending && parsed.EventKey is not null)
        {
            db.PaymentEvents.Add(new PaymentEventEntity { Gateway = gateway.Id, EventKey = parsed.EventKey, DonationId = donation.Id, Outcome = Describe(outcome), At = clock.GetUtcNow() });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A concurrent delivery of the same event recorded it first; the donation was handled once either way.
                db.ChangeTracker.Clear();
            }
        }

        return new WebhookResult(WebhookDisposition.Accepted, donation.Reference);
    }

    // A refund or dispute message: only a signed one counts, it is stored for finance to act on, and nothing about the donation changes by itself.
    private async Task<WebhookResult> ProcessNoticeAsync(string gateway, WebhookParse parsed, IncomingWebhook webhook, CancellationToken cancellationToken)
    {
        if (parsed.Authenticity != WebhookAuthenticity.Verified)
        {
            await settlement.RejectAsync(gateway, RejectionReasons.BadSignature, webhook.SourceIp, parsed.Reference, cancellationToken);
            return new WebhookResult(WebhookDisposition.Rejected, null);
        }

        var (donation, _) = await FindAsync(gateway, parsed, cancellationToken);
        if (donation is null && parsed.TransactionId is not null)
        {
            var donationId = await db.PaymentSettlements.AsNoTracking().Where(p => p.Gateway == gateway && p.TransactionId == parsed.TransactionId).Select(p => p.DonationId).FirstOrDefaultAsync(cancellationToken);
            donation = donationId is null ? null : await db.Donations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == donationId, cancellationToken);
        }

        if (donation is null)
        {
            await settlement.RejectAsync(gateway, RejectionReasons.UnknownPayment, webhook.SourceIp, parsed.Reference ?? parsed.TransactionId, cancellationToken);
            return new WebhookResult(WebhookDisposition.Rejected, null);
        }

        if (parsed.EventKey is not null && await db.ProviderNotices.AnyAsync(n => n.Gateway == gateway && n.EventKey == parsed.EventKey, cancellationToken))
        {
            return new WebhookResult(WebhookDisposition.Accepted, donation.Reference);
        }

        db.ProviderNotices.Add(new ProviderNoticeEntity
        {
            Gateway = gateway,
            EventKey = parsed.EventKey ?? Guid.NewGuid().ToString("N"),
            Kind = parsed.Notice == NoticeKind.Refund ? NoticeKinds.Refund : NoticeKinds.Dispute,
            DonationId = donation.Id,
            TransactionId = parsed.TransactionId,
            Amount = parsed.NoticeAmount?.Amount,
            Currency = parsed.NoticeAmount?.Currency,
            At = clock.GetUtcNow()
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            NoticeRecorded(logger, gateway, parsed.Notice == NoticeKind.Refund ? NoticeKinds.Refund : NoticeKinds.Dispute, donation.Reference, null);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }

        return new WebhookResult(WebhookDisposition.Accepted, donation.Reference);
    }

    private async Task<(DonationEntity? Donation, string? ProviderPaymentId)> FindAsync(string gateway, WebhookParse parsed, CancellationToken cancellationToken)
    {
        PaymentAttemptEntity? attempt = null;
        if (parsed.ProviderPaymentId is not null)
        {
            attempt = await db.PaymentAttempts.AsNoTracking().OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(a => a.Gateway == gateway && a.Status == PaymentAttemptStatuses.Created && a.ProviderPaymentId == parsed.ProviderPaymentId, cancellationToken);
        }
        else if (parsed.Reference is not null)
        {
            attempt = await (from a in db.PaymentAttempts.AsNoTracking()
                             join d in db.Donations.AsNoTracking() on a.DonationId equals d.Id
                             where a.Gateway == gateway && a.Status == PaymentAttemptStatuses.Created && d.Reference == parsed.Reference
                             orderby a.Id descending
                             select a).FirstOrDefaultAsync(cancellationToken);
        }

        if (attempt is null)
        {
            return (null, null);
        }

        var donation = await db.Donations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == attempt.DonationId && d.Kind != DonationKinds.Reversal, cancellationToken);
        return (donation, attempt.ProviderPaymentId);
    }

    internal static string Describe(SettlementOutcome outcome) => outcome switch
    {
        SettlementOutcome.Confirmed => "confirmed",
        SettlementOutcome.AlreadyConfirmed => "already_confirmed",
        SettlementOutcome.Failed => "failed",
        SettlementOutcome.Cancelled => "cancelled",
        _ => "ignored"
    };
}
