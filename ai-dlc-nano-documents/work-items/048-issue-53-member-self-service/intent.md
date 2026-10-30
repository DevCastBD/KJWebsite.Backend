# Member self-service (BE-046, #53)
- Source: issue #53 https://github.com/DevCastBD/KJWebsite.Backend/issues/53 (milestone 4 / P3)
- Docs: frontend contract KJWebsite.FrontEnd src/portal/api.ts + types.ts (/api/v1/members/me/*); ADR-014 (PII, consent, export)
- Type: feature
## Request
- Profile view/edit: phone, address, expertise direct; firstName, lastName, nameBn, dateOfBirth, bloodGroup queue for moderation; reviewer approve/reject
- Per-field opt-in consents (name, photograph, expertise; default false), notification prefs, photo upload, sessions, export of everything held, deletion request, membership card PDF/QR (Bengali)
- A member can only ever reach their own data (subject claim, no ids in paths)
## Decisions
- Card: PDF now (user): QuestPDF Community licence + bundled Noto Sans Bengali (OFL); QR via QRCoder (MIT)
- campaigns/current gets the frontend's fields additively (user), separate commit
- (approved) profile source of truth moves to Membership (Member row, PII data-protected); Auth profile stays the provisioning snapshot; sessions served from Auth at /members/me/sessions
## Out of scope
- Executing erasure and retention (#58), directory publication (#54), public card verification endpoint
## Follow-ups
