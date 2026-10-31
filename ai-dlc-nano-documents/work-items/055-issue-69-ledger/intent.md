# Double-entry ledger, chart of accounts, append-only postings, period close (BE-065)
- Source: issue #69 https://github.com/DevCastBD/KJWebsite.Backend/issues/69 (milestone 5, P4)
- Docs: docs/DONATIONS.md; FE contract GET /ledger (src/admin/finance/api.ts adaptEntry)
- Type: feature (high blast radius: money, audit evidence)
## Request
- Chart of Accounts + postings (debits == credits per posting, proven by a trial balance); append-only, corrections are linked reversing postings.
- Automatic posting from donations (expenses/dues post through the same API when they exist); closed periods reject postings.
- No update/delete path on ledger entries; >= 90% coverage on the ledger domain; property-based money tests.
## Decisions
- Order change: #69 before #62 (#62 depends on #69); then #62,#61,#63,#64,#65,#66,#67,#68,#70,#71,#72 (user)
- Fiscal year: `Finance:Ledger:FiscalYearStartMonth`, default 7 (July-June); each posting stores its fiscal-year label when posted (user)
- Chart: seeded generic NPO chart, extendable; accounts can be added/archived, never deleted or retyped (user)
- Manual adjustment + monthly close in this item, Admin-only perms `finance.ledger.adjust`, `finance.period.close`; close is one-way; reversal of an old posting lands in the current open period (user)
- Assumed: posting model = Posting (header, unique source for idempotency) + LedgerEntry lines; per-currency balance, no conversion; donation confirmation posts Dr gateway clearing (amount+fee) / Cr donation income / Cr fee contributions; confirm/reverse live in one transaction via a service that #62/#63 call; the #59 reversal endpoint also reverses the posting
- Assumed: DB defence in depth on PostgreSQL (deferred balance trigger, closed-period trigger, append-only triggers)
## Out of scope
- Four-eyes on adjustments (#72), budgets (#68), statutory reports (#71), decision #112 (fiscal calendar/standard) skipped by user
## Follow-ups
- Posting lines cannot be added to a committed posting by the app; only the app (no trigger) stops it (docs/LEDGER.md)
- Reopen of a closed period and four-eyes on adjustments: #72
- Align the seeded chart and fiscal year with the auditor once #112 is answered
- Fixed: test factory ClearAllPools() killed other tests' SQLite connections (disposed-object flakes)
- Still flaky: tests asserting a body does not contain digits like "82"/"017" match timestamp digits (MembershipReviewTests, MemberDirectoryTests)
