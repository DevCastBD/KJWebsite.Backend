using FinanceService.Data;
using FinanceService.Data.Entities;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Finance;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Ledger;

public static class DonationPostings
{
    /// <summary>
    /// A confirmed donation: the money sits in the clearing (or cash/bank) account for the method it arrived by, for the whole amount charged;
    /// the designated gift is income, and a covered fee is income of its own, so the later gateway fee expense has something to offset.
    /// </summary>
    public static PostingDraft ForConfirmation(DonationEntity donation, string method, DateOnly postedOn)
    {
        var amount = Money.Of(donation.Amount, donation.Currency);
        var fee = Money.Of(donation.FeeAmount, donation.Currency);
        var income = donation.DesignationType == Designations.General ? LedgerAccounts.DonationsGeneral : LedgerAccounts.DonationsDesignated;
        List<LedgerLine> lines = [LedgerLine.Debit(LedgerAccounts.ClearingFor(method), amount + fee), LedgerLine.Credit(income, amount)];
        if (fee.IsPositive)
        {
            lines.Add(LedgerLine.Credit(LedgerAccounts.FeeContributions, fee));
        }

        return new PostingDraft(postedOn, $"Donation {donation.Reference}", PostingSources.Donation, donation.Id, lines);
    }
}

/// <summary>
/// Marks a donation confirmed and posts it, in one commit. Gateway webhooks (BE-056) and offline recording (BE-058) call this, and nothing else
/// sets a donation to confirmed. It is safe to call again for the same donation, however often and however concurrently: the second call finds the
/// first one's work and changes nothing, so a webhook replayed ten times leaves one confirmed event and one posting.
/// </summary>
public sealed class DonationConfirmation(FinanceDbContext db, LedgerService ledger, FinanceService.Receipts.ReceiptService receipts, FinanceService.Donors.DonorService donors, TimeProvider clock)
{
    /// <summary>Success carries true when this call confirmed the donation, false when it already was.</summary>
    public async Task<Result<bool>> ConfirmAsync(
        string donationId, string method, string? note, DateOnly? postedOn = null, Action<FinanceDbContext>? alsoStage = null, CancellationToken cancellationToken = default)
    {
        // Two confirmations racing for the same receipt number: the loser's whole commit fails, and it tries again with the next number (or finds the donation done).
        const int attempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            var (result, retry) = await TryConfirmAsync(donationId, method, note, postedOn, alsoStage, cancellationToken);
            if (!retry || attempt == attempts)
            {
                return retry ? Result.Failure<bool>("CONFLICT", "The confirmation could not be saved; please try again.") : result;
            }

            // A short, uneven pause so racing confirmations do not collide on the same number again in lockstep.
            await Task.Delay(Random.Shared.Next(2, 25), cancellationToken);
        }
    }

    private async Task<(Result<bool> Result, bool Retry)> TryConfirmAsync(
        string donationId, string method, string? note, DateOnly? postedOn, Action<FinanceDbContext>? alsoStage, CancellationToken cancellationToken)
    {
        var donation = await db.Donations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == donationId, cancellationToken);
        if (donation is null)
        {
            return (Result.Failure<bool>("NOT_FOUND", "Donation not found."), false);
        }

        if (donation.Kind == DonationKinds.Reversal || await db.Donations.AnyAsync(r => r.ReversesDonationId == donationId, cancellationToken))
        {
            return (Result.Failure<bool>("NOT_CONFIRMABLE", "A reversal, or a donation that has been reversed, cannot be confirmed."), false);
        }

        var latest = await db.DonationEvents.Where(e => e.DonationId == donationId).OrderByDescending(e => e.Id).Select(e => e.Status).FirstOrDefaultAsync(cancellationToken);
        if (latest == DonationStatuses.Confirmed)
        {
            return (Result.Success(false), false);
        }

        var staged = await ledger.StageAsync(DonationPostings.ForConfirmation(donation, method, postedOn ?? ledger.Today), actor: null, cancellationToken);
        if (!staged.IsSuccess)
        {
            return (Result.Failure<bool>(staged.Error!.Code, staged.Error.Message), false);
        }

        db.DonationEvents.Add(new DonationEventEntity { DonationId = donationId, Status = DonationStatuses.Confirmed, Note = note, At = clock.GetUtcNow() });

        // Anything that must stand or fall with the confirmation (an approval record, say) is saved in the same commit.
        alsoStage?.Invoke(db);

        // The receipt is issued in the same commit, so a confirmed donation always has exactly one.
        var receipt = await receipts.StageIssueAsync(donation, method, cancellationToken);
        if (receipt is not null)
        {
            // The donor record, consent from the form and the thank-you (if consented) are saved with it.
            await donors.StageAsync(donation, receipt, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return (Result.Success(true), false);
        }
        catch (DbUpdateException)
        {
            // Either a concurrent confirmation of this donation won the posting's unique index (its event counts), or another donation took our receipt number (try again).
            db.ChangeTracker.Clear();
            var nowConfirmed = await db.DonationEvents.Where(e => e.DonationId == donationId).OrderByDescending(e => e.Id).Select(e => e.Status).FirstOrDefaultAsync(cancellationToken) == DonationStatuses.Confirmed;
            return nowConfirmed ? (Result.Success(false), false) : (Result.Success(false), true);
        }
    }

    /// <summary>Records that a payment failed or was cancelled. Only a donation still `pending` moves: a confirmed donation is never taken back by a late failure call, only by a reversal.</summary>
    public async Task<Result<bool>> MarkAsync(string donationId, string status, string? note, Action<FinanceDbContext>? alsoStage = null, CancellationToken cancellationToken = default)
    {
        if (status is not (DonationStatuses.Failed or DonationStatuses.Cancelled))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "Only failed or cancelled can be recorded here; confirmation posts to the ledger and has its own method.");
        }

        var latest = await db.DonationEvents.Where(e => e.DonationId == donationId).OrderByDescending(e => e.Id).Select(e => e.Status).FirstOrDefaultAsync(cancellationToken);
        if (latest is null)
        {
            return Result.Failure<bool>("NOT_FOUND", "Donation not found.");
        }

        if (latest != DonationStatuses.Pending)
        {
            return Result.Success(false);
        }

        db.DonationEvents.Add(new DonationEventEntity { DonationId = donationId, Status = status, Note = note, At = clock.GetUtcNow() });
        alsoStage?.Invoke(db);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(true);
    }
}
