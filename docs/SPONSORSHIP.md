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

- **BUILT (BE-104, #211)**: staff can create a donor before any gift; it appears with zero giving, no segments and an empty first-gift date. Receipts join on the stable donor key; new gifts find the donor by the lookup hash of the current email. Correcting an email preserves history without editing receipts; a later gift from the old address starts a different donor.
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
- Staff gift lists keep reversed gifts with their status; donor endpoints (#212) omit them. Donor changes are audited with before and after values, excluding name, email, phone, address, notes and both keys.
- `linkedStudents` and the has-active-links filter arrive with issue 214; they are not built yet. The Sponsorship module exists from #213 (students and consent only).

### Sponsorship students and consent

- **BUILT (BE-106, #213)**: Staff can now create and manage students, record guardian consent, store a private photo and signed form, and preview the donor view. The new `SponsorshipService` uses port `7006`, schema `sponsorship`, and gateway route `/api/v1/admin/sponsorship/**`. Decision D10 default is applied. Children's data is not in Finance.
- All routes below use `/api/v1/admin/sponsorship`. Every response has `Cache-Control: private, no-store`. No route accepts a donor token. No token returns `401`; a token without the required permission key returns `403`.
- `GET /projects` requires `sponsorship.view`. It returns `{ items: [{ id, name }] }` from Content projects. `lang` accepts `en` or `bn`, default `en`. Unreachable Content returns `503 PROJECTS_UNAVAILABLE`.
- `GET /students` requires `sponsorship.view`. It returns `{ items, total, limit, offset }`, newest first. `limit` is `1..100`, default `20`. Items contain `id`, `name`, `preferredName`, `class`, `school`, `programme { id, name }`, `district`, `status`, `sponsored`, `hasPhoto`, and `consentGranted`.
- Student filters are `q`, `programme`, `status`, and `sponsored`. `q` matches full name, preferred name, or ID without case sensitivity. `programme` is a project slug. `status` accepts `active`, `inactive`, or `graduated`. `sponsored` accepts `true` or `false`. No student is sponsored until #214, so `true` returns an empty page. Bad values return `400 VALIDATION_ERROR`.
- `POST /students` requires `sponsorship.student.manage` and supports `Idempotency-Key`. Required fields are `name` (max 200), `preferredName` (max 100), `dateOfBirth`, `school` (max 200), `class` (max 50), `programme` (existing project slug), `district` (max 100), and `guardianName` (max 200). `dateOfBirth` uses `yyyy-MM-dd`, is not in the future, and is within 30 years.
- Optional creation fields are `gender` (`male`, `female`, `other`), `address` (max 500), `guardianPhone` (`+` followed by 7 to 15 digits), `storyEn` and `storyBn` (max 4000 each), and `status` (default `active`).
- Creation returns `201` with the full record and an `ETag`. `400 VALIDATION_ERROR` lists every missing or invalid field with message `"Invalid fields: a, b."`. Unknown projects are reported on `programme`. If Content cannot confirm the project, it returns `503 PROJECTS_UNAVAILABLE` and saves nothing.
- Student IDs are opaque, such as `stu_` followed by 32 hex characters. Name, date of birth, address, and guardian details are encrypted at rest. These values never enter the audit log.
- `GET /students/{id}` requires `sponsorship.view`. It returns every field, plus `hasPhoto`, `hasConsentForm`, `consentFormName`, `consent { granted, recordedAt, actorId }`, `consents`, `links`, `createdAt`, and `updatedAt`. It includes an `ETag`; a missing student returns `404 NOT_FOUND`. `links` stays empty until #214.
- `consents` contains history, newest first. Each row has `id`, `granted`, `hasForm`, `recordedAt`, `actorId`, and `note`. Each student detail read creates a `read` audit event with the actor, applying decision D12 default for guardian contact details.
- `PATCH /students/{id}` requires `sponsorship.student.manage`. It accepts the same body fields, all optional. Omitted fields stay unchanged; `""` clears optional text. A changed `programme` is checked again. Missing `If-Match` returns `428 PRECONDITION_REQUIRED`; a stale value returns `412 PRECONDITION_FAILED`. Success returns `200` with the record and new `ETag`.
- Any record, consent, photo, or form change raises the version. There is no student delete endpoint. Students are never removed through the API; erasure follows D13.
- `POST /students/{id}/consents` requires `sponsorship.student.manage` and supports `Idempotency-Key`. The body is `{ granted: true|false, note? }`. One consent covers photo and story. Each successful call appends a dated row with the actor. Rows are never changed or removed.
- Granting consent requires an uploaded signed form, otherwise `409 CONSENT_FORM_REQUIRED`. Repeating the current state returns `409 CONSENT_UNCHANGED`. Success returns `201 { consent, row }` and the new `ETag`.
- Withdrawing consent removes photo and story from the donor view at once. Both consent rows remain, and the photo file stays in the staff store. Frozen copies in #214 are unaffected.
- `PUT /students/{id}/consent-form` requires `sponsorship.student.manage`. Multipart field `form` accepts PDF, JPEG, or PNG up to 5 MB. Content is checked against its type; images lose location metadata. Errors are `413 PAYLOAD_TOO_LARGE` and `415 UNSUPPORTED_MEDIA_TYPE`. Success returns `200 { fileName, contentType, bytes }`.
- Replaced consent forms remain in the private store because consent rows reference them. `GET /students/{id}/consent-form` requires `sponsorship.view` and returns the file, or `404 NOT_FOUND` if absent. No token returns `401`; a donor token returns `403`.
- `PUT /students/{id}/photo` requires `sponsorship.student.manage`. Multipart field `photo` accepts JPEG or PNG up to 2 MB. EXIF, XMP, IPTC, comments, and PNG text chunks are removed. It replaces the earlier photo and deletes its file.
- Photo upload returns `409 CONSENT_REQUIRED` without recorded consent, `413 PAYLOAD_TOO_LARGE` for excess size, or `415 UNSUPPORTED_MEDIA_TYPE` for another type, a wrong signature, or an invalid image. Success returns `200 { hasPhoto, contentType, bytes }`.
- `GET /students/{id}/photo` requires a staff token with `sponsorship.view`. It returns the file, or `404 NOT_FOUND` if absent. `DELETE /students/{id}/photo` requires `sponsorship.student.manage`, removes the file, and returns `204`, or `404` if absent.
- `GET /students/{id}/donor-view` requires `sponsorship.view`. Optional `lang` accepts `en` or `bn`. It uses exactly the D2 donor allow-list: `id`, `preferredName`, `class`, `school`, `programme { id, name }`, `district`, `status`, `hasPhoto`, `story`, and `storyLanguage`.
- Donor-view photo and story availability requires granted consent; `story` is `null` otherwise. The story uses the requested language, falling back to the other. `storyLanguage` identifies the language used. The donor endpoints in #214 must use this same projection.
- `GET /audit` requires `audit.view`. It returns the module audit feed with the same shape, cursor paging, and filters as other audit endpoints. Student and consent changes include before and after values. Encrypted fields, stories, file keys, and file names are excluded.
- Files live in the private store configured by `Sponsorship:FilesPath`. They are never served without a staff token with `sponsorship.view`.

### Links

- `GET /api/v1/admin/sponsorship/donors/{donorId}/students`, permission `sponsorship.view`: list the donor's student links; document response shape and statuses.
- `POST /api/v1/admin/sponsorship/donors/{donorId}/students`, permission `sponsorship.link.manage`: link a student; return `409 STUDENT_ALREADY_SPONSORED` for a second active donor and `409 LINK_EXISTS` for a duplicate link.
- Successful linking queues a Bengali or English donor notice through the email outbox and donor consent gate; publish the consent line and link success status.
- `POST /api/v1/admin/sponsorship/donors/{donorId}/students/{studentId}/unlink`, permission `sponsorship.link.manage` for `type: ended`: require a real `endDate`, allow optional `reason`, and create the frozen donor snapshot.
- The same unlink endpoint, permission `sponsorship.link.void` for `type: mistaken`: require `reason`, immediately revoke access, create no snapshot or past entry, and preserve the audit record marked as a mistake; restrict to SuperAdmin.
- Unlink validation returns `400 VALIDATION_ERROR`; document success and permission-failure statuses for both types.
- Ended snapshots contain exactly the donor-visible fields, photo and story visible when the link ended; later reads serve this frozen copy.
- Later consent withdrawal removes consent-dependent content from the current donor view but leaves frozen photo/story intact, subject to foundation sign-off.

### Donor self-service

- All donor endpoints require the donor role and resolve the donor from the authenticated token; disabled accounts and inactive donors receive `403` on every request.
- `GET /api/v1/donors/me`: return the donor profile; the proposed projection is `name`, `type`, `organisation`, `email`, `phone`, `address`, `country`, `language`, `since`, excluding notes and status; final fields and success status remain to be confirmed.
- `GET /api/v1/donors/me/students`: return current and ended students together in one list, with sponsorship status, end date, `hasPhoto` and `since`; use no separate `past` list and distinguish student status from sponsorship status.
- `GET /api/v1/donors/me/students/{id}`: return the current permitted projection for an active link or the frozen detail for an ended link; unknown students and mistaken links return `404 NOT_FOUND`.
- `GET /api/v1/donors/me/students/{id}/photo`: serve a current photo only with active link and consent, otherwise `404`; ended links serve their frozen photo; mistaken links return `404 NOT_FOUND`.
- Donor student fields are preferred name, class, school, programme, district and status; photo/story require consent except for frozen copies; document exact wire names.
- Select donor story by language, falling back to the other language when empty; document its response representation.
- Ended cards and detail pages display: "This student is no longer under your sponsorship and the information is not updated."
- `GET /api/v1/donors/me/donations`: return donor history excluding reversed gifts; exclude reversals from donor totals and document success status, paging and response fields.
- Every donor response carries `Cache-Control: private, no-store`; `/members/me` and `/donors/me` remain profile endpoints, with roles obtained from Identity.

### Gift designation

- `POST /api/v1/admin/finance/donations`, permission `finance.donation.record`: accept designation type `student` with `designationId`; validate an active link on the gift's received date.
- Invalid student designation returns `400 VALIDATION_ERROR` with the field error on `designationId`; document gift creation success status and stable field codes.
- Gift responses include `studentId` and `studentName`; gift list GET routes support a designated-student filter, with exact paths, filter names and permissions published in OpenAPI.
- Recorded gifts are immutable; corrections use reversal operations, whose exact methods, paths, permissions and statuses must be documented.
- Staff gift lists include reversed gifts with status; donor history and totals exclude them.
- Donors receive the designated student name only while a link or frozen copy exists; otherwise display "Student sponsorship" without the name.

## Privacy rules

- The server enforces permissions, projections, consent, link ownership, account state and password-change restrictions on every applicable request.
- Resolve the donor only from the authenticated token, never a caller-supplied donor identifier.
- Donor responses are private and use `Cache-Control: private, no-store`; store photos and signed forms privately.
- Frozen copies contain only content visible at ended unlink time, never later updates; preserving photo/story after withdrawal requires foundation sign-off.
- A mistaken link leaves no donor access, snapshot or past entry; retain only the staff audit evidence marked as a mistake.
- Signed guardian consent forms are staff-only and require `sponsorship.view` to read.
- Keep personal data out of logs; passwords are never returned, logged or audited.

## Open items

- D4: confirm CRM additions and the final donor self-service profile projection.
- D10: confirm the Sponsorship module and schema ownership.
- D12: confirm the proposed staff guardian-contact read audit policy.
- D13 blocks launch: agree retention and erasure covering frozen snapshots and signed forms; link the policy from backend #209, frontend #314 and backend #110.
- D15: confirm aggregate impact reporting and its relationship to BE-085.
- Agree guardian consent wording with the foundation.
- Obtain Q10 photo-storage sign-off with D11.
- Obtain explicit foundation sign-off for frozen photo/story surviving later consent withdrawal, alongside wording and retention.
- Real data waits for authentication hardening beyond development-only plain-text passwords and in-memory tokens, and resolution of the launch policy items.
