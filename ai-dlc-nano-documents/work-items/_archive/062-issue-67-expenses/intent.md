# Expenses: submission, approval, reimbursement, rollups (BE-062)
- Source: issue #67 https://github.com/DevCastBD/KJWebsite.Backend/issues/67 (milestone 5, P4)
- Docs: docs/LEDGER.md, docs/OFFLINE_DONATIONS.md; FE contract GET|POST /expenses, POST /expenses/{id}/approve|reject (src/admin/finance/api.ts)
- Type: feature (high blast radius: money out, segregation of duties)
## Request
- Submit expenses with receipts, category and project coding; configurable amount-based multi-step approval; posting on approval; reimbursement tracking; per-project rollups reconciling with the ledger; submitter can never approve (server-side).
## Decisions
- Approvals: count of distinct approvers by amount tier per currency (BDT <10,000:1, <100,000:2, else 3; USD 100/1,000, GBP 80/800, EUR 90/900), configurable; approver is never the submitter and never approves twice; any rejection ends it (user)
- Posting: accrue on final approval Dr expense account by category / Cr payable (2020 reimbursement, 2100 vendor); separate pay step (Dr payable / Cr cash/bank/wallet) marks it paid, by someone other than the submitter (user)
- Assumed: categories configured (programme, fundraising, administrative, events, other) mapped to accounts 5010/5020/5030/5050/5900, matched case-insensitively from the FE's free text, unknown -> 400; optional projectSlug (opaque) and campaign-free; receipts reuse the proof store (PDF/JPEG/PNG/WebP, 5 MB, hash, retention) and at least one is required before the first approval; expense records, approvals, receipts and payments are append-only; FE statuses pending|approved|rejected (+ paid flag); permissions finance.expense.submit (existing), finance.expense.approve (existing), new finance.expense.pay; approval-in-progress is `pending` with approvals n/required; rollups by project and category from expenses, reconciled against ledger postings sourced by expense id; submitter name from email claim else id
## Out of scope
- Budgets (#68), thresholds for ledger adjustments and refund workflow (#72), vendor master data
## Follow-ups
- Budgets (#68) should reuse the category keys; budget-vs-actual reads approved expenses
- Amount thresholds for ledger adjustments, refund workflow and a two-person rule on reversing an approved expense: #72
- Receipts go through the Finance proof store, not the media pipeline (#41); object storage per ADR-006 later
- Expense edit/withdraw by the submitter is deliberately absent: reject and resubmit
