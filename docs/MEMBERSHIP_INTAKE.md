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

- `GET /api/v1/membership/campaigns/current` (public) → `{intake, campaign}`; `intake` is `open`, `closed` or `unrestricted`. It also carries the public site's shape: `state` (`open`/`upcoming`/`closed`, null when unrestricted), `id`, `name {en, bn}`, `opensOn`, `closesOn`, `nextOpensOn` (yyyy-MM-dd).
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

## Approval provisions the member (BE-045, #52)

Moving an application to `approved` (the transition API above) does five things as one action, per application:

1. **Identity creates the login.** Membership calls `POST /api/v1/admin/auth/members` on the Auth service (config `Services:Auth:Url`, default `http://localhost:7003/`) with the **approver's own access token**, so Identity checks the new permission `membership.provision` (seeded into `MembershipOfficer`, and so into `Admin`/`SuperAdmin`; an existing database must grant it to a custom role by hand). The account has the `Member` role and the profile from the application, no password, and a confirmed email: nobody can sign in until its owner uses the setup link. Idempotent per application id (`auth.users.ProvisionedFor`); `409 EMAIL_TAKEN` if another account uses the email.
2. **Membership commits once**: the `Member` row (`membership.members`), its code `KJ-<year>-<nnnn>` (unique, numbered from 0001 per year, retried on a race), the `approved` history event and the welcome message.
3. The welcome message is `member.welcome` in `notification_outbox` (applicant's language; instead of, not in addition to, the generic status message). Its payload carries the member code and the password-setup token, so **every outbox payload is data-protected**. The setup link is `POST /api/v1/auth/reset-password` with the applicant's email and that token.

**Failure handling.** Identity unreachable, refusing, or the email taken ends that item with `provisioning_failed`, `forbidden` or `email_taken`, and the application stays `interview_scheduled` with no member, event or message. If the local commit fails after Identity succeeded, the passwordless account is left behind (it cannot be used) and a retry of the approval reuses it. There is no compensating delete; a cross-service transaction is not possible (ADR-001). The result for a success carries `memberCode`.

The profile stays in Identity's `member_profiles` (its interim home); the Member row keeps only the code, link and name. Service-to-service credentials (BE-093) replace token forwarding later.

## Member self-service (BE-046, #53)

Everything is about the signed-in member and takes no member id: the member is found from the token's subject, so one member cannot name another. Routes need `member.profile.edit` (the `Member` role has it); an account without a member record gets `404 NOT_A_MEMBER`. Gateway: `/api/v1/members/me/*` goes to Membership, except `/sessions` which Identity serves.

| Call | Notes |
|---|---|
| `GET/PATCH /api/v1/members/me/profile` | `PATCH {changes:{field:value}}`. `phone`, `address`, `expertise` apply now. `firstName`, `lastName`, `nameBn`, `dateOfBirth`, `bloodGroup` queue for review; the record is unchanged until a reviewer approves. `pending` in the profile shows each field's latest request (`pending`, or `rejected` with the reviewer's reason). A newer request replaces the waiting one. Bad values: `400` naming the fields only |
| `GET/PUT /consents` | `{name, photograph, expertise}`, all `false` until the member opts in; every change is appended to `member_consent_events` |
| `GET/PUT /notifications` | `{news, events, membership}` (default on) |
| `PUT/GET/DELETE /photo` | raw body, jpeg/png/webp up to 2 MB, content checked; stored with scan `pending` (see #41); only the owner can read it |
| `POST /deletion-request` | `202 {reference, requestedAt}`, idempotent. Executing erasure belongs to #58 |
| `GET /export` | profile, consents + history, edit requests, the application with its history, **reviewer notes**, score and reviewer, messages sent, audit entries about the member's records, and Identity's account, sessions and audit entries. Never password hashes or token values. `identityAvailable:false` if Identity was unreachable |
| `GET /card` and `/card.pdf` | code, name in both scripts, since-date, QR (SVG in JSON; PNG in the PDF) |
| `GET/DELETE /sessions[/{id}]` | Identity: the member's devices; another member's id is a 404 |

Moderation (`membership.manage`): `GET /api/v1/admin/membership/profile-changes?status=pending|approved|rejected`, `POST .../{id}/approve`, `POST .../{id}/reject {reason}` (reason is required and shown to the member). Deciding twice is `409`.

**Where data lives.** At approval the application snapshot becomes the member's profile in `membership.members` (phone, address, date of birth, blood group data-protected). Identity's `member_profiles` keeps only the provisioning snapshot; an approved name change is not pushed back to it.

**Card.** The PDF uses QuestPDF under its Community licence and an embedded Noto Sans Bengali (SIL OFL, `Assets/Fonts/OFL.txt`), so conjuncts shape correctly on a server with no fonts. The QR encodes `Membership:CardVerifyUrl` + `/` + code; no public verification page exists yet. Set `KJ_CARD_PDF_OUT` when running `TheCardCarriesTheCodeAQrAndABengaliPdf` to keep the PDF for a look.

## Member directory (BE-047, #54)

**Public** (`GET /api/v1/members/directory?q=&chapter=&page=`, anonymous, the shape the site reads): `{items:[{id, name?, nameBn?, photoUrl?, expertise?[]}], total, pageSize: 24, chapters: []}`.

- **Consent is enforced in the SQL statement** (`DirectoryEndpoints.PublicQuery`): rows are active members with at least one consent and no erasure request; each field is projected as `CASE WHEN consent THEN column END`, so an unapproved field is never read by the application. A test asserts the statement does not mention the contact columns. The search `q` matches only published fields, so a hidden name or skill cannot be probed. Order is by the opaque member id so ordering reveals nothing.
- **Withdrawal is immediate**: nothing is cached (`Cache-Control: no-store`); the next request is the first without the member. Consent changes in the portal (`PUT /members/me/consents`) are the only switch.
- **Photos** are offered only when the member consented **and** the file's scan status is `clean`. Nothing sets `clean` yet (the media pipeline, #41, will, including EXIF stripping), so no photo is published until then. `GET /api/v1/members/directory/{id}/photo` answers 404 identically for a missing member, no consent or an unscanned file. Set `Membership:PublicApiBaseUrl` to make `photoUrl` absolute.
- **No harvesting**: 24 per page, page 1-50 (else 400), contact details never returned, and the gateway throttles per IP (`RateLimiting:MembershipDirectory`, 60 requests / 10 min, `429` + `Retry-After`).
- `chapter` is accepted and ignored and `chapters` is empty until chapters exist (#55).

**Staff** (`member.profile.view`): `GET /api/v1/admin/membership/members?q=&status=&limit=&offset=` (name, member code, expertise; no contact details) and `GET .../members/{id}` (email, phone, address, date of birth, blood group for that one member). Whether ordinary members get an internal directory of their own is a policy question for the core members; it is not built.

## Committees, chapters and terms (BE-048, #55)

A **committee** has a `kind` (`executive`, `advisory`, `project`, `chapter`), a bilingual name, an optional parent (nesting up to 6 deep; cycles are refused) and can be archived. A **term** seats a member in a position (`positionKey` such as `chair`, bilingual title) from `startsOn` to an optional `endsOn`. A term is **current** from its first through its last day, inclusive (UTC dates), and historical afterwards. Terms are ended, never deleted; a committee with children or any term can only be archived (`409 HAS_HISTORY` on delete). The same member cannot hold the same position in overlapping periods (`409 TERM_OVERLAP`).

**Admin** (`membership.manage`, `/api/v1/admin/membership/committees`): `GET` (flat list; `includeArchived=true`), `POST`, `PUT /{id}`, `POST /{id}/archive|unarchive`, `DELETE /{id}`; terms: `GET /{id}/members?scope=current|past|all`, `POST /{id}/members`, `PUT /{id}/members/{termId}`, `POST /{id}/members/{termId}/end {endsOn?}`. The back office's `{name, parentId, chair}` still works: `name` is the English name (the Bengali name then copies it and `nameBnIsFallback` is true so a translator can find it). `chair` is **derived** (the current holder of a `chair` position) and a chair sent in a draft is ignored: the UI needs a member picker. All changes are audited.

**Public** (anonymous, never cached): `GET /api/v1/committees` is the org chart as a tree with current seats; `GET /api/v1/committees/{slug}` adds past seats. A seat is listed only for members who consented to publish their name (the directory's consent), are active and have no erasure request; the SQL selects nothing about anyone else, and a member who withdraws consent leaves the chart on the next request. Archived committees are not public.

**Directory**: `chapters` now lists the chapter committees, and `chapter=<English name>` filters to current members of that chapter who agreed to be named.
