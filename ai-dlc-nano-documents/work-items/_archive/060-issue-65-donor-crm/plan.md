<!-- phase: DONE | branch: Feature/issue-65-donor-crm | tasks: 9/9
     base: 21b2f16 | updated: 2026-10-31
     next: none (wrapped up) -->
# Plan: Donor CRM (BE-060, #65)
## Tasks
- [x] Entities + migration: donors, donor_consents (append-only), donor_suppressions, donor_exports, donation.CommunicationConsent column; options
- [x] Segments: pure reproducible computation (new, lapsed, major, recurring) with asOf and configurable thresholds
- [x] DonorService: upsert at confirmation (same commit, non-anonymous with email), consent grant from the form, thank-you via the consent gate, suppression records
- [x] ConsentGate + DonorMessenger: central check used by thank-you and segment messaging; consent history API
- [x] Endpoints: donors list (FE shape) / detail / donations, consent change, segment message queue, CSV export with export log, ledger reconciliation
- [x] Permissions finance.donor.manage, finance.donor.export; audit config; donation form `communicationConsent` (public + offline)
- [x] Docs (docs/DONORS.md), openapi regen, map
- [x] Tests: anonymous creates nothing, upsert/merge by email, history + LTV exactly equal ledger, segments per rule and reproducibility by asOf, consent opt-in/withdraw/enforced on thank-you and messages, export content/filter/injection/log, permissions, PG triggers
- [x] Full suite, format, OpenAPI verify, coverage check
## Tests
- High blast radius for personal data: property tests on segments, reconciliation equality, consent enforcement paths
## Files
- src/Services/FinanceService/Donors/**, Data/**, Migrations/**, Ledger/DonationPostings.cs, Donations/*, Offline/*, Program.cs; BuildingBlocks Permissions; tests/**; docs/**; openapi.v1.yaml
