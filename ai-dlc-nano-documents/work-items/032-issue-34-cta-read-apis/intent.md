# CTA read APIs, export, anti-spam
- Source: issue #34 https://github.com/DevCastBD/KJWebsite.Backend/issues/34
- Docs: docs/MASTER_PLAN.md, docs/MASTER_PLAN.md §3.3
- Type: feature
## Request
- Admin list/filter/page, GET by id, PATCH status/assignee/notes (audited), CSV export with BOM; honeypot + timing check; optional Turnstile/hCaptcha.
## Decisions
- Gated by a CTA permission (existing RBAC seed); access audited, no PII in logs
- Honeypot field + minimum-fill-time token; Turnstile optional behind config, off by default
## Out of scope
- Full inbox (P2)
## Follow-ups
- none yet
