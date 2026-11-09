# Donor CRM (BE-060, #65)

## Who is a donor
A donor record is made (or refreshed) when a **non-anonymous** gift with an **email address** is confirmed, in the same commit as the confirmation and its receipt. Staff can also create a donor before any gift; it is listed with zero giving, no segments and an empty first-gift date.

Each donor has an id (`dnr_...`), a **stable donor key** carried by every receipt of that donor, and a unique lookup hash of the **current email** (`EmailKey`, case-insensitive). The stable key is fixed at creation: the keyed hash of the first email unless another donor already holds it, then a fresh key. A new gift joins the donor whose current email it carries. Existing donors were migrated with the lookup hash equal to their key.

Staff can correct a donor's email unless the donor has a sign-in account (`409 EMAIL_HAS_ACCOUNT`). History stays attached through the stable key; no stored receipt is edited. A gift from the old address then starts a different donor.

**An anonymous gift creates and touches nothing**, and a walk-in with no email has no record: an anonymous option that still left a profile would not be anonymous. Contact details are encrypted and follow the donor's latest gift.

## Giving history and lifetime value
Nothing about totals is stored on the donor, so nothing can drift from the books. History, count, last gift and totals are **derived from valid, non-anonymous receipts** (a reversed gift's receipt is void and drops out), per currency (never converted), with money and in-kind value kept apart. `GET /api/v1/admin/finance/donors/reconciliation` proves it: for every donor and currency it compares the receipts' total with the net donation income (`4010`/`4020`, reversals netted) the ledger holds for the same donations, and reports `reconciled` plus any difference.

Staff gift lists for a donor keep reversed gifts with their status; donor endpoints (#212) omit them.

## Profiles and sign-in (BE-104, #211)
- **Profile**: type is `individual` or `organisation`; an organisation needs its name (max 200). Name is required (max 200), email is required and valid (max 254), phone is optional (`+` followed by 7 to 15 digits), language is `bn` or `en` (default `bn`). Address (max 500) and internal notes (max 2000) are encrypted; country is two uppercase letters. Donor status is `active` or `inactive`. The row also holds `accountSubject`, `accountShared`, `accountStatus` (`none`, `active`, `disabled`) and `version`.
- **Read** (`finance.view`): the list accepts `status`, `accountStatus` and `hasAccount=true|false`; unknown values return `400 VALIDATION_ERROR`. Rows include type, status and account status. Detail returns a `profile` object and an `ETag`; `mustChangePassword` is read live from Identity and is `null` without an account or when Identity cannot be reached.
- **Create and edit** (`finance.donor.manage`): `POST .../donors` returns `201` with an `ETag`; invalid fields return `400 VALIDATION_ERROR`, and an existing email in any letter case returns `409 DONOR_EXISTS` naming the existing donor id. `PATCH .../donors/{id}` requires `If-Match` with the quoted version number: missing gives `428 PRECONDITION_REQUIRED`, stale gives `412 PRECONDITION_FAILED`. Omitted fields stay; an optional text field sent as `""` is cleared. Every change increases the version, including a gift refreshing contact details.
- **Provision sign-in** (`finance.donor.provision`): `POST .../donors/{id}/login {password}` calls Identity (#210) with the staff member's own token and uses the donor email as the sign-in name. It returns `201` with `{donorId, accountStatus, accountShared, mustChangePassword}`, or `200` when it adds the Donor role to an existing member account (`accountShared=true`). Errors are `409 ACCOUNT_EXISTS`, `409 EMAIL_TAKEN` (an account of another kind), `409 DONOR_INACTIVE`, `400 VALIDATION_ERROR` (password policy) and `503 AUTH_UNAVAILABLE` (nothing saved). The password is never returned, logged or audited.
- **Manage sign-in** (`finance.donor.provision`): `PUT .../donors/{id}/login/password {password}` sets a new initial password, requires a change and ends sessions; it rejects `409 NO_ACCOUNT` and `409 ACCOUNT_SHARED`. `PUT .../donors/{id}/login/status {status: active|disabled}` disables or enables sign-in; disabling ends sessions. It rejects `409 ACCOUNT_SHARED`, `409 NO_ACCOUNT` and, when enabling for an inactive donor, `409 DONOR_INACTIVE`.
- **Inactive donors**: setting donor status to `inactive` disables a donor-only sign-in in Identity first; failure returns `503 AUTH_UNAVAILABLE` and saves nothing. A sign-in shared with a member is left alone. Reactivating a donor does not enable sign-in.
- **Identity lookup**: `GET /api/v1/admin/auth/donors/{userId}` (`finance.view`) returns status, `mustChangePassword`, roles and `shared`; an account without the Donor role returns `404`.
- **Audit and links**: changes are audited with before and after values, excluding name, email, phone, address, notes and both keys. `linkedStudents` and the has-active-links filter arrive with #214; the Sponsorship module does not exist yet.

## Segments (reproducible)
Pure functions of a donor's gifts, an `asOf` date and configuration (`Finance:Donors`): nothing reads "now" or a stored flag, so any segment can be re-run for any past date. Gifts after `asOf` do not exist yet.
- **new**: first gift within `NewWithinDays` (90) of `asOf`
- **lapsed**: last gift more than `LapsedAfterMonths` (12) calendar months before `asOf`
- **major**: lifetime giving in any one currency at or above its `MajorThresholds` entry (BDT 100000, USD 1000, GBP 800, EUR 900); currencies are never added together
- **recurring**: any gift came from a recurring plan

## Communication consent
Three purposes, **opt-in only**: `thanks` (follow-up thank-yous beyond the receipt), `updates` (impact news), `appeals` (fundraising asks). Nothing is assumed from silence.
- Every grant and withdrawal is a new append-only row (`donor_consents`); the latest per purpose is current. Consent here is separate from newsletter consent (#80): a donation is not a subscription.
- The donation form (public, and offline entry) may carry an optional `communicationConsent`; ticked, it grants `thanks` and `updates` (source `donation_form`) and **never `appeals`**. Staff can record a change with `POST .../donors/{id}/consent` (source `admin` or `donor_request`; `finance.donor.manage`).
- **One gate** (`DonorMessenger`) decides every non-receipt message. If the donor's latest word for the message's purpose is not a grant, the message is **not queued** and a suppression row (`donor_suppressions`, append-only) records it, so it is provable nothing went out without consent. Receipts are transactional and are not gated.

## Thank-you automation
At confirmation, a thank-you is queued to the outbox (`donor.welcome_thanks` for the first gift, `donor.thank_you` after) only if the donor holds `thanks`; otherwise it is suppressed and recorded. Delivery waits on the email sender (BE-032/033).

## Messaging a segment
`POST .../donors/messages {template: donor.appeal|donor.update, segment?, asOf?}` (`finance.donor.manage`) queues the template to the segment through the same gate (appeals need `appeals`, updates need `updates`) and answers how many were queued and suppressed.

## Export
`GET .../donors/export?segment&asOf&consent` (`finance.donor.export`, Admin and SuperAdmin only) returns CSV for the same donors and rules as the list, so an export for a date can be reproduced byte for byte. `consent=appeals` (or thanks, updates) keeps only donors who granted it. Cells that a spreadsheet could run as formulas are neutralised, and **every export is logged** (`donor_exports`: who, segment, date, filter, rows).

## Retention
How long donor records are kept waits on the retention decision (#110); financial records (receipts, ledger) are kept regardless. The donor row is the only mutable table here; consent, suppressions and the export log are append-only.
