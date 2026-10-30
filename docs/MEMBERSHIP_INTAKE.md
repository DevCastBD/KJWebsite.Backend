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

`reference` is `KJ-<year>-<nnnn>`, one sequence per year.

## Personal data (ADR-014)

- `email`, `phone`, `bloodGroup`, `currentAddress` and `dateOfBirth` are encrypted per field with ASP.NET Core Data Protection. Set `DataProtection:KeysPath` to a persistent folder in every deployment, or rows cannot be read after a restart.
- Duplicate detection compares HMAC-SHA256 hashes of the normalised email (trimmed, lower-cased) and phone (digits, last ten). `Membership:HashKey` (base64, 32+ bytes) is required outside Development; changing it breaks matching for existing rows.
- Nothing from a request body is logged, and validation errors name fields only.

## Attachments

CV: pdf/doc/docx up to 5 MB; photo: jpeg/png/webp up to 2 MB. The leading bytes must match the declared type. A second upload of the same kind replaces the first. Files go to `Membership:AttachmentsPath` (local disk, development default under the temp folder) behind `IAttachmentStore`, with `scanStatus = pending`: virus scanning and EXIF stripping arrive with the media pipeline (#41), which also replaces the local store. Do not serve these files until then.
