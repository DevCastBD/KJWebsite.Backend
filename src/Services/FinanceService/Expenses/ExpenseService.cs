using System.Globalization;
using System.Text.RegularExpressions;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using FinanceService.Ledger;
using FinanceService.Offline;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Expenses;

/// <summary>
/// Expense claims with a real approval trail. The rules that make it trustworthy are enforced here, from the signed-in user ids, whatever the client shows: the submitter
/// cannot approve (or pay) their own claim, nobody approves twice, an expense needs as many different approvers as its amount tier says, and approval posts to the ledger
/// in the same commit as the decision that makes it final.
/// </summary>
public sealed partial class ExpenseService(
    FinanceDbContext db, LedgerService ledger, IProofStore proofs, IOptions<ExpenseOptions> options, IOptions<ProofOptions> proofOptions, TimeProvider clock)
{
    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Slug();

    private static readonly string[] PayMethods = ["bank", "cash", "cheque", "bkash", "nagad"];

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<Result<ExpenseDetail>> SubmitAsync(ExpenseRequest request, string actor, string actorName, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        var payee = request.Payee?.Trim();
        if (payee is not { Length: > 0 and <= 200 })
        {
            invalid.Add("payee");
        }

        var description = request.Description?.Trim();
        if (description is not { Length: > 0 and <= 1000 })
        {
            invalid.Add("description");
        }

        var categories = options.Value.EffectiveCategories;
        var category = request.Category?.Trim();
        var categoryKey = category is null ? null : categories.Keys.FirstOrDefault(k => string.Equals(k, category, StringComparison.OrdinalIgnoreCase));
        if (categoryKey is null)
        {
            invalid.Add("category");
        }

        var amount = Money.Parse(request.Amount, request.Currency);
        if (!amount.IsSuccess || !amount.Value.IsPositive)
        {
            invalid.Add("amount");
        }

        var project = string.IsNullOrWhiteSpace(request.Project) ? null : request.Project.Trim();
        if (project is not null && (project.Length > 100 || !Slug().IsMatch(project)))
        {
            invalid.Add("project");
        }

        var incurred = Today;
        if (!string.IsNullOrWhiteSpace(request.Date) && (!DateOnly.TryParseExact(request.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out incurred) || incurred > Today || incurred < Today.AddYears(-2)))
        {
            invalid.Add("date");
        }

        if (invalid.Count > 0)
        {
            return Result.Failure<ExpenseDetail>("VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
        }

        var now = clock.GetUtcNow();
        var expense = new ExpenseEntity
        {
            Id = PublicDonationEndpoints.NewId("exp"),
            Payee = payee!,
            Description = description!,
            CategoryKey = categoryKey!.ToLowerInvariant(),
            AccountCode = categories[categoryKey],
            Amount = amount.Value.Amount,
            Currency = amount.Value.Currency,
            ProjectSlug = project,
            IsReimbursement = request.Reimbursement,
            IncurredOn = incurred,
            RequiredApprovals = options.Value.RequiredApprovers(amount.Value.Currency, amount.Value.Amount),
            SubmittedBy = actor,
            SubmittedByName = actorName,
            SubmittedAt = now
        };
        db.Expenses.Add(expense);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success((await DetailAsync(expense.Id, cancellationToken))!);
    }

    public async Task<Result<ExpenseReceiptView>> AddReceiptAsync(string expenseId, Stream content, string? contentType, string? fileName, string actor, CancellationToken cancellationToken)
    {
        var expense = await db.Expenses.AsNoTracking().FirstOrDefaultAsync(e => e.Id == expenseId, cancellationToken);
        if (expense is null)
        {
            return Result.Failure<ExpenseReceiptView>("NOT_FOUND", "Expense not found.");
        }

        if (await db.ExpenseDecisions.AnyAsync(d => d.ExpenseId == expenseId, cancellationToken))
        {
            return Result.Failure<ExpenseReceiptView>("ALREADY_DECIDED", "Receipts can only be added while the expense awaits a decision.");
        }

        if (await db.ExpenseReceipts.CountAsync(r => r.ExpenseId == expenseId, cancellationToken) >= ProofRules.MaxPerRecord)
        {
            return Result.Failure<ExpenseReceiptView>("TOO_MANY_PROOFS", $"At most {ProofRules.MaxPerRecord} receipts per expense.");
        }

        var prepared = await ProofIntake.PrepareAsync(content, contentType, fileName, cancellationToken);
        if (!prepared.IsSuccess)
        {
            return Result.Failure<ExpenseReceiptView>(prepared.Error!.Code, prepared.Error.Message);
        }

        var key = Guid.NewGuid().ToString("N");
        using (var stream = new MemoryStream(prepared.Value!.Bytes))
        {
            await proofs.SaveAsync(key, stream, cancellationToken);
        }

        var now = clock.GetUtcNow();
        var receipt = new ExpenseReceiptEntity
        {
            Id = PublicDonationEndpoints.NewId("rcx"),
            ExpenseId = expenseId,
            FileName = prepared.Value.FileName,
            ContentType = prepared.Value.ContentType,
            SizeBytes = prepared.Value.Bytes.Length,
            Sha256 = prepared.Value.Sha256,
            StorageKey = key,
            UploadedBy = actor,
            UploadedAt = now,
            RetainUntil = DateOnly.FromDateTime(now.UtcDateTime).AddYears(Math.Max(1, proofOptions.Value.RetentionYears))
        };
        db.ExpenseReceipts.Add(receipt);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(ToView(receipt));
    }

    public async Task<(ExpenseReceiptEntity Receipt, Stream Content)?> OpenReceiptAsync(string expenseId, string receiptId, CancellationToken cancellationToken)
    {
        var receipt = await db.ExpenseReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.ExpenseId == expenseId && r.Id == receiptId, cancellationToken);
        if (receipt is null)
        {
            return null;
        }

        var stream = await proofs.OpenReadAsync(receipt.StorageKey, cancellationToken);
        return stream is null ? null : (receipt, stream);
    }

    /// <summary>
    /// Adds this approver's approval. The submitter is refused, a repeat from the same person changes nothing, and a receipt must be attached first. When the approvals
    /// reach the tier's count the final decision and the ledger posting are saved in the same commit; if two last approvals race, a follow-up check finishes the job once.
    /// </summary>
    public async Task<Result<ExpenseDetail>> ApproveAsync(string expenseId, string? note, string actor, CancellationToken cancellationToken)
    {
        var expense = await db.Expenses.AsNoTracking().FirstOrDefaultAsync(e => e.Id == expenseId, cancellationToken);
        if (expense is null)
        {
            return Result.Failure<ExpenseDetail>("NOT_FOUND", "Expense not found.");
        }

        if (string.Equals(expense.SubmittedBy, actor, StringComparison.Ordinal))
        {
            return Result.Failure<ExpenseDetail>("SEPARATION_OF_DUTIES", "The person who submitted an expense cannot approve it.");
        }

        var decision = await db.ExpenseDecisions.AsNoTracking().FirstOrDefaultAsync(d => d.ExpenseId == expenseId, cancellationToken);
        var already = await db.ExpenseApprovals.AnyAsync(a => a.ExpenseId == expenseId && a.ApprovedBy == actor, cancellationToken);
        if (decision is not null || already)
        {
            // Approving again is harmless (and finishes a decision a race left undone); approving something rejected is not possible.
            if (decision?.Decision == ExpenseDecisions.Rejected)
            {
                return Result.Failure<ExpenseDetail>("ALREADY_DECIDED", "This expense was rejected.");
            }

            var repaired = decision is null ? await FinishIfReadyAsync(expense, actor, cancellationToken) : Result.Success();
            return repaired.IsSuccess ? Result.Success((await DetailAsync(expenseId, cancellationToken))!) : Result.Failure<ExpenseDetail>(repaired.Error!.Code, repaired.Error.Message);
        }

        if (options.Value.ReceiptRequired && !await db.ExpenseReceipts.AnyAsync(r => r.ExpenseId == expenseId, cancellationToken))
        {
            return Result.Failure<ExpenseDetail>("RECEIPT_REQUIRED", "At least one receipt must be attached before an expense can be approved.");
        }

        var now = clock.GetUtcNow();
        var existing = await db.ExpenseApprovals.CountAsync(a => a.ExpenseId == expenseId, cancellationToken);
        db.ExpenseApprovals.Add(new ExpenseApprovalEntity { ExpenseId = expenseId, ApprovedBy = actor, Note = Trim(note), At = now });
        if (existing + 1 >= expense.RequiredApprovals)
        {
            var staged = await StageFinalApprovalAsync(expense, actor, cancellationToken);
            if (!staged.IsSuccess)
            {
                db.ChangeTracker.Clear();
                return Result.Failure<ExpenseDetail>(staged.Error!.Code, staged.Error.Message);
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent approval got in first (same approver twice, or the deciding approval): look again at where things stand.
            db.ChangeTracker.Clear();
        }

        var settled = await FinishIfReadyAsync(expense, actor, cancellationToken);
        return settled.IsSuccess ? Result.Success((await DetailAsync(expenseId, cancellationToken))!) : Result.Failure<ExpenseDetail>(settled.Error!.Code, settled.Error.Message);
    }

    public async Task<Result<ExpenseDetail>> RejectAsync(string expenseId, string? note, string actor, CancellationToken cancellationToken)
    {
        var text = Trim(note);
        if (text is not { Length: >= 3 })
        {
            return Result.Failure<ExpenseDetail>("VALIDATION_ERROR", "Invalid fields: note (3-500 characters).");
        }

        var expense = await db.Expenses.AsNoTracking().FirstOrDefaultAsync(e => e.Id == expenseId, cancellationToken);
        if (expense is null)
        {
            return Result.Failure<ExpenseDetail>("NOT_FOUND", "Expense not found.");
        }

        if (string.Equals(expense.SubmittedBy, actor, StringComparison.Ordinal))
        {
            return Result.Failure<ExpenseDetail>("SEPARATION_OF_DUTIES", "The person who submitted an expense cannot decide it.");
        }

        if (await db.ExpenseDecisions.AnyAsync(d => d.ExpenseId == expenseId, cancellationToken))
        {
            return Result.Failure<ExpenseDetail>("ALREADY_DECIDED", "This expense has already been decided.");
        }

        db.ExpenseDecisions.Add(new ExpenseDecisionEntity { ExpenseId = expenseId, Decision = ExpenseDecisions.Rejected, DecidedBy = actor, Note = text, At = clock.GetUtcNow() });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Result.Failure<ExpenseDetail>("ALREADY_DECIDED", "This expense has already been decided.");
        }

        return Result.Success((await DetailAsync(expenseId, cancellationToken))!);
    }

    public async Task<Result<ExpenseDetail>> PayAsync(string expenseId, ExpensePayRequest request, string actor, CancellationToken cancellationToken)
    {
        var method = request.Method?.Trim();
        var invalid = new List<string>();
        if (method is null || !PayMethods.Contains(method))
        {
            invalid.Add("method");
        }

        var paidOn = Today;
        if (!string.IsNullOrWhiteSpace(request.PaidOn) && (!DateOnly.TryParseExact(request.PaidOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out paidOn) || paidOn > Today))
        {
            invalid.Add("paidOn");
        }

        var reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        if (reference is { Length: > 100 })
        {
            invalid.Add("reference");
        }

        if (invalid.Count > 0)
        {
            return Result.Failure<ExpenseDetail>("VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
        }

        var expense = await db.Expenses.AsNoTracking().FirstOrDefaultAsync(e => e.Id == expenseId, cancellationToken);
        if (expense is null)
        {
            return Result.Failure<ExpenseDetail>("NOT_FOUND", "Expense not found.");
        }

        if (string.Equals(expense.SubmittedBy, actor, StringComparison.Ordinal))
        {
            return Result.Failure<ExpenseDetail>("SEPARATION_OF_DUTIES", "The person who submitted an expense cannot be the one who records its payment.");
        }

        var decision = await db.ExpenseDecisions.AsNoTracking().FirstOrDefaultAsync(d => d.ExpenseId == expenseId, cancellationToken);
        if (decision?.Decision != ExpenseDecisions.Approved)
        {
            return Result.Failure<ExpenseDetail>("NOT_APPROVED", "Only an approved expense can be paid.");
        }

        if (await db.ExpensePayments.AnyAsync(p => p.ExpenseId == expenseId, cancellationToken))
        {
            return Result.Failure<ExpenseDetail>("ALREADY_PAID", "This expense has already been paid.");
        }

        var money = Money.Of(expense.Amount, expense.Currency);
        var posting = await ledger.StageAsync(new PostingDraft(paidOn, Memo("Payment of expense", expense), PostingSources.Expense, $"{expense.Id}:payment",
            [LedgerLine.Debit(PayableAccount(expense), money), LedgerLine.Credit(PaymentAccount(method!), money)]), actor, cancellationToken);
        if (!posting.IsSuccess)
        {
            db.ChangeTracker.Clear();
            return Result.Failure<ExpenseDetail>(posting.Error!.Code, posting.Error.Message);
        }

        var payment = new ExpensePaymentEntity { ExpenseId = expenseId, Method = method!, Reference = reference, PaidOn = paidOn, PaidBy = actor, At = clock.GetUtcNow() };
        db.ExpensePayments.Add(payment);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Result.Failure<ExpenseDetail>("ALREADY_PAID", "This expense has already been paid.");
        }

        return Result.Success((await DetailAsync(expenseId, cancellationToken))!);
    }

    /// <summary>The decision and the posting go in together or not at all. The posting is dated the day the expense was incurred unless that month is closed, then today.</summary>
    private async Task<Result> StageFinalApprovalAsync(ExpenseEntity expense, string actor, CancellationToken cancellationToken)
    {
        var closed = await ledger.IsClosedAsync(expense.IncurredOn, cancellationToken);
        var money = Money.Of(expense.Amount, expense.Currency);
        var staged = await ledger.StageAsync(new PostingDraft(closed ? Today : expense.IncurredOn, Memo("Expense", expense), PostingSources.Expense, expense.Id,
            [LedgerLine.Debit(expense.AccountCode, money), LedgerLine.Credit(PayableAccount(expense), money)]), actor, cancellationToken);
        if (!staged.IsSuccess)
        {
            return Result.Failure(staged.Error!.Code, staged.Error.Message);
        }

        var decision = new ExpenseDecisionEntity { ExpenseId = expense.Id, Decision = ExpenseDecisions.Approved, DecidedBy = actor, At = clock.GetUtcNow() };
        db.ExpenseDecisions.Add(decision);
        return Result.Success();
    }

    // Repairs a race: if the approvals now meet the tier but nobody recorded the decision, record it (once: the decision is unique per expense).
    private async Task<Result> FinishIfReadyAsync(ExpenseEntity expense, string actor, CancellationToken cancellationToken)
    {
        if (await db.ExpenseDecisions.AnyAsync(d => d.ExpenseId == expense.Id, cancellationToken)
            || await db.ExpenseApprovals.CountAsync(a => a.ExpenseId == expense.Id, cancellationToken) < expense.RequiredApprovals)
        {
            return Result.Success();
        }

        var staged = await StageFinalApprovalAsync(expense, actor, cancellationToken);
        if (!staged.IsSuccess)
        {
            db.ChangeTracker.Clear();
            return staged;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }

        return Result.Success();
    }

    public async Task<ExpenseDetail?> DetailAsync(string expenseId, CancellationToken cancellationToken = default)
    {
        var expense = await db.Expenses.AsNoTracking().FirstOrDefaultAsync(e => e.Id == expenseId, cancellationToken);
        if (expense is null)
        {
            return null;
        }

        var approvals = (await db.ExpenseApprovals.AsNoTracking().Where(a => a.ExpenseId == expenseId).ToListAsync(cancellationToken)).OrderBy(a => a.Id).ToList();
        var receipts = (await db.ExpenseReceipts.AsNoTracking().Where(r => r.ExpenseId == expenseId).ToListAsync(cancellationToken)).OrderBy(r => r.UploadedAt).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        var decision = await db.ExpenseDecisions.AsNoTracking().FirstOrDefaultAsync(d => d.ExpenseId == expenseId, cancellationToken);
        var payment = await db.ExpensePayments.AsNoTracking().FirstOrDefaultAsync(p => p.ExpenseId == expenseId, cancellationToken);
        var posting = await db.Postings.AsNoTracking().Where(p => p.SourceType == PostingSources.Expense && p.SourceId == expenseId).Select(p => (long?)p.Id).FirstOrDefaultAsync(cancellationToken);
        return new ExpenseDetail(
            Row(expense, approvals.Count, decision, payment is not null),
            expense.AccountCode, expense.IncurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [.. approvals.Select(a => new ExpenseApprovalView(a.ApprovedBy, a.Note, a.At))], [.. receipts.Select(ToView)],
            payment is null ? null : new ExpensePaymentView(payment.Method, payment.Reference, payment.PaidOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), payment.PaidBy, payment.At),
            posting?.ToString(CultureInfo.InvariantCulture));
    }

    public static ExpenseRow Row(ExpenseEntity e, int approvals, ExpenseDecisionEntity? decision, bool paid) => new(
        e.Id, e.Payee, e.Description, e.CategoryKey, e.Amount.ToString("0.00", CultureInfo.InvariantCulture), e.Currency, e.ProjectSlug, e.IsReimbursement, e.SubmittedBy, e.SubmittedByName,
        DateOnly.FromDateTime(e.SubmittedAt.UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), decision?.Decision ?? "pending", approvals, e.RequiredApprovals, paid,
        decision?.DecidedBy, decision?.Note);

    private static string PayableAccount(ExpenseEntity expense) => expense.IsReimbursement ? "2020" : "2100";

    private static string PaymentAccount(string method) => method switch
    {
        "cash" => LedgerAccounts.Cash,
        "bkash" or "nagad" => LedgerAccounts.MobileWallets,
        _ => LedgerAccounts.Bank
    };

    private static string Memo(string prefix, ExpenseEntity e)
    {
        var text = $"{prefix} {e.Id}: {e.Payee} - {e.Description}";
        return text.Length > PostingRules.MaxMemo ? text[..PostingRules.MaxMemo] : text;
    }

    private static string? Trim(string? note)
    {
        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return text is { Length: > 500 } ? text[..500] : text;
    }

    private static ExpenseReceiptView ToView(ExpenseReceiptEntity r) =>
        new(r.Id, r.FileName, r.ContentType, r.SizeBytes, r.Sha256, r.UploadedBy, r.UploadedAt, r.RetainUntil.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}
