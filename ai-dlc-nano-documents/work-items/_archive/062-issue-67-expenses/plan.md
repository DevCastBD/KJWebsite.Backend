<!-- phase: DONE | branch: Feature/issue-67-expenses | tasks: 9/9
     base: ac17cd6 | updated: 2026-10-31
     next: none (wrapped up) -->
# Plan: Expenses (BE-062, #67)
## Tasks
- [x] Entities + migration (append-only, PG triggers): expenses, expense_receipts, expense_approvals (unique per expense+approver), expense_decisions (final), expense_payments; options (tiers, categories); permission finance.expense.pay
- [x] ExpenseService: submit (validation, category, project), receipts, approve (tier lookup, distinct approvers, not submitter, receipt required), reject, final posting in the same commit
- [x] Pay step: separate-person rule, method account, posting, paid state
- [x] Endpoints (FE shape): GET|POST /expenses, approve|reject, receipts upload/download, pay, detail with approvals/receipts/payment
- [x] Rollups: per project and category with reconciliation against ledger
- [x] Audit config, docs/EXPENSES.md, openapi regen, map
- [x] Tests: segregation (submitter, repeat approver), tiers by amount and currency, config override, receipts rules, rejection, posting accounts, payment, closed period, rollup=ledger, concurrency of approvals, permissions, audit, PG triggers
- [x] Full suite, format, OpenAPI verify, coverage check
## Tests
- High blast radius: separation of duties proven by test; concurrent final approvals post once
## Files
- src/Services/FinanceService/Expenses/**, Data/**, Migrations/**, Program.cs; BuildingBlocks Permissions; tests/**; docs/**; openapi.v1.yaml
