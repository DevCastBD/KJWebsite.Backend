# What the platform does with personal data (BE-052, #58; closes G30)

This is the behaviour the published privacy statement (frontend F2-6) must describe. `GET /api/v1/membership/privacy` returns the live settings (notice version, data location, retention windows) so the statement can show the real numbers instead of copying them.

## Retention

Rejected, withdrawn and postponed applications are removed after a window set in configuration, in whole months, counted from the day the application entered that status:

| Setting | Applies to |
|---|---|
| `Privacy:RejectedApplicationMonths` | `rejected` |
| `Privacy:WithdrawnApplicationMonths` | `withdrawn` |
| `Privacy:PostponedApplicationMonths` | `postponed` |

**There are no defaults** (ADR-014; the numbers are decision #110). A status with no window is never purged, and the public endpoint reports `null` for it. Applications still being decided (`awaiting`, `under_review`, `shortlisted`, `interview_scheduled`) and `approved` ones (they became members) are never purged by retention.

A purge deletes the application with its CV and photo files, reviewer notes, status history and queued messages. It runs from a hosted worker every `Privacy:WorkerIntervalHours` (default 24; only acts when a window is set; `Privacy:WorkerEnabled=false` stops it), and staff with `membership.privacy` can run it by hand: `POST /api/v1/admin/membership/retention/run` is a **dry run** unless `?dryRun=false`. Every run is recorded in `retention_runs` with the policy used, and a purge records the opaque application ids it removed with their status and the day it began (`GET .../retention/runs`, `.../runs/{id}/items`). Those records hold nothing personal.

## Consent records

`consent_records` is an append-only ledger: subject (an application or member id), purpose, granted or withdrawn, when, source (`public_form`, `portal`, `staff`, `system`), who acted, and the privacy notice version (`Privacy:NoticeVersion`). Submitting the application form records `application_processing`; the portal and staff actions record `publish_name`, `publish_photograph`, `publish_expertise`, `publish_alumni`. Consent to publish is opt-in per field and defaults to off. The previous member consent log was copied into it.

## Export

- **Members**: `GET /api/v1/members/me/export` (signed in): everything held, including the application, reviewer notes, lifecycle (termination reason included), consent ledger, messages sent, audit entries about them, and Identity's account and sessions.
- **Applicants** (never approved, no login): `POST /api/v1/membership/applications/export {reference, email}`: the same data about the application. Reference and email must match; any mismatch is the same `404`; rate limited at the gateway.

## Erasure

Asking creates a request: members use `POST /api/v1/members/me/deletion-request`, applicants `POST /api/v1/membership/applications/erasure-request {reference, email}` (an applicant who became a member is treated as that member). Nothing is deleted by asking, so nobody can erase an application by knowing an email address. Staff with `membership.privacy` list requests (`GET /api/v1/admin/membership/erasure-requests`) and carry them out (`POST .../{id}/execute`).

Erasing a **member**: the Identity account is erased first (email and user name replaced by a placeholder, password, profile, roles and sessions removed; idempotent, so a failed run can be repeated); then in one transaction the application, its files, notes and queued messages, the member's edit requests and photo are deleted, the profile fields are blanked (the row stays as `Erased Member` with status `erased`, because committee history and the audit trail refer to its id), open committee terms end, consents are withdrawn, free-text notes and termination reasons on the status history are cleared, and an `erasure_records` row notes who, when and counts. Erasing an **applicant** deletes the application and its files. The public directory, org chart and every other view stop showing them at once.

### The audit log
It stays immutable, with one deliberate exception. Audit rows never contain personal data by design: names, contact details, profile fields, notes and termination reasons are excluded from what is recorded (ids, status and what changed are enough). The one identifying part is the *actor*: while signed in, a person's own audit rows carry their email, IP and browser. An erasure nulls exactly those three columns on that person's rows; a database trigger (PostgreSQL) allows an update only when every other column is unchanged and those three are set to NULL, and still refuses any other edit or deletion.

### Not covered here
- **Backups** keep copies until they expire: backup retention must not exceed the period the privacy statement promises (an operations decision, with #110 and hosting #106).
- **Donor records, analytics events** and statutory financial retention (P4, #111, #114).
- Data already sent out (an email that was delivered) cannot be recalled.
