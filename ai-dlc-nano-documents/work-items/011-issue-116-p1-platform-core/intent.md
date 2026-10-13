# P1 — Production-grade platform core
- Source: issue #116 https://github.com/DevCastBD/KJWebsite.Backend/issues/116
- Type: feature
## Request
- Deliver the P1 backend epic in execution order, covering security defects, data and identity, content, platform, email, engagement, and operations.
- Meet the exit criteria in `docs/MASTER_PLAN.md` §5 P1, including durable auth/content, throttling, CORS, email, backup restore, and integration coverage.
- Keep the static frontend deployable with the API disabled; maintain the OpenAPI contract.
## Decisions
- First child issue: #21, selected by user; the epic remains paused while #21 is active.
- ADR-001 accepted true microservices on 2026-10-12; see `docs/adr/0001-true-microservices.md`.
- 2026-10-14: #21 done (merged, PR #155). Checked P1's next listed item, BE-004 #27 (CORS) — it depends on BE-013 #30 (gateway config), which depends on BE-002 #12 (ratify remaining ADRs, epic #115/P0, still open). User chose to do #12 first; epic stays paused here until #12 lands.
- 2026-10-14: #12 done (11/14 ADRs ratified, see work-items/015). #30 (BE-013, gateway config) is next real blocker before #27 can start; check its state before resuming.
## Out of scope
- None confirmed.
## Follow-ups
- None yet.
