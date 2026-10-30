# Public application intake (BE-042, #49)
- Source: issue #49 https://github.com/DevCastBD/KJWebsite.Backend/issues/49 (milestone 4 / P3, run order BE-042 after skipped #109)
- Docs: ADR-014 (field-level encryption, no PII in logs); frontend contract in KJWebsite.FrontEnd src/api/endpoints/membership.ts
- Type: feature
## Request
- New MembershipService: POST /api/v1/membership/applications with the full legacy field set (+ language, Idempotency-Key) -> 201 {id, reference}
- POST .../check {email, phone} -> {duplicate}; warns, never blocks
- PUT .../{id}/attachments/{cv|photo} raw body + X-Filename; type/size limits as frontend states (cv 5MB pdf/doc/docx, photo 2MB jpeg/png/webp)
- PII minimised, encrypted at field level, never logged
## Decisions
- #109/#110 decisions skipped by user; no dues/member-type modelling here
## Out of scope
- Campaigns, public status lookup (#50), review pipeline (#51), provisioning (#52)
## Follow-ups
