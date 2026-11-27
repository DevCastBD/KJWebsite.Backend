# Testimonials, partners, success stories and consent (BE-081, #85)

Testimonials (`/testimonials`), partners (`/partners`) and success stories (`/success-stories`, new type `success-story`, headline, summary and body required) are generic content types. Their real people and organisations are the reason for the rules below. The legacy theme's invented content stays out (D6); this is what makes real content possible.

## Consent gate
`POST /api/v1/admin/content/items/{type}/{slug}/consents` (`content.update`) records who agreed (`subjectName`), what (`scope`), on what `basis` (`written`, `recorded-verbal`, `guardian-written`, `agreement`), where the evidence is kept (`evidence`: a reference, never the file), an optional `expiresAt`, who recorded it and when. Records are never edited; they are withdrawn.

An item of these types can only go live (create as published, `PUT` status, `POST .../workflow` to published or scheduled, and the scheduled publisher at the moment it fires) if it has an active consent (not withdrawn, not expired). Create as a draft, record consent, then publish. `GET .../consents` lists the records (personal data, `content.update`).

## Withdrawal
`POST /api/v1/admin/content/consents/{id}/withdraw {note}` needs only `content.update` (honouring a withdrawal must not wait for someone with publish rights). If it was the last active consent, the item goes back to draft in the same save, a revision `consent-withdrawn` is written, and the public output cache is evicted, so it is gone from the API immediately. `GET /api/v1/content/consent-withdrawals?since=` (default 7 days) lists the public paths withdrawn, with no personal data, for the frontend build or a purge job to remove prerendered pages and CDN copies. Purging those copies is the frontend's job.

## Minors and beneficiaries
A minor's consent needs `isMinor: true`, basis `guardian-written` and a `guardianName`. While `Consent:BeneficiaryPolicyApproved` is not `true`, an item whose only consent is a minor's cannot be published at all. Setting that flag is the organisation's decision, after approving a policy for beneficiary testimonials. A **draft of that policy is not written here** (it is a decision for the board, not something to invent in code); the gate stays closed until they approve one.

## Partners
`GET|PUT /api/v1/admin/content/partners/{slug}/details`: `tier`, `websiteUrl`, `logoUrl` and the logo rights, `logoRights` = `unknown`, `granted`, `restricted` (needs `logoConditions`) or `withdrawn`; granted and restricted need `logoEvidence`. A logo is stored only while rights are granted or restricted, and the public API shows it only then (conditions and evidence stay internal). Public: `GET /api/v1/content/partners/{slug}/details`, `GET /api/v1/content/partner-registry?lang=&tier=`. A partner's consent record is the organisation agreeing to be listed.

## Success stories
Tie a story to a project with the generic related-items list. Beneficiaries are not linked by id (the student records are private in Sponsorship); the consent record names who agreed.
