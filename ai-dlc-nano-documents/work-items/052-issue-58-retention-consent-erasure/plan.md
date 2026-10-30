<!-- phase: DONE | branch: Feature/issue-58-retention-consent-erasure | tasks: 7/7
     base: 33222a9 | updated: 2026-10-31
     next: none (done) -->
# Plan: Retention, consent, erasure
## Tasks
- [x] Consent ledger consent_records (subject, purpose, granted, at, source, actor, notice version); intake writes application_processing; portal/terminate write it; replaces member_consent_events (data copied); export shows it
- [x] Retention: Retention:* month windows (unset=keep), RetentionPurger (delete application+files+outbox, non-identifying run/item records), hosted worker, admin POST run (dryRun) + GET runs (privacy permission)
- [x] Erasure: requests for members and applicants; admin list/execute: Identity anonymise endpoint, member anonymised, application/attachments/photo/outbox/changes deleted, erasure record
- [x] Audit: ignore PII in Member + Identity audit; PG trigger migration allowing only ActorEmail/Ip/UserAgent nulling; redaction on erasure
- [x] Applicant data export (reference+email) + erasure request; GET public privacy-behaviour
- [x] Tests (purge boundary, unset window keeps, dry run, erasure completeness incl. Identity + audit redaction, consent ledger, export), docs PRIVACY_BEHAVIOUR.md, openapi, PG, commit
## Tests
- High blast radius (deletion, PII): boundaries, idempotence, nothing identifying left, PG trigger
## Files
- MembershipService (Privacy/), AuthIdentityService, BuildingBlocks (audit), ApiGateway, tests, docs
