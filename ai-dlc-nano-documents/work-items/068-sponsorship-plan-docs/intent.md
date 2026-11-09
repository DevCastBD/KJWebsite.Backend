# Record the sponsorship plan and contract in the backend docs
- Source: owner request to fix the gaps found when checking whether the decisions are current (epic #208, decisions on #209)
- Type: docs
## Request
- Add rows BE-102 to BE-109 to docs/MASTER_PLAN.md (the plan wins over any issue) and a frontend-alignment row.
- Add docs/SPONSORSHIP.md: the agreed frontend and backend contract and decisions, so developers do not depend on issue comments.
- Refresh the codebase map and tech-stack size line (166 files recorded, 639 now).
## Decisions
- Contract and decisions are those recorded on #208 (comment) and #209 (table marked DECIDED or OPEN)
- Added during Identity clarify: D7 one account for member and donor; API enforces must-change (403 PASSWORD_CHANGE_REQUIRED); one-time Admin key backfill; platform password policy
- openapi.v1.yaml is generated from code, so sponsorship endpoints appear when they are built, not now
## Out of scope
- Any code (issues #210 to #216)
## Follow-ups
- BE plan section 1 (Where we are) may need a sponsorship line once #210 lands
