# Donor CRM: history, segments, consent, thank-you, reconciliation (BE-060)
- Source: issue #65 https://github.com/DevCastBD/KJWebsite.Backend/issues/65 (milestone 5, P4)
- Docs: docs/RECEIPTS.md, docs/LEDGER.md; FE contract GET /donors?segment, /donors/{id}/donations (src/admin/finance/api.ts)
- Type: feature (personal data + consent)
## Request
- Donor records with giving history, lifetime value, segments (recurring, major, new, lapsed), communication consent enforced on every send, thank-you automation; anonymous gifts create no attributable donor; totals reconcile exactly with the ledger; segments reproducible and exportable.
## Decisions
- Consent: three purposes (thanks, updates, appeals), opt-in only, append-only grant/withdraw history, current = latest per purpose; donation form may carry optional communicationConsent (grants thanks+updates, never appeals); one central gate before any non-receipt message (user)
- Thank-you: queued at confirmation for non-anonymous donors holding `thanks` consent (first gift = welcome variant); suppressed attempts recorded append-only (user)
- Assumed: donor record created/updated at confirmation in the same commit, keyed by the receipt donor key (email hash), only for non-anonymous gifts with an email; giving history and totals are DERIVED from valid non-anonymous receipts (no mutable cached total, so nothing to drift) and reconciled against ledger income postings on demand; segments are pure functions of receipts + asOf (new <= 90 days, lapsed > 12 months, major >= per-currency threshold, recurring = has a recurring-plan gift), thresholds configurable; export is CSV, permission-gated, audited in an append-only export log, filterable to donors with a consent purpose, spreadsheet-injection safe; segment/appeal messaging endpoint goes through the consent gate; donor rows mutable for contact fields only; retention of donor records waits on #110
## Out of scope
- Real email delivery, newsletter consent (#80), campaigns (#66)
## Follow-ups
- Donor record retention and erasure wait on #110 (financial records are kept regardless)
- Consent withdrawal by the donor themselves needs a public/self-service surface (unsubscribe link) once the email sender exists
- Donor lists are computed in memory from receipts: add paging in SQL or a refreshed snapshot if donor counts reach tens of thousands
