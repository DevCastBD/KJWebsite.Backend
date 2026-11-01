<!-- phase: DONE | branch: Feature/issue-63-offline-donations | tasks: 8/8
     base: dadecc5 | updated: 2026-10-31
     next: none (wrapped up) -->
# Plan: Offline donations with maker-checker (BE-058, #63)
## Tasks
- [x] Entities + migration (append-only, PG triggers): offline_records, offline_decisions (one per record), proofs; seed account 1250 + in_kind clearing; permissions
- [x] Proof store: IProofStore + local disk, rules (type/size/signature/name), retention date
- [x] Domain: validation (method, date <= today, in-kind valuation, designation string), OfflineDonationService (record, approve, reject) with different-person rule
- [x] DonationConfirmation/MarkAsync: posted-on date + same-commit extra entities hook
- [x] Endpoints: POST donations (FE shape), proof upload/list/download, approve, reject, detail section; list shows method/offline
- [x] Audit config, docs/OFFLINE_DONATIONS.md, DONATIONS/LEDGER notes, openapi regen, map
- [x] Tests: record/approve/reject, same-person refused (approve and via second token), proof rules (type/size/signature/missing), in-kind rules + posting, closed-period date, replay approve once, audit, permissions, PG triggers
- [x] Full suite, format, OpenAPI verify, coverage check
## Tests
- High blast radius: integration (SQLite) incl. separation-of-duties, ledger effects, proof handling; PG trigger test
## Files
- src/Services/FinanceService/Offline/**, Data/**, Migrations/**, Ledger/**, Donations/*, Program.cs; BuildingBlocks Permissions; tests/**; docs/**; openapi.v1.yaml
