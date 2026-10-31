<!-- phase: DONE | branch: Feature/issue-68-budgets | tasks: 9/9
     base: 9d2c544 | updated: 2026-11-01
     next: none (wrapped up) -->
# Plan: Budgets and fiscal years (BE-064, #68)
## Tasks
- [x] FiscalCalendar.Range(label,startMonth); fiscal_year_closes + budgets + budget_alerts entities, migration (PG triggers: closes/alerts append-only; postings into a closed fiscal year refused)
- [x] Fiscal-year close in LedgerService (and the posting check FISCAL_YEAR_CLOSED); list/close endpoints, only after the year ends
- [x] Budget CRUD (FE-compatible GET /budgets?year), frozen when the year is closed; permission finance.budget.manage
- [x] Variance engine: planned vs ledger actual per line (project attribution via expense postings, reversals netted), percent used, remaining
- [x] Alerts: thresholds, once per line, rows + log + optional outbox emails; timer worker + admin run + list endpoint
- [x] Docs (docs/BUDGETS.md), openapi regen, map
- [x] Tests: year range math, close rules (before end refused, after end ok, one-way), closed year rejects postings (all paths), budget CRUD/validation/freeze, variance exactness incl project attribution/reversal/adjustment/other currency, alerts fire once at thresholds, config override, permissions, PG trigger
- [x] Full suite, format, OpenAPI verify, coverage check
## Tests
- Variance reconciles with the ledger by construction and by test; closed year blocks every posting path
## Files
- src/Services/FinanceService/Budgets/**, Ledger/**, Data/**, Migrations/**, Program.cs; BuildingBlocks Permissions; tests/**; docs/**; openapi.v1.yaml
