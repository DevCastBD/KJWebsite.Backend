# BE-030 Idempotency, output caching, ETag/If-Match (#48)
- Source: issue #48 https://github.com/DevCastBD/KJWebsite.Backend/issues/48 (milestone 3 / P2, run order BE-030..BE-040; BE-029 #108 skipped by user)
- Docs: docs/MASTER_PLAN.md §1.2 G27, §3.8, §4.1
- Type: feature
## Request
- `Idempotency-Key` honoured on public POSTs; replay returns the original response, creates nothing (closes G27)
- Output caching on public content reads, per language, evicted on admin mutation; Redis when configured
- ETag on admin GET, `If-Match` on admin PUT -> 409 on conflict
## Decisions
- Autopilot run; branch Feature/issue-<n>-<slug>, first off origin/develop, later ones stacked (user confirmed)
- Redis not in repo/Docker yet: in-memory default, Redis selected by `Redis:ConnectionString` (unverified live, no Docker)
- If-Match optional (absent = last-write-wins) so existing clients keep working; 409 per issue text, not 412
## Out of scope
- Idempotency on admin POSTs; P4 webhook dedupe
## Follow-ups
