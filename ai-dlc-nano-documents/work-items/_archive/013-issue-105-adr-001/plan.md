<!-- phase: DONE | branch: docs/issue-105-true-microservices | tasks: 4/4
     base: a07fa23 | updated: 2026-10-12
     next: published as PR #151; await review -->
# Plan: Ratify ADR-001 as true microservices
## Tasks
- [x] Write `docs/adr/0001-true-microservices.md` with status, ratifier/date, context, decision, rejected monolith, rationale, and consequences.
- [x] Update `docs/MASTER_PLAN.md` §2.1 topology and §4.2 ADR table to reflect the ratified choice and independent deployment/domain flexibility.
- [x] Align later plan references to ADR-001, including P1 cross-service requirements, optional extraction, risks, and open questions.
- [x] Review changed sections for contradictions and verify the document diff.
## Tests
- Documentation-only change: no new automated tests. Run `git diff --check` and targeted searches for stale ADR-001/monolith language; inspect the rendered section text.
## Files
- `docs/adr/0001-true-microservices.md`
- `docs/MASTER_PLAN.md`
