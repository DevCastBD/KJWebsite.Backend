# Campaigns, references and public status lookup (BE-043, #50)
- Source: issue #50 https://github.com/DevCastBD/KJWebsite.Backend/issues/50 (milestone 4 / P3)
- Docs: frontend contract in KJWebsite.FrontEnd src/api/endpoints/membership.ts (POST .../status {reference,email} -> {timeline:[{status,at}]}, mismatch 404, 429 Retry-After)
- Type: feature
## Request
- Campaigns (seasons) with bilingual name and open/close window; applications link to the open one
- Reference numbers unique, non-sequential, non-guessable (replaces #49's sequential KJ-yyyy-nnnn, unpushed)
- POST status lookup needs reference AND email; every mismatch gives one identical 404; rate limited; timeline = status+time only
- Closing a season closes intake with a clear error
## Decisions
- (approved) no campaigns defined = intake stays open, so #49 and the static frontend keep working
## Out of scope
- Review transitions (#51) beyond the initial `received` event; notes/scores do not exist yet
## Follow-ups
