<!-- phase: DONE | branch: Feature/issue-64-receipts | tasks: 10/10
     base: 357517e | updated: 2026-10-31
     next: none (wrapped up) -->
# Plan: Receipts and statements (BE-059, #64)
## Tasks
- [x] Entities + migration (append-only, PG triggers): receipts (unique series+number, hash chain), receipt_voids, receipt_reissues, notification_outbox, statement_sends (unique donor/year)
- [x] ReceiptNumbering + chain hash + donor key (HMAC), options (org details, donor key secret)
- [x] Issue in the same commit as confirmation (retry on conflict); void on donation reversal; email outbox row when donor has an email
- [x] PDF rendering (QuestPDF, embedded Bengali font, bn digits, DUPLICATE/VOID marks, org details)
- [x] Endpoints: public receipt.pdf + receiptUrl on status; admin list/detail/pdf, re-issue (reason, audited), verify chain
- [x] Statements: per donor per fiscal year aggregation, admin list + PDF, queue-to-outbox once per donor/year
- [x] Permissions finance.receipt.reissue (Admin, FinanceOfficer); audit config
- [x] Docs (docs/RECEIPTS.md), openapi regen, map
- [x] Tests: concurrent gapless numbering (many parallel confirmations on PG + sequential SQLite), chain verify + tamper detect, PDF text incl Bengali, re-issue, void, statements totals, outbox, permissions, PG triggers
- [x] Full suite, format, OpenAPI verify, coverage check
## Tests
- High blast radius: concurrency on PostgreSQL, hash chain, PDF content checks, aggregation properties
## Files
- src/Services/FinanceService/Receipts/**, Data/**, Migrations/**, Ledger/DonationPostings.cs, Donations/*, Program.cs, csproj (QuestPDF, font); tests/**; docs/**; openapi.v1.yaml
