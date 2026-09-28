# B0-10 — Import legacy email.html as the member-acceptance template seed
- Source: issue #14 https://github.com/DevCastBD/KJWebsite.Backend/issues/14 (P0); frontend counterpart: FrontEnd #22 (A-10)
- Type: feature (documentation import)
## Request
- Move the frontend's legacy-html/email.html (a transactional email, not a page) to docs/legacy/email-templates/
- Record provenance and what it seeds: P6 template catalogue (#82), P3 approval email (#52), P5 event agenda structure
- Flag what must not be reused verbatim; tell the frontend it can remove its copy
## Found at intake
- Frontend repo is PRIVATE, backend repo is PUBLIC — importing publishes what is currently private
- Contains 2 named officers (presenter; signatory = Vice President), a phone (+880 1977 202 303) that differs from the site's current number, a 2019 event date/venue/schedule
- Images hosted at kolpojontro.org/email-img/ — domain unreachable, images not in any repo (lost); venue.kolpojontro.org also down
- Hard-coded "Dear Concern" greeting, no merge fields; no typo'd kolpojantro domain in this file
## Decisions
- Private → public repo: redact the 2 names, the signatory's role and the phone; keep everything else verbatim (user chose)
## Out of scope
- Building the P6 templating system; deleting the frontend copy (frontend's own change via #22)
## Follow-ups
- kolpojontro.org and venue.kolpojontro.org are unreachable (2026-09-29) — relevant to FrontEnd #121 (domain)
