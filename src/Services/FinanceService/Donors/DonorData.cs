using System.Globalization;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using FinanceService.Ledger;
using KJWebsite.BuildingBlocks.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Donors;

public sealed record DonorGift(GiftFact Fact, string DonationId, string Reference, string ReceiptNumber, string? Method, string DesignationType, string? DesignationId, bool Offline);

/// <summary>A donor with everything derived from their valid, non-anonymous receipts as of a date.</summary>
public sealed class DonorView
{
    public required DonorEntity Entity { get; init; }
    public required string Name { get; init; }
    public required string Email { get; init; }
    public required IReadOnlyList<DonorGift> Gifts { get; init; }
    public required IReadOnlyList<string> Segments { get; init; }
    public required IReadOnlyList<(string Currency, Money Cash, Money InKind)> Totals { get; init; }

    public DonorRow Row()
    {
        if (Totals.Count == 0)
        {
            return new DonorRow(Entity.Id, Name, Email, "0.00", "BDT", 0, string.Empty, Segments, [], Entity.Type, Entity.Status, Entity.AccountStatus);
        }

        // The frontend shows one figure: the currency this donor has given the most in (ties go to the first alphabetically).
        var main = Totals.OrderByDescending(t => (t.Cash + Money.Of(t.InKind.Amount, t.Cash.Currency)).Amount).ThenBy(t => t.Currency, StringComparer.Ordinal).First();
        return new DonorRow(Entity.Id, Name, Email, (main.Cash.Amount + main.InKind.Amount).ToString("0.00", CultureInfo.InvariantCulture), main.Currency, Gifts.Count,
            Gifts.Max(g => g.Fact.ReceivedOn).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Segments,
            [.. Totals.Select(t => new DonorTotalView(t.Currency, t.Cash.ToDecimalString(), t.InKind.ToDecimalString()))], Entity.Type, Entity.Status, Entity.AccountStatus);
    }

    public IReadOnlyList<DonorGiftView> GiftViews() =>
    [
        .. Gifts.OrderByDescending(g => g.Fact.ReceivedOn).ThenByDescending(g => g.ReceiptNumber, StringComparer.Ordinal).Select(g => new DonorGiftView(
            g.DonationId, g.Reference, g.Fact.ReceivedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Entity.Id, Name, g.Fact.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            g.Fact.Currency, g.Method, g.Offline, g.DesignationId is null ? g.DesignationType : $"{g.DesignationType}:{g.DesignationId}", "confirmed", g.ReceiptNumber))
    ];
}

/// <summary>Reads the donor picture from the books. Nothing here is stored: it is recomputed from receipts, so it cannot differ from them.</summary>
public sealed class DonorData(FinanceDbContext db, IDonorProtector protector, DonorMessenger messenger, IOptions<DonorOptions> options, TimeProvider clock)
{
    public DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<List<DonorView>> LoadAsync(DateOnly asOf, CancellationToken cancellationToken)
    {
        var donors = await db.Donors.AsNoTracking().ToListAsync(cancellationToken);
        var gifts = await (from r in db.Receipts.AsNoTracking()
                           join d in db.Donations.AsNoTracking() on r.DonationId equals d.Id
                           where !r.IsAnonymous && !db.ReceiptVoids.Any(v => v.ReceiptId == r.Id)
                           select new { r.DonorKey, r.ReceivedOn, r.Currency, r.Amount, r.IsInKind, d.RecurringPlanId, r.Number, DonationId = d.Id, d.Reference, r.Method, r.DesignationType, r.DesignationId, Offline = db.OfflineRecords.Any(o => o.DonationId == d.Id) })
            .ToListAsync(cancellationToken);
        var byKey = gifts.Where(g => g.ReceivedOn <= asOf).ToLookup(g => g.DonorKey);
        var everGave = gifts.Select(g => g.DonorKey).ToHashSet(StringComparer.Ordinal);

        var views = new List<DonorView>();
        foreach (var donor in donors)
        {
            var mine = byKey[donor.DonorKey].Select(g => new DonorGift(new GiftFact(g.ReceivedOn, g.Currency, g.Amount, g.IsInKind, g.RecurringPlanId is not null), g.DonationId, g.Reference, g.Number, g.Method, g.DesignationType, g.DesignationId, g.Offline)).ToList();
            // A donor with no gift at all (made by staff before a first gift) is listed; one whose gifts all fall after the date is not yet.
            if (mine.Count == 0 && everGave.Contains(donor.DonorKey))
            {
                continue;
            }

            var facts = mine.Select(g => g.Fact).ToList();
            views.Add(new DonorView
            {
                Entity = donor,
                Name = protector.Unprotect(donor.NameProtected),
                Email = protector.Unprotect(donor.EmailProtected),
                Gifts = mine,
                Segments = DonorSegments.Of(facts, asOf, options.Value),
                Totals = DonorSegments.Totals(facts)
            });
        }

        return views;
    }

    /// <summary>One donor as the back office sees them, built from the books like the list. A donor with no gift yet gets an empty picture.</summary>
    public async Task<DonorView> ViewOfAsync(DonorEntity donor, CancellationToken cancellationToken) =>
        (await LoadAsync(Today, cancellationToken)).FirstOrDefault(v => v.Entity.Id == donor.Id)
        ?? new DonorView { Entity = donor, Name = protector.Unprotect(donor.NameProtected), Email = protector.Unprotect(donor.EmailProtected), Gifts = [], Segments = [], Totals = [] };

    public async Task<DonorDetail> DetailAsync(DonorView donor, DonorAccountState? live, CancellationToken cancellationToken)
    {
        var history = (await db.DonorConsents.AsNoTracking().Where(c => c.DonorId == donor.Entity.Id).ToListAsync(cancellationToken)).OrderBy(c => c.Id)
            .Select(c => new ConsentEventView(c.Purpose, c.Granted, c.Source, c.RecordedBy, c.At)).ToList();
        return new DonorDetail(donor.Row(), donor.Entity.PhoneProtected is null ? null : protector.Unprotect(donor.Entity.PhoneProtected),
            donor.Gifts.Count == 0 ? string.Empty : donor.Gifts.Min(g => g.Fact.ReceivedOn).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            await DonorEndpoints.ConsentOf(db, donor.Entity.Id, messenger), history, donor.GiftViews(),
            new DonorProfile(donor.Entity.Type, donor.Entity.Organisation, donor.Entity.AddressProtected is null ? null : protector.Unprotect(donor.Entity.AddressProtected), donor.Entity.Country,
                donor.Entity.NotesProtected is null ? null : protector.Unprotect(donor.Entity.NotesProtected), donor.Entity.Language, donor.Entity.Status, donor.Entity.AccountStatus,
                donor.Entity.AccountShared, live?.MustChangePassword, donor.Entity.CreatedAt));
    }

    /// <summary>Per donor and currency: valid receipts against the net donation income in the ledger for the same donations.</summary>
    public async Task<DonorReconciliation> ReconcileAsync(CancellationToken cancellationToken)
    {
        var donors = await db.Donors.AsNoTracking().ToListAsync(cancellationToken);
        var receipts = await db.Receipts.AsNoTracking().Where(r => !r.IsAnonymous).ToListAsync(cancellationToken);
        var voided = (await db.ReceiptVoids.AsNoTracking().Select(v => v.ReceiptId).ToListAsync(cancellationToken)).ToHashSet();
        var keyToDonor = donors.ToDictionary(d => d.DonorKey, d => d.Id);
        var donationToDonor = receipts.Where(r => keyToDonor.ContainsKey(r.DonorKey)).ToDictionary(r => r.DonationId, r => keyToDonor[r.DonorKey]);

        var postings = await db.Postings.AsNoTracking().Where(p => p.SourceType == PostingSources.Donation || p.ReversesPostingId != null)
            .Select(p => new { p.Id, p.SourceType, p.SourceId, p.ReversesPostingId }).ToListAsync(cancellationToken);
        var donationOfPosting = postings.Where(p => p.SourceType == PostingSources.Donation && p.SourceId != null).ToDictionary(p => p.Id, p => p.SourceId!);
        foreach (var reversal in postings.Where(p => p.ReversesPostingId is not null))
        {
            if (donationOfPosting.TryGetValue(reversal.ReversesPostingId!.Value, out var donationId))
            {
                donationOfPosting[reversal.Id] = donationId;
            }
        }

        var income = new[] { LedgerAccounts.DonationsGeneral, LedgerAccounts.DonationsDesignated };
        var lines = await (from e in db.LedgerEntries.AsNoTracking()
                           join a in db.Accounts.AsNoTracking() on e.AccountId equals a.Id
                           where income.Contains(a.Code)
                           select new { e.PostingId, e.Currency, e.Debit, e.Credit }).ToListAsync(cancellationToken);

        var ledger = new Dictionary<(string Donor, string Currency), decimal>();
        foreach (var line in lines)
        {
            if (donationOfPosting.TryGetValue(line.PostingId, out var donationId) && donationToDonor.TryGetValue(donationId, out var donorId))
            {
                ledger[(donorId, line.Currency)] = ledger.GetValueOrDefault((donorId, line.Currency)) + line.Credit - line.Debit;
            }
        }

        var books = receipts.Where(r => !voided.Contains(r.Id) && keyToDonor.ContainsKey(r.DonorKey))
            .GroupBy(r => (Donor: keyToDonor[r.DonorKey], r.Currency)).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

        var mismatches = new List<DonorReconciliationLine>();
        foreach (var key in books.Keys.Union(ledger.Keys).OrderBy(k => k.Donor, StringComparer.Ordinal).ThenBy(k => k.Currency, StringComparer.Ordinal))
        {
            var fromReceipts = books.GetValueOrDefault(key);
            var fromLedger = ledger.GetValueOrDefault(key);
            if (fromReceipts != fromLedger)
            {
                mismatches.Add(new DonorReconciliationLine(key.Donor, key.Currency, fromReceipts.ToString("0.00", CultureInfo.InvariantCulture), fromLedger.ToString("0.00", CultureInfo.InvariantCulture), (fromReceipts - fromLedger).ToString("0.00", CultureInfo.InvariantCulture)));
            }
        }

        return new DonorReconciliation(mismatches.Count == 0, donors.Count, mismatches);
    }
}
