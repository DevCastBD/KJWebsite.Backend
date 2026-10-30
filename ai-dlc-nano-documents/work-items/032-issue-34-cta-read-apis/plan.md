<!-- phase: DONE | branch: feature/issue-34-cta-read-apis | tasks: 5/5
     base: a557685 | updated: 2026-10-30
     next: none; committed locally -->
# Plan: CTA read APIs, export, anti-spam
## Tasks
- [x] Contracts, endpoints, filters, paging; PATCH + audit trail
- [x] CSV export with UTF-8 BOM, Bengali intact
- [x] Honeypot/timing anti-spam; optional Turnstile verifier
- [x] Permission + migration if needed; tests incl. authz, CSV, spam rejection
- [x] openapi regen, docs, full suite + format
## Tests
- High blast radius (PII + authz): authz and audit paths tested
## Files
- src/Services/CtaSubmissionService/**, tests, openapi, docs
