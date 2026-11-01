# Financial reports and the auditor pack (BE-067, #71)

A finance officer produces a month's report without SQL: `POST /api/v1/admin/finance/reports {month}` builds and stores the pack; the other endpoints give each report live. **Everything is computed from the double-entry ledger** (one load of the ledger lines, every report cut from it), so the figures reconcile by construction and each report states the check it passed. Currencies are never mixed or converted: every statement has one section per currency.

## Reports
`GET /api/v1/admin/finance/reports/{name}?from&to&asOf&currency&format` (`finance.view`). `from`/`to` default to the current month, `asOf` (balances) to `to`; the range is at most about ten years. `format` is `json` (default), `csv` or `xlsx`. In JSON amounts are decimal strings and each report is `{title, from, to, asOf, checks, tables:[{name, headers, rows}]}`.

| name | what it shows |
|---|---|
| `income-statement` | income and expense by account for the period, per currency, and the surplus |
| `balance-sheet` | assets, liabilities, equity as of a date, with **accumulated surplus** (no year-end closing entries exist, so income less expense since the books began is its own line); check: assets = liabilities + equity + surplus |
| `cash-flow` | direct method: movement on cash, bank-account and wallet accounts (1010, 1020, 1030 and every registered bank account's ledger account), opening + movement = closing; check: closing equals the ledger balance |
| `donations` | by channel (the account the money landed in), designation (general / campaign / project) and month; reversals come off |
| `project-costs` | spend by project and category; postings that did not come from an approved expense are shown as `(manual postings)` so the total equals the income statement; check: total = total expenses |
| `donors` | counts only: donors, new and returning, gifts, total, average and largest gift, gifts with no donor record. No name, email or other personal detail |
| `trial-balance` | debits and credits per account as of a date; check: totals equal |
| `general-ledger`, `donation-register`, `expense-register` | the lines behind the figures, for the auditor |
| `auditor-pack` | every sheet above plus a summary |

Cash-flow buckets: each posting's cash movement goes to the bucket of the largest non-cash line on its other side (lowest account code on a tie): donations received, other income, expenses paid, gateway settlements, payables and refunds, other. The buckets add up to the movement exactly; a transfer between two cash accounts is not a movement.

Where a figure needs a label the ledger does not hold (the campaign of a donation, the project of an expense, the donor of a gift), the label is joined from the donation, expense or receipt by the posting's source id; the **amount** always comes from the ledger.

## Exports
- **CSV**: UTF-8 **with a byte-order mark** (Excel reads Bengali only with it), CRLF line ends, quoted where needed, amounts as plain two-decimal numbers. A text cell that starts with `=`, `+`, `-` or `@` is prefixed with an apostrophe so a spreadsheet never runs it as a formula. A report with several tables (the auditor pack) is a zip of CSV files.
- **XLSX**: a real workbook, one sheet per table, text stored as UTF-8 (Bengali exact), money as numbers formatted to two decimals, bold frozen header row, sheet names cleaned and unique. Control characters XML cannot carry are dropped. The bytes depend only on the content.

## Monthly packs (frontend contract)
- `POST /api/v1/admin/finance/reports {month: "2026-03"}` (`finance.report.generate`: Admin, SuperAdmin, FinanceOfficer) builds the auditor-pack workbook for that month, stores it and returns `{id, month, generatedAt, url, generatedBy, sha256, sizeBytes}`. A future month is refused; a month with nothing in it still gives a valid pack.
- `GET /api/v1/admin/finance/reports` lists packs, newest first, each with a fresh `url`.
- **The stored file never changes**: later postings change the live reports, not an old pack, so what a trustee or auditor was given can be shown again exactly. Generating again makes a new pack. Packs are append-only (application guard and a PostgreSQL trigger); the SHA-256 is checked before every download and a file that no longer matches is not served (`REPORT_CORRUPT`).
- `url` is absolute (the frontend only follows https links) and points at `GET /api/v1/finance/report-downloads/{id}?token=...` (routed by the gateway; deliberately outside `/admin`, where nothing may be anonymous), which needs **no bearer token** because a browser download link cannot send one. The token is a signed, expiring credential for that one report (`Finance:Reports:LinkMinutes`, default 15); list again for a new link. Set `Finance:Reports:PublicBaseUrl` to the public origin (for example `https://api.kolpojontro.org`) so links are built with it; without it the request's own origin is used. The pack's summary sheet names the organisation (`Finance:Reports:EntityName`, default "Kolpojontro"), the period, who generated it and every check result.

## Statutory format
The auditor's required layout depends on the legal entity (#112, #114, still open). This version ships a **generic auditor pack** (trial balance, general ledger, income statement, balance sheet, cash flow, donation and expense registers, summary). A specific legal format is added as another table set over the same ledger rows once it is decided; nothing here assumes one.

## Performance
Reports read the ledger once and aggregate in memory (SQLite cannot sum a decimal). A pack over 3,000 postings (6,000 lines) is generated well inside the 2 second budget in the test suite. The ledger is read up to the end of the period, so a very large history will need an aggregate query or a snapshot table.
