# B0-9 — Move kj-registration snapshots to legacy/, exclude from the build
- Source: issue #13 https://github.com/DevCastBD/KJWebsite.Backend/issues/13 (P0)
- Type: feature (repo hygiene)
## Request
- Move kj-registration/ and kj-registration-AwaitingUserFeatures/ into legacy/ with a README: read-only, what to mine, what never to run
- Keep them out of KJWebsite.Backend.slnx; check the MySQL dumps for real personal data and record the finding
## Decisions
- Dumps checked by the user (2026-09-29): all 10 rows are test data (Test*/lorem ipsum/"New User N" placeholders; dev seed accounts admin@admin.com, user1@kol.com; phone fields empty) → keep, no history rewrite
- Build exclusion already holds: not in the .slnx; Directory.*.props skip paths containing "kj-registration", which survives the move
- Tracked .vs/ IDE caches and bin/obj build output (28 files) → dropped in the move and git-ignored (user confirmed)
## Out of scope
- Mining the domain model itself (P3); plan docs that describe this move as future work
## Follow-ups
- The two snapshots are identical except one README; dedupe to one folder later
- VolunteeingExperience misspelling lives on in AuthIdentityService's schema; fixing it needs a migration (P3)
