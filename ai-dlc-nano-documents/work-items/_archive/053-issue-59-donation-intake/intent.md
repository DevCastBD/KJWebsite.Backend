# Donation intake domain (BE-054)
- Source: issue #59 https://github.com/DevCastBD/KJWebsite.Backend/issues/59 (milestone 5, P4)
- Docs: docs/adr/0008-payments.md, docs/IDEMPOTENCY.md, docs/MASTER_PLAN.md P4
- Type: feature
## Request
- New finance domain (closes G17): one-time + recurring donations, presets/custom amounts, BDT/USD/GBP/EUR, designation, anonymous, dedication, cover-the-fee.
- decimal(18,2) + ISO-4217 currency everywhere; records append-only (reversing entries only).
- Anonymous never attributable in any response/export; property-based tests on money arithmetic.
## Decisions
- Milestone order: BE number order, decision issues #111/#112 skipped, epic #119 skipped (user)
- Branch Feature/issue-59-donation-intake off fresh origin/develop; later issues stacked (user)
- USD/GBP/EUR in BDT campaigns -> per-currency totals, no silent conversion; optional recorded BDT-equivalent (rate+date) (user)
- Anonymous -> hidden from public API and any export; finance.view staff may see donor (user; narrows the issue AC wording, consistent with its Notes)
- Designation -> opaque campaignSlug/projectSlug now; campaign validation arrives with #66 (user)
- Assumed: hand-rolled seeded property tests (no new package); reversal endpoint needs new permission held by Admin only until #72 four-eyes
## Out of scope
- Payment gateways/webhooks (#60, #62), receipts (#64), campaigns (#66), ledger posting (#69)
## Follow-ups
- Frontend adaptDonation drops rows without `method`/`donorId`; #60/#63 populate method, #65 donor ids
- BDT-equivalent snapshot (rate+date) when payments confirm (#60/#62)
- PrivacyTests.ApplicationsPastTheirWindowArePurged failed once in a full run, passes alone (flaky)
