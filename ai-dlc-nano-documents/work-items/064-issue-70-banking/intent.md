# Banking: account registry, statement import, assisted reconciliation (BE-066)
- Source: issue #70 https://github.com/DevCastBD/KJWebsite.Backend/issues/70 (milestone 5, P4)
- Docs: docs/LEDGER.md, docs/PAYMENT_GATEWAYS.md (payout matcher is the gateway side of the same proof); FE contract GET /reconciliation, POST /reconciliation/match|unmatch (src/admin/finance/api.ts)
- Type: feature (high blast radius: proves the books against the bank)
## Request
- Bank account registry; CSV/OFX statement import tolerant of format variation; automatic matching of common cases; unmatched queue resolved by people; unparseable rows reported never skipped; account numbers masked and access-restricted.
## Decisions
- Import is all-or-nothing: any unparseable row rejects the file with a full list (row number + reason); already-imported rows are reported as duplicates, never silent (user)
- Unmatched lines: match to a ledger entry, post-and-match (against another account), or dismiss with a reason; all audited; unmatch reverses a match or dismissal (user)
- Assumed: a bank account maps to a ledger asset account (default 1020; specific accounts can be created) so matching works on ledger entries on that account (deposit = debit, withdrawal = credit, same currency, exact amount, within +-5 days, one candidate both ways, or narrowed by reference found in the posting memo); gateway payout postings land on the same account so payouts reconcile through the same path; CSV per-account column mapping with header-synonym detection when none is set, tolerant number/date parsing (thousands separators, parentheses, Dr/Cr suffix, Bengali digits); OFX 1.x SGML and 2.x XML, currency must match; a statement file imports once (hash); overlapping statements dedupe by a per-account line fingerprint with occurrence counters; account number stored encrypted + masked (last 4), revealed only with finance.bank.reveal (Admin) and every reveal logged; current links mutable with an append-only event history; permissions finance.bank.manage (new), finance.bank.reveal (new), finance.reconcile (existing, now also FinanceOfficer), post-and-match needs finance.ledger.adjust
## Out of scope
- Live bank feeds/APIs, multi-currency transfers, cheque clearing workflows, automatic posting of bank charges
## Follow-ups
