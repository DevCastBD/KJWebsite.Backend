using System.Globalization;
using System.Security.Claims;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Ledger;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Idempotency;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Expenses;

public static class ExpenseEndpoints
{
    private const string Route = "/api/v1/admin/finance/expenses";
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    public static void MapExpenses(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, async (HttpContext context, ExpenseRequest payload, ExpenseService service) =>
        {
            var submitted = await service.SubmitAsync(payload, Actor(context), ActorName(context), context.RequestAborted);
            return submitted.IsSuccess ? Results.Created($"{Route}/{submitted.Value!.Expense.Id}", submitted.Value.Expense) : Failure(context, submitted.Error!);
        })
        .RequireIdempotency()
        .WithName("SubmitExpense")
        .WithSummary("Submit an expense")
        .WithDescription("`{payee, description, category, amount, currency, project?, date?, reimbursement?}`. `category` is one of the configured keys (programme, fundraising, administrative, events, other); `project` is an optional project slug for rollups; `reimbursement: true` means the money is owed to the person who paid out of pocket. How many different people must approve is fixed now from the amount tier. Attach receipts with `PUT .../{id}/receipts`. Requires `finance.expense.submit`.")
        .WithTags("Expenses")
        .RequirePermission(Permissions.FinanceExpenseSubmit);

        app.MapGet(Route, async (FinanceDbContext db, string? status, string? project, string? category, int? limit, int? offset) =>
        {
            var query = from e in db.Expenses.AsNoTracking()
                        select new
                        {
                            Expense = e,
                            Approvals = db.ExpenseApprovals.Count(a => a.ExpenseId == e.Id),
                            Decision = db.ExpenseDecisions.Where(d => d.ExpenseId == e.Id).Select(d => d.Decision).FirstOrDefault(),
                            DecidedBy = db.ExpenseDecisions.Where(d => d.ExpenseId == e.Id).Select(d => d.DecidedBy).FirstOrDefault(),
                            Note = db.ExpenseDecisions.Where(d => d.ExpenseId == e.Id).Select(d => d.Note).FirstOrDefault(),
                            Paid = db.ExpensePayments.Any(p => p.ExpenseId == e.Id)
                        };
            if (!string.IsNullOrWhiteSpace(status))
            {
                query = status == "pending" ? query.Where(x => x.Decision == null) : query.Where(x => x.Decision == status);
            }

            if (!string.IsNullOrWhiteSpace(project))
            {
                query = query.Where(x => x.Expense.ProjectSlug == project);
            }

            if (!string.IsNullOrWhiteSpace(category))
            {
                query = query.Where(x => x.Expense.CategoryKey == category);
            }

            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var skip = Math.Max(0, offset ?? 0);
            var total = await query.CountAsync();
            var page = (await query.ToListAsync()).OrderByDescending(x => x.Expense.SubmittedAt).ThenByDescending(x => x.Expense.Id, StringComparer.Ordinal).Skip(skip).Take(take).ToList();
            return Results.Ok(new PagingEnvelope<ExpenseRow>([.. page.Select(x => ExpenseService.Row(x.Expense, x.Approvals, x.Decision is null ? null : new ExpenseDecisionEntity { Decision = x.Decision, DecidedBy = x.DecidedBy ?? string.Empty, Note = x.Note }, x.Paid))], total, null, take, skip));
        })
        .WithName("ListExpenses")
        .WithSummary("Expenses, newest first")
        .WithDescription("`status` is pending (no final decision yet, including part-approved), approved or rejected; `approvals` of `requiredApprovals` shows progress and `paid` whether the money has gone out. Filters: status, project, category.")
        .WithTags("Expenses")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/{{id}}", async (HttpContext context, string id, ExpenseService service) =>
            await service.DetailAsync(id, context.RequestAborted) is { } detail ? Results.Ok(detail) : ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Expense not found."))
        .WithName("GetExpense")
        .WithSummary("One expense with approvals, receipts, payment and its ledger posting")
        .WithTags("Expenses")
        .RequirePermission(Permissions.FinanceView);

        app.MapPut($"{Route}/{{id}}/receipts", async (HttpContext context, string id, ExpenseService service) =>
        {
            var added = await service.AddReceiptAsync(id, context.Request.Body, context.Request.ContentType, context.Request.Headers["X-Filename"].ToString(), Actor(context), context.RequestAborted);
            return added.IsSuccess ? Results.Created($"{Route}/{id}/receipts/{added.Value!.Id}/file", added.Value) : Failure(context, added.Error!);
        })
        .WithName("AddExpenseReceipt")
        .WithSummary("Attach a receipt")
        .WithDescription("Raw file body with its Content-Type (pdf, jpeg, png, webp, up to 5 MB, checked against the file's own bytes) and an optional URL-encoded `X-Filename`; up to ten, only before the expense is decided. A receipt is a financial record: nothing can delete it. Requires `finance.expense.submit`.")
        .WithTags("Expenses")
        .RequirePermission(Permissions.FinanceExpenseSubmit);

        app.MapGet($"{Route}/{{id}}/receipts/{{receiptId}}/file", async (HttpContext context, string id, string receiptId, ExpenseService service) =>
        {
            var opened = await service.OpenReceiptAsync(id, receiptId, context.RequestAborted);
            if (opened is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Receipt not found.");
            }

            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(opened.Value.Content, opened.Value.Receipt.ContentType, opened.Value.Receipt.FileName);
        })
        .WithName("DownloadExpenseReceipt")
        .WithSummary("Download a receipt")
        .WithTags("Expenses")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/{{id}}/approve", async (HttpContext context, string id, ExpenseDecisionRequest payload, ExpenseService service) =>
        {
            var approved = await service.ApproveAsync(id, payload.Note, Actor(context), context.RequestAborted);
            return approved.IsSuccess ? Results.Ok(approved.Value!.Expense) : Failure(context, approved.Error!);
        })
        .RequireIdempotency()
        .WithName("ApproveExpense")
        .WithSummary("Add your approval")
        .WithDescription("The submitter cannot approve (403 SEPARATION_OF_DUTIES), nobody approves twice, and a receipt must be attached first (409 RECEIPT_REQUIRED). The expense becomes `approved`, and posts to the ledger (Dr the category's expense account, Cr payables), when the number of different approvers its amount tier requires has approved. Requires `finance.expense.approve`.")
        .WithTags("Expenses")
        .RequirePermission(Permissions.FinanceExpenseApprove);

        app.MapPost($"{Route}/{{id}}/reject", async (HttpContext context, string id, ExpenseDecisionRequest payload, ExpenseService service) =>
        {
            var rejected = await service.RejectAsync(id, payload.Note, Actor(context), context.RequestAborted);
            return rejected.IsSuccess ? Results.Ok(rejected.Value!.Expense) : Failure(context, rejected.Error!);
        })
        .RequireIdempotency()
        .WithName("RejectExpense")
        .WithSummary("Reject an expense")
        .WithDescription("One rejection ends it, with a note of 3-500 characters; nothing is posted. The submitter cannot decide their own expense. A rejected expense is submitted again, not edited. Requires `finance.expense.approve`.")
        .WithTags("Expenses")
        .RequirePermission(Permissions.FinanceExpenseApprove);

        app.MapPost($"{Route}/{{id}}/pay", async (HttpContext context, string id, ExpensePayRequest payload, ExpenseService service) =>
        {
            var paid = await service.PayAsync(id, payload, Actor(context), context.RequestAborted);
            return paid.IsSuccess ? Results.Ok(paid.Value) : Failure(context, paid.Error!);
        })
        .WithName("PayExpense")
        .WithSummary("Record that an approved expense was paid")
        .WithDescription("`{method: bank|cash|cheque|bkash|nagad, reference?, paidOn?}`. Only an approved expense can be paid, once, and not by its submitter. Posts Dr payable (2020 for a reimbursement, 2100 for a vendor) / Cr the cash, bank or wallet account. Requires `finance.expense.pay`.")
        .WithTags("Expenses")
        .RequirePermission(Permissions.FinanceExpensePay);

        app.MapGet($"{Route}/rollups", async (HttpContext context, FinanceDbContext db, string? from, string? to) =>
        {
            if (!TryDate(from, out var fromDate) || !TryDate(to, out var toDate))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: from or to (yyyy-MM-dd).");
            }

            var rows = await ApprovedAsync(db, fromDate, toDate);
            RollupLine[] Group(Func<Approved, string> key) =>
            [
                .. rows.GroupBy(r => (Key: key(r), r.Expense.Currency)).OrderBy(g => g.Key.Key, StringComparer.Ordinal).ThenBy(g => g.Key.Currency, StringComparer.Ordinal)
                    .Select(g => new RollupLine(g.Key.Key, g.Key.Currency, Fmt(g.Sum(r => r.Expense.Amount)), Fmt(g.Where(r => r.Paid).Sum(r => r.Expense.Amount)), g.Count()))
            ];
            return Results.Ok(new ExpenseRollups(Group(r => r.Expense.ProjectSlug ?? "(none)"), Group(r => r.Expense.CategoryKey)));
        })
        .WithName("GetExpenseRollups")
        .WithSummary("Approved spend by project and by category")
        .WithDescription("Approved expenses only, per currency (never added together), with how much of it has been paid. Optional `from`/`to` filter on the date the expense was incurred.")
        .WithTags("Expenses")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/rollups/reconciliation", async (FinanceDbContext db) =>
        {
            var rows = await ApprovedAsync(db, null, null);
            var byId = rows.ToDictionary(r => r.Expense.Id);

            // What the ledger holds on expense accounts for each expense's own posting, reversals netted: the figure the rollups must equal.
            var postings = await db.Postings.AsNoTracking().Where(p => p.SourceType == PostingSources.Expense || p.ReversesPostingId != null).Select(p => new { p.Id, p.SourceType, p.SourceId, p.ReversesPostingId }).ToListAsync();
            var expenseOfPosting = postings.Where(p => p.SourceType == PostingSources.Expense && p.SourceId != null && !p.SourceId.EndsWith(":payment", StringComparison.Ordinal)).ToDictionary(p => p.Id, p => p.SourceId!);
            foreach (var reversal in postings.Where(p => p.ReversesPostingId is not null))
            {
                if (expenseOfPosting.TryGetValue(reversal.ReversesPostingId!.Value, out var expenseId))
                {
                    expenseOfPosting[reversal.Id] = expenseId;
                }
            }

            var entries = await (from e in db.LedgerEntries.AsNoTracking() join a in db.Accounts.AsNoTracking() on e.AccountId equals a.Id where a.Type == AccountTypes.Expense select new { e.PostingId, e.Currency, e.Debit, e.Credit }).ToListAsync();
            var ledgerByExpense = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (expenseOfPosting.TryGetValue(entry.PostingId, out var expenseId))
                {
                    ledgerByExpense[expenseId] = ledgerByExpense.GetValueOrDefault(expenseId) + entry.Debit - entry.Credit;
                }
            }

            var lines = new List<RollupReconciliationLine>();
            foreach (var (dimension, key) in new (string, Func<Approved, string>)[] { ("project", r => r.Expense.ProjectSlug ?? "(none)"), ("category", r => r.Expense.CategoryKey) })
            {
                foreach (var group in rows.GroupBy(r => (Key: key(r), r.Expense.Currency)).OrderBy(g => g.Key.Key, StringComparer.Ordinal).ThenBy(g => g.Key.Currency, StringComparer.Ordinal))
                {
                    var fromExpenses = group.Sum(r => r.Expense.Amount);
                    var fromLedger = group.Sum(r => ledgerByExpense.GetValueOrDefault(r.Expense.Id));
                    lines.Add(new RollupReconciliationLine(dimension, group.Key.Key, group.Key.Currency, Fmt(fromExpenses), Fmt(fromLedger), Fmt(fromExpenses - fromLedger)));
                }
            }

            _ = byId;
            return Results.Ok(new RollupReconciliation(lines.All(l => l.Difference == "0.00"), lines));
        })
        .WithName("ReconcileExpenseRollups")
        .WithSummary("Compare approved spend per project and category with the ledger")
        .WithDescription("For each project and category and currency: approved expenses against the net debit the ledger holds on expense accounts for the same expenses (reversals netted). Every difference is zero when the books agree.")
        .WithTags("Expenses")
        .RequirePermission(Permissions.FinanceView);
    }

    private sealed record Approved(ExpenseEntity Expense, bool Paid);

    private static async Task<List<Approved>> ApprovedAsync(FinanceDbContext db, DateOnly? from, DateOnly? to)
    {
        var rows = await (from e in db.Expenses.AsNoTracking()
                          where db.ExpenseDecisions.Any(d => d.ExpenseId == e.Id && d.Decision == ExpenseDecisions.Approved)
                          select new { Expense = e, Paid = db.ExpensePayments.Any(p => p.ExpenseId == e.Id) }).ToListAsync();
        return [.. rows.Where(r => (from is null || r.Expense.IncurredOn >= from) && (to is null || r.Expense.IncurredOn <= to)).Select(r => new Approved(r.Expense, r.Paid))];
    }

    private static bool TryDate(string? text, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = parsed;
            return true;
        }

        return false;
    }

    private static string Actor(HttpContext context) => context.User.FindFirstValue(KjClaims.Subject) ?? string.Empty;

    private static string ActorName(HttpContext context) => context.User.FindFirstValue(KjClaims.Email) ?? Actor(context);

    private static string Fmt(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static IResult Failure(HttpContext context, ResultError error) => ApiError.Create(context, error.Code switch
    {
        "NOT_FOUND" => StatusCodes.Status404NotFound,
        "SEPARATION_OF_DUTIES" => StatusCodes.Status403Forbidden,
        "ALREADY_DECIDED" or "ALREADY_PAID" or "NOT_APPROVED" or "RECEIPT_REQUIRED" or "TOO_MANY_PROOFS" or "PERIOD_CLOSED" => StatusCodes.Status409Conflict,
        "FILE_TOO_LARGE" => StatusCodes.Status413PayloadTooLarge,
        "UNSUPPORTED_TYPE" => StatusCodes.Status415UnsupportedMediaType,
        _ => StatusCodes.Status400BadRequest
    }, error.Code, error.Message);
}
