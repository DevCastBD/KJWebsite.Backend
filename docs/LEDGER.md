# Double-entry ledger (BE-065, #69)

The books of `FinanceService` (schema `finance`). Every figure on a report derives from here, so the rules are enforced in three places: the domain (`PostingRules`), the `DbContext` (nothing unbalanced or edited can be saved) and PostgreSQL triggers.

## Model
- **Account** (chart of accounts): `code` (unique), `name`, `type` (asset, liability, equity, income, expense). A generic fund-accounting chart is seeded at startup (`LedgerAccounts.Seed`, idempotent): cash, bank, mobile wallets, one clearing account per gateway, refunds and reimbursements payable, net assets, donation and fee-contribution income, dues, expense categories. Accounts can be **added and archived**, never deleted, and their code and type never change (postings rely on them). Seeded accounts cannot be archived. No accounting standard is claimed (decision #112 is open): align the chart with the auditor before go-live.
- **Posting** = header (`Id` is the sequential posting number, `postedOn`, `periodKey` yyyy-MM, `fiscalYear`, `memo`, `sourceType` + `sourceId`, `reversesPostingId`, `reason`, `createdBy`). **LedgerEntry** = a line: one account, a `debit` or a `credit` (never both, never zero, never negative), a currency.
- **Per-currency**: debits equal credits in *each* currency; nothing is converted. A trial balance has one section per currency.
- **Fiscal year**: `Finance:Ledger:FiscalYearStartMonth` (default 7, July-June, label `2026-27`; 1 gives `2026`). The label is **stored on the posting**, so changing the setting later cannot rewrite history.

## Append-only
No code path updates or deletes a posting, a line or a period close.
- `FinanceDbContext.SaveChanges` refuses `Modified`/`Deleted` ledger rows, refuses lines that are not added together with their own new posting, and refuses a posting with fewer than two one-sided lines or any unbalanced currency.
- PostgreSQL: triggers refuse `UPDATE`/`DELETE` on `postings`, `ledger_entries`, `period_closes`; accounts cannot be deleted or change code/type; a check constraint keeps lines one-sided; deferred constraint triggers check at commit that every posting has two or more lines and balances per currency; a trigger refuses a posting dated in a closed period. (Adding a *line* to an already committed posting is stopped by the application only, not by a trigger.)
- A **correction** is a reversal: a new posting that mirrors the original with debits and credits swapped, dated today (the current open period), linked through `reversesPostingId`. A posting is reversed once; a reversal cannot be reversed (post an adjustment). Reversal ignores archived accounts so it is always possible.

## Idempotent posting
`sourceType` + `sourceId` is unique. Posting the same donation again returns the first posting and writes nothing, so a webhook replayed ten times (or two requests racing) yields one posting. `DonationConfirmation.ConfirmAsync` is the one place a donation becomes `confirmed`: it writes the `confirmed` event and the posting in **one commit** and is safe to repeat. Webhooks (BE-056) and offline recording (BE-058) call it.

## Donation posting
Dr the method's account (gateway clearing for `sslcommerz`, `bkash`, `nagad`, `stripe`, `paypal`; cash or bank offline) for amount + fee; Cr donations income (`4010` general, `4020` campaign/project) for the amount; Cr `4090` fee contributions for a covered fee. Expenses and dues use the same `LedgerService` when those modules exist (BE-062, dues). Reversing a donation (#59 endpoint) now also reverses its posting in the same commit and refuses with `NOT_POSTED` when none exists.

## Offline gifts
An offline donation is confirmed (and posted) only when a different user approves it (see OFFLINE_DONATIONS.md): Dr cash, bank or the in-kind account by method, Cr donation income, dated the day the gift arrived unless that month is closed.

## Receipts
Confirmation also issues the donation's numbered receipt in the same commit (see RECEIPTS.md).

## Payout posting
Importing a gateway payout (BE-056) posts each matched donation: Dr bank (net received), Dr `5040` payment processing fees, Cr the gateway's clearing account (gross), source `payout`, unique per payout and donation. Together with the confirmation posting this leaves the clearing account at zero once a donation is settled.

## Periods
Months. Closing adds a `period_closes` row: no posting or reversal dated in it is accepted afterwards (`PERIOD_CLOSED`, 409). Only a past or current month can be closed, once. There is no reopen yet: it arrives with the two-approver flow of BE-068.

## API (`/api/v1/admin/finance/ledger`)
| Endpoint | Permission |
|---|---|
| `GET /` lines (`from`, `to`, `account`, paging) in the frontend's shape: `id, postingId, date, account, memo, debit?, credit?, currency, reversalOf?, reversedBy?` | `finance.view` |
| `GET postings/{id}`, `GET trial-balance?asOf&currency`, `GET accounts`, `GET periods` | `finance.view` |
| `POST postings` manual adjustment `{postedOn?, memo, reason, lines:[{account, debit\|credit, currency}]}` | `finance.ledger.adjust` |
| `POST postings/{id}/reversal {reason}`, `POST accounts {code,name,type}`, `POST accounts/{id}/archive` | `finance.ledger.adjust` |
| `POST periods/{yyyy-MM}/close` | `finance.period.close` |

`finance.ledger.adjust` and `finance.period.close` are held by Admin and SuperAdmin only until the four-eyes controls (BE-068). Every write is audited with the caller (`GET /api/v1/admin/finance/audit`), except automatic postings made by the system with no signed-in caller.
