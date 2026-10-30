# Membership application intake (BE-042, #49)

`MembershipService` (port 7004, schema `membership`, gateway route `/api/v1/membership/*`) accepts public applications. The shapes follow what the frontend client already assumes (`KJWebsite.FrontEnd/src/api/endpoints/membership.ts`).

| Call | Result |
|---|---|
| `POST /api/v1/membership/applications` (+ optional `Idempotency-Key`) | `201 {id, reference, status, createdAt}`; `400 VALIDATION_ERROR` names the invalid fields, never their values |
| `POST /api/v1/membership/applications/check` `{email, phone}` | `200 {duplicate}`. Warn only: a duplicate never blocks a submission |
| `PUT /api/v1/membership/applications/{id}/attachments/{cv\|photo}` raw body, `Content-Type`, `X-Filename` (URL-encoded) | `200 {kind, fileName, contentType, bytes, scanStatus}`; `413` over the cap, `415` for a wrong or mismatching type |

All three are anonymous and throttled per IP at the gateway (`RateLimiting:MembershipIntake`). Reading applications is the review pipeline's job (#51) and needs permissions.

## Fields

Required: `firstName`, `lastName`, `email`, `phone`, `bloodGroup` (A/B/AB/O ±), `currentAddress`, `academicInstitute`, `occupation`, `language` (`bn`|`en`). Optional: `reasonForJoining`, `volunteeringExperience`, `presentOrganization`, `heardAboutUs`, and the legacy `gender`, `dateOfBirth` (`yyyy-MM-dd`), `cityOfResidence`, `countryOfResidence`. The legacy typo `VolunteeingExperience` is not carried over.

`reference` is `KJ-<year>-<8 random Crockford base32 characters>`: unique, non-sequential, not guessable.

## Personal data (ADR-014)

- `email`, `phone`, `bloodGroup`, `currentAddress` and `dateOfBirth` are encrypted per field with ASP.NET Core Data Protection. Set `DataProtection:KeysPath` to a persistent folder in every deployment, or rows cannot be read after a restart.
- Duplicate detection compares HMAC-SHA256 hashes of the normalised email (trimmed, lower-cased) and phone (digits, last ten). `Membership:HashKey` (base64, 32+ bytes) is required outside Development; changing it breaks matching for existing rows.
- Nothing from a request body is logged, and validation errors name fields only.

## Attachments

CV: pdf/doc/docx up to 5 MB; photo: jpeg/png/webp up to 2 MB. The leading bytes must match the declared type. A second upload of the same kind replaces the first. Files go to `Membership:AttachmentsPath` (local disk, development default under the temp folder) behind `IAttachmentStore`, with `scanStatus = pending`: virus scanning and EXIF stripping arrive with the media pipeline (#41), which also replaces the local store. Do not serve these files until then.

## Campaigns (BE-043, #50)

A campaign is a recruitment season: `slug`, bilingual name, `opensAt`, optional `closesAt`, and a manual `closedAt`. It is open inside its window until closed.

- `GET /api/v1/membership/campaigns/current` (public) → `{intake, campaign}`; `intake` is `open`, `closed` or `unrestricted`.
- **No campaign defined = intake stays open** (`unrestricted`), so nothing breaks before the first season is created. Once any campaign exists, applications need an open one; otherwise `409 INTAKE_CLOSED`. Applications are linked to the open campaign.
- Admin (`membership.manage`, gateway `/api/v1/admin/membership/...`): `GET/POST /campaigns`, `PUT /campaigns/{id}`, `POST /campaigns/{id}/close`. Changes are audited (`GET /api/v1/admin/membership/audit`, `audit.view`).

## Status lookup

`POST /api/v1/membership/applications/status {reference, email}` → `200 {timeline:[{status, at}]}`. Unknown reference, wrong email and malformed input all return the same `404`. The response carries status and time only, never notes or scores. Throttled per IP (`RateLimiting:MembershipStatus`, 10 per 15 min). Each application starts with a `awaiting` event; the review pipeline (#51) appends the rest.

## Review pipeline (BE-044, #51)

Back office, all under `/api/v1/admin/membership/applications` with `membership.manage`. Notes, scores and reviewers exist only here; no public route returns them.

| Call | Purpose |
|---|---|
| `GET ?status=&campaignId=&limit=&offset=` | `{items, total}` oldest first, each with notes, score, reviewer, history and `nextStatuses` |
| `GET /{id}` | one application |
| `PATCH /{id}` | `reviewerId`, `score` (0-100), `interviewAt`; absent = unchanged, `null` = clear |
| `POST /{id}/notes` | `{text}` internal note |
| `POST /transition` | `{ids (1-200), to, interviewAt?}` → `200 {results:[{id, ok, notified, error?}]}` |

**State machine** (`Review/StatusTransitions.cs`): `awaiting → under_review → shortlisted → interview_scheduled → approved`, one step at a time. `rejected`, `postponed` and `withdrawn` from any open state; `postponed → under_review`. `approved`, `rejected`, `withdrawn` are final. `interview_scheduled` needs an interview time. A failed item reports `not_found`, `final`, `invalid_transition`, `interview_date_required` or `unknown_status` and changes nothing; the others still go through.

**Every successful transition** appends a status event (who, when), is audited (personal columns are excluded from audit rows) and queues a notification in `membership.notification_outbox` in the same commit. The template is `application.status.<status>` in the applicant's language. Delivery belongs to the email outbox work (#32/#33), not yet on develop: rows wait with `SentAt` null.

**History is append-only**: EF refuses to modify an event, and PostgreSQL refuses `UPDATE` on `application_status_events` (SQLSTATE 42501). Deleting an application (retention, erasure) still removes its history.

Not built here: provisioning a member on `approved` (#52), a reviewers list (needs Identity data), a multi-criteria rubric, calendar (.ics) invites.
