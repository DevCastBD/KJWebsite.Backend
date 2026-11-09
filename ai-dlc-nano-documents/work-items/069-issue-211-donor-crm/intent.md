# Donor profile create and update, and donor sign-in accounts in the donor CRM (BE-104)
- Source: issue #211 https://github.com/DevCastBD/KJWebsite.Backend/issues/211 (epic #208; builds on #210, merged in PR #220)
- Type: feature (personal data, auth calls, concurrency)
## Request
- Create and edit donor profiles (POST, PATCH with If-Match and ETag); new fields type, organisation, address, country, status, notes (address and notes encrypted).
- Give a donor a sign-in through Identity (#210): login, password reset, enable or disable; account subject stored on the donor.
- Extend list and detail: account status (none, active, disabled), mustChangePassword, filters; staff still see reversed gifts.
## Decisions
- Email change -> a donor keeps a stable key it was created with; a separate hash of the current email (unique) matches new gifts; receipts are never edited
- Inactive donor with a shared member account -> sign-in left alone (donor endpoints refuse them, #212); a donor-only account is disabled in Identity
- mustChangePassword in the detail -> read live from Identity through a new read endpoint; null if Identity is unreachable
- linkedStudents and the has-active-links filter -> added with #214, not now
- D4 donor fields -> the proposed defaults stand (type, organisation, address, country, status, notes)
## Out of scope
- Students and links (#213, #214), donor self-service (#212), merging two donors.
## Follow-ups
- A donor with a sign-in account cannot change its email (409 EMAIL_HAS_ACCOUNT): the sign-in email lives in Identity; needs an Identity email-change endpoint
- Finance DonorProfile migration and its gated test were not run (no local Postgres)
- The donor list and every create or update response rebuild the whole donor picture from all receipts; fine now, revisit at scale
