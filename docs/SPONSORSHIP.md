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

- `POST /api/v1/admin/auth/donors`, permission `finance.donor.provision`: provision a donor login with a write-only initial password and `mustChangePassword` for a new account; return `400 VALIDATION_ERROR` for invalid input; publish the success status and exact payload.
- `POST /api/v1/admin/auth/donors`, permission `finance.donor.provision`: when email belongs to a member, add the donor role to that account, preserving its password and sessions and leaving the must-change flag unset.
- `PUT /api/v1/admin/auth/donors/{userId}/password`, permission `finance.donor.provision`: staff set a new initial password for a donor who has lost access; it sets `mustChangePassword` again and revokes every refresh token.
- `PUT /api/v1/admin/auth/donors/{userId}/status`, permission `finance.donor.provision`: disable or re-enable the account; disabling revokes every refresh token.
- `POST /api/v1/auth/change-password`, any signed-in account: accept current and new passwords, enforce existing policy and a different new password, then clear `mustChangePassword`; document success and failure statuses.
- The `POST /api/v1/auth/login` response and `GET /api/v1/auth/me`: report `mustChangePassword` and roles `member` and `donor`; profiles do not report roles.
- While `mustChangePassword` is set, allow only change-password, logout and `/auth/me`; every other authenticated API operation returns `403 PASSWORD_CHANGE_REQUIRED`.
- Sign-in returns `403 ACCOUNT_DISABLED` only after correct credentials; wrong email or password receives the same generic authentication failure.
- Apply the existing minimum 10-character password policy, rate limits and lockout of 5 failures for 15 minutes; never return, log or audit passwords.
- Publish account-status and token-revocation behavior; enforce disabled/inactive access checks on every donor request with `403`.
- Publish the five permission keys and D9 mapping, including the widened `finance.donor.manage` scope; donor role membership must not cause `403` on shared portal calls.
- Backfill the four non-void sponsorship permissions into existing Admin roles once, recording completion; keep `sponsorship.link.void` exclusive to SuperAdmin.

### Finance donors

- `GET /api/v1/admin/finance/donors`, permission `finance.view`: return paged donors and `linkedStudents`; document exact status, has-account and has-active-links filter names and success status.
- `POST /api/v1/admin/finance/donors`, permission `finance.donor.manage`: create a CRM donor; return `400 VALIDATION_ERROR` for invalid fields and `409 DONOR_EXISTS` for a duplicate donor; document success status.
- `GET` (permission `finance.view`) and `PATCH` (permission `finance.donor.manage`) `/api/v1/admin/finance/donors/{donorId}`: return ETags and enforce PATCH preconditions with `428` and `412`; publish the success statuses.
- `POST /api/v1/admin/finance/donors/{donorId}/login`, permission `finance.donor.provision`: provision the sign-in through Identity with the login name (the email) and the staff-chosen initial password, and store the account subject on the donor; `409 ACCOUNT_EXISTS` if it already has one; the password is never returned.
- `PUT /api/v1/admin/finance/donors/{donorId}/login/password`, permission `finance.donor.provision`: set a new initial password through Identity; the donor must change it at the next sign-in.
- `PUT /api/v1/admin/finance/donors/{donorId}/login/status`, permission `finance.donor.provision`: disable or re-enable the sign-in through Identity. Staff gift lists for a donor keep reversed gifts with their status.
- Donor list account states are `none`, `active`, `disabled`, with a separate `mustChangePassword` flag; there is no pending setup state.

### Sponsorship students and consent

- `GET /api/v1/admin/sponsorship/students`, permission `sponsorship.view`: return the paged staff student list; document success status and paging defaults.
- `POST /api/v1/admin/sponsorship/students`, permission `sponsorship.student.manage`: create a student; return `400 VALIDATION_ERROR` for invalid fields; document success status.
- `GET /api/v1/admin/sponsorship/students/{id}`, permission `sponsorship.view`: return the staff record, current consent and `ETag`; document success and missing-record statuses.
- `PATCH /api/v1/admin/sponsorship/students/{id}`, permission `sponsorship.student.manage`: require `If-Match`; return `400 VALIDATION_ERROR`, `428` or `412` as applicable and a new ETag on success.
- Student schemas use `class`, `storyEn`, `storyBn` and a programme project reference; document the reference field and project display name representation.
- Projects selection endpoint, exact method/path and permission to publish: provide existing project IDs and names for programme selection; document statuses.
- `POST /api/v1/admin/sponsorship/students/{id}/consents`, permission `sponsorship.student.manage`: append consent records; one consent covers photo and story; require the uploaded signed form before consent can be affirmed.
- Consent records determine current consent; document withdrawal recording, including whether it creates its own dated, actor-attributed row, and all response statuses.
- `PUT /api/v1/admin/sponsorship/students/{id}/consent-form`, permission `sponsorship.student.manage`: accept the guardian-signed form as PDF, JPEG or PNG up to 5 MB, store it privately and document validation and status codes.
- `GET /api/v1/admin/sponsorship/students/{id}/consent-form`, permission `sponsorship.view`: allow staff only; document success and access-failure statuses; never expose the form to donors or public users.
- `PUT /api/v1/admin/sponsorship/students/{id}/photo`, permission `sponsorship.student.manage`: accept multipart field `photo`, JPEG/PNG up to 2 MB; strip location metadata; return `409` without consent, `415` for unsupported format and `413` above the limit.
- `GET /api/v1/admin/sponsorship/students/{id}/photo`, permission `sponsorship.view`: require a bearer token and serve the private photo; document success and missing-photo statuses.
- `DELETE /api/v1/admin/sponsorship/students/{id}/photo`, permission `sponsorship.student.manage`: delete the current photo; document success and missing-photo statuses.
- `GET /api/v1/admin/sponsorship/students/{id}/donor-view`, permission `sponsorship.view`: return the backend donor-visible projection; document statuses and response fields.
- `GET /api/v1/admin/sponsorship/audit`, permission `audit.view`: provide the module audit feed for the required pre-launch staff screen; document filters, paging and statuses.

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
