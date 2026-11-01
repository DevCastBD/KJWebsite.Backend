<!-- phase: DONE | branch: Feature/issue-70-banking (proposed, stacked on Feature/issue-68-budgets) | tasks: 9/9
     base: b4436c7 | updated: 2026-11-01
     next: await plan + branch approval -->
# Plan: Banking and reconciliation (BE-066, #70)
## Tasks
- [x] Entities + migration (PG triggers): bank_accounts (mutable), bank_statements, bank_lines, bank_match_events, bank_reveals (append-only), bank_line_links (mutable current state, unique line and entry); permissions
- [x] CSV importer (mapping, header-synonym detection, tolerant numbers/dates, quotes) and OFX importer (SGML/XML), shared row model + fingerprints
- [x] Account registry endpoints: create/list/update/archive, masked number, reveal with log
- [x] Statement import endpoint: all-or-nothing report, duplicates reported, file-once by hash, then auto-match
- [x] Matcher: candidates on the bank's ledger account (side, amount, currency, window), mutual uniqueness, reference narrowing; re-run endpoint
- [x] Reconciliation endpoints (FE shape): GET /reconciliation, match, unmatch, post-and-match, dismiss, summary (statement vs ledger balance)
- [x] Audit config, docs/BANKING.md, openapi regen, map
- [x] Tests: CSV/OFX variants and every rejection class, dedupe, auto-match cases (unique, ambiguous, window, reference, mutual), manual/post/dismiss/unmatch rules, masking/reveal/log, payout posting reconciles with a statement line, permissions, PG triggers + unique entry
- [x] Full suite, format, OpenAPI verify, coverage check
## Tests
- High blast radius: parser corner cases, matcher determinism, one-ledger-entry-one-line guarantee on PG
## Files
- src/Services/FinanceService/Banking/**, Data/**, Migrations/**, Program.cs; BuildingBlocks Permissions; tests/**; docs/**; openapi.v1.yaml
