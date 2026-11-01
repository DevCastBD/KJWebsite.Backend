# Financial reports and statutory exports (BE-067)
- Source: issue #71 https://github.com/DevCastBD/KJWebsite.Backend/issues/71 (milestone 5, P4)
- Docs: docs/LEDGER.md, docs/BUDGETS.md, docs/BANKING.md; FE contract POST|GET /reports {month} -> {id, month, generatedAt, url} (src/admin/finance/api.ts, Reports.tsx; url must be absolute https and is used as a plain download link, so it cannot rely on an Authorization header)
- Type: feature
## Request
- Income statement, balance sheet, cash flow, donation summaries (channel/campaign/period), project cost report, donor report, auditor export; a finance officer produces a month's report without SQL; every report reconciles with the ledger; Bengali survives CSV/XLSX (BOM); within 2 s.
## Decisions
- A report is a frozen file pack: generating a month stores the XLSX and its SHA-256; list and download serve those exact bytes. Live JSON/CSV views also exist (user)
- Statutory export is a generic auditor pack (trial balance, general ledger, income statement, balance sheet, cash flow, donation and expense registers) with configurable entity name; a specific legal format waits for #112/#114 (user)
- Cash flow uses the direct method from the ledger (user)
- Assumed: amounts always from ledger entries; donation channel/campaign and expense project are only labels joined from the donation/expense rows by posting SourceId; per-currency sections, never converted; balance sheet shows accumulated surplus (no year-end closing entries exist) so assets = liabilities + equity + surplus; cash accounts = the 1010/1020/1030 family plus every bank account's ledger account; each posting's cash movement goes to one bucket (the largest counter line, lowest code on ties) so buckets sum to the total; donor report is aggregate only (counts, new vs returning, bands, top gift) with no personal data; download link is a short-lived signed URL (data protection time-limited) because a browser anchor sends no bearer token; new permission finance.report.generate (Admin, SuperAdmin, FinanceOfficer), viewing needs finance.view; CSV has UTF-8 BOM, CRLF and a guard against formula injection; XLSX written without a heavy dependency
## Out of scope
- Report builder UI (frontend F7-7), non-ledger KPIs, currency conversion, a legal-entity-specific statutory layout, year-end closing entries
## Follow-ups
