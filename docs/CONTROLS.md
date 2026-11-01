# Financial controls (BE-068, #72)

The controls are enforced **on the server, always**. A UI that hides a button is a convenience, not a control: every rule below holds for any caller of the API, and the key ones are enforced again by PostgreSQL.

## 1. Segregation of duties
- **Expenses** (BE-067): whoever submitted an expense can never approve, reject or pay it, even holding every permission; approvals come from distinct people, in tiers by amount (1 approver below 10,000 BDT, 2 from there; configurable). Proven by `TheSubmitterCanNeverApproveRejectOrPayTheirOwnExpenseEvenHoldingEveryPermission`. Unique indexes make a second approval, decision or payment impossible even under concurrency.
- **Everything else sensitive** goes through one mechanism below.

## 2. Four-eyes: requests and approvals
A sensitive action is a **request**; it takes effect only when a **different** person with `finance.control.approve` approves it. The kinds:

| kind | asked with | permission to ask | what an approval does |
|---|---|---|---|
| `adjustment` | `POST ledger/postings` | `finance.ledger.adjust` | posts the balanced adjustment, dated as asked |
| `posting_reversal` | `POST ledger/postings/{id}/reversal` | `finance.ledger.adjust` | appends the mirror posting |
| `refund` | `POST donations/{id}/reversal` | `finance.donation.reverse` | reverses the gift, its ledger posting and voids its receipt |
| `period_reopen` | `POST ledger/periods/{period}/reopen` | `finance.period.close` | removes the month's close |
| `fiscal_year_reopen` | `POST fiscal-years/{label}/reopen` | `finance.period.close` | removes the year's close |

Asking returns **`202`** with the request (the old direct endpoints no longer post: a direct path would defeat the control). The request is validated up front (accounts, balance, date, closed periods, "already reversed") and never changes. `GET controls/requests` (filters `status`, `kind`) and `.../{id}` show it, its outcome and what it produced (`postingId`, `donationId`).

- `POST controls/requests/{id}/approve` and `/reject {note?}` need `finance.control.approve`. **You cannot approve or reject your own request** (`403 SEGREGATION_OF_DUTIES`); the requester can `withdraw` it, nobody else can.
- **The action runs in the same database commit as the approval**, or not at all. If it can no longer be done (the month was closed since, the gift was refunded meanwhile) the approval fails with the reason and the request stays open to be rejected. Nothing is ever half done.
- **One decision per request** (unique key), so many approvers clicking at once post exactly once. Only one open request per posting, donation or period at a time.
- Approval is "one distinct approver" at every amount; an amount threshold for adjustments (two approvers above a limit) was considered and not built. Expense tiers are the amount-based control.
- PostgreSQL refuses a decision where the approver is the requester (or a withdrawal by someone else), and refuses to edit or delete requests, decisions and close events.

Role defaults: **FinanceOfficer** now holds `finance.ledger.adjust`, `finance.donation.reverse`, `finance.period.close` and `finance.control.approve` (so two different officers can act); Admin and SuperAdmin hold everything. A deployment with only one person holding approval rights cannot complete a request, by design.

## 3. Refunds and the gateway
An approved refund reverses in the ledger (never a delete) and is reconciled against the gateway's own refund record: `GET controls/refunds` shows each refund request with `reconciliation`: `awaiting_gateway` (approved, no signed refund notice yet; finance refunds at the gateway), `reconciled` (the signed Stripe/PayPal notice matches the donation's amount and currency), `amount_mismatch`, or `n/a` while not approved. `unexpectedNotices` lists refund notices for donations finance never approved a refund for (a refund someone did directly at the gateway, or a notice that arrived before approval). **No gateway refund API is called by this system**; finance refunds in the gateway's dashboard and the signed notice closes the loop.

## 4. Reopening a close
Closes are append-only; a month or fiscal year reopens only through an approved request, with a recorded reason. The removal of the close row is allowed by PostgreSQL only while an approved, distinct-person `reopened` event for it is on record (`close_events`: every close and reopen with who asked and who approved). Closing again adds a new close. Closing itself is a single-person action (`finance.period.close`) because it only restricts.

## 5. Audit
Every change to a financial record is written to the audit log in the same transaction with the actor and before/after (`GET audit`, `audit.view`). A test enumerates every entity of the finance model and fails if a new record type is neither audited nor on a short, justified allowlist (the donor-message outbox, the gateway replay guard and the refused-webhook log, which the system writes for anonymous callers). Requests are audited under the requester, decisions under the approver, and the posting an approval makes is audited under the approver's click while carrying the requester as `createdBy`.

## Go-live gate: external review (not done by this change)
The plan makes an external review of the financial controls a condition of go-live. This issue delivers the controls and this checklist; **the review itself has to be performed by an independent reviewer and recorded before launch**. Suggested scope:
1. Chart of accounts and the July-June fiscal calendar against the auditor's expectations (#112); statutory report layout (#114).
2. Role assignments: at least two people hold `finance.control.approve`; nobody who asks routinely approves their own work in practice (the system prevents it, process must too); `finance.bank.reveal` is limited to Admin.
3. Walk each flow end to end in a staging copy: adjustment, reversal, refund (with a real gateway refund and its notice), reopen and re-close, period close, fiscal-year close, expense tiers, offline donation maker-checker.
4. Confirm gateway adapters against live sandboxes (they are verified here only against recorded doubles) and that refund notices arrive signed.
5. Review the audit log for a sample month: every mutation has an actor and before/after.
6. Backups and restore of the finance database, since append-only evidence lives there.
