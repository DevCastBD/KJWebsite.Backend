using FinanceService.Data;
using FinanceService.Data.Entities;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Ledger;

/// <summary>A posting that was just written, or the existing one a repeated source resolved to.</summary>
public sealed record PostingOutcome(PostingEntity Posting, bool Created);

public sealed record TrialBalanceLine(string Code, string Name, string Type, string Currency, decimal Debit, decimal Credit)
{
    /// <summary>Positive in the account's normal direction (debit for assets and expenses, credit for the rest).</summary>
    public decimal Balance => AccountTypes.IsDebitNormal(Type) ? Debit - Credit : Credit - Debit;
}

public sealed record CurrencyTrialBalance(string Currency, IReadOnlyList<TrialBalanceLine> Lines, decimal TotalDebit, decimal TotalCredit)
{
    public bool Balanced => TotalDebit == TotalCredit;
}

/// <summary>
/// The only way entries get into the ledger. `Stage*` adds a whole posting to the DbContext without saving, so a caller can save it in the same
/// transaction as the thing that caused it (a confirmed donation and its posting are one commit); `Post`/`Reverse` stage and save.
/// No method here, or anywhere, updates or deletes a posting: a correction is a new posting that mirrors the original.
/// </summary>
public sealed class LedgerService(FinanceDbContext db, IOptions<LedgerOptions> options, TimeProvider clock)
{
    public DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<Result<PostingOutcome>> StageAsync(PostingDraft draft, string? actor, CancellationToken cancellationToken = default)
    {
        if (draft.SourceId is not null)
        {
            var existing = await db.Postings.Include(p => p.Entries).AsNoTracking().FirstOrDefaultAsync(p => p.SourceType == draft.SourceType && p.SourceId == draft.SourceId, cancellationToken);
            if (existing is not null)
            {
                return Result.Success(new PostingOutcome(existing, false));
            }
        }

        if (draft.PostedOn > Today)
        {
            return Result.Failure<PostingOutcome>("POSTING_DATE_FUTURE", "A posting cannot be dated in the future.");
        }

        var codes = draft.Lines.Select(l => l.AccountCode).Distinct().ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => codes.Contains(a.Code)).ToDictionaryAsync(a => a.Code, cancellationToken);
        var valid = PostingRules.Validate(draft, accounts);
        if (!valid.IsSuccess)
        {
            return Result.Failure<PostingOutcome>(valid.Error!.Code, valid.Error.Message);
        }

        var period = FiscalCalendar.PeriodKey(draft.PostedOn);
        if (await db.PeriodCloses.AnyAsync(c => c.PeriodKey == period, cancellationToken))
        {
            return Result.Failure<PostingOutcome>("PERIOD_CLOSED", $"Period {period} is closed.");
        }

        var posting = new PostingEntity
        {
            PostedOn = draft.PostedOn,
            PeriodKey = period,
            FiscalYear = FiscalCalendar.FiscalYear(draft.PostedOn, options.Value.FiscalYearStartMonth),
            Memo = draft.Memo.Trim(),
            SourceType = draft.SourceType,
            SourceId = draft.SourceId,
            Reason = draft.Reason,
            CreatedBy = actor,
            CreatedAt = clock.GetUtcNow(),
            Entries =
            [
                .. draft.Lines.Select(l => new LedgerEntryEntity
                {
                    AccountId = accounts[l.AccountCode].Id,
                    Debit = l.Side == LedgerSide.Debit ? l.Amount.Amount : 0m,
                    Credit = l.Side == LedgerSide.Credit ? l.Amount.Amount : 0m,
                    Currency = l.Amount.Currency
                })
            ]
        };
        db.Postings.Add(posting);
        return Result.Success(new PostingOutcome(posting, true));
    }

    /// <summary>Stages and saves. Two requests posting the same source at once end up with one posting: the loser's unique-index failure resolves to the winner's.</summary>
    public async Task<Result<PostingOutcome>> PostAsync(PostingDraft draft, string? actor, CancellationToken cancellationToken = default)
    {
        var staged = await StageAsync(draft, actor, cancellationToken);
        if (!staged.IsSuccess || !staged.Value!.Created)
        {
            return staged;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return staged;
        }
        catch (DbUpdateException) when (draft.SourceId is not null)
        {
            db.ChangeTracker.Clear();
            var winner = await db.Postings.Include(p => p.Entries).AsNoTracking().FirstOrDefaultAsync(p => p.SourceType == draft.SourceType && p.SourceId == draft.SourceId, cancellationToken);
            return winner is null ? throw new InvalidOperationException("The posting could not be saved.") : Result.Success(new PostingOutcome(winner, false));
        }
    }

    /// <summary>
    /// Stages a posting that mirrors <paramref name="postingId"/> with debits and credits swapped, dated today (the current open period): history is never
    /// rewritten, and the original and its reversal both stay visible. A posting can be reversed once, and a reversal cannot itself be reversed.
    /// The accounts' archived state is ignored: reversing must always be possible.
    /// </summary>
    public async Task<Result<PostingOutcome>> StageReversalAsync(long postingId, string reason, string? actor, CancellationToken cancellationToken = default)
    {
        var original = await db.Postings.Include(p => p.Entries).AsNoTracking().FirstOrDefaultAsync(p => p.Id == postingId, cancellationToken);
        if (original is null)
        {
            return Result.Failure<PostingOutcome>("NOT_FOUND", "Posting not found.");
        }

        if (original.ReversesPostingId is not null)
        {
            return Result.Failure<PostingOutcome>("NOT_REVERSIBLE", "A reversal cannot be reversed; post an adjustment instead.");
        }

        var existing = await db.Postings.Include(p => p.Entries).AsNoTracking().FirstOrDefaultAsync(p => p.ReversesPostingId == postingId, cancellationToken);
        if (existing is not null)
        {
            return Result.Failure<PostingOutcome>("ALREADY_REVERSED", "This posting has already been reversed.");
        }

        var today = Today;
        var period = FiscalCalendar.PeriodKey(today);
        if (await db.PeriodCloses.AnyAsync(c => c.PeriodKey == period, cancellationToken))
        {
            return Result.Failure<PostingOutcome>("PERIOD_CLOSED", $"Period {period} is closed; the reversal would land in it.");
        }

        var memo = $"Reversal of posting {original.Id}: {original.Memo}";
        var reversal = new PostingEntity
        {
            PostedOn = today,
            PeriodKey = period,
            FiscalYear = FiscalCalendar.FiscalYear(today, options.Value.FiscalYearStartMonth),
            Memo = memo.Length > PostingRules.MaxMemo ? $"Reversal of posting {original.Id}" : memo,
            SourceType = PostingSources.Reversal,
            ReversesPostingId = original.Id,
            Reason = reason,
            CreatedBy = actor,
            CreatedAt = clock.GetUtcNow(),
            Entries = [.. original.Entries.Select(e => new LedgerEntryEntity { AccountId = e.AccountId, Debit = e.Credit, Credit = e.Debit, Currency = e.Currency })]
        };
        db.Postings.Add(reversal);
        return Result.Success(new PostingOutcome(reversal, true));
    }

    public async Task<Result<PostingOutcome>> ReverseAsync(long postingId, string reason, string? actor, CancellationToken cancellationToken = default)
    {
        var staged = await StageReversalAsync(postingId, reason, actor, cancellationToken);
        if (!staged.IsSuccess)
        {
            return staged;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return staged;
        }
        catch (DbUpdateException)
        {
            // The unique index on the reversed posting: another request reversed it first.
            db.ChangeTracker.Clear();
            return Result.Failure<PostingOutcome>("ALREADY_REVERSED", "This posting has already been reversed.");
        }
    }

    /// <summary>Closes a month, which then takes no new postings. Only past or current months can be closed, and only once.</summary>
    public async Task<Result> ClosePeriodAsync(string periodKey, string? actor, CancellationToken cancellationToken = default)
    {
        if (!FiscalCalendar.TryParsePeriod(periodKey, out var firstDay))
        {
            return Result.Failure("PERIOD_INVALID", "A period is a month written yyyy-MM.");
        }

        if (firstDay > Today)
        {
            return Result.Failure("PERIOD_IN_FUTURE", "A month that has not started cannot be closed.");
        }

        if (await db.PeriodCloses.AnyAsync(c => c.PeriodKey == periodKey, cancellationToken))
        {
            return Result.Failure("ALREADY_CLOSED", $"Period {periodKey} is already closed.");
        }

        db.PeriodCloses.Add(new PeriodCloseEntity { PeriodKey = periodKey, ClosedAt = clock.GetUtcNow(), ClosedBy = actor });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Result.Failure("ALREADY_CLOSED", $"Period {periodKey} is already closed.");
        }

        return Result.Success();
    }

    /// <summary>
    /// Debits and credits per account up to and including <paramref name="asOf"/>, one section per currency (nothing is converted). Each section's
    /// totals match by construction because every posting balances; showing them is the proof. Sums are taken in memory because SQLite cannot sum a decimal.
    /// </summary>
    public async Task<IReadOnlyList<CurrencyTrialBalance>> TrialBalanceAsync(DateOnly asOf, string? currency = null, CancellationToken cancellationToken = default)
    {
        var query = from e in db.LedgerEntries.AsNoTracking()
                    join p in db.Postings.AsNoTracking() on e.PostingId equals p.Id
                    join a in db.Accounts.AsNoTracking() on e.AccountId equals a.Id
                    where p.PostedOn <= asOf && (currency == null || e.Currency == currency)
                    select new { a.Code, a.Name, a.Type, e.Currency, e.Debit, e.Credit };
        var rows = await query.ToListAsync(cancellationToken);
        return
        [
            .. rows.GroupBy(r => r.Currency).OrderBy(g => g.Key, StringComparer.Ordinal).Select(group =>
            {
                var lines = group.GroupBy(r => (r.Code, r.Name, r.Type)).OrderBy(g => g.Key.Code, StringComparer.Ordinal)
                    .Select(g => new TrialBalanceLine(g.Key.Code, g.Key.Name, g.Key.Type, group.Key, g.Sum(r => r.Debit), g.Sum(r => r.Credit))).ToList();
                return new CurrencyTrialBalance(group.Key, lines, lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
            })
        ];
    }
}

public static class LedgerSeeder
{
    /// <summary>Adds any seeded account that is missing, by code. Idempotent and safe to run at every start; it never touches an account that exists.</summary>
    public static async Task EnsureChartAsync(FinanceDbContext db, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        var existing = (await db.Accounts.AsNoTracking().Select(a => a.Code).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var (code, name, type) in LedgerAccounts.Seed.Where(a => !existing.Contains(a.Code)))
        {
            db.Accounts.Add(new AccountEntity { Id = $"acc_{Guid.NewGuid():N}", Code = code, Name = name, Type = type, IsSystem = true, CreatedAt = clock.GetUtcNow() });
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
