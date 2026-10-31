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
public sealed class DonationConfirmation(FinanceDbContext db, LedgerService ledger, TimeProvider clock)
{
    /// <summary>Success carries true when this call confirmed the donation, false when it already was.</summary>
    public async Task<Result<bool>> ConfirmAsync(string donationId, string method, string? note, CancellationToken cancellationToken = default)
    {
        var donation = await db.Donations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == donationId, cancellationToken);
        if (donation is null)
        {
            return Result.Failure<bool>("NOT_FOUND", "Donation not found.");
        }

        if (donation.Kind == DonationKinds.Reversal || await db.Donations.AnyAsync(r => r.ReversesDonationId == donationId, cancellationToken))
        {
            return Result.Failure<bool>("NOT_CONFIRMABLE", "A reversal, or a donation that has been reversed, cannot be confirmed.");
        }

        var latest = await db.DonationEvents.Where(e => e.DonationId == donationId).OrderByDescending(e => e.Id).Select(e => e.Status).FirstOrDefaultAsync(cancellationToken);
        if (latest == DonationStatuses.Confirmed)
        {
            return Result.Success(false);
        }

        var staged = await ledger.StageAsync(DonationPostings.ForConfirmation(donation, method, ledger.Today), actor: null, cancellationToken);
        if (!staged.IsSuccess)
        {
            return Result.Failure<bool>(staged.Error!.Code, staged.Error.Message);
        }

        db.DonationEvents.Add(new DonationEventEntity { DonationId = donationId, Status = DonationStatuses.Confirmed, Note = note, At = clock.GetUtcNow() });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success(true);
        }
        catch (DbUpdateException)
        {
            // A concurrent confirmation won the unique index on the donation's posting; its event is the one that counts.
            db.ChangeTracker.Clear();
            return Result.Success(false);
        }
    }

    /// <summary>Records that a payment failed or was cancelled. Only a donation still `pending` moves: a confirmed donation is never taken back by a late failure call, only by a reversal.</summary>
    public async Task<Result<bool>> MarkAsync(string donationId, string status, string? note, CancellationToken cancellationToken = default)
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
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(true);
    }
}
