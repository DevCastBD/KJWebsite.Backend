# Donor gift history shows the designated student name (BE-111, #225)
- Source: issue #225 https://github.com/DevCastBD/KJWebsite.Backend/issues/225 (part of epic #208, depends on #215, merged)
- Type: feature (children's data, high blast radius)
## Request
- Add `studentName` (preferred name) and the name in the label for a student gift in `GET /api/v1/donors/me/donations`, only while the donor can see that student under `/api/v1/donors/me/students`.
- Active started link: live name. Ended link: frozen copy name. Never linked, mistaken, cancelled before start, another donor's student: no name and the identical plain label.
- Sponsorship unreachable: 200 with the plain label.
## Decisions
- New donor-side lookup `GET /api/v1/donors/me/students/names?ids=` in Sponsorship (GET only, keeps the 405-on-writes rule); Finance calls it with the donor's own bearer → plan approved by user 2026-11-17.
- Label with a name: `Student sponsorship: {name}` / bn `শিক্ষার্থী পৃষ্ঠপোষকতা: {name}`; plain label otherwise → in the approved plan (default, user may change).
- `designationId` stays null for student gifts; names stay out of receipts and PDFs → approved plan.
## Out of scope
- Staff-side names; service credentials (BE-093); Identity-direct disable window (#229).
## Follow-ups
- (none yet)
