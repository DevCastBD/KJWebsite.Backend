# Verify G5 (CORS) closed
- Source: issue #19 (fixed by #27)
- Type: bug
## Request
- Confirm CORS acceptance criteria hold; no code expected
## Decisions
- Verified live on gateway :7000 + via integration tests on #27 branch
## Out of scope
- Production origin values (set per env via Cors__SiteOrigins__0)
## Follow-ups
