<!-- phase: PLAN | branch: feature/issue-34-cta-read-apis | tasks: 0/5
     base: — | updated: 2026-10-30
     next: await plan approval, then branch feature/issue-34-cta-read-apis (feature/issue-32-email-outbox) -->
# Plan: CTA read APIs, export, anti-spam
## Tasks
- [ ] Contracts, endpoints, filters, paging; PATCH + audit trail
- [ ] CSV export with UTF-8 BOM, Bengali intact
- [ ] Honeypot/timing anti-spam; optional Turnstile verifier
- [ ] Permission + migration if needed; tests incl. authz, CSV, spam rejection
- [ ] openapi regen, docs, full suite + format
## Tests
- High blast radius (PII + authz): authz and audit paths tested
## Files
- src/Services/CtaSubmissionService/**, tests, openapi, docs
