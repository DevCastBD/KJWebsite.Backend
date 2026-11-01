<!-- phase: DONE | branch: Feature/issue-69-ledger | tasks: 9/9
     base: c25e550 | updated: 2026-10-31
     next: none (wrapped up) -->
# Plan: Double-entry ledger (BE-065, #69)
## Tasks
- [x] Domain: FiscalCalendar, PostingDraft/LedgerLine validation (>=2 lines, one-sided positive Money, per-currency debit==credit), account types
- [x] Entities + migration: Account, Posting, LedgerEntry, PeriodClose (append-only guards, SaveChanges invariants, PG triggers incl. deferred balance + closed-period)
- [x] LedgerService: Stage/Post (idempotent by source, period + account checks), Reverse (linked, once), ClosePeriod, TrialBalance
- [x] Seeded NPO chart at startup (idempotent) + accounts API (list/create/archive)
- [x] Donation posting: DonationConfirmation (event + posting in one txn, replay-safe) and #59 reversal also reverses the posting
- [x] Ledger API: GET ledger (FE shape), posting detail, manual adjustment, reversal, periods + close, trial balance; permissions finance.ledger.adjust / finance.period.close
- [x] Audit config, docs/LEDGER.md, DONATIONS.md note, openapi regen, codebase-map
- [x] Tests: domain + property (random balanced postings keep trial balance balanced), service, API, replay x10, closed period, append-only, PG triggers
- [x] Measure coverage of ledger domain (>= 90%)
## Tests
- High blast radius: unit + property + integration (SQLite) + PG-gated trigger test; full suite, format, OpenAPI --verify; coverage check on FinanceService/Ledger
## Files
- src/Services/FinanceService/Ledger/**, Data/**, Migrations/**, Donations/*, Program.cs; BuildingBlocks Permissions; tests/**; docs/LEDGER.md; openapi.v1.yaml
