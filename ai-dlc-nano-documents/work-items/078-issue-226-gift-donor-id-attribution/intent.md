# Attribute online gifts to donors by id (BE-112, #226)
- Source: issue #226 https://github.com/DevCastBD/KJWebsite.Backend/issues/226 (part of epic #208, needs-decision, depends on #215)
- Type: enhancement (money records, receipts, public endpoint, migration)
## Request
- Extend donor-id attribution to online gifts; record the decision on public-form identity, old rows and email change history.
- An email change must never move a gift to another donor; anonymous gifts unchanged; old rows stay attached to the same donor.
## Decisions
- Token plus pin at recording: the public form stores the signed-in donor's id when a donor token is present, else the id of the donor who already owns the typed email (first-time donors still created at confirmation); anonymous gifts unchanged → user chose 2026-11-18.
- Old rows: no change, no backfill, no mapping table (receipt DonorKey is frozen and in the chain hash) → findings in the plan.
- No email history table (the audit trail covers email changes; the stored id removes the need) → approved plan.
- Recurring plans get a nullable DonorId so later instalments inherit it; no instalment creator here → approved plan.
## Out of scope
- Rewriting existing rows, attributing anonymous gifts, instalment creation, frontend changes.
## Follow-ups
- Found while building: a gift tied by id used to skip the profile refresh and take the donor profile on its receipt; kept the old behaviour for online gifts (refresh only for a matching email, receipt keeps typed name and email) and offline gifts unchanged.
- Not fixed: anyone can type a donor email on the public form and overwrite that donor's name and phone at confirmation (existing).
