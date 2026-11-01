# Receipts, numbering, bilingual PDF, statements (BE-059)
- Source: issue #64 https://github.com/DevCastBD/KJWebsite.Backend/issues/64 (milestone 5, P4)
- Docs: docs/DONATIONS.md, docs/LEDGER.md, FE contract receiptUrl in donation status
- Type: feature (high blast radius: legal/audit document series)
## Request
- Sequential gapless tamper-evident receipt numbers under concurrency; bilingual PDF with legal details; automatic email via outbox; re-issue preserves the original and is audited; annual per-donor statements.
## Decisions
- Series: one gapless series per fiscal year KJR-2026-27-000001, each receipt hash-chained to the previous (user)
- Statements: per donor (keyed hash of email) per fiscal year; admin PDF + queue to outbox once per donor/year (user)
- Assumed: receipt issued in the same commit as confirmation (all paths) with a retry on number conflict; number = max+1 with unique index (nothing is ever deleted so no gaps); a receipt snapshot (donor, amounts, designation, org details) is stored so the PDF is reproducible; re-issue keeps the same number, adds an append-only reissue row and marks the PDF DUPLICATE; reversing a donation voids its receipt by an append-only void row (PDF shows VOID); legal entity details come from config placeholders (#114 open) and the PDF carries whatever is set; public receipt at GET /api/v1/donations/{reference}/receipt.pdf and receiptUrl on status; email via a Finance notification_outbox (no sender exists yet, like membership); Bengali numerals line + embedded Noto Sans Bengali via QuestPDF; donor key HMAC secret in config (required outside Development)
## Out of scope
- A real email sender (outbox rows only), donor CRM (#65), tax-exemption wording pending #114
## Follow-ups
- Legal receipt content (registration number, tax wording) needs #114; set Finance:Receipts:Organisation before go-live
- Receipt/statement email sending waits on the email sender (BE-032/033); rows sit in the Finance outbox
- PDF text (Bengali shaping) is only checked by embedded font presence; eyeball a real receipt before launch
- Donor key secret must be set and kept stable (Finance:Receipts:DonorKeySecret); donor CRM (#65) may replace the email-hash grouping
