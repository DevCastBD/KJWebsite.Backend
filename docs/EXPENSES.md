# Expenses (BE-062, #67)

## Flow
1. **Submit** (`finance.expense.submit`): `POST /api/v1/admin/finance/expenses` with `{payee, description, category, amount, currency, project?, date?, reimbursement?}` (the frontend's body plus optional detail). `category` is one of the configured keys (default: programme, fundraising, administrative, events, other), matched without regard to case, and maps to a ledger expense account (5010, 5020, 5030, 5050, 5900). `project` is an optional project slug for rollups. `reimbursement: true` means the money is owed to the person who paid out of pocket. `date` is when it was incurred (not in the future, not older than two years). The expense is `pending` and the number of different approvers it needs is **fixed now** from the amount tier.
2. **Receipts**: `PUT .../expenses/{id}/receipts` (raw file, PDF/JPEG/PNG/WebP up to 5 MB, content-checked, up to ten, SHA-256 and retention date kept, nothing can delete one) while it awaits a decision. At least one is required before the first approval (`Finance:Expenses:ReceiptRequired`, default true). `GET .../receipts/{id}/file` downloads one.
3. **Approve** (`finance.expense.approve`): `POST .../approve`. **The submitter cannot approve (or reject) their own expense** (`403 SEPARATION_OF_DUTIES`), however many permissions they hold: the check is the signed-in user id against the submitter's, on the server. Nobody approves twice; asking again changes nothing. The expense becomes `approved` when as many **different** people as its tier requires have approved; one **rejection** (with a note) ends it. A rejected expense is submitted again, never edited.
4. **Posting**: the final approval, the decision and the ledger posting are saved in **one commit**: Dr the category's expense account / Cr payables (`2020` for a reimbursement, `2100` for a vendor), dated the day it was incurred, or today if that month is closed. Rejected and part-approved expenses post nothing. If two last approvals race, the decision is still recorded once (unique per expense) with one posting.
5. **Pay** (`finance.expense.pay`): `POST .../pay {method: bank|cash|cheque|bkash|nagad, reference?, paidOn?}` for an approved expense, once, and **not by its submitter**. Posts Dr payable / Cr cash, bank or mobile-wallet account and marks the expense `paid`; approved-but-unpaid is visible as the payable balance.

## Approval tiers (configurable)
Per currency, ordered by `Below`, under `Finance:Expenses:Approvals:<CUR>`; an expense falls in the first tier whose `Below` exceeds its amount, the open-ended tier otherwise. Defaults:

| Currency | 1 approver | 2 approvers | 3 approvers |
|---|---|---|---|
| BDT | under 10,000 | under 100,000 | 100,000 and over |
| USD | under 100 | under 1,000 | 1,000 and over |
| GBP | under 80 | under 800 | 800 and over |
| EUR | under 90 | under 900 | 900 and over |

A currency with no defaults gets the strictest tier. Configured tiers replace a currency's defaults. Categories are configurable the same way (`Finance:Expenses:Categories:<key>` = account code).

## Rollups and reconciliation
`GET .../expenses/rollups[?from&to]` gives approved spend (and how much is paid) per project and per category, per currency (never added together); pending and rejected expenses are out. `GET .../expenses/rollups/reconciliation` compares each project's and category's approved total with the net debit the ledger holds on expense accounts for the same expenses (reversals netted); every difference is zero when the books agree.

## Records
Expenses, approvals, the decision, receipts and the payment are append-only (application guard and PostgreSQL triggers; approvals, the decision and the payment are unique in the database). Every step is audited with its own actor. Budgets (BE-064) and the four-eyes controls on ledger adjustments (BE-068) build on this.
