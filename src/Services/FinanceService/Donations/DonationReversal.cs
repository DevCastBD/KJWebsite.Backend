using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Ledger;
using KJWebsite.BuildingBlocks;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Donations;

/// <summary>
/// Refunding a confirmed donation: the donation gets a reversing entry of kind `reversal`, its ledger posting is reversed and its receipt voided, all staged on the context so the caller
/// saves them in one commit (with the approval that allowed it). It is only reached through an approved refund request.
/// </summary>
public sealed class DonationReversal(FinanceDbContext db, LedgerService ledger, TimeProvider clock)
{
    /// <summary>Whether the donation could be reversed now, without staging anything.</summary>
    public async Task<Result> CheckAsync(string donationId, CancellationToken cancellationToken)
    {
        var donation = await db.Donations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == donationId, cancellationToken);
        if (donation is null)
        {
            return Result.Failure("NOT_FOUND", "Donation not found.");
        }

        if (await db.Donations.AnyAsync(d => d.ReversesDonationId == donationId, cancellationToken))
        {
            return Result.Failure("ALREADY_REVERSED", "This donation has already been reversed.");
        }

        var latest = await db.DonationEvents.Where(e => e.DonationId == donationId).OrderByDescending(e => e.Id).Select(e => e.Status).FirstOrDefaultAsync(cancellationToken) ?? DonationStatuses.Pending;
        if (donation.Kind == DonationKinds.Reversal || latest != DonationStatuses.Confirmed)
        {
            return Result.Failure("NOT_REVERSIBLE", "Only a confirmed donation can be reversed.");
        }

        return await db.Postings.AnyAsync(p => p.SourceType == PostingSources.Donation && p.SourceId == donationId, cancellationToken)
            ? Result.Success()
            : Result.Failure("NOT_POSTED", "This donation has no ledger posting to reverse.");
    }

    public async Task<Result<DonationEntity>> StageAsync(string donationId, string reason, string? actor, CancellationToken cancellationToken)
    {
        var check = await CheckAsync(donationId, cancellationToken);
        if (!check.IsSuccess)
        {
            return Result.Failure<DonationEntity>(check.Error!.Code, check.Error.Message);
        }

        var original = await db.Donations.AsNoTracking().FirstAsync(d => d.Id == donationId, cancellationToken);
        var now = clock.GetUtcNow();
        var postingId = await db.Postings.AsNoTracking().Where(p => p.SourceType == PostingSources.Donation && p.SourceId == original.Id).Select(p => p.Id).FirstAsync(cancellationToken);
        var staged = await ledger.StageReversalAsync(postingId, reason, actor, cancellationToken);
        if (!staged.IsSuccess)
        {
            return Result.Failure<DonationEntity>(staged.Error!.Code, staged.Error.Message);
        }

        var reversal = new DonationEntity
        {
            Id = PublicDonationEndpoints.NewId("don"),
            Reference = await PublicDonationEndpoints.NewReferenceAsync(db),
            Kind = DonationKinds.Reversal,
            Amount = -original.Amount,
            FeeAmount = -original.FeeAmount,
            Currency = original.Currency,
            DesignationType = original.DesignationType,
            DesignationId = original.DesignationId,
            IsAnonymous = original.IsAnonymous,
            DonorNameProtected = original.DonorNameProtected,
            DonorEmailProtected = original.DonorEmailProtected,
            DonorPhoneProtected = original.DonorPhoneProtected,
            Language = original.Language,
            RecurringPlanId = original.RecurringPlanId,
            ReversesDonationId = original.Id,
            ReversalReason = reason,
            CreatedBy = actor,
            CreatedAt = now,
            ReceivedOn = DateOnly.FromDateTime(now.UtcDateTime)
        };
        reversal.Events.Add(new DonationEventEntity { Status = DonationStatuses.Confirmed, Note = $"Reverses {original.Reference}", At = now });
        db.Donations.Add(reversal);

        // The donor's receipt is withdrawn with the gift: a void row, so the receipt keeps its number and the series stays gapless.
        var receiptId = await db.Receipts.AsNoTracking().Where(r => r.DonationId == original.Id).Select(r => r.Id).FirstOrDefaultAsync(cancellationToken);
        if (receiptId is not null && !await db.ReceiptVoids.AnyAsync(v => v.ReceiptId == receiptId, cancellationToken))
        {
            db.ReceiptVoids.Add(new ReceiptVoidEntity { ReceiptId = receiptId, Reason = reason, VoidedBy = actor, At = now });
        }

        return Result.Success(reversal);
    }
}
