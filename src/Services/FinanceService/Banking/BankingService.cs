using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using FinanceService.Ledger;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Banking;

public sealed class BankingOptions
{
    public const string SectionName = "Finance:Banking";

    /// <summary>How many days apart a bank line and a ledger entry may be dated and still be matched automatically. Zero or less means 5.</summary>
    public int MatchWindowDays { get; set; }

    public int EffectiveMatchWindowDays => MatchWindowDays > 0 ? MatchWindowDays : 5;

    /// <summary>Largest statement file accepted, in bytes. Zero or less means 5 MB.</summary>
    public int MaxFileBytes { get; set; }

    public int EffectiveMaxFileBytes => MaxFileBytes > 0 ? MaxFileBytes : 5_000_000;
}

public sealed record BankAccountRequest(string? Name, string? BankName, string? Number, string? Currency, string? LedgerAccount, CsvMapping? CsvMapping);

public sealed record BankAccountChange(string? Name, string? BankName, string? LedgerAccount, CsvMapping? CsvMapping, bool? ClearCsvMapping, bool? Archived);

public sealed record BankAccountView(string Id, string Name, string BankName, string NumberMasked, string Currency, string LedgerAccount, CsvMapping? CsvMapping, bool Archived, int UnmatchedLines);

public sealed record BankStatementView(string Id, string BankAccountId, string Format, string? FileName, int Lines, int Duplicates, string? From, string? To, string? ImportedBy, DateTimeOffset ImportedAt);

public sealed record BankLineView(string Id, string BankAccountId, string Date, string Description, string? Reference, string Amount, string Currency, string Status, string? MatchedEntryId, bool Automatic, string? Reason, string StatementId);

public sealed record DuplicateRow(int Row, string Date, string Amount, string Description);

public sealed record ImportReport(BankStatementView Statement, int Imported, IReadOnlyList<DuplicateRow> Duplicates, int AutoMatched, int Unmatched);

/// <summary>Either a report, a refusal (`Error`) or, when rows could not be read, the full list of them (`Errors`) with nothing stored.</summary>
public sealed record ImportResult(ImportReport? Report, IReadOnlyList<ImportError> Errors, ResultError? Error)
{
    public static ImportResult Fail(string code, string message) => new(null, [], new ResultError(code, message));
}

public sealed record BankSummary(string BankAccountId, string Currency, string LedgerBalance, string? BankBalance, int Matched, int Dismissed, int UnmatchedLines, string UnmatchedLinesNet, int UnmatchedEntries, string UnmatchedEntriesNet, string DismissedNet, string? Unexplained);

public sealed partial class BankingService(FinanceDbContext db, IDonorProtector protector, LedgerService ledger, IOptions<BankingOptions> options, TimeProvider clock)
{
    [GeneratedRegex(@"^[A-Za-z0-9]{6,34}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AccountNumber();

    [GeneratedRegex(@"[A-Za-z0-9\-/]+", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private DateTimeOffset Now => clock.GetUtcNow();

    // ---- registry -------------------------------------------------------------------------------------------------------------------------------------------

    public async Task<Result<BankAccountView>> CreateAccountAsync(BankAccountRequest request, string? actor, CancellationToken cancellationToken = default)
    {
        var invalid = new List<string>();
        var name = request.Name?.Trim() ?? string.Empty;
        var bank = request.BankName?.Trim() ?? string.Empty;
        var number = (request.Number ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        var currency = request.Currency?.Trim().ToUpperInvariant() ?? Currencies.Bdt;
        if (name.Length is 0 or > 100)
        {
            invalid.Add("name");
        }

        if (bank.Length is 0 or > 100)
        {
            invalid.Add("bankName");
        }

        if (!AccountNumber().IsMatch(number))
        {
            invalid.Add("number");
        }

        if (!Currencies.IsSupported(currency))
        {
            invalid.Add("currency");
        }

        if (request.CsvMapping is not null && !MappingIsSane(request.CsvMapping))
        {
            invalid.Add("csvMapping");
        }

        if (invalid.Count > 0)
        {
            return Result.Failure<BankAccountView>("VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
        }

        var resolved = await ResolveLedgerAccountAsync(request.LedgerAccount ?? LedgerAccounts.Bank, null, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return Result.Failure<BankAccountView>(resolved.Error!.Code, resolved.Error.Message);
        }

        var entity = new BankAccountEntity
        {
            Id = "bank_" + Guid.NewGuid().ToString("N"),
            Name = name,
            BankName = bank,
            NumberProtected = protector.Protect(number),
            Last4 = number[^4..],
            Currency = currency,
            LedgerAccountId = resolved.Value!.Id,
            CsvMapping = request.CsvMapping is null ? null : JsonSerializer.Serialize(request.CsvMapping, MappingJson),
            CreatedBy = actor,
            CreatedAt = Now
        };
        db.BankAccounts.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(entity).State = EntityState.Detached;
            return Result.Failure<BankAccountView>("BANK_LEDGER_ACCOUNT_TAKEN", "Another bank account already uses that ledger account; give this one its own.");
        }

        return Result.Success(ToView(entity, resolved.Value.Code, 0));
    }

    public async Task<Result<BankAccountView>> UpdateAccountAsync(string id, BankAccountChange change, CancellationToken cancellationToken = default)
    {
        var entity = await db.BankAccounts.Include(a => a.LedgerAccount).FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (entity is null)
        {
            return Result.Failure<BankAccountView>("NOT_FOUND", "Bank account not found.");
        }

        if (change.Name is not null)
        {
            var name = change.Name.Trim();
            if (name.Length is 0 or > 100)
            {
                return Result.Failure<BankAccountView>("VALIDATION_ERROR", "Invalid fields: name.");
            }

            entity.Name = name;
        }

        if (change.BankName is not null)
        {
            var bank = change.BankName.Trim();
            if (bank.Length is 0 or > 100)
            {
                return Result.Failure<BankAccountView>("VALIDATION_ERROR", "Invalid fields: bankName.");
            }

            entity.BankName = bank;
        }

        if (change.CsvMapping is not null)
        {
            if (!MappingIsSane(change.CsvMapping))
            {
                return Result.Failure<BankAccountView>("VALIDATION_ERROR", "Invalid fields: csvMapping.");
            }

            entity.CsvMapping = JsonSerializer.Serialize(change.CsvMapping, MappingJson);
        }
        else if (change.ClearCsvMapping == true)
        {
            entity.CsvMapping = null;
        }

        if (change.LedgerAccount is not null)
        {
            if (await db.BankLines.AnyAsync(l => l.BankAccountId == id, cancellationToken))
            {
                return Result.Failure<BankAccountView>("LEDGER_ACCOUNT_LOCKED", "The ledger account cannot change once statements have been imported.");
            }

            var resolved = await ResolveLedgerAccountAsync(change.LedgerAccount, id, cancellationToken);
            if (!resolved.IsSuccess)
            {
                return Result.Failure<BankAccountView>(resolved.Error!.Code, resolved.Error.Message);
            }

            entity.LedgerAccountId = resolved.Value!.Id;
            entity.LedgerAccount = resolved.Value;
        }

        if (change.Archived is { } archived)
        {
            entity.ArchivedAt = archived ? entity.ArchivedAt ?? Now : null;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Result.Failure<BankAccountView>("BANK_LEDGER_ACCOUNT_TAKEN", "Another bank account already uses that ledger account.");
        }

        return Result.Success(ToView(entity, entity.LedgerAccount!.Code, await UnmatchedCountAsync(id, cancellationToken)));
    }

    public async Task<IReadOnlyList<BankAccountView>> ListAccountsAsync(bool includeArchived, CancellationToken cancellationToken = default)
    {
        var query = db.BankAccounts.AsNoTracking().Include(a => a.LedgerAccount).AsQueryable();
        if (!includeArchived)
        {
            query = query.Where(a => a.ArchivedAt == null);
        }

        var accounts = await query.OrderBy(a => a.Name).ToListAsync(cancellationToken);
        var unmatched = await db.BankLines.AsNoTracking().Where(l => !db.BankLineLinks.Any(k => k.LineId == l.Id)).GroupBy(l => l.BankAccountId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        return [.. accounts.Select(a => ToView(a, a.LedgerAccount!.Code, unmatched.GetValueOrDefault(a.Id)))];
    }

    public async Task<BankAccountView?> GetAccountAsync(string id, CancellationToken cancellationToken = default)
    {
        var entity = await db.BankAccounts.AsNoTracking().Include(a => a.LedgerAccount).FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        return entity is null ? null : ToView(entity, entity.LedgerAccount!.Code, await UnmatchedCountAsync(id, cancellationToken));
    }

    /// <summary>The full number, only for a caller with the reveal permission; the read is written to the reveal log first.</summary>
    public async Task<string?> RevealNumberAsync(string id, string? actor, CancellationToken cancellationToken = default)
    {
        var entity = await db.BankAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        db.BankReveals.Add(new BankRevealEntity { BankAccountId = id, By = actor, At = Now });
        await db.SaveChangesAsync(cancellationToken);
        return protector.Unprotect(entity.NumberProtected);
    }

    private static readonly JsonSerializerOptions MappingJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private static bool MappingIsSane(CsvMapping mapping)
    {
        var all = new[] { mapping.Date, mapping.Description, mapping.Reference, mapping.Amount, mapping.Debit, mapping.Credit, mapping.Balance, mapping.DateFormat, mapping.Delimiter };
        if (all.Any(x => x is { Length: > 60 }) || (mapping.Delimiter is { } d && d.Length != 1))
        {
            return false;
        }

        if (mapping.Amount is not null && (mapping.Debit is not null || mapping.Credit is not null))
        {
            return false;
        }

        if ((mapping.Debit is null) != (mapping.Credit is null))
        {
            return false;
        }

        if (mapping.DateFormat is not null)
        {
            try
            {
                _ = new DateOnly(2026, 3, 9).ToString(mapping.DateFormat, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<Result<AccountEntity>> ResolveLedgerAccountAsync(string code, string? exceptBankAccount, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Code == code.Trim(), cancellationToken);
        if (account is null || account.ArchivedAt is not null)
        {
            return Result.Failure<AccountEntity>("LEDGER_ACCOUNT_INVALID", $"Ledger account '{code}' does not exist or is archived.");
        }

        if (account.Type != AccountTypes.Asset)
        {
            return Result.Failure<AccountEntity>("LEDGER_ACCOUNT_INVALID", "A bank account maps to an asset account of the ledger.");
        }

        if (await db.BankAccounts.AnyAsync(b => b.LedgerAccountId == account.Id && b.ArchivedAt == null && b.Id != exceptBankAccount, cancellationToken))
        {
            return Result.Failure<AccountEntity>("BANK_LEDGER_ACCOUNT_TAKEN", "Another bank account already uses that ledger account; give this one its own.");
        }

        return Result.Success(account);
    }

    private Task<int> UnmatchedCountAsync(string accountId, CancellationToken cancellationToken) =>
        db.BankLines.CountAsync(l => l.BankAccountId == accountId && !db.BankLineLinks.Any(k => k.LineId == l.Id), cancellationToken);

    private static BankAccountView ToView(BankAccountEntity a, string ledgerCode, int unmatched) =>
        new(a.Id, a.Name, a.BankName, "••••" + a.Last4, a.Currency, ledgerCode, StatementParsers.ReadMapping(a.CsvMapping), a.ArchivedAt is not null, unmatched);

    // ---- statement import ----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Imports one statement file. If any row cannot be read nothing is stored and every bad row is reported. Lines already imported from an earlier, overlapping statement are
    /// not stored again but are listed in the report. New lines are then matched automatically where that is unambiguous.
    /// </summary>
    public async Task<ImportResult> ImportAsync(string accountId, string text, string? format, string? fileName, string? actor, CancellationToken cancellationToken = default)
    {
        var account = await db.BankAccounts.Include(a => a.LedgerAccount).FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return ImportResult.Fail("NOT_FOUND", "Bank account not found.");
        }

        if (account.ArchivedAt is not null)
        {
            return ImportResult.Fail("BANK_ACCOUNT_ARCHIVED", "An archived bank account takes no statements.");
        }

        var kind = (format ?? (StatementParsers.LooksLikeOfx(text) ? "ofx" : "csv")).ToLowerInvariant();
        if (kind is not ("csv" or "ofx"))
        {
            return ImportResult.Fail("VALIDATION_ERROR", "Invalid fields: format (csv or ofx).");
        }

        var hash = StatementParsers.Hash(text);
        if (await db.BankStatements.AnyAsync(s => s.BankAccountId == accountId && s.FileHash == hash, cancellationToken))
        {
            return ImportResult.Fail("ALREADY_IMPORTED", "This exact statement file was already imported for this account.");
        }

        var parsed = kind == "ofx" ? StatementParsers.ParseOfx(text) : StatementParsers.ParseCsv(text, StatementParsers.ReadMapping(account.CsvMapping));
        var errors = parsed.Errors.ToList();
        if (kind == "ofx" && parsed.Currency is not null && !string.Equals(parsed.Currency, account.Currency, StringComparison.OrdinalIgnoreCase))
        {
            errors.Insert(0, new ImportError(1, $"The statement is in {parsed.Currency} but this account is in {account.Currency}."));
        }

        if (errors.Count > 0)
        {
            return new ImportResult(null, errors, new ResultError("IMPORT_REJECTED", $"{errors.Count} row(s) could not be read; nothing was imported."));
        }

        return await StoreAsync(account, parsed, kind, hash, fileName, actor, cancellationToken);
    }

    private async Task<ImportResult> StoreAsync(BankAccountEntity account, ParsedStatement parsed, string kind, string hash, string? fileName, string? actor, CancellationToken cancellationToken)
    {
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        var keyed = parsed.Lines.Select(line =>
        {
            var fingerprint = StatementParsers.Fingerprint(line);
            counters[fingerprint] = counters.GetValueOrDefault(fingerprint) + 1;
            return (Line: line, Fingerprint: fingerprint, Occurrence: counters[fingerprint]);
        }).ToList();

        var fingerprints = keyed.Select(k => k.Fingerprint).Distinct().ToList();
        var existing = (await db.BankLines.AsNoTracking().Where(l => l.BankAccountId == account.Id && fingerprints.Contains(l.Fingerprint))
            .Select(l => new { l.Fingerprint, l.Occurrence }).ToListAsync(cancellationToken)).Select(x => (x.Fingerprint, x.Occurrence)).ToHashSet();

        var fresh = keyed.Where(k => !existing.Contains((k.Fingerprint, k.Occurrence))).ToList();
        var duplicates = keyed.Where(k => existing.Contains((k.Fingerprint, k.Occurrence))).Select(k => new DuplicateRow(k.Line.Row, Day(k.Line.Date), Money(k.Line.Amount), k.Line.Description)).ToList();

        var statement = new BankStatementEntity
        {
            BankAccountId = account.Id,
            FileHash = hash,
            Format = kind,
            FileName = fileName is null ? null : fileName[..Math.Min(fileName.Length, 200)],
            LineCount = fresh.Count,
            DuplicateCount = duplicates.Count,
            FromDate = parsed.Lines.Count == 0 ? null : parsed.Lines.Min(l => l.Date),
            ToDate = parsed.Lines.Count == 0 ? null : parsed.Lines.Max(l => l.Date),
            ImportedBy = actor,
            ImportedAt = Now
        };
        db.BankStatements.Add(statement);
        foreach (var (line, fingerprint, occurrence) in fresh)
        {
            db.BankLines.Add(new BankLineEntity
            {
                Statement = statement,
                BankAccountId = account.Id,
                Date = line.Date,
                Description = line.Description,
                Reference = line.Reference,
                Amount = line.Amount,
                Balance = line.Balance,
                Currency = account.Currency,
                Fingerprint = fingerprint,
                Occurrence = occurrence
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ImportResult.Fail("IMPORT_CONFLICT", "Another import for this account changed the same lines; try again.");
        }

        var matched = await AutoMatchAsync(account, cancellationToken);
        var unmatched = await UnmatchedCountAsync(account.Id, cancellationToken);
        return new ImportResult(new ImportReport(ToView(statement), fresh.Count, duplicates, matched, unmatched), [], null);
    }

    private static BankStatementView ToView(BankStatementEntity s) =>
        new(Id(s.Id), s.BankAccountId, s.Format, s.FileName, s.LineCount, s.DuplicateCount, s.FromDate is { } f ? Day(f) : null, s.ToDate is { } t ? Day(t) : null, s.ImportedBy, s.ImportedAt);

    public async Task<IReadOnlyList<BankStatementView>> ListStatementsAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var rows = await db.BankStatements.AsNoTracking().Where(s => s.BankAccountId == accountId).OrderByDescending(s => s.Id).ToListAsync(cancellationToken);
        return [.. rows.Select(ToView)];
    }

    // ---- automatic matching --------------------------------------------------------------------------------------------------------------------------------

    private sealed record Candidate(long EntryId, decimal Debit, decimal Credit, DateOnly PostedOn, string Memo);

    /// <summary>
    /// Matches unresolved lines to unmatched ledger entries on the bank's ledger account: money in against a debit, money out against a credit, the same amount in the same
    /// currency, dated within the window. A line is matched only when it has one candidate (after narrowing several by a reference found in the posting memo) and that entry
    /// has no other claimant, so the outcome does not depend on the order of lines. Everything else stays in the queue for a person.
    /// </summary>
    public async Task<int> AutoMatchAsync(BankAccountEntity account, CancellationToken cancellationToken = default)
    {
        var lines = await db.BankLines.AsNoTracking().Where(l => l.BankAccountId == account.Id && !db.BankLineLinks.Any(k => k.LineId == l.Id)).OrderBy(l => l.Id).ToListAsync(cancellationToken);
        if (lines.Count == 0)
        {
            return 0;
        }

        var window = options.Value.EffectiveMatchWindowDays;
        var earliest = lines.Min(l => l.Date).AddDays(-window);
        var latest = lines.Max(l => l.Date).AddDays(window);
        var entries = await (from e in db.LedgerEntries.AsNoTracking()
                             join p in db.Postings.AsNoTracking() on e.PostingId equals p.Id
                             where e.AccountId == account.LedgerAccountId && e.Currency == account.Currency && p.PostedOn >= earliest && p.PostedOn <= latest
                                 && !db.BankLineLinks.Any(k => k.LedgerEntryId == e.Id)
                             orderby e.Id
                             select new Candidate(e.Id, e.Debit, e.Credit, p.PostedOn, p.Memo)).ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return 0;
        }

        var choices = new Dictionary<long, List<Candidate>>();
        foreach (var line in lines)
        {
            var found = entries.Where(e => Fits(line, e) && Math.Abs(line.Date.DayNumber - e.PostedOn.DayNumber) <= window).ToList();
            if (found.Count > 1)
            {
                var narrowed = found.Where(e => MentionsLine(e.Memo, line)).ToList();
                if (narrowed.Count > 0)
                {
                    found = narrowed;
                }
            }

            if (found.Count > 0)
            {
                choices[line.Id] = found;
            }
        }

        var claimants = choices.SelectMany(c => c.Value.Select(e => (Entry: e.EntryId, Line: c.Key))).GroupBy(x => x.Entry).ToDictionary(g => g.Key, g => g.Count());
        var matched = 0;
        foreach (var (lineId, found) in choices.OrderBy(c => c.Key))
        {
            if (found.Count == 1 && claimants[found[0].EntryId] == 1)
            {
                db.BankLineLinks.Add(new BankLineLinkEntity { LineId = lineId, State = BankLineStates.Matched, LedgerEntryId = found[0].EntryId, Automatic = true, By = null, At = Now });
                db.BankMatchEvents.Add(new BankMatchEventEntity { LineId = lineId, Action = BankMatchActions.AutoMatched, LedgerEntryId = found[0].EntryId, At = Now });
                matched++;
            }
        }

        if (matched == 0)
        {
            return 0;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A person matched one of these at the same moment; leave everything for the next run rather than guess.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => (e.Entity is BankLineLinkEntity or BankMatchEventEntity) && e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            return 0;
        }

        return matched;
    }

    private static bool Fits(BankLineEntity line, Candidate entry) =>
        line.Amount > 0m ? entry.Debit == line.Amount : entry.Credit == -line.Amount;

    private static bool MentionsLine(string memo, BankLineEntity line)
    {
        if (line.Reference is { Length: >= 4 } reference && memo.Contains(reference, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Token().Matches(line.Description).Any(t => t.Value.Length >= 6 && t.Value.Any(char.IsDigit) && memo.Contains(t.Value, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Result<int>> RematchAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        return account is null ? Result.Failure<int>("NOT_FOUND", "Bank account not found.") : Result.Success(await AutoMatchAsync(account, cancellationToken));
    }

    // ---- resolving the queue ---------------------------------------------------------------------------------------------------------------------------------

    private async Task<(BankLineEntity Line, BankAccountEntity Account, BankLineLinkEntity? Link)?> LoadLineAsync(long lineId, CancellationToken cancellationToken)
    {
        var line = await db.BankLines.FirstOrDefaultAsync(l => l.Id == lineId, cancellationToken);
        if (line is null)
        {
            return null;
        }

        var account = await db.BankAccounts.Include(a => a.LedgerAccount).FirstAsync(a => a.Id == line.BankAccountId, cancellationToken);
        var link = await db.BankLineLinks.FirstOrDefaultAsync(k => k.LineId == lineId, cancellationToken);
        return (line, account, link);
    }

    public async Task<Result<BankLineView>> MatchAsync(long lineId, long entryId, string? actor, CancellationToken cancellationToken = default)
    {
        if (await LoadLineAsync(lineId, cancellationToken) is not { } loaded)
        {
            return Result.Failure<BankLineView>("NOT_FOUND", "Statement line not found.");
        }

        var (line, account, link) = loaded;
        if (link is not null)
        {
            return Result.Failure<BankLineView>("LINE_ALREADY_RESOLVED", link.State == BankLineStates.Matched ? "This line is already matched; unmatch it first." : "This line was dismissed; reopen it first.");
        }

        var entry = await db.LedgerEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken);
        if (entry is null || entry.AccountId != account.LedgerAccountId)
        {
            return Result.Failure<BankLineView>("ENTRY_NOT_ON_ACCOUNT", "That ledger entry is not on this bank account's ledger account.");
        }

        if (entry.Currency != account.Currency || !Fits(line, new Candidate(entry.Id, entry.Debit, entry.Credit, default, string.Empty)))
        {
            return Result.Failure<BankLineView>("AMOUNT_MISMATCH", "The entry's side, amount or currency does not equal the statement line.");
        }

        if (await db.BankLineLinks.AnyAsync(k => k.LedgerEntryId == entryId, cancellationToken))
        {
            return Result.Failure<BankLineView>("ENTRY_ALREADY_MATCHED", "That ledger entry is already matched to another line.");
        }

        db.BankLineLinks.Add(new BankLineLinkEntity { LineId = lineId, State = BankLineStates.Matched, LedgerEntryId = entryId, By = actor, At = Now });
        db.BankMatchEvents.Add(new BankMatchEventEntity { LineId = lineId, Action = BankMatchActions.Matched, LedgerEntryId = entryId, By = actor, At = Now });
        return await SaveResolutionAsync(lineId, cancellationToken);
    }

    public async Task<Result<BankLineView>> UnmatchAsync(long lineId, string? actor, CancellationToken cancellationToken = default)
    {
        if (await LoadLineAsync(lineId, cancellationToken) is not { } loaded)
        {
            return Result.Failure<BankLineView>("NOT_FOUND", "Statement line not found.");
        }

        var link = loaded.Link;
        if (link is null || link.State != BankLineStates.Matched)
        {
            return Result.Failure<BankLineView>("NOT_MATCHED", link is null ? "This line is not matched." : "This line was dismissed, not matched; reopen it instead.");
        }

        db.BankLineLinks.Remove(link);
        db.BankMatchEvents.Add(new BankMatchEventEntity { LineId = lineId, Action = BankMatchActions.Unmatched, LedgerEntryId = link.LedgerEntryId, By = actor, At = Now });
        return await SaveResolutionAsync(lineId, cancellationToken);
    }

    public async Task<Result<BankLineView>> DismissAsync(long lineId, string? reason, string? actor, CancellationToken cancellationToken = default)
    {
        var why = reason?.Trim() ?? string.Empty;
        if (why.Length is < 3 or > 500)
        {
            return Result.Failure<BankLineView>("VALIDATION_ERROR", "A dismissal needs a reason of 3 to 500 characters.");
        }

        if (await LoadLineAsync(lineId, cancellationToken) is not { } loaded)
        {
            return Result.Failure<BankLineView>("NOT_FOUND", "Statement line not found.");
        }

        if (loaded.Link is not null)
        {
            return Result.Failure<BankLineView>("LINE_ALREADY_RESOLVED", "This line is already resolved.");
        }

        db.BankLineLinks.Add(new BankLineLinkEntity { LineId = lineId, State = BankLineStates.Dismissed, Reason = why, By = actor, At = Now });
        db.BankMatchEvents.Add(new BankMatchEventEntity { LineId = lineId, Action = BankMatchActions.Dismissed, Reason = why, By = actor, At = Now });
        return await SaveResolutionAsync(lineId, cancellationToken);
    }

    public async Task<Result<BankLineView>> ReopenAsync(long lineId, string? actor, CancellationToken cancellationToken = default)
    {
        if (await LoadLineAsync(lineId, cancellationToken) is not { } loaded)
        {
            return Result.Failure<BankLineView>("NOT_FOUND", "Statement line not found.");
        }

        var link = loaded.Link;
        if (link is null || link.State != BankLineStates.Dismissed)
        {
            return Result.Failure<BankLineView>("NOT_DISMISSED", "Only a dismissed line can be reopened; use unmatch for a match.");
        }

        db.BankLineLinks.Remove(link);
        db.BankMatchEvents.Add(new BankMatchEventEntity { LineId = lineId, Action = BankMatchActions.Reopened, Reason = link.Reason, By = actor, At = Now });
        return await SaveResolutionAsync(lineId, cancellationToken);
    }

    /// <summary>
    /// For a line the books know nothing about (a bank charge, interest, a deposit nobody recorded): posts the balanced entry against another account, dated the line's date, and
    /// matches the bank side to the line in one step. Posting again for the same line returns the same posting, so a retry cannot post twice.
    /// </summary>
    public async Task<Result<BankLineView>> PostAndMatchAsync(long lineId, string? counterAccount, string? memo, string? actor, CancellationToken cancellationToken = default)
    {
        if (await LoadLineAsync(lineId, cancellationToken) is not { } loaded)
        {
            return Result.Failure<BankLineView>("NOT_FOUND", "Statement line not found.");
        }

        var (line, account, link) = loaded;
        if (link is not null)
        {
            return Result.Failure<BankLineView>("LINE_ALREADY_RESOLVED", "This line is already resolved.");
        }

        var counter = counterAccount?.Trim() ?? string.Empty;
        if (counter.Length == 0 || counter == account.LedgerAccount!.Code)
        {
            return Result.Failure<BankLineView>("VALIDATION_ERROR", "Invalid fields: account (a ledger account other than the bank account's own).");
        }

        var text = string.IsNullOrWhiteSpace(memo) ? "Bank: " + line.Description : memo.Trim();
        if (text.Length > PostingRules.MaxMemo)
        {
            text = text[..PostingRules.MaxMemo];
        }

        var amount = KJWebsite.BuildingBlocks.Finance.Money.Of(Math.Abs(line.Amount), line.Currency);
        var bankCode = account.LedgerAccount!.Code;
        IReadOnlyList<LedgerLine> lines = line.Amount > 0m
            ? [LedgerLine.Debit(bankCode, amount), LedgerLine.Credit(counter, amount)]
            : [LedgerLine.Debit(counter, amount), LedgerLine.Credit(bankCode, amount)];
        var posted = await ledger.PostAsync(new PostingDraft(line.Date, text, PostingSources.Adjustment, "bank-line:" + Id(lineId), lines, "Posted from bank statement line " + Id(lineId)), actor, cancellationToken);
        if (!posted.IsSuccess)
        {
            return Result.Failure<BankLineView>(posted.Error!.Code, posted.Error.Message);
        }

        var postingId = posted.Value!.Posting.Id;
        var entryId = await db.LedgerEntries.AsNoTracking().Where(e => e.PostingId == postingId && e.AccountId == account.LedgerAccountId).Select(e => e.Id).FirstAsync(cancellationToken);
        db.BankLineLinks.Add(new BankLineLinkEntity { LineId = lineId, State = BankLineStates.Matched, LedgerEntryId = entryId, By = actor, At = Now });
        db.BankMatchEvents.Add(new BankMatchEventEntity { LineId = lineId, Action = BankMatchActions.PostedAndMatched, LedgerEntryId = entryId, PostingId = postingId, By = actor, At = Now });
        return await SaveResolutionAsync(lineId, cancellationToken);
    }

    private async Task<Result<BankLineView>> SaveResolutionAsync(long lineId, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Result.Failure<BankLineView>("CONFLICT", "Someone else resolved this line or entry at the same moment; reload and try again.");
        }

        db.ChangeTracker.Clear();
        return Result.Success((await LinesAsync(lineId, null, "all", 1, cancellationToken))[0]);
    }

    // ---- reads -------------------------------------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<BankLineView>> LinesAsync(long? lineId, string? accountId, string status, int limit, CancellationToken cancellationToken = default)
    {
        var query = from l in db.BankLines.AsNoTracking()
                    join k in db.BankLineLinks.AsNoTracking() on l.Id equals k.LineId into links
                    from k in links.DefaultIfEmpty()
                    select new { Line = l, Link = k };
        if (lineId is { } one)
        {
            query = query.Where(x => x.Line.Id == one);
        }

        if (!string.IsNullOrWhiteSpace(accountId))
        {
            query = query.Where(x => x.Line.BankAccountId == accountId);
        }

        query = status switch
        {
            "unmatched" => query.Where(x => x.Link == null),
            "matched" => query.Where(x => x.Link != null && x.Link.State == BankLineStates.Matched),
            "dismissed" => query.Where(x => x.Link != null && x.Link.State == BankLineStates.Dismissed),
            _ => query
        };
        var rows = await query.OrderByDescending(x => x.Link == null).ThenByDescending(x => x.Line.Date).ThenByDescending(x => x.Line.Id).Take(limit).ToListAsync(cancellationToken);
        return [.. rows.Select(x => new BankLineView(
            Id(x.Line.Id), x.Line.BankAccountId, Day(x.Line.Date), x.Line.Description, x.Line.Reference, Money(x.Line.Amount), x.Line.Currency,
            x.Link is null ? "unmatched" : x.Link.State, x.Link?.LedgerEntryId is { } e ? Id(e) : null, x.Link?.Automatic ?? false, x.Link?.Reason, Id(x.Line.StatementId)))];
    }

    /// <summary>The ledger lines a person can match against: unmatched entries on live bank accounts' ledger accounts, plus the entries the listed lines are matched to.</summary>
    public async Task<IReadOnlyList<LedgerEntryRow>> EntriesAsync(string? accountId, IReadOnlyList<BankLineView> shown, int limit, CancellationToken cancellationToken = default)
    {
        var accounts = db.BankAccounts.AsNoTracking().Where(a => a.ArchivedAt == null);
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            accounts = accounts.Where(a => a.Id == accountId);
        }

        var ledgerIds = await accounts.Select(a => a.LedgerAccountId).ToListAsync(cancellationToken);
        var linked = shown.Where(l => l.MatchedEntryId is not null).Select(l => long.Parse(l.MatchedEntryId!, CultureInfo.InvariantCulture)).ToList();
        var query = from e in db.LedgerEntries.AsNoTracking()
                    join p in db.Postings.AsNoTracking() on e.PostingId equals p.Id
                    join a in db.Accounts.AsNoTracking() on e.AccountId equals a.Id
                    where linked.Contains(e.Id) || (ledgerIds.Contains(e.AccountId) && !db.BankLineLinks.Any(k => k.LedgerEntryId == e.Id))
                    orderby p.PostedOn descending, e.Id descending
                    select new { Entry = e, Posting = p, Account = a };
        var page = await query.Take(limit).ToListAsync(cancellationToken);
        var postingIds = page.Select(x => x.Posting.Id).Distinct().ToList();
        var reversedBy = await db.Postings.AsNoTracking().Where(p => p.ReversesPostingId != null && postingIds.Contains(p.ReversesPostingId.Value))
            .Select(p => new { Original = p.ReversesPostingId!.Value, p.Id }).ToDictionaryAsync(x => x.Original, x => x.Id, cancellationToken);
        return [.. page.Select(x => new LedgerEntryRow(
            Id(x.Entry.Id), Id(x.Posting.Id), Day(x.Posting.PostedOn), $"{x.Account.Code} {x.Account.Name}", x.Account.Code, x.Posting.Memo,
            x.Entry.Debit > 0m ? Money(x.Entry.Debit) : null, x.Entry.Credit > 0m ? Money(x.Entry.Credit) : null, x.Entry.Currency, x.Posting.FiscalYear,
            x.Posting.ReversesPostingId is { } r ? Id(r) : null, reversedBy.TryGetValue(x.Posting.Id, out var rb) ? Id(rb) : null))];
    }

    public async Task<BankSummary?> SummaryAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = await db.BankAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return null;
        }

        var entries = db.LedgerEntries.AsNoTracking().Where(e => e.AccountId == account.LedgerAccountId && e.Currency == account.Currency);
        var ledgerBalance = (await entries.SumAsync(e => (decimal?)e.Debit, cancellationToken) ?? 0m) - (await entries.SumAsync(e => (decimal?)e.Credit, cancellationToken) ?? 0m);
        var unmatchedEntries = entries.Where(e => !db.BankLineLinks.Any(k => k.LedgerEntryId == e.Id));
        var unmatchedEntryCount = await unmatchedEntries.CountAsync(cancellationToken);
        var unmatchedEntryNet = (await unmatchedEntries.SumAsync(e => (decimal?)e.Debit, cancellationToken) ?? 0m) - (await unmatchedEntries.SumAsync(e => (decimal?)e.Credit, cancellationToken) ?? 0m);

        var lines = db.BankLines.AsNoTracking().Where(l => l.BankAccountId == accountId);
        var unmatchedLines = lines.Where(l => !db.BankLineLinks.Any(k => k.LineId == l.Id));
        var unmatchedLineCount = await unmatchedLines.CountAsync(cancellationToken);
        var unmatchedLineNet = await unmatchedLines.SumAsync(l => (decimal?)l.Amount, cancellationToken) ?? 0m;
        var dismissed = lines.Where(l => db.BankLineLinks.Any(k => k.LineId == l.Id && k.State == BankLineStates.Dismissed));
        var dismissedCount = await dismissed.CountAsync(cancellationToken);
        var dismissedNet = await dismissed.SumAsync(l => (decimal?)l.Amount, cancellationToken) ?? 0m;
        var matchedCount = await lines.CountAsync(l => db.BankLineLinks.Any(k => k.LineId == l.Id && k.State == BankLineStates.Matched), cancellationToken);

        var latest = await lines.Where(l => l.Balance != null).OrderByDescending(l => l.Date).ThenByDescending(l => l.Id).Select(l => l.Balance).FirstOrDefaultAsync(cancellationToken);
        string? unexplained = latest is { } bank ? Money(bank - unmatchedLineNet - dismissedNet - (ledgerBalance - unmatchedEntryNet)) : null;
        return new BankSummary(accountId, account.Currency, Money(ledgerBalance), latest is { } b ? Money(b) : null, matchedCount, dismissedCount, unmatchedLineCount, Money(unmatchedLineNet),
            unmatchedEntryCount, Money(unmatchedEntryNet), Money(dismissedNet), unexplained);
    }
}
