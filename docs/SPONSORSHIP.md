# Student sponsorship: decisions and API contract

## Status and sources
This document mirrors issue #209's decisions table: DECIDED rows are final, OPEN rows retain their defaults, and #209 is authoritative if sources differ; the contract comment on #208 defines the API agreement.
The frontend counterpart is DevCastBD/KJWebsite.FrontEnd issues #347 to #354 and ADR FE-116's Amendments section.
Issues #210 to #216 hold the acceptance criteria.

## Decisions

| ID | Decision | Status | Owner issue |
| --- | --- | --- | --- |
| D1 | Collect the default student fields, with separate English and Bengali stories and a project reference for programme. | DECIDED | #213 |
| D2 | Donors may see preferred name, class, school, programme, district and status; photo and story require consent, subject to ended snapshots; never expose full name, birth date, address, guardian details, notes or age. | DECIDED | #213, #214 |
| D3 | Allow one active donor per student. | DECIDED | #214 |
| D4 | Retain the proposed CRM additions: donor type, organisation, address, country, status and internal notes; final fields remain unresolved. | OPEN | #211 |
| D5 | Recorded gifts cannot be edited; correct them by reversal; allow student designation validated against active links. | DECIDED | #215 |
| D6 | Staff set a write-only initial password; new accounts must change it at first sign-in; no setup link or pending setup state; the API enforces the flag with 403 PASSWORD_CHANGE_REQUIRED except for change-password, logout and /auth/me; use the existing minimum 10-character policy and lockout after 5 failures for 15 minutes; the new password must differ from the current password. | DECIDED | #210, #211 |
| D7 | Email is required and is the only sign-in name; one account may hold member and donor roles; provisioning against an existing member email adds donor without changing its password or sessions or setting mustChangePassword. | DECIDED | #210, #211 |
| D8 | Use BE-069's public events API when available; the frontend keeps its static events until then. No backend work. | Frontend only | #209 |
| D9 | Admin and SuperAdmin hold sponsorship.view, sponsorship.student.manage, sponsorship.link.manage and finance.donor.provision; only SuperAdmin holds sponsorship.link.void; no new officer role; backfill the four non-void keys into existing Admin roles once and record completion. | DECIDED | #210 |
| D10 | Default to a new Sponsorship module with its own schema; keep children's data out of Finance books. | OPEN | #213 |
| D11 | Allow one staff-uploaded JPEG or PNG photo up to 2 MB; strip location metadata and support deletion. | DECIDED | #213 |
| D12 | Default to auditing staff reads of guardian contact details, but not donor reads. | OPEN | #213 |
| D13 | Agree a written retention and erasure policy covering student and donor data, frozen snapshots and signed consent forms before launch; extend #110. | OPEN | #209, #110 |
| D14 | Send a short donor notice when a student is linked, using Bengali and English templates, the email outbox and the donor consent gate. | DECIDED | #214 |
| D15 | Default to aggregate sponsored-student counts only, never public records; align with BE-085. | OPEN | #216 |
| D16 | Deny disabled accounts and inactive donors every donor endpoint with 403 on every request; inactive donors cannot sign in; return 403 ACCOUNT_DISABLED only after a correct password, otherwise use the generic authentication failure. | DECIDED | #210, #212 |

## API contract

### Shared conventions

- Publish this contract in `openapi.v1.yaml` and issues #209 to #216 under epic #208, including exact request/response fields, examples, permissions, success statuses and stable error codes.
- Validation failures return `400 VALIDATION_ERROR` with a per-field `errors` map; document each stable field error code.
- Writes carry `Idempotency-Key`; document replay behavior and any endpoint exceptions.
- Student and donor lists accept `limit` and `offset`, return `items`, `total`, `limit`, `offset`, and cap `limit` at 100; document defaults.
- Student and donor GET responses and every PATCH response return `ETag`; PATCH requires `If-Match`, returning `428` when missing and `412` when stale.
- Unspecified paths, payload names, filters and success statuses below require publication in OpenAPI; they are unresolved wire details, not permission to change the agreed behavior.

### Identity

Built in BE-103 (#210). Error bodies carry the code at `error.code`.

- `POST /api/v1/admin/auth/donors`, permission `finance.donor.provision`. Body `{ donorId, email, password }`. Creates an email-confirmed `Donor` account with the staff-chosen initial password and `mustChangePassword` set: `201 { userId, created: true, linkedToMember: false, mustChangePassword: true }`. The password is never returned. Idempotent per `donorId`: a repeat returns `200` with the same `userId` and does not touch the password. `400 VALIDATION_ERROR` for a missing donor id, a bad email, or a password that fails the policy.
- The same endpoint when the email belongs to a `Member` account: the `Donor` role is added, and the password, sessions and flag are untouched: `200 { created: false, linkedToMember: true }`. Any other existing account returns `409 EMAIL_TAKEN`.
- `PUT /api/v1/admin/auth/donors/{userId}/password`, permission `finance.donor.provision`. Body `{ password }`. Sets a new initial password for a donor who lost access, sets `mustChangePassword` again, clears a lockout and ends every session: `200 { userId, mustChangePassword: true }`. `400 VALIDATION_ERROR`, `404 NOT_FOUND`.
- `PUT /api/v1/admin/auth/donors/{userId}/status`, permission `finance.donor.provision`. Body `{ status: "active" | "disabled" }`. Disabling ends every session: `200 { userId, status }`. `400 VALIDATION_ERROR`, `404 NOT_FOUND`.
- Both `PUT` endpoints refuse an account that is not donor-only (a member who is also a donor, or any other account) with `409 ACCOUNT_SHARED`: membership staff own that decision. The member status and erase endpoints skip donor-created accounts (`404`).
- `POST /api/v1/auth/change-password`, any signed-in account, allowed while `mustChangePassword` is set. Body `{ currentPassword, newPassword }`. The new password must differ and meet the policy. Success clears the flag, ends every session and returns the same body as sign-in (a fresh token pair without the `pwd_change` claim). `400 VALIDATION_ERROR` (field named in the message), `401`, `423 ACCOUNT_LOCKED`: wrong current passwords count toward the sign-in lockout.
- The `POST /api/v1/auth/login` response and `GET /api/v1/auth/me` report `mustChangePassword` and `roles` (a list; the single `role` field stays). Profiles do not report roles.
- While `mustChangePassword` is set the access token carries `pwd_change: true`, and every endpoint of every service except change-password, `POST /api/v1/auth/logout-all` and `GET /api/v1/auth/me` returns `403 PASSWORD_CHANGE_REQUIRED`. A token issued before the change keeps the claim until it expires (15 minutes), but its session has ended.
- Sign-in returns `403 ACCOUNT_DISABLED` only after the correct password; a wrong password or an unknown email always returns the same `401`. A wrong guess at a disabled account does not count toward the lockout.
- Password policy is the platform's (minimum 10 characters, lockout after 5 failures for 15 minutes). Passwords are never returned, logged or audited.
- Permission keys: `finance.donor.provision`, `sponsorship.view`, `sponsorship.student.manage`, `sponsorship.link.manage`, `sponsorship.link.void`. Admin holds all but `sponsorship.link.void`; only SuperAdmin holds it. The migration `DonorIdentity` gives an already-seeded Admin role the four non-void keys once; a fresh database gets them from the seed. The `Donor` role holds no permission.

### Finance donors

- **BUILT (BE-104, #211)**: staff can create a donor before any gift; it appears with zero giving, no segments and an empty first-gift date. Receipts join on the stable donor key; offline gifts with `donorId` find the donor by id (#215), while gifts without it use the lookup hash of the current email. Correcting an email preserves history without editing receipts; a later gift without a donor id from the old address starts a different donor. Extending id attribution to online and legacy gifts is #226 (BE-112).
- `GET /api/v1/admin/finance/donors` (`finance.view`): returns the list, with `type`, `status` and `accountStatus` on each row. New query filters: `status=active|inactive`, `accountStatus=none|active|disabled`, `hasAccount=true|false`; unknown values return `400 VALIDATION_ERROR`.
- `GET /api/v1/admin/finance/donors/{id}` (`finance.view`): returns the donor, an `ETag` header and `profile: {type, organisation, address, country, notes, language, status, accountStatus, accountShared, mustChangePassword, createdAt}`. `mustChangePassword` comes live from Identity and is `null` without an account or when Identity cannot be reached.
- Profile request fields: `name` (required, max 200), `email` (required, valid, max 254), `phone` (optional, `+` followed by 7 to 15 digits), `language` (`bn|en`, default `bn`), `type` (`individual|organisation`), `organisation` (max 200, required for an organisation), `address` (max 500), `country` (two uppercase letters), `status` (`active|inactive`), `notes` (max 2000). Address and notes are encrypted.
- `POST /api/v1/admin/finance/donors` (`finance.donor.manage`): accepts the profile fields; returns `201` with an `ETag`. Errors: `400 VALIDATION_ERROR` listing the fields; `409 DONOR_EXISTS` when any donor has the email in any letter case, with the existing donor id in the message.
- `PATCH /api/v1/admin/finance/donors/{id}` (`finance.donor.manage`): accepts profile changes and requires `If-Match: "<version>"`. Omitted fields stay; optional text sent as `""` is cleared. Errors: `428 PRECONDITION_REQUIRED` without `If-Match`, `412 PRECONDITION_FAILED` when stale, `409 EMAIL_HAS_ACCOUNT` when changing an email with a sign-in account. Setting `status=inactive` first disables a donor-only sign-in; `503 AUTH_UNAVAILABLE` saves nothing. Shared member sign-ins stay enabled; reactivating a donor does not enable sign-in. Every change increases the version, including a gift refreshing contact details.
- `POST /api/v1/admin/finance/donors/{id}/login` (`finance.donor.provision`), body `{password}`: calls Identity (#210) with the staff member's own token; sign-in name is the donor email. Returns `201` with `{donorId, accountStatus, accountShared, mustChangePassword}`, or `200` when the Donor role is added to an existing member account (`accountShared=true`). Errors: `409 ACCOUNT_EXISTS`, `409 EMAIL_TAKEN` (another account kind), `409 DONOR_INACTIVE`, `400 VALIDATION_ERROR` (password policy), `503 AUTH_UNAVAILABLE` (nothing saved). The password is never returned, logged or audited.
- `PUT /api/v1/admin/finance/donors/{id}/login/password` (`finance.donor.provision`), body `{password}`: sets a new initial password in Identity, requires the donor to change it and ends sessions. Errors: `409 NO_ACCOUNT`, `409 ACCOUNT_SHARED`.
- `PUT /api/v1/admin/finance/donors/{id}/login/status` (`finance.donor.provision`), body `{status: active|disabled}`: changes sign-in status; disabling ends sessions. Errors: `409 ACCOUNT_SHARED`, `409 NO_ACCOUNT`, `409 DONOR_INACTIVE` when enabling for an inactive donor.
- `GET /api/v1/admin/auth/donors/{userId}` (`finance.view`): returns status, `mustChangePassword`, roles and `shared`; returns `404` for an account without the Donor role.
- The GET, PATCH and both PUT routes answer `200`.
- Staff gift lists keep reversed gifts with their status; donor endpoints (#212) omit them. The staff donor gift list discrepancy is #227 (BE-113). Donor changes are audited with before and after values, excluding name, email, phone, address, notes and both keys.
- `linkedStudents` and the Finance has-active-links filter are not built yet; they wait on a Finance-to-Sponsorship call. Sponsorship student links are built (#214).

### Sponsorship students and consent

- **BUILT (BE-106, #213)**: Staff can now create and manage students, record guardian consent, store a private photo and signed form, and preview the donor view. The new `SponsorshipService` uses port `7006`, schema `sponsorship`, and gateway route `/api/v1/admin/sponsorship/**`. Decision D10 default is applied. Children's data is not in Finance.
- All routes below use `/api/v1/admin/sponsorship`. Every response has `Cache-Control: private, no-store`. No route accepts a donor token. No token returns `401`; a token without the required permission key returns `403`.
- `GET /projects` requires `sponsorship.view`. It returns `{ items: [{ id, name }] }` from Content projects. `lang` accepts `en` or `bn`, default `en`. Unreachable Content returns `503 PROJECTS_UNAVAILABLE`.
- `GET /students` requires `sponsorship.view`. It returns `{ items, total, limit, offset }`, newest first. `limit` is `1..100`, default `20`. Items contain `id`, `name`, `preferredName`, `class`, `school`, `programme { id, name }`, `district`, `status`, `sponsored`, `hasPhoto`, and `consentGranted`.
- Student filters are `q`, `programme`, `status`, and `sponsored`. `q` matches full name, preferred name, or ID without case sensitivity. `programme` is a project slug. `status` accepts `active`, `inactive`, or `graduated`. `sponsored` accepts `true` or `false`. `sponsored` reflects an active student-donor link. Bad values return `400 VALIDATION_ERROR`.
- `POST /students` requires `sponsorship.student.manage` and supports `Idempotency-Key`. Required fields are `name` (max 200), `preferredName` (max 100), `dateOfBirth`, `school` (max 200), `class` (max 50), `programme` (existing project slug), `district` (max 100), and `guardianName` (max 200). `dateOfBirth` uses `yyyy-MM-dd`, is not in the future, and is within 30 years.
- Optional creation fields are `gender` (`male`, `female`, `other`), `address` (max 500), `guardianPhone` (`+` followed by 7 to 15 digits), `storyEn` and `storyBn` (max 4000 each), and `status` (default `active`).
- Creation returns `201` with the full record and an `ETag`. `400 VALIDATION_ERROR` lists every missing or invalid field with message `"Invalid fields: a, b."`. Unknown projects are reported on `programme`. If Content cannot confirm the project, it returns `503 PROJECTS_UNAVAILABLE` and saves nothing.
- Student IDs are opaque, such as `stu_` followed by 32 hex characters. Name, date of birth, address, and guardian details are encrypted at rest. These values never enter the audit log.
- `GET /students/{id}` requires `sponsorship.view`. It returns every field, plus `hasPhoto`, `hasConsentForm`, `consentFormName`, `consent { granted, recordedAt, actorId }`, `consents`, `links`, `createdAt`, and `updatedAt`. It includes an `ETag`; a missing student returns `404 NOT_FOUND`. `links` contains the student's active and historical donor links.
- `consents` contains history, newest first. Each row has `id`, `granted`, `hasForm`, `recordedAt`, `actorId`, and `note`. Each student detail read creates a `read` audit event with the actor, applying decision D12 default for guardian contact details.
- `PATCH /students/{id}` requires `sponsorship.student.manage`. It accepts the same body fields, all optional. Omitted fields stay unchanged; `""` clears optional text. A changed `programme` is checked again. Missing `If-Match` returns `428 PRECONDITION_REQUIRED`; a stale value returns `412 PRECONDITION_FAILED`. Success returns `200` with the record and new `ETag`.
- Any record, consent, photo, or form change raises the version. There is no student delete endpoint. Students are never removed through the API; erasure follows D13.
- `POST /students/{id}/consents` requires `sponsorship.student.manage` and supports `Idempotency-Key`. The body is `{ granted: true|false, note? }`. One consent covers photo and story. Each successful call appends a dated row with the actor. Rows are never changed or removed.
- Granting consent requires an uploaded signed form, otherwise `409 CONSENT_FORM_REQUIRED`. Repeating the current state returns `409 CONSENT_UNCHANGED`. Success returns `201 { consent, row }` and the new `ETag`.
- Withdrawing consent removes photo and story from the donor view at once. Both consent rows remain, and the photo file stays in the staff store. Ended frozen copies are unaffected.
- `PUT /students/{id}/consent-form` requires `sponsorship.student.manage`. Multipart field `form` accepts PDF, JPEG, or PNG up to 5 MB. Content is checked against its type; images lose location metadata. Errors are `413 PAYLOAD_TOO_LARGE` and `415 UNSUPPORTED_MEDIA_TYPE`. Success returns `200 { fileName, contentType, bytes }`.
- Replaced consent forms remain in the private store because consent rows reference them. `GET /students/{id}/consent-form` requires `sponsorship.view` and returns the file, or `404 NOT_FOUND` if absent. No token returns `401`; a donor token returns `403`.
- `PUT /students/{id}/photo` requires `sponsorship.student.manage`. Multipart field `photo` accepts JPEG or PNG up to 2 MB. EXIF, XMP, IPTC, comments, and PNG text chunks are removed. It replaces the earlier photo and deletes its file unless a frozen copy references it (`409 PHOTO_FROZEN`).
- Photo upload returns `409 CONSENT_REQUIRED` without recorded consent, `413 PAYLOAD_TOO_LARGE` for excess size, or `415 UNSUPPORTED_MEDIA_TYPE` for another type, a wrong signature, or an invalid image. Success returns `200 { hasPhoto, contentType, bytes }`.
- `GET /students/{id}/photo` requires a staff token with `sponsorship.view`. It returns the file, or `404 NOT_FOUND` if absent. `DELETE /students/{id}/photo` requires `sponsorship.student.manage`, removes the file, and returns `204`, or `404` if absent.
- Photo PUT and DELETE return `409 STUDENT_CHANGED` when the student changed at the same moment: nothing is replaced or deleted, and a newly uploaded file is removed.
- `GET /students/{id}/donor-view` requires `sponsorship.view`. Optional `lang` accepts `en` or `bn`. It uses exactly the D2 donor allow-list: `id`, `preferredName`, `class`, `school`, `programme { id, name }`, `district`, `status`, `hasPhoto`, `story`, and `storyLanguage`.
- Donor-view photo and story availability requires granted consent; `story` is `null` otherwise. The story uses the requested language, falling back to the other. `storyLanguage` identifies the language used. The donor student endpoints use this same projection.
- `GET /audit` requires `audit.view`. It returns the module audit feed with the same shape, cursor paging, and filters as other audit endpoints. Student and consent changes include before and after values. Encrypted fields, stories, file keys, and file names are excluded.
- Files live in the private store configured by `Sponsorship:FilesPath`. Signed forms require a staff token with `sponsorship.view`; donor photos require an owned active link with consent or an ended frozen copy.

### Links

- `GET /api/v1/admin/sponsorship/donors/{donorId}/students` requires `sponsorship.view`. Returns `{ items, total, limit, offset }`, newest first, active links by default; `status=all` adds ended and mistaken history. Invalid status returns `400 VALIDATION_ERROR`. Paging defaults to `limit=20`, `offset=0`, clamped to `1..100` and `0` or more.
- `POST` on the same path requires `sponsorship.link.manage` and honours optional `Idempotency-Key`, like the other writes. Body: `{ studentId, startDate }`, with `startDate` in `yyyy-MM-dd`. Success returns `201` with the link, including `id`, donor/student ids, preferred name, start/end dates, end type/reason, notice status, creation time and version. The `Location` header is `/api/v1/admin/sponsorship/donors/{donorId}/students`, the donor link list path.
- Invalid fields return `400 VALIDATION_ERROR`; unknown student or donor returns `404 NOT_FOUND`. Conflicts are `409 STUDENT_NOT_ACTIVE`, `DONOR_INACTIVE`, `LINK_EXISTS` (same active pair) or `STUDENT_ALREADY_SPONSORED` (another active donor). Finance lookup failure returns `503 FINANCE_UNAVAILABLE` and saves no link.
- A successful link calls Finance to stage template `donor.student_linked`, gated by the donor's `updates` consent. The payload holds only the link id; the Finance notice endpoint requires `Idempotency-Key`. `noticeStatus` is `queued`, `suppressed`, or `pending` on failure; notice failure still returns `201` and keeps the link. Bilingual notice templates do not exist yet; delivery waits on the email sender (BE-032/033).
- `POST /api/v1/admin/sponsorship/donors/{donorId}/students/{studentId}/unlink` requires `sponsorship.link.manage`. Body: `{ type: ended|mistaken, endDate?, reason? }`. `ended` on a started link requires `endDate` in `yyyy-MM-dd`, no earlier than the start and no later than today; `reason` is optional (max 500). Success returns `200` with the updated link. Invalid fields return `400 VALIDATION_ERROR`; no active link returns `409 LINK_NOT_ACTIVE`, including repeat unlink.
- Dates and donor visibility use the foundation time zone, `Sponsorship:TimeZone` (default `Asia/Dhaka`), not UTC. An unknown zone id prevents startup.
- To cancel a link that has not started, use `type: ended`: `endDate` is optional and ignored, stored as null, with no frozen copy. It is never shown to the donor and remains listed to staff as ended. A supplied malformed or future end date still returns `400 VALIDATION_ERROR`.
- Unlink returns `409 STUDENT_CHANGED` if the student changed at the same moment; no unlink or frozen copy is saved.
- `mistaken` also requires `sponsorship.link.void` and a non-empty reason; without the void permission it returns `403`. It immediately revokes access through that link, creates no frozen copy or past entry, and remains in staff history and audit with its reason. The same pair can be linked again later. An earlier ended link and its frozen copy keep being served after a later link for the same pair is voided as mistaken; only the mistaken link is voided.
- `ended` on a started link bumps the student version in the same transaction, changing the staff student ETag, and creates an immutable frozen copy of the donor-visible student fields, programme names and status. Story and photo reference are included only if consent was granted at unlink. Later edits or consent withdrawal do not change it. Photo files are kept while referenced; staff photo PUT and DELETE return `409 PHOTO_FROZEN` for a referenced photo. An active link alone does not prevent photo changes.
- Link create and both unlink types are audited with actor, time and before/after values. Frozen-copy audit values exclude story text and photo keys. Staff link responses carry `Cache-Control: private, no-store`.

### Donor self-service

- Student routes require the Donor role. Every request calls Finance `GET /api/v1/donors/me/identity` with the donor's own token, resolving donor id and language from its subject. Query/body donor ids are ignored. Inactive donors receive `403 DONOR_INACTIVE`, disabled accounts `403 ACCOUNT_DISABLED`, unknown subjects `404 NOT_A_DONOR`, and Finance failure `503 FINANCE_UNAVAILABLE`.
- `GET /api/v1/donors/me/students` returns `{ items, total, limit, offset }`. Active and ended links share one list, one row per student, preferring an active relink. Future-start active links stay hidden until their start date. `limit` is `1..100` (default `20`), `offset` is non-negative (default `0`); invalid or non-numeric values return `400 VALIDATION_ERROR`.
- `GET /api/v1/donors/me/students/names?ids=stu_...,stu_...` uses the same donor-self policy and returns `{ items: [{ id, preferredName }] }`, ordered by id, for visible students only. Accepts up to 100 distinct ids, each `stu_` plus 32 lowercase hex characters; empty, malformed or more than 100 distinct ids return `400 VALIDATION_ERROR`. Started active links use the live preferred name; ended links require a frozen copy and use its preferred name, unchanged by later edits. An active relink wins. Unknown ids, other donors' students, future-start links, links voided as mistaken, links cancelled before starting and never-linked students are absent, with no distinction between these cases.
- Row and detail fields are exactly `id`, `preferredName`, `class`, `school`, `programme { id, name }`, `district`, `status`, `hasPhoto`, `story`, `storyLanguage`, `since`, `sponsorship` (`active|ended`), and `endedOn` (null for active). Full name, birth date, address and guardian details are never included. `status` is student status, distinct from sponsorship status.
- `GET /api/v1/donors/me/students/{id}` serves the live donor projection for an active link or the frozen copy for an ended link. Other donors' students, never-linked, nonexistent and mistaken-link students receive the same `404 NOT_FOUND` response.
- `GET /api/v1/donors/me/students/{id}/photo` serves a live photo only with an owned active link and current consent; ended links serve their frozen photo. Missing access or photo returns `404 NOT_FOUND`. Current photo/story disappear on consent withdrawal; frozen content remains subject to foundation sign-off.
- Story follows the donor's language, falling back to the other language; `storyLanguage` identifies the language used and story is null without consent. Ended cards and detail pages display: "This student is no longer under your sponsorship and the information is not updated."
- `POST`, `PUT`, `PATCH` and `DELETE` on list, names, detail and photo paths return `405 METHOD_NOT_ALLOWED` with `Allow: GET`. Every response, including refusals, carries `Cache-Control: private, no-store` and `Vary: Authorization`.
- Finance serves `GET /api/v1/donors/me` (profile) and `/api/v1/donors/me/donations` (own gifts excluding reversed gifts); fields, paging and totals are documented in [DONORS.md](DONORS.md). Unlinking does not change gifts.

### Gift designation

- **BUILT (BE-108, #215)**: `POST /api/v1/admin/finance/donations` requires `finance.donation.record`, accepts `Idempotency-Key`, and records a pending gift with maker-checker unchanged. A student gift uses `designation: "student"` (not `student:stu_...`), `designationId` (`stu_` plus 32 hex characters) and required `donorId` (`dnr_...`). Gift to donor, gift to student and student to donor (#214) connect by id; email is only a contact detail. Public donations and recurring plans still refuse `student`.
- The donor's link must have started on or before the received calendar date and not ended before it; the end date is inclusive. Voided (mistaken) links and links cancelled before starting never count. Validation happens once at recording; approval does not repeat it, even after a link is voided. `400 VALIDATION_ERROR` uses `Invalid fields: ...`, naming `designationId`, `donorId` or `anonymous` as applicable (an anonymous student gift without `donorId` names both `anonymous` and `donorId`); `503 SPONSORSHIP_UNAVAILABLE` saves nothing. Full field and donor contact rules are in [OFFLINE_DONATIONS.md](OFFLINE_DONATIONS.md).
- Approval takes the receipt's stable donor key from the donor found by id, preserving history across email changes; the gift never changes the donor. With `donorId`, recording uses the donor's current name, email, phone and language and ignores typed contact details; approval uses the donor's current name and email for the receipt and notification. Contact corrections go through the donor profile (#211). Staff record responses, gift rows and detail include `donorId`, `studentId` and `studentName` (preferred name only, never stored on the gift). Staff reads fetch names at read time, once in a batch per page; Sponsorship failure leaves names null with `200`. `GET /api/v1/admin/finance/donations` and `/donations/totals` accept `studentId` or `designation=student:stu_...`, with `finance.view`.
- Under `/api/v1/admin/sponsorship`, Finance calls these endpoints with the staff member's own token, using finance keys so FinanceOfficer needs no sponsorship permission. Service credentials wait on BE-093. Only student ids and preferred names are exposed as student data (D2), and these reads are not audited:
  - `GET donors/{donorId}/students/{studentId}/designation?date=yyyy-MM-dd` (`finance.donation.record`): `{donorId, studentId, date, active, studentName}`, with name only when active.
  - `GET donors/{donorId}/designatable-students?date=yyyy-MM-dd` (`finance.donation.record`): `{items:[{studentId, preferredName, startDate, endDate}]}` for the recording form's student picker, at most 200. The date defaults to today in the foundation time zone.
  - `GET students/names?ids=a,b,...` (`finance.view`): `{items:[{id, preferredName}]}`, at most 100 ids, each `stu_` plus 32 hex characters.
- Recorded gifts are immutable; corrections use reversal operations (see [DONATIONS.md](DONATIONS.md) and [CONTROLS.md](CONTROLS.md)). Reversals copy the donor id. Staff gift lists keep reversed gifts with status `refunded`; the staff donor list discrepancy is #227 (BE-113). Donor history and totals exclude reversed gifts. Ledger postings are unchanged: student designation is attribution, not a separate fund, unless fiscal decision #112 says otherwise.
- Receipts and annual statements print only `Student sponsorship / শিক্ষার্থী পৃষ্ঠপোষকতা`, with no student id or name. Donor gift history includes nullable `studentName` and shows `Student sponsorship: {name}` (`শিক্ষার্থী পৃষ্ঠপোষকতা: {name}` for a `bn` donor) for a started active link or an ended link with a frozen copy, using the live or frozen preferred name respectively; an active relink wins. Unknown ids, other donors' students, future-start links, links voided as mistaken, links cancelled before starting and never-linked students show the plain label with `studentName` null, as does an ended link without a frozen copy. `designationId` stays null. If Sponsorship is unreachable, history still returns `200` with null names and plain labels. Names from this path do not carry into receipts, PDFs, reports or staff views.

## Privacy rules

- The server enforces permissions, projections, consent, link ownership, account state and password-change restrictions on every applicable request.
- Resolve the donor only from the authenticated token, never a caller-supplied donor identifier.
- Donor responses are private and use `Cache-Control: private, no-store`; store photos and signed forms privately.
- Frozen copies contain only content visible at ended unlink time, never later updates; preserving photo/story after withdrawal requires foundation sign-off.
- A mistaken link leaves no donor access, snapshot or past entry through that link; an earlier ended copy remains available. Retain the staff audit evidence marked as a mistake.
- Signed guardian consent forms are staff-only and require `sponsorship.view` to read.
- Keep personal data out of logs; passwords are never returned, logged or audited.

## Open items

- D4: confirm CRM additions and the final donor self-service profile projection.
- D10: confirm the Sponsorship module and schema ownership.
- D12: confirm the proposed staff guardian-contact read audit policy.
- Retry delivery staging for links whose notice status is `pending`.
- D13 blocks launch: agree retention and erasure covering frozen snapshots and signed forms; link the policy from backend #209, frontend #314 and backend #110.
- D15: confirm aggregate impact reporting and its relationship to BE-085.
- Agree guardian consent wording with the foundation.
- Obtain Q10 photo-storage sign-off with D11.
- Obtain explicit foundation sign-off for frozen photo/story surviving later consent withdrawal, alongside wording and retention.
- Real data waits for authentication hardening beyond development-only plain-text passwords and in-memory tokens, and resolution of the launch policy items.
