using System.Globalization;
using System.Security.Claims;
using System.Text;
using FinanceService.Data;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Banking;

public sealed record MatchRequest(string? LineId, string? EntryId);

public sealed record UnmatchRequest(string? LineId);

public sealed record DismissRequest(string? LineId, string? Reason);

public sealed record PostAndMatchRequest(string? LineId, string? Account, string? Memo);

public static class BankingEndpoints
{
    private const string Route = "/api/v1/admin/finance";
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;

    public static void MapBanking(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{Route}/bank-accounts", async (BankingService banking, bool? includeArchived, HttpContext context) =>
            Results.Ok(new { items = await banking.ListAccountsAsync(includeArchived == true, context.RequestAborted) }))
        .WithName("ListBankAccounts")
        .WithSummary("Bank accounts with the number masked to its last four digits")
        .WithTags("Banking")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/bank-accounts", async (HttpContext context, BankAccountRequest payload, BankingService banking) =>
        {
            var created = await banking.CreateAccountAsync(payload, Actor(context), context.RequestAborted);
            return created.IsSuccess ? Results.Created($"{Route}/bank-accounts/{created.Value!.Id}", created.Value) : Failure(context, created.Error!);
        })
        .WithName("CreateBankAccount")
        .WithSummary("Register a bank account")
        .WithDescription("`{name, bankName, number, currency, ledgerAccount?, csvMapping?}`. The number is stored encrypted and shown masked; `ledgerAccount` is the asset account of the ledger that holds this bank's balance (default 1020) and may be used by one live bank account only. `csvMapping` names the statement's columns (`date`, `description`, `reference`, `amount` or `debit`+`credit`, `balance`, `dateFormat`, `delimiter`); without it the header row is detected. Requires `finance.bank.manage`.")
        .WithTags("Banking")
        .RequirePermission(Permissions.FinanceBankManage);

        app.MapPatch($"{Route}/bank-accounts/{{id}}", async (HttpContext context, string id, BankAccountChange payload, BankingService banking) =>
        {
            var changed = await banking.UpdateAccountAsync(id, payload, context.RequestAborted);
            return changed.IsSuccess ? Results.Ok(changed.Value) : Failure(context, changed.Error!);
        })
        .WithName("UpdateBankAccount")
        .WithSummary("Rename, re-map or archive a bank account")
        .WithDescription("Any of `name`, `bankName`, `csvMapping` (or `clearCsvMapping`), `archived`, and `ledgerAccount` (only before the first statement is imported). The number cannot be changed: register a new account. Requires `finance.bank.manage`.")
        .WithTags("Banking")
        .RequirePermission(Permissions.FinanceBankManage);

        app.MapGet($"{Route}/bank-accounts/{{id}}/number", async (HttpContext context, string id, BankingService banking) =>
            await banking.RevealNumberAsync(id, Actor(context), context.RequestAborted) is { } number
                ? Results.Ok(new { number })
                : ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Bank account not found."))
        .WithName("RevealBankAccountNumber")
        .WithSummary("The full account number (logged)")
        .WithDescription("Every read is recorded with who read it. Requires `finance.bank.reveal`, which only Admin holds by default.")
        .WithTags("Banking")
        .RequirePermission(Permissions.FinanceBankReveal);

        app.MapPost($"{Route}/bank-accounts/{{id}}/statements", async (HttpContext context, string id, string? format, BankingService banking, IOptions<BankingOptions> options) =>
        {
            var limit = options.Value.EffectiveMaxFileBytes;
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    return ApiError.Create(context, StatusCodes.Status413PayloadTooLarge, "FILE_TOO_LARGE", $"A statement file may be at most {limit} bytes.");
                }

                buffer.Write(chunk, 0, read);
            }

            if (buffer.Length == 0)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "The request body is empty: send the statement file as the body.");
            }

            var text = new UTF8Encoding(false, false).GetString(buffer.ToArray());
            var imported = await banking.ImportAsync(id, text, format, context.Request.Headers["X-Filename"].ToString() is { Length: > 0 } name ? name : null, Actor(context), context.RequestAborted);
            if (imported.Report is { } report)
            {
                return Results.Created($"{Route}/bank-accounts/{id}/statements/{report.Statement.Id}", report);
            }

            var error = imported.Error!;
            if (error.Code == "IMPORT_REJECTED")
            {
                return Results.Problem(detail: error.Message, instance: context.Request.Path, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Unprocessable Entity",
                    type: "urn:kjwebsite:error:import-rejected", extensions: new Dictionary<string, object?> { ["error"] = new LegacyApiError(error.Code, error.Message), ["errors"] = imported.Errors });
            }

            return Failure(context, error);
        })
        .WithName("ImportBankStatement")
        .WithSummary("Import a CSV or OFX statement")
        .WithDescription("Send the file as the request body (`?format=csv|ofx`, detected if omitted; `X-Filename` is recorded). All or nothing: if any row cannot be read the response is 422 `IMPORT_REJECTED` with `errors:[{row, reason}]` for every bad row and nothing is stored. Lines already imported from an earlier statement are not stored again and are listed under `duplicates`. The same file cannot be imported twice (409). Unambiguous lines are matched to ledger entries automatically. Requires `finance.reconcile`.")
        .WithTags("Banking")
        .RequirePermission(Permissions.FinanceReconcile);

        app.MapGet($"{Route}/bank-accounts/{{id}}/statements", async (string id, BankingService banking, HttpContext context) =>
            Results.Ok(new { items = await banking.ListStatementsAsync(id, context.RequestAborted) }))
        .WithName("ListBankStatements")
        .WithSummary("Statements imported for a bank account, newest first")
        .WithTags("Banking")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/bank-accounts/{{id}}/rematch", async (HttpContext context, string id, BankingService banking) =>
        {
            var matched = await banking.RematchAsync(id, context.RequestAborted);
            return matched.IsSuccess ? Results.Ok(new { matched = matched.Value }) : Failure(context, matched.Error!);
        })
        .WithName("RematchBankLines")
        .WithSummary("Run automatic matching again for an account")
        .WithDescription("Useful after new ledger entries were posted. Same rules as at import. Requires `finance.reconcile`.")
        .WithTags("Banking")
        .RequirePermission(Permissions.FinanceReconcile);

        app.MapGet($"{Route}/bank-accounts/{{id}}/summary", async (HttpContext context, string id, BankingService banking) =>
            await banking.SummaryAsync(id, context.RequestAborted) is { } summary ? Results.Ok(summary) : ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Bank account not found."))
        .WithName("GetBankReconciliationSummary")
        .WithSummary("Ledger balance against the statement, with what is still unexplained")
        .WithDescription("`ledgerBalance` of the mapped ledger account, `bankBalance` (the running balance on the newest statement line, when the file has one), counts and net of unmatched lines and entries, and `unexplained`: the bank balance less unmatched and dismissed lines, less the ledger balance less unmatched entries. It is zero when every difference is itemised in the queue.")
        .WithTags("Banking")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/reconciliation", async (HttpContext context, BankingService banking, string? account, string? status, int? limit) =>
        {
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var wanted = (status ?? "all").ToLowerInvariant();
            if (wanted is not ("all" or "unmatched" or "matched" or "dismissed"))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: status (all, unmatched, matched, dismissed).");
            }

            var lines = await banking.LinesAsync(null, string.IsNullOrWhiteSpace(account) ? null : account, wanted, take, context.RequestAborted);
            var entries = await banking.EntriesAsync(string.IsNullOrWhiteSpace(account) ? null : account, lines, take, context.RequestAborted);
            return Results.Ok(new { lines, entries });
        })
        .WithName("GetReconciliation")
        .WithSummary("Statement lines and the ledger entries they can be matched with")
        .WithDescription("`lines` are statement lines (unmatched first, then newest first; `amount` is signed, money in positive) with `status` unmatched, matched or dismissed and `matchedEntryId`; `entries` are the unmatched ledger entries on the bank accounts' ledger accounts plus the ones the listed lines are matched to. Filters: `account`, `status`, `limit`.")
        .WithTags("Banking")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/reconciliation/match", async (HttpContext context, MatchRequest payload, BankingService banking) =>
        {
            if (!long.TryParse(payload.LineId, NumberStyles.None, CultureInfo.InvariantCulture, out var line) || !long.TryParse(payload.EntryId, NumberStyles.None, CultureInfo.InvariantCulture, out var entry))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: lineId, entryId.");
            }

            var matched = await banking.MatchAsync(line, entry, Actor(context), context.RequestAborted);
            return matched.IsSuccess ? Results.Ok(matched.Value) : Failure(context, matched.Error!);
        })
        .WithName("MatchBankLine")
        .WithSummary("Match a statement line to a ledger entry")
        .WithDescription("`{lineId, entryId}`. The entry must be on the bank account's ledger account, on the matching side (money in is a debit, out a credit), for exactly the line's amount and currency, and not matched to another line. Requires `finance.reconcile`.")
        .WithTags("Banking")
        .RequirePermission(Permissions.FinanceReconcile);

        app.MapPost($"{Route}/reconciliation/unmatch", async (HttpContext context, UnmatchRequest payload, BankingService banking) =>
            await Resolve(context, payload.LineId, line => banking.UnmatchAsync(line, Actor(context), context.RequestAborted)))
        .WithName("UnmatchBankLine")
        .WithSummary("Undo a match")
        .WithDescription("`{lineId}`. Only a match is undone; a posting made by post-and-match stays in the ledger (reverse it from the ledger if it was wrong). A dismissed line is reopened with its own endpoint. Requires `finance.reconcile`.")
        .WithTags("Banking")
        .RequirePermission(Permissions.FinanceReconcile);

        app.MapPost($"{Route}/reconciliation/dismiss", async (HttpContext context, DismissRequest payload, BankingService banking) =>
            await Resolve(context, payload.LineId, line => banking.DismissAsync(line, payload.Reason, Actor(context), context.RequestAborted)))
        .WithName("DismissBankLine")
        .WithSummary("Set a line aside with a reason")
        .WithDescription("`{lineId, reason}` (3 to 500 characters), for a line that needs no ledger entry. Requires `finance.reconcile`.")
        .WithTags("Banking")
        .RequirePermission(Permissions.FinanceReconcile);

        app.MapPost($"{Route}/reconciliation/reopen", async (HttpContext context, UnmatchRequest payload, BankingService banking) =>
            await Resolve(context, payload.LineId, line => banking.ReopenAsync(line, Actor(context), context.RequestAborted)))
        .WithName("ReopenBankLine")
        .WithSummary("Put a dismissed line back in the queue")
        .WithTags("Banking")
        .RequirePermission(Permissions.FinanceReconcile);

        app.MapPost($"{Route}/reconciliation/post-and-match", async (HttpContext context, PostAndMatchRequest payload, BankingService banking) =>
            await Resolve(context, payload.LineId, line => banking.PostAndMatchAsync(line, payload.Account, payload.Memo, Actor(context), context.RequestAborted)))
        .WithName("PostAndMatchBankLine")
        .WithSummary("Post the missing ledger entry for a line and match it")
        .WithDescription("`{lineId, account, memo?}`: posts a balanced entry between the bank account's ledger account and `account` (a ledger account code), dated the line's date, then matches the bank side to the line. Money in debits the bank account, money out credits it. It follows the ledger's rules (no future date, no closed period) and cannot post twice for one line. Requires `finance.ledger.adjust`.")
        .WithTags("Banking")
        .RequirePermission(Permissions.FinanceLedgerAdjust);
    }

    private static async Task<IResult> Resolve(HttpContext context, string? lineId, Func<long, Task<Result<BankLineView>>> action)
    {
        if (!long.TryParse(lineId, NumberStyles.None, CultureInfo.InvariantCulture, out var line))
        {
            return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: lineId.");
        }

        var done = await action(line);
        return done.IsSuccess ? Results.Ok(done.Value) : Failure(context, done.Error!);
    }

    private static string? Actor(HttpContext context) => context.User.FindFirstValue(KjClaims.Subject);

    private static IResult Failure(HttpContext context, ResultError error) =>
        ApiError.Create(context, error.Code switch
        {
            "NOT_FOUND" => StatusCodes.Status404NotFound,
            "VALIDATION_ERROR" => StatusCodes.Status400BadRequest,
            "ENTRY_NOT_ON_ACCOUNT" or "AMOUNT_MISMATCH" or "LEDGER_ACCOUNT_INVALID" => StatusCodes.Status422UnprocessableEntity,
            _ when error.Code.StartsWith("POSTING_", StringComparison.Ordinal) => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status409Conflict
        }, error.Code, error.Message);
}
