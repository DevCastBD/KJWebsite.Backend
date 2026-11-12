# Sponsorship module: students, guardian consent, photos, donor view (BE-106)
- Source: issue #213 https://github.com/DevCastBD/KJWebsite.Backend/issues/213 (epic #208, decisions #209, contract docs/SPONSORSHIP.md)
- Type: feature
## Request
- New Sponsorship module with its own schema (D10 default) and staff endpoints under /api/v1/admin/sponsorship.
- Students with encrypted personal fields, programme as a project reference, bilingual story, ETag and If-Match.
- Append-only guardian consent; granting needs the signed form first; private photo (2 MB) and form (5 MB) stores.
- donor-view preview built on one donor allow-list (D2) that #214 reuses; module audit feed.
## Decisions
- User, 2026-11-12: no approval stops for this ticket; plan, branch, push and PR are approved; never merge.
- Module shape: new SponsorshipService (port 7006, schema sponsorship, gateway cluster); children's data stays out of Finance.
- Project reference: slug held locally, checked against Content over HTTP (404 gives 400 on programme, unreachable gives 503).
- Photo needs recorded consent (409 CONSENT_REQUIRED, per docs/SPONSORSHIP.md); withdrawal hides photo and story but keeps the file.
- D12 default applied: reads of the student detail (guardian contact) are written to the audit log as `read` events.
- No student delete endpoint; links (empty list) and the sponsored filter wait for #214.
- Metadata stripping is a small structural JPEG/PNG walker (no image library); malformed files are refused with 415.
## Out of scope
- Links, unlink, frozen copies, donor endpoints, donor notice email (#214); student designation (#215); privacy suite (#216).
## Follow-ups
- Student search decrypts names in memory; add a search hash if the list grows large.
- Photos and forms sit on local disk with no virus scan (same gap as membership attachments, #41).
- Consent append-only rule is enforced in code only; no database trigger yet.
- Postgres migration and a KJ_TEST_POSTGRES test not run here.
