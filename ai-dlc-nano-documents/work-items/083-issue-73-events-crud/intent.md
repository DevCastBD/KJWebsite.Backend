# Events CRUD: bilingual content, venue, capacity, sessions, recurrence (BE-069)
- Source: issue #73 https://github.com/DevCastBD/KJWebsite.Backend/issues/73 (milestone 6, P5)
- Type: feature
## Request
- Event as a ContentItem type: bilingual via the editorial workflow, venue/online details, capacity, ordered sessions/agenda, recurrence rule, explicit timezone.
- Part of a stacked run of milestone-6 issues (#73, #75-#79; #74 skipped as decision), each branch on the previous.
## Decisions
- Stays in ContentService (Event extends ContentItem, plan §3.6) → assumed, pending plan approval
## Out of scope
- Registration, attendance (#74/#75), iCal feeds (#79)
## Follow-ups
