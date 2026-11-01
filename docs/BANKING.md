# Banking: accounts, statement import and reconciliation (BE-066, #70)

Proves the books against the bank. A statement (CSV or OFX) is imported, each line is matched to the ledger entry that explains it, and what cannot be matched sits in a queue that a person resolves. Every action is audited.

## Bank accounts
`GET|POST /api/v1/admin/finance/bank-accounts`, `PATCH .../{id}`. An account has a name, bank, currency and a **ledger account**: the asset account of the chart that holds this bank's balance (default `1020`, "Bank accounts"; create further asset accounts for more banks). Only one live bank account may use a ledger account, because matching works on that account's entries; archiving frees it. The ledger account is fixed once a statement has been imported.

The **number** is stored encrypted (data protection, as donor details are) and listed masked (`••••9012`). `GET .../{id}/number` returns it in full and needs `finance.bank.reveal` (Admin, SuperAdmin only); every read is written to an append-only reveal log. The audit log never contains the number.

Permissions: `finance.bank.manage` registers and edits accounts (Admin, SuperAdmin, FinanceOfficer), `finance.reconcile` imports and resolves (the same permission that already covers gateway payouts; FinanceOfficer now holds it), `finance.ledger.adjust` is additionally needed for post-and-match.

## Importing a statement
`POST .../bank-accounts/{id}/statements?format=csv|ofx` with the file as the request body (`X-Filename` is recorded; format is detected when omitted; at most `Finance:Banking:MaxFileBytes`, default 5 MB, 20,000 rows).

- **All or nothing.** If any row cannot be read the response is `422 IMPORT_REJECTED` with `errors:[{row, reason}]` listing every bad row, and nothing is stored. Rows count from the file's first line (CSV header is row 1; OFX transactions are numbered in order).
- **Duplicates are reported, never silent.** A line already imported from an earlier, overlapping statement is not stored again; it appears under `duplicates` with its row. Identity is a fingerprint of date, amount, description, reference and running balance plus an occurrence counter, so two genuinely identical lines on one day are both kept. The very same file cannot be imported twice (`409 ALREADY_IMPORTED`).
- The response also says how many lines were matched automatically and how many remain unmatched.

**CSV** tolerates variation. Without a mapping the header row is read against common names (date / transaction date / value date; description / narration / particulars / details; reference / ref no / chq no; amount, or debit/withdrawal and credit/deposit; balance), the delimiter is detected (`,` `;` tab `|`), a BOM and quoted fields with commas or line breaks are handled. An account can carry a **`csvMapping`** (`date`, `description`, `reference`, `amount` or `debit`+`credit`, `balance`, each a header name or 1-based position; `dateFormat` such as `dd/MM/yyyy`; `delimiter`). Amounts may carry thousands commas (1,234 or the lakh style 1,23,456), a currency mark (৳, BDT, Tk), brackets or a `Dr`/`Cr` suffix, and Bengali digits. A comma is only ever a thousands separator: `1,50` and `1.000,00` are rejected rather than misread, and so is any amount with more than two decimals (nothing is rounded). Dates are day-first when ambiguous. **OFX** 1.x (SGML) and 2.x (XML) are read as they come; the statement currency must equal the account's.

## Automatic matching
Runs after every import and on demand (`POST .../bank-accounts/{id}/rematch`). A line is matched to a ledger entry on the account's ledger account when: money in meets a **debit**, money out a **credit**; the amount and currency are exactly equal; the posting date is within `Finance:Banking:MatchWindowDays` (default 5) of the line; the entry is not matched to another line. A line is matched only if it has **one** candidate, and that entry has **no other claimant**, so the result does not depend on the order lines arrive in. When several entries fit, a reference found in the posting memo (the line's reference, or a reference-like token in its description) narrows them. Everything else stays in the queue. Gateway payouts post to the same account, so they reconcile through the same path.

## Resolving the queue
`GET /api/v1/admin/finance/reconciliation` returns `{lines, entries}` (the frontend's contract): lines (unmatched first, then newest; signed `amount`, `status` unmatched / matched / dismissed, `matchedEntryId`, `reason`) and the unmatched ledger entries on the bank accounts plus the entries the listed lines are matched to. Filters `account`, `status`, `limit`.

- `POST .../reconciliation/match {lineId, entryId}`: the entry must be on the account's ledger account, on the right side, for exactly the line's amount and currency, and free. A person may match across the date window.
- `POST .../reconciliation/unmatch {lineId}`: undoes a **match only**. A posting made by post-and-match stays in the ledger (reverse it there if it was wrong); posting again for the same line returns the same posting, never a second one.
- `POST .../reconciliation/post-and-match {lineId, account, memo?}`: for what the books lack (a bank charge, interest, an unrecorded deposit). Posts a balanced entry between the bank's ledger account and `account`, dated the line's date, under the ledger's rules (no future date, no closed period or fiscal year), then matches. Needs `finance.ledger.adjust`.
- `POST .../reconciliation/dismiss {lineId, reason}` sets a line aside with a reason (3 to 500 characters); `POST .../reconciliation/reopen {lineId}` puts a dismissed line back.

Each action writes an append-only event (who, when, what) and the current link row is unique per ledger entry, so one entry can never prove two lines, even under concurrent requests (the loser gets `409`).

## Proving the balance
`GET .../bank-accounts/{id}/summary`: the ledger balance, the running balance on the newest statement line (if the file has one), counts and net of unmatched lines and entries, dismissed lines, and `unexplained`: the bank balance less unmatched and dismissed lines, less the ledger balance less unmatched entries. It is `0.00` when every difference is an item in the queue; a non-zero figure is a balance that existed before the first imported statement or a line missing from the import.

## Database guarantees (PostgreSQL)
Statements, lines, match events and reveals are append-only (trigger `finance.refuse_ledger_change`); a line is never zero; a statement file is unique per account; a line is unique per account, fingerprint and occurrence; a ledger entry has at most one link; two live accounts cannot share a ledger account.

## Not in this version
Live bank feeds, automatic posting of bank charges, cheque-clearing workflows and multi-currency transfers. Other CSV layouts that need a mapping beyond the one above (for example two date columns) are handled by adding to `csvMapping`, not by code changes per bank.
