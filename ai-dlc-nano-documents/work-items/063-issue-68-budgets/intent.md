# Fiscal years, budgets, budget-vs-actual, over-budget alerts (BE-064)
- Source: issue #68 https://github.com/DevCastBD/KJWebsite.Backend/issues/68 (milestone 5, P4)
- Docs: docs/LEDGER.md (fiscal calendar), docs/EXPENSES.md; FE contract GET /budgets?year -> {id, category, planned, actual} (src/admin/finance/api.ts)
- Type: feature
## Request
- Fiscal years that can be opened/closed (closed year rejects postings); budgets per project/category; variance from the ledger; configurable over-budget alerts.
## Decisions
- Actuals: ledger expense-account debits dated in the year; project lines count only postings sourced from an approved expense coded to that project (reversals netted); the no-project line is the whole category on the organisation (user)
- Alerts: append-only alert rows + Warning log + optional outbox emails (Finance:Budgets:AlertEmails); thresholds configurable (default 80 and 100), each fires once per line (user)
- #112 (fiscal calendar/standard) skipped by user: uses the ledger's configurable July-June default; label-to-date range derived from the same setting
- Assumed: a fiscal year is implicit (derived label) and open until closed by an append-only close row; close is allowed only after the year's last day, is one-way (reopen waits for #72's two-approver flow), and makes postings dated in it fail with FISCAL_YEAR_CLOSED (service + PG trigger) and freezes its budgets; budget rows are editable while the year is open and audited (a plan, not evidence); one budget line per (year, currency, project, category); categories reuse the expense category keys; amounts positive Money; permission finance.budget.manage (Admin, FinanceOfficer); alert check runs on a timer (default 15 min, 0 = off) and via admin endpoint
## Out of scope
- Financial statements (#71), approvals/four-eyes on budgets (#72), budget revisions history beyond audit
## Follow-ups
- Confirm the July-June calendar and chart with the auditor once #112 is answered; changing the start month later would relabel future years only (postings keep their stored label)
- Reopening a closed year and approval of budget changes: #72
- Statutory/financial statements built on budgets and the ledger: #71
- Budget alerts need the real email sender (BE-032/033); until then they are rows, logs and outbox entries
