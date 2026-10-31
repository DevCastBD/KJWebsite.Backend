using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using FinanceService.Data;
using FinanceService.Data.Entities;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Finance;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Ledger;

/// <summary>
/// The books. Reading needs `finance.view`; every write needs a narrower permission and is audited with the caller. There is no endpoint that
/// edits or deletes a posting: a correction is a reversal that points back at the original.
/// </summary>
public static partial class LedgerEndpoints
{
    private const string Route = "/api/v1/admin/finance/ledger";
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    [GeneratedRegex(@"^[0-9A-Za-z.-]{1,16}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AccountCode();

    public static void MapLedger(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (FinanceDbContext db, string? from, string? to, string? account, int? limit, int? offset) =>
        {
            var query = from e in db.LedgerEntries.AsNoTracking()
                        join p in db.Postings.AsNoTracking() on e.PostingId equals p.Id
                        join a in db.Accounts.AsNoTracking() on e.AccountId equals a.Id
                        select new { Entry = e, Posting = p, Account = a };
            if (DateOnly.TryParse(from, CultureInfo.InvariantCulture, out var fromDate))
            {
                query = query.Where(x => x.Posting.PostedOn >= fromDate);
            }

            if (DateOnly.TryParse(to, CultureInfo.InvariantCulture, out var toDate))
            {
                query = query.Where(x => x.Posting.PostedOn <= toDate);
            }

            if (!string.IsNullOrWhiteSpace(account))
            {
                query = query.Where(x => x.Account.Code == account);
            }

            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var skip = Math.Max(0, offset ?? 0);
            var total = await query.CountAsync();
            var page = await query.OrderByDescending(x => x.Posting.PostedOn).ThenByDescending(x => x.Entry.Id).Skip(skip).Take(take).ToListAsync();
            var postingIds = page.Select(x => x.Posting.Id).Distinct().ToList();
            var reversedBy = await db.Postings.AsNoTracking().Where(p => p.ReversesPostingId != null && postingIds.Contains(p.ReversesPostingId.Value))
                .Select(p => new { Original = p.ReversesPostingId!.Value, p.Id }).ToDictionaryAsync(x => x.Original, x => x.Id);
            var rows = page.Select(x => new LedgerEntryRow(
                x.Entry.Id.ToString(CultureInfo.InvariantCulture), Id(x.Posting.Id), PublicDonationDate(x.Posting.PostedOn), $"{x.Account.Code} {x.Account.Name}", x.Account.Code,
                x.Posting.Memo, x.Entry.Debit > 0m ? Format(x.Entry.Debit) : null, x.Entry.Credit > 0m ? Format(x.Entry.Credit) : null, x.Entry.Currency, x.Posting.FiscalYear,
                x.Posting.ReversesPostingId is { } r ? Id(r) : null, reversedBy.TryGetValue(x.Posting.Id, out var rb) ? Id(rb) : null));
            return Results.Ok(new PagingEnvelope<LedgerEntryRow>([.. rows], total, null, take, skip));
        })
        .WithName("ListLedgerEntries")
        .WithSummary("Ledger lines, newest first")
        .WithDescription("One row per posting line. Filters: from/to (posting dates, inclusive), account (code). `reversalOf` / `reversedBy` link a posting to its correction. Read-only: there is no way to change a line.")
        .WithTags("Ledger")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/postings/{{id:long}}", async (HttpContext context, long id, FinanceDbContext db) =>
        {
            var view = await PostingViewAsync(db, id);
            return view is null ? ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Posting not found.") : Results.Ok(view);
        })
        .WithName("GetLedgerPosting")
        .WithSummary("One posting with all its lines")
        .WithTags("Ledger")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/postings", async (HttpContext context, AdjustmentRequest payload, FinanceDbContext db, LedgerService ledger) =>
        {
            var parsed = ParseAdjustment(payload, ledger.Today);
            if (parsed.Draft is null)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", parsed.Invalid)}.");
            }

            var posted = await ledger.PostAsync(parsed.Draft, Actor(context), context.RequestAborted);
            if (!posted.IsSuccess)
            {
                return Failure(context, posted.Error!);
            }

            return Results.Created($"{Route}/postings/{posted.Value!.Posting.Id}", await PostingViewAsync(db, posted.Value.Posting.Id));
        })
        .WithName("PostLedgerAdjustment")
        .WithSummary("Post a manual adjustment")
        .WithDescription("A balanced posting by hand: at least two lines, each with an account code, exactly one of `debit` or `credit` (decimal strings) and a currency; debits must equal credits in every currency. A `reason` is required. The date defaults to today and may not be in the future or in a closed period. Requires `finance.ledger.adjust`.")
        .WithTags("Ledger")
        .RequirePermission(Permissions.FinanceLedgerAdjust);

        app.MapPost($"{Route}/postings/{{id:long}}/reversal", async (HttpContext context, long id, PostingReversalRequest payload, FinanceDbContext db, LedgerService ledger) =>
        {
            var reason = payload.Reason?.Trim();
            if (reason is not { Length: >= 3 and <= 500 })
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: reason (3-500 characters).");
            }

            var reversed = await ledger.ReverseAsync(id, reason, Actor(context), context.RequestAborted);
            if (!reversed.IsSuccess)
            {
                return Failure(context, reversed.Error!);
            }

            return Results.Created($"{Route}/postings/{reversed.Value!.Posting.Id}", await PostingViewAsync(db, reversed.Value.Posting.Id));
        })
        .WithName("ReverseLedgerPosting")
        .WithSummary("Reverse a posting")
        .WithDescription("Appends a posting that mirrors the original with debits and credits swapped, dated today and linked to it. The original is untouched. A posting can be reversed once; a reversal cannot be reversed. Requires `finance.ledger.adjust`.")
        .WithTags("Ledger")
        .RequirePermission(Permissions.FinanceLedgerAdjust);

        app.MapGet($"{Route}/trial-balance", async (HttpContext context, LedgerService ledger, string? asOf, string? currency) =>
        {
            var date = ledger.Today;
            if (!string.IsNullOrWhiteSpace(asOf) && !DateOnly.TryParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: asOf (yyyy-MM-dd).");
            }

            if (!string.IsNullOrWhiteSpace(currency) && !Currencies.IsSupported(currency))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: currency.");
            }

            var sections = await ledger.TrialBalanceAsync(date, string.IsNullOrWhiteSpace(currency) ? null : currency, context.RequestAborted);
            return Results.Ok(new TrialBalanceView(PublicDonationDate(date), [.. sections.Select(s => new TrialBalanceSection(
                s.Currency,
                [.. s.Lines.Select(l => new TrialBalanceAccount(l.Code, l.Name, l.Type, Format(l.Debit), Format(l.Credit), Format(l.Balance)))],
                Format(s.TotalDebit), Format(s.TotalCredit), s.Balanced))]));
        })
        .WithName("GetTrialBalance")
        .WithSummary("Trial balance per currency")
        .WithDescription("Debits and credits per account up to `asOf` (default today), one section per currency, never converted. `balanced` is true when total debits equal total credits, which every posting guarantees.")
        .WithTags("Ledger")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/accounts", async (FinanceDbContext db) =>
            Results.Ok(new { items = (await db.Accounts.AsNoTracking().OrderBy(a => a.Code).ToListAsync()).Select(AccountToView).ToList() }))
        .WithName("ListLedgerAccounts")
        .WithSummary("The chart of accounts")
        .WithTags("Ledger")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/accounts", async (HttpContext context, AccountRequest payload, FinanceDbContext db, TimeProvider clock) =>
        {
            var invalid = new List<string>();
            var code = payload.Code?.Trim();
            var name = payload.Name?.Trim();
            if (code is null || !AccountCode().IsMatch(code))
            {
                invalid.Add("code");
            }

            if (name is not { Length: > 0 and <= 200 })
            {
                invalid.Add("name");
            }

            if (payload.Type is null || !AccountTypes.All.Contains(payload.Type))
            {
                invalid.Add("type");
            }

            if (invalid.Count > 0)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
            }

            if (await db.Accounts.AnyAsync(a => a.Code == code))
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "ACCOUNT_EXISTS", $"Account {code} already exists.");
            }

            var account = new AccountEntity { Id = $"acc_{Guid.NewGuid():N}", Code = code!, Name = name!, Type = payload.Type!, CreatedAt = clock.GetUtcNow() };
            db.Accounts.Add(account);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "ACCOUNT_EXISTS", $"Account {code} already exists.");
            }

            return Results.Created($"{Route}/accounts/{account.Id}", AccountToView(account));
        })
        .WithName("CreateLedgerAccount")
        .WithSummary("Add an account to the chart")
        .WithDescription("Codes are 1-16 letters, digits, dots or dashes and unique; the type (asset, liability, equity, income, expense) cannot be changed later. Requires `finance.ledger.adjust`.")
        .WithTags("Ledger")
        .RequirePermission(Permissions.FinanceLedgerAdjust);

        app.MapPost($"{Route}/accounts/{{id}}/archive", async (HttpContext context, string id, FinanceDbContext db, TimeProvider clock) =>
        {
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id);
            if (account is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Account not found.");
            }

            if (account.IsSystem)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "ACCOUNT_IN_USE", "Seeded accounts are used by automatic postings and cannot be archived.");
            }

            account.ArchivedAt ??= clock.GetUtcNow();
            await db.SaveChangesAsync();
            return Results.Ok(AccountToView(account));
        })
        .WithName("ArchiveLedgerAccount")
        .WithSummary("Archive an account: no new postings, history kept")
        .WithDescription("Seeded (system) accounts cannot be archived. Requires `finance.ledger.adjust`.")
        .WithTags("Ledger")
        .RequirePermission(Permissions.FinanceLedgerAdjust);

        app.MapGet($"{Route}/periods", async (FinanceDbContext db) =>
        {
            var counts = await db.Postings.AsNoTracking().GroupBy(p => p.PeriodKey).Select(g => new { Key = g.Key, Count = g.Count() }).ToListAsync();
            var closes = await db.PeriodCloses.AsNoTracking().ToListAsync();
            var keys = counts.Select(c => c.Key).Union(closes.Select(c => c.PeriodKey)).OrderByDescending(k => k, StringComparer.Ordinal);
            var items = keys.Select(key =>
            {
                var close = closes.FirstOrDefault(c => c.PeriodKey == key);
                return new PeriodView(key, close is not null, close?.ClosedAt.ToString("O", CultureInfo.InvariantCulture), close?.ClosedBy, counts.FirstOrDefault(c => c.Key == key)?.Count ?? 0);
            });
            return Results.Ok(new { items = items.ToList() });
        })
        .WithName("ListLedgerPeriods")
        .WithSummary("Months that have postings or are closed")
        .WithTags("Ledger")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/periods/{{period}}/close", async (HttpContext context, string period, LedgerService ledger) =>
        {
            var closed = await ledger.ClosePeriodAsync(period, Actor(context), context.RequestAborted);
            return closed.IsSuccess ? Results.Ok(new { periodKey = period, closed = true }) : Failure(context, closed.Error!);
        })
        .WithName("CloseLedgerPeriod")
        .WithSummary("Close a month")
        .WithDescription("A closed month rejects new postings and reversals dated in it, and there is no reopen in this version. Only a past or current month can be closed. Requires `finance.period.close`.")
        .WithTags("Ledger")
        .RequirePermission(Permissions.FinancePeriodClose);
    }

    private static (PostingDraft? Draft, List<string> Invalid) ParseAdjustment(AdjustmentRequest payload, DateOnly today)
    {
        var invalid = new List<string>();
        var postedOn = today;
        if (!string.IsNullOrWhiteSpace(payload.PostedOn) && !DateOnly.TryParseExact(payload.PostedOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out postedOn))
        {
            invalid.Add("postedOn");
        }

        var memo = payload.Memo?.Trim();
        if (memo is not { Length: > 0 and <= PostingRules.MaxMemo })
        {
            invalid.Add("memo");
        }

        var reason = payload.Reason?.Trim();
        if (reason is not { Length: >= 3 and <= 500 })
        {
            invalid.Add("reason");
        }

        var lines = new List<LedgerLine>();
        foreach (var (line, index) in (payload.Lines ?? []).Select((l, i) => (l, i)))
        {
            var hasDebit = !string.IsNullOrWhiteSpace(line.Debit);
            var hasCredit = !string.IsNullOrWhiteSpace(line.Credit);
            // Exactly one side per line; its amount is a plain decimal string in a supported currency.
            var amount = hasDebit == hasCredit ? null : Money.Parse(hasDebit ? line.Debit : line.Credit, line.Currency);
            if (string.IsNullOrWhiteSpace(line.Account) || amount is not { IsSuccess: true })
            {
                invalid.Add($"lines[{index}]");
                continue;
            }

            lines.Add(new LedgerLine(line.Account.Trim(), hasDebit ? LedgerSide.Debit : LedgerSide.Credit, amount.Value));
        }

        if (invalid.Count > 0)
        {
            return (null, invalid);
        }

        return (new PostingDraft(postedOn, memo!, PostingSources.Adjustment, null, lines, reason), invalid);
    }

    private static async Task<PostingView?> PostingViewAsync(FinanceDbContext db, long id)
    {
        var posting = await db.Postings.AsNoTracking().Include(p => p.Entries).ThenInclude(e => e.Account).FirstOrDefaultAsync(p => p.Id == id);
        if (posting is null)
        {
            return null;
        }

        var reversedBy = await db.Postings.AsNoTracking().Where(p => p.ReversesPostingId == id).Select(p => (long?)p.Id).FirstOrDefaultAsync();
        return new PostingView(
            Id(posting.Id), PublicDonationDate(posting.PostedOn), posting.PeriodKey, posting.FiscalYear, posting.Memo, posting.SourceType, posting.SourceId, posting.Reason, posting.CreatedBy,
            posting.ReversesPostingId is { } r ? Id(r) : null, reversedBy is { } rb ? Id(rb) : null,
            [.. posting.Entries.OrderBy(e => e.Id).Select(e => new PostingLineView(e.Account!.Code, e.Account.Name, e.Debit > 0m ? Format(e.Debit) : null, e.Credit > 0m ? Format(e.Credit) : null, e.Currency))]);
    }

    private static IResult Failure(HttpContext context, ResultError error) => ApiError.Create(context, error.Code switch
    {
        "NOT_FOUND" => StatusCodes.Status404NotFound,
        "ALREADY_REVERSED" or "NOT_REVERSIBLE" or "PERIOD_CLOSED" or "ALREADY_CLOSED" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest
    }, error.Code, error.Message);

    private static AccountView AccountToView(AccountEntity a) => new(a.Id, a.Code, a.Name, a.Type, a.IsSystem, a.ArchivedAt is not null);

    private static string? Actor(HttpContext context) => context.User.FindFirstValue(KjClaims.Subject);

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static string Format(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string PublicDonationDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
