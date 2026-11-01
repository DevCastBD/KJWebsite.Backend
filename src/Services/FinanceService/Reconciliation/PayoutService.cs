using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using FinanceService.Ledger;
using FinanceService.Payments;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Finance;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Reconciliation;

public sealed record PayoutLineInput(string? Reference, string? Gross, string? Fee, string? Net, string? Currency);

public sealed record PayoutImport(string? Gateway, string? PayoutRef, string? PaidOn, IReadOnlyList<PayoutLineInput>? Lines);

public sealed record PayoutLineView(string Reference, string Gross, string Fee, string Net, string Currency, string MatchStatus, string? Reason, string? PostingId);

public sealed record PayoutView(string Id, string Gateway, string PayoutRef, string PaidOn, string ImportedAt, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<PayoutLineView> Lines);

public sealed record UnpaidDonationRow(string Id, string Reference, string Date, string Gateway, string Amount, string Fee, string Charge, string Currency);

/// <summary>
/// Matches what a gateway says it paid out against the donations we confirmed. A line matches only when its donation is confirmed, not reversed, came through that
/// gateway, was not already settled, and the gross is exactly what was charged; each match posts the settlement to the ledger (the bank receives the net, the fee
/// is an expense, the gateway's clearing account is cleared by the gross). Anything else is kept as an unmatched line for a person to chase, never forced to fit.
/// The whole batch is written together or not at all.
/// </summary>
public sealed class PayoutService(FinanceDbContext db, LedgerService ledger, PaymentGatewayRegistry registry, TimeProvider clock)
{
    public const int MaxLines = 500;

    public async Task<Result<PayoutView>> ImportAsync(PayoutImport request, string? actor, CancellationToken cancellationToken = default)
    {
        var invalid = new List<string>();
        var gateway = request.Gateway?.Trim();
        if (gateway is null || !registry.KnownIds.Contains(gateway))
        {
            invalid.Add("gateway");
        }

        var payoutRef = request.PayoutRef?.Trim();
        if (payoutRef is not { Length: > 0 and <= 100 })
        {
            invalid.Add("payoutRef");
        }

        var paidOn = ledger.Today;
        if (!string.IsNullOrWhiteSpace(request.PaidOn) && !DateOnly.TryParseExact(request.PaidOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out paidOn))
        {
            invalid.Add("paidOn");
        }

        var inputs = request.Lines ?? [];
        if (inputs.Count is 0 or > MaxLines)
        {
            invalid.Add("lines");
        }

        var parsed = new List<(string Reference, Money Gross, Money Fee, Money Net)>();
        for (var i = 0; i < inputs.Count && inputs.Count <= MaxLines; i++)
        {
            var line = inputs[i];
            var gross = Money.Parse(line.Gross, line.Currency);
            var fee = Money.Parse(line.Fee ?? "0.00", line.Currency);
            var net = Money.Parse(line.Net, line.Currency);
            if (!DonationReference.LooksValid(line.Reference) || !gross.IsSuccess || !fee.IsSuccess || !net.IsSuccess
                || !gross.Value.IsPositive || fee.Value.IsNegative || net.Value.IsNegative || gross.Value != fee.Value + net.Value)
            {
                invalid.Add($"lines[{i}]");
                continue;
            }

            parsed.Add((line.Reference!, gross.Value, fee.Value, net.Value));
        }

        if (invalid.Count > 0)
        {
            return Result.Failure<PayoutView>("VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
        }

        if (await db.Payouts.AnyAsync(p => p.Gateway == gateway && p.PayoutRef == payoutRef, cancellationToken))
        {
            return Result.Failure<PayoutView>("ALREADY_IMPORTED", "This payout has already been imported.");
        }

        var references = parsed.Select(p => p.Reference).Distinct().ToList();
        var known = await (from d in db.Donations.AsNoTracking()
                           where references.Contains(d.Reference) && d.Kind != DonationKinds.Reversal
                           select new
                           {
                               Donation = d,
                               Latest = db.DonationEvents.Where(e => e.DonationId == d.Id).OrderByDescending(e => e.Id).Select(e => e.Status).FirstOrDefault(),
                               Reversed = db.Donations.Any(r => r.ReversesDonationId == d.Id),
                               Via = db.PaymentAttempts.Where(a => a.DonationId == d.Id && a.Status == PaymentAttemptStatuses.Created).OrderByDescending(a => a.Id).Select(a => a.Gateway).FirstOrDefault(),
                               Settled = db.PayoutLines.Any(l => l.DonationId == d.Id && l.MatchStatus == MatchStatuses.Matched)
                           }).ToListAsync(cancellationToken);
        var byReference = known.ToDictionary(k => k.Donation.Reference);

        var payout = new PayoutEntity { Gateway = gateway!, PayoutRef = payoutRef!, PaidOn = paidOn, ImportedBy = actor, ImportedAt = clock.GetUtcNow() };
        var settledInThisBatch = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (reference, gross, fee, net) in parsed)
        {
            var line = new PayoutLineEntity { Payout = payout, Reference = reference, Gross = gross.Amount, Fee = fee.Amount, Net = net.Amount, Currency = gross.Currency };
            if (!byReference.TryGetValue(reference, out var match) || match.Via != gateway)
            {
                (line.MatchStatus, line.Reason) = (MatchStatuses.Unmatched, "UNKNOWN_PAYMENT");
            }
            else if (match.Settled || !settledInThisBatch.Add(reference))
            {
                (line.DonationId, line.MatchStatus, line.Reason) = (match.Donation.Id, MatchStatuses.Duplicate, "ALREADY_SETTLED");
            }
            else if (match.Latest != DonationStatuses.Confirmed || match.Reversed)
            {
                (line.DonationId, line.MatchStatus, line.Reason) = (match.Donation.Id, MatchStatuses.Unmatched, match.Reversed ? "REVERSED" : "NOT_CONFIRMED");
            }
            else if (match.Donation.Currency != gross.Currency || match.Donation.Amount + match.Donation.FeeAmount != gross.Amount)
            {
                (line.DonationId, line.MatchStatus, line.Reason) = (match.Donation.Id, MatchStatuses.AmountMismatch, "AMOUNT_MISMATCH");
            }
            else
            {
                var staged = await ledger.StageAsync(Settlement(payout, match.Donation, gateway!, gross, fee, net), actor, cancellationToken);
                if (!staged.IsSuccess || !staged.Value!.Created)
                {
                    db.ChangeTracker.Clear();
                    return Result.Failure<PayoutView>(staged.IsSuccess ? "ALREADY_IMPORTED" : staged.Error!.Code, staged.IsSuccess ? "This payout has already been imported." : staged.Error!.Message);
                }

                (line.DonationId, line.MatchStatus, line.Posting) = (match.Donation.Id, MatchStatuses.Matched, staged.Value.Posting);
            }

            payout.Lines.Add(line);
        }

        db.Payouts.Add(payout);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Result.Failure<PayoutView>("ALREADY_IMPORTED", "This payout, or one of its donations, has already been settled.");
        }

        return Result.Success(await ViewAsync(payout.Id, cancellationToken) ?? throw new InvalidOperationException("The payout could not be read back."));
    }

    public async Task<PayoutView?> ViewAsync(long id, CancellationToken cancellationToken = default)
    {
        var payout = await db.Payouts.AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        return payout is null ? null : ToView(payout);
    }

    public static PayoutView ToView(PayoutEntity payout) => new(
        payout.Id.ToString(CultureInfo.InvariantCulture), payout.Gateway, payout.PayoutRef, payout.PaidOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        payout.ImportedAt.ToString("O", CultureInfo.InvariantCulture),
        payout.Lines.GroupBy(l => l.MatchStatus).ToDictionary(g => g.Key, g => g.Count()),
        [.. payout.Lines.OrderBy(l => l.Id).Select(l => new PayoutLineView(l.Reference, Format(l.Gross), Format(l.Fee), Format(l.Net), l.Currency, l.MatchStatus, l.Reason, l.PostingId?.ToString(CultureInfo.InvariantCulture)))]);

    /// <summary>Confirmed gateway donations no imported payout has settled yet: what finance should expect to see in a payout.</summary>
    public async Task<(IReadOnlyList<UnpaidDonationRow> Rows, int Total)> UnpaidAsync(string? gateway, int skip, int take, CancellationToken cancellationToken = default)
    {
        var query = from d in db.Donations.AsNoTracking()
                    where d.Kind != DonationKinds.Reversal
                        && db.DonationEvents.Where(e => e.DonationId == d.Id).OrderByDescending(e => e.Id).Select(e => e.Status).FirstOrDefault() == DonationStatuses.Confirmed
                        && !db.Donations.Any(r => r.ReversesDonationId == d.Id)
                        && !db.PayoutLines.Any(l => l.DonationId == d.Id && l.MatchStatus == MatchStatuses.Matched)
                    select new { Donation = d, Via = db.PaymentAttempts.Where(a => a.DonationId == d.Id && a.Status == PaymentAttemptStatuses.Created).OrderByDescending(a => a.Id).Select(a => a.Gateway).FirstOrDefault() };
        query = query.Where(x => x.Via != null && (gateway == null || x.Via == gateway));
        var total = await query.CountAsync(cancellationToken);
        var page = await query.OrderBy(x => x.Donation.ReceivedOn).ThenBy(x => x.Donation.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return ([.. page.Select(x => new UnpaidDonationRow(x.Donation.Id, x.Donation.Reference, x.Donation.ReceivedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), x.Via!,
            Format(x.Donation.Amount), Format(x.Donation.FeeAmount), Format(x.Donation.Amount + x.Donation.FeeAmount), x.Donation.Currency))], total);
    }

    private static PostingDraft Settlement(PayoutEntity payout, DonationEntity donation, string gateway, Money gross, Money fee, Money net)
    {
        List<LedgerLine> lines = [LedgerLine.Debit(LedgerAccounts.Bank, net), LedgerLine.Credit(LedgerAccounts.ClearingFor(gateway), gross)];
        if (fee.IsPositive)
        {
            lines.Add(LedgerLine.Debit(LedgerAccounts.PaymentProcessingFees, fee));
        }

        // Unique per payout and donation; hashed so the id stays short whatever the gateway's payout reference looks like.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{gateway}|{payout.PayoutRef}|{donation.Id}")))[..32];
        return new PostingDraft(payout.PaidOn, $"Payout {payout.PayoutRef} ({gateway}): {donation.Reference}", PostingSources.Payout, key, lines);
    }

    private static string Format(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
