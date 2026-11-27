# Volunteer operations (BE-072, #76)

Lives in the Membership service (`src/Services/MembershipService/Volunteering/`), because hours and skills belong to members. Organiser calls need `events.manage` (the EventManager role); a volunteer uses their own member token (`member.profile.edit`), through `/api/v1/members/me/volunteer/...`.

## Hours
`POST /api/v1/members/me/volunteer/hours` (`projectSlug`, `date` not in the future, `minutes` 1 to 1440, `description`; optional `eventSlug`) creates a `submitted` entry. **Nothing counts until approved**, and never by the member who logged it (`403 SELF_REVIEW`).
- Organiser: `GET /api/v1/admin/membership/volunteer/hours?status=&project=&memberId=`, `POST .../hours/{id}/approve`, `POST .../hours/{id}/reject` (a reason is required).
- Volunteer: `GET .../me/volunteer/hours` shows each entry with its status and the **rejection reason**, plus approved/pending totals. A `submitted` or `rejected` entry can be corrected (`PUT`, back to review) or withdrawn (`DELETE`); an approved one is locked.

## Rollups and provenance
`GET /api/v1/admin/membership/volunteer/rollups?by=project|member&project=` sums **approved entries only**, with pending counts. Each row carries `fingerprint`: a hash of the approved entry ids and minutes behind that figure, so a published total (impact metrics, BE-085) can be recomputed from, and traced to, those entries. Per-project and per-member rollups come from the same rows, so they agree.

## Opportunity board and shifts
- Organiser: `POST/PUT /api/v1/admin/membership/volunteer/opportunities` (bilingual text, `projectSlug`, `skills`, `isOpen`, `shifts` with `slots`), `GET .../opportunities`.
- Volunteer: `GET .../me/volunteer/opportunities` lists open opportunities with upcoming shifts, free slots and a `matchScore` against the member's own expertise (matched on comma/semicolon-separated terms, case-insensitive). `POST/DELETE .../me/volunteer/shifts/{shiftId}/signup`. The last slot goes to one person: the shift's sign-up count is a concurrency token, the loser gets `409 SHIFT_FULL`; signing up twice is idempotent.
- Consent: a member sees their own match whatever their consent, but the organiser suggestion list (`GET .../opportunities/{id}/matches`) only includes active members who consented to publish expertise.
