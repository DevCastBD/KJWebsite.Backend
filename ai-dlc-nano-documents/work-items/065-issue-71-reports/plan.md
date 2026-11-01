<!-- phase: DONE | branch: Feature/issue-71-reports (proposed, stacked on Feature/issue-70-banking) | tasks: 8/8
     base: d974484 | updated: 2026-11-01
     next: await plan + branch approval -->
# Plan: Financial reports (BE-067, #71)
## Tasks
- [x] ReportBuilder: trial-balance-style aggregates and income statement, balance sheet (assets = liabilities + equity + surplus), direct cash flow (opening + movement = closing), per currency
- [x] Donation summaries (channel, campaign, period), project cost report, donor aggregate report; labels joined, amounts from the ledger
- [x] Writers: CSV (BOM, CRLF, formula guard) and multi-sheet XLSX (Bengali safe); auditor pack workbook
- [x] Entity ReportPack + migration (append-only), store bytes in the proof store with hash; permissions
- [x] Endpoints: live reports (json/csv/xlsx), POST|GET /reports (FE shape), signed download, auditor pack
- [x] Audit config, docs/REPORTS.md, openapi regen, map
- [x] Tests: each statement reconciles to the ledger and to the trial balance, cash flow proof, Bengali round trip in CSV and XLSX, formula guard, frozen pack unchanged after later postings, signed link expiry/tamper, permissions, performance with thousands of postings, PG
- [x] Full suite twice with PG, format, OpenAPI verify
## Tests
- Reconciliation invariants are the core; XLSX read back with System.IO.Compression to check content and encoding
## Files
- src/Services/FinanceService/Reports/**, Data/**, Migrations/**, Program.cs; BuildingBlocks Permissions; tests/**; docs/**; openapi.v1.yaml
