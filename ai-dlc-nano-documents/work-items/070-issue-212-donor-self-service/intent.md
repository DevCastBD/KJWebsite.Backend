# BE-105: read-only donor self-service (/api/v1/donors/me and giving history)
- Source: issue #212 https://github.com/DevCastBD/KJWebsite.Backend/issues/212 (part of #208, depends on #211, merged)
- Type: feature
## Request
- `GET /api/v1/donors/me`: own profile, donor-visible fields only (name, type, organisation, email, phone, address, country, language, since); never notes or status.
- `GET /api/v1/donors/me/donations`: own gifts from valid receipts, newest first, `limit`/`offset` (limit over 100 is 400), optional `year`, per-currency totals; reversed gifts absent.
- Ownership comes only from the token subject; any donor id in query or body is ignored. Writes are 405. Responses `Cache-Control: private, no-store`.
- Non-donor role 403; inactive donor or disabled account 403 on every request (D16); must-change-password still gated.
## Decisions (assumed, pending approval)
- Access is a role policy (Donor role claim), not a permission: the Donor role holds no permission (D9, #210).
- A Donor-role token with no matching donor row gets 404 NOT_A_DONOR; inactive donor gets 403 DONOR_INACTIVE; account status disabled gets 403 ACCOUNT_DISABLED (state held by Finance).
- `year` is the calendar year of the receipt date; `totals` is `[{currency, cash, inKind}]` over the whole filtered set, not the page.
- `designation` is a display label (campaign name, else general text); the student name arrives with #213/#214, no student designation exists yet.
- No `receiptUrl` yet (no donor receipt download exists). Statements and recurring stay out of scope.
- Gateway gets routes for `/api/v1/donors/me` and `/api/v1/donors/me/donations` only.
## Out of scope
- `/donors/me/statements`, `/recurring`, `/students` and photos, receipt download.
## Follow-ups
