using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using FinanceService.Expenses;
using FinanceService.Ledger;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Finance;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Budgets;

public sealed record BudgetRequest(string? Year, string? Category, string? Project, string? Planned, string? Currency);

public sealed record BudgetChange(string? Planned);

/// <summary>A budget line as the frontend lists it (`category`, `planned`, `actual`, `currency`), with the variance worked out from the ledger.</summary>
public sealed record BudgetRow(string Id, string FiscalYear, string Category, string? Project, string Planned, string Actual, string Currency, string Remaining, string PercentUsed, string Status, bool YearClosed);

public sealed record FiscalYearRow(string Label, string Start, string End, string Status, DateTimeOffset? ClosedAt, string? ClosedBy, int Postings, int Budgets);

public sealed record BudgetAlertRow(string Id, string BudgetId, string FiscalYear, string Category, string? Project, string Currency, int ThresholdPercent, string Planned, string Actual, DateTimeOffset At);

public static partial class BudgetEndpoints
{
    private const string Route = "/api/v1/admin/finance";
    private const int DefaultLimit = 100;
    private const int MaxLimit = 200;

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Slug();

    public static void MapBudgets(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{Route}/fiscal-years", async (FinanceDbContext db, LedgerService ledger, IOptions<LedgerOptions> ledgerOptions) =>
        {
            var closes = (await db.FiscalYearCloses.AsNoTracking().ToListAsync()).ToDictionary(c => c.Label);
            var postingYears = (await db.Postings.AsNoTracking().GroupBy(p => p.FiscalYear).Select(g => new { Label = g.Key, Count = g.Count() }).ToListAsync()).ToDictionary(g => g.Label, g => g.Count);
            var budgetYears = (await db.Budgets.AsNoTracking().GroupBy(b => b.FiscalYear).Select(g => new { Label = g.Key, Count = g.Count() }).ToListAsync()).ToDictionary(g => g.Label, g => g.Count);
            var labels = closes.Keys.Union(postingYears.Keys).Union(budgetYears.Keys).Append(ledger.FiscalYearOf(ledger.Today)).Distinct().OrderByDescending(l => l, StringComparer.Ordinal);
            var rows = labels.Where(l => FiscalCalendar.TryRange(l, ledgerOptions.Value.FiscalYearStartMonth, out _, out _)).Select(label =>
            {
                FiscalCalendar.TryRange(label, ledgerOptions.Value.FiscalYearStartMonth, out var start, out var end);
                closes.TryGetValue(label, out var close);
                return new FiscalYearRow(label, start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), close is null ? "open" : "closed", close?.ClosedAt, close?.ClosedBy,
                    postingYears.GetValueOrDefault(label), budgetYears.GetValueOrDefault(label));
            });
            return Results.Ok(new { items = rows.ToList() });
        })
        .WithName("ListFiscalYears")
        .WithSummary("Fiscal years that have postings, budgets or a close, and the current one")
        .WithDescription("A year is open until it is closed. Labels and dates follow the ledger's fiscal calendar (`Finance:Ledger:FiscalYearStartMonth`, July by default: `2026-27` runs 2026-07-01 to 2027-06-30).")
        .WithTags("Budgets")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/fiscal-years/{{label}}/close", async (HttpContext context, string label, LedgerService ledger) =>
        {
            var closed = await ledger.CloseFiscalYearAsync(label, context.User.FindFirstValue(KjClaims.Subject), context.RequestAborted);
            return closed.IsSuccess ? Results.Ok(new { fiscalYear = label, closed = true }) : Failure(context, closed.Error!);
        })
        .WithName("CloseFiscalYear")
        .WithSummary("Close a fiscal year")
        .WithDescription("Only a year that has ended can be closed, once. Afterwards the ledger rejects any posting or reversal dated in it (`FISCAL_YEAR_CLOSED`, 409) and its budgets are frozen. There is no reopen in this version. Requires `finance.period.close`.")
        .WithTags("Budgets")
        .RequirePermission(Permissions.FinancePeriodClose);

        app.MapGet($"{Route}/budgets", async (HttpContext context, FinanceDbContext db, BudgetMath math, LedgerService ledger, IOptions<LedgerOptions> ledgerOptions, IOptions<BudgetOptions> options, string? year, string? currency, string? project, string? category, int? limit, int? offset) =>
        {
            var label = string.IsNullOrWhiteSpace(year) ? ledger.FiscalYearOf(ledger.Today) : year;
            if (!FiscalCalendar.TryRange(label, ledgerOptions.Value.FiscalYearStartMonth, out _, out _))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: year (a fiscal year such as 2026-27).");
            }

            var query = db.Budgets.AsNoTracking().Where(b => b.FiscalYear == label);
            if (!string.IsNullOrWhiteSpace(currency))
            {
                query = query.Where(b => b.Currency == currency);
            }

            if (!string.IsNullOrWhiteSpace(project))
            {
                query = query.Where(b => b.ProjectSlug == project);
            }

            if (!string.IsNullOrWhiteSpace(category))
            {
                query = query.Where(b => b.CategoryKey == category);
            }

            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var skip = Math.Max(0, offset ?? 0);
            var total = await query.CountAsync();
            var page = (await query.ToListAsync()).OrderBy(b => b.ProjectSlug ?? string.Empty, StringComparer.Ordinal).ThenBy(b => b.CategoryKey, StringComparer.Ordinal).ThenBy(b => b.Currency, StringComparer.Ordinal).Skip(skip).Take(take).ToList();
            var actuals = await math.ActualsAsync(page);
            var closed = await db.FiscalYearCloses.AnyAsync(c => c.Label == label);
            return Results.Ok(new PagingEnvelope<BudgetRow>([.. page.Select(b => Row(b, actuals[b.Id], options.Value.EffectiveThresholds, closed))], total, null, take, skip));
        })
        .WithName("ListBudgets")
        .WithSummary("Budget lines for a fiscal year with actual spend from the ledger")
        .WithDescription("`year` defaults to the current fiscal year. `actual` is read from the ledger: debits less credits on the category's expense account in the line's currency, dated inside the year; a project line counts only spend from expenses coded to that project. `status` is ok, warning (past the first alert threshold) or over (actual above plan).")
        .WithTags("Budgets")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/budgets", async (HttpContext context, BudgetRequest payload, FinanceDbContext db, LedgerService ledger, BudgetMath math, IOptions<LedgerOptions> ledgerOptions, IOptions<ExpenseOptions> expenseOptions, IOptions<BudgetOptions> options, TimeProvider clock) =>
        {
            var invalid = new List<string>();
            var label = string.IsNullOrWhiteSpace(payload.Year) ? ledger.FiscalYearOf(ledger.Today) : payload.Year.Trim();
            if (!FiscalCalendar.TryRange(label, ledgerOptions.Value.FiscalYearStartMonth, out _, out _))
            {
                invalid.Add("year");
            }

            var categories = expenseOptions.Value.EffectiveCategories;
            var categoryKey = payload.Category is null ? null : categories.Keys.FirstOrDefault(k => string.Equals(k, payload.Category.Trim(), StringComparison.OrdinalIgnoreCase));
            if (categoryKey is null)
            {
                invalid.Add("category");
            }

            var project = string.IsNullOrWhiteSpace(payload.Project) ? null : payload.Project.Trim();
            if (project is not null && (project.Length > 100 || !Slug().IsMatch(project)))
            {
                invalid.Add("project");
            }

            var planned = Money.Parse(payload.Planned, payload.Currency);
            if (!planned.IsSuccess || !planned.Value.IsPositive)
            {
                invalid.Add("planned");
            }

            if (invalid.Count > 0)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
            }

            if (await db.FiscalYearCloses.AnyAsync(c => c.Label == label))
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "FISCAL_YEAR_CLOSED", $"Fiscal year {label} is closed; its budgets cannot change.");
            }

            var currency = planned.Value.Currency;
            var key = categoryKey!.ToLowerInvariant();
            if (await db.Budgets.AnyAsync(b => b.FiscalYear == label && b.Currency == currency && b.CategoryKey == key && b.ProjectSlug == project))
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "BUDGET_EXISTS", "There is already a budget line for that year, currency, project and category; edit it instead.");
            }

            var line = new BudgetEntity
            {
                Id = PublicDonationEndpoints.NewId("bud"),
                FiscalYear = label,
                Currency = currency,
                ProjectSlug = project,
                CategoryKey = key,
                PlannedAmount = planned.Value.Amount,
                CreatedBy = context.User.FindFirstValue(KjClaims.Subject),
                CreatedAt = clock.GetUtcNow()
            };
            db.Budgets.Add(line);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "BUDGET_EXISTS", "There is already a budget line for that year, currency, project and category; edit it instead.");
            }

            return Results.Created($"{Route}/budgets/{line.Id}", Row(line, (await math.ActualsAsync([line]))[line.Id], options.Value.EffectiveThresholds, false));
        })
        .WithName("CreateBudget")
        .WithSummary("Add a budget line")
        .WithDescription("`{year?, category, project?, planned, currency}`: `year` defaults to the current fiscal year, `category` is an expense category key, `project` (a slug) narrows it to one project and omitting it budgets the whole organisation for that category, `planned` is a positive decimal string. One line per year, currency, project and category. A closed year takes no new lines. Requires `finance.budget.manage`.")
        .WithTags("Budgets")
        .RequirePermission(Permissions.FinanceBudgetManage);

        app.MapPut($"{Route}/budgets/{{id}}", async (HttpContext context, string id, BudgetChange payload, FinanceDbContext db, BudgetMath math, IOptions<BudgetOptions> options) =>
        {
            var line = await db.Budgets.FirstOrDefaultAsync(b => b.Id == id);
            if (line is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Budget line not found.");
            }

            var planned = Money.Parse(payload.Planned, line.Currency);
            if (!planned.IsSuccess || !planned.Value.IsPositive)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: planned.");
            }

            if (await db.FiscalYearCloses.AnyAsync(c => c.Label == line.FiscalYear))
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "FISCAL_YEAR_CLOSED", $"Fiscal year {line.FiscalYear} is closed; its budgets cannot change.");
            }

            line.PlannedAmount = planned.Value.Amount;
            await db.SaveChangesAsync();
            return Results.Ok(Row(line, (await math.ActualsAsync([line]))[line.Id], options.Value.EffectiveThresholds, false));
        })
        .WithName("UpdateBudget")
        .WithSummary("Change a budget line's planned amount")
        .WithDescription("Only the amount can change; the year, currency, project and category identify the line. Audited. Refused once the year is closed. Requires `finance.budget.manage`.")
        .WithTags("Budgets")
        .RequirePermission(Permissions.FinanceBudgetManage);

        app.MapDelete($"{Route}/budgets/{{id}}", async (HttpContext context, string id, FinanceDbContext db) =>
        {
            var line = await db.Budgets.FirstOrDefaultAsync(b => b.Id == id);
            if (line is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Budget line not found.");
            }

            if (await db.FiscalYearCloses.AnyAsync(c => c.Label == line.FiscalYear))
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "FISCAL_YEAR_CLOSED", $"Fiscal year {line.FiscalYear} is closed; its budgets cannot change.");
            }

            db.Budgets.Remove(line);
            await db.SaveChangesAsync();
            return Results.NoContent();
        })
        .WithName("DeleteBudget")
        .WithSummary("Remove a budget line from an open year")
        .WithDescription("A budget is a plan, so it can be removed while its year is open; the removal is audited and alerts already fired stay on record. Requires `finance.budget.manage`.")
        .WithTags("Budgets")
        .RequirePermission(Permissions.FinanceBudgetManage);

        app.MapGet($"{Route}/budgets/alerts", async (FinanceDbContext db, int? limit, int? offset) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 100);
            var skip = Math.Max(0, offset ?? 0);
            var query = db.BudgetAlerts.AsNoTracking();
            var total = await query.CountAsync();
            var page = await query.OrderByDescending(a => a.Id).Skip(skip).Take(take).ToListAsync();
            var ids = page.Select(a => a.BudgetId).Distinct().ToList();
            var lines = (await db.Budgets.AsNoTracking().Where(b => ids.Contains(b.Id)).ToListAsync()).ToDictionary(b => b.Id);
            return Results.Ok(new PagingEnvelope<BudgetAlertRow>([.. page.Select(a =>
            {
                lines.TryGetValue(a.BudgetId, out var line);
                return new BudgetAlertRow(a.Id.ToString(CultureInfo.InvariantCulture), a.BudgetId, line?.FiscalYear ?? string.Empty, line?.CategoryKey ?? string.Empty, line?.ProjectSlug, line?.Currency ?? string.Empty,
                    a.ThresholdPercent, BudgetMath.Fmt(a.PlannedAmount), BudgetMath.Fmt(a.ActualAmount), a.At);
            })], total, null, take, skip));
        })
        .WithName("ListBudgetAlerts")
        .WithSummary("Over-budget alerts that have fired, newest first")
        .WithTags("Budgets")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/budgets/alerts/run", async (HttpContext context, BudgetAlertService alerts) => Results.Ok(await alerts.EvaluateAsync(context.RequestAborted)))
        .WithName("RunBudgetAlerts")
        .WithSummary("Check every open budget line against the alert thresholds now")
        .WithDescription("Fires, once per line and threshold, an alert for each configured percentage (default 80 and 100) of the plan that spend has reached; the same check runs on a timer. Requires `finance.budget.manage`.")
        .WithTags("Budgets")
        .RequirePermission(Permissions.FinanceBudgetManage);
    }

    private static BudgetRow Row(BudgetEntity b, decimal actual, IReadOnlyList<int> thresholds, bool yearClosed) => new(
        b.Id, b.FiscalYear, b.CategoryKey, b.ProjectSlug, BudgetMath.Fmt(b.PlannedAmount), BudgetMath.Fmt(actual), b.Currency, BudgetMath.Fmt(b.PlannedAmount - actual),
        BudgetMath.PercentUsed(b.PlannedAmount, actual).ToString("0.0", CultureInfo.InvariantCulture), BudgetMath.Status(b.PlannedAmount, actual, thresholds), yearClosed);

    private static IResult Failure(HttpContext context, ResultError error) => ApiError.Create(context, error.Code switch
    {
        "ALREADY_CLOSED" or "FISCAL_YEAR_NOT_ENDED" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest
    }, error.Code, error.Message);
}
