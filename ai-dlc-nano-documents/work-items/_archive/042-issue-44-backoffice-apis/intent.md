# BE-038 Back-office APIs: dashboard, global search, bulk actions (#44)
- Source: issue #44 (milestone 3, order BE-038)
- Type: feature
## Request
- Cheap cached dashboard summary; permission-filtered global search; bulk actions with per-item results
## Decisions
- Scope = what exists on this base: ContentService (items, tags, redirects, settings, audit). CTA submissions (admin API in unmerged #34), media (#41, blocked) and cross-service fan-out are left as documented extension points
- Dashboard cached in-process for 30 s per permission set; recent activity read from the audit log (needs audit.view, otherwise omitted)
- Search covers only entity kinds the caller may read; others are omitted, not reported as empty
## Out of scope
- Submissions/media/user widgets, search ranking beyond simple relevance (Bengali FTS is #47)
## Follow-ups
