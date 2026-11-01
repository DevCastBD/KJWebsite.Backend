# Donor CRM (BE-060, #65)

## Who is a donor
A donor record is made (or refreshed) when a **non-anonymous** gift with an **email address** is confirmed, in the same commit as the confirmation and its receipt. Donors are recognised by the keyed hash of their email (the receipt donor key, case-insensitive), so one person's gifts join up. **An anonymous gift creates and touches nothing**, and a walk-in with no email has no record: an anonymous option that still left a profile would not be anonymous. Contact details are encrypted and follow the donor's latest gift.

## Giving history and lifetime value
Nothing about totals is stored on the donor, so nothing can drift from the books. History, count, last gift and totals are **derived from valid, non-anonymous receipts** (a reversed gift's receipt is void and drops out), per currency (never converted), with money and in-kind value kept apart. `GET /api/v1/admin/finance/donors/reconciliation` proves it: for every donor and currency it compares the receipts' total with the net donation income (`4010`/`4020`, reversals netted) the ledger holds for the same donations, and reports `reconciled` plus any difference.

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
