<!-- phase: DONE | branch: 14-feature-b0-10-import-legacy-email-template | tasks: 3/3
     base: 3ffedb2 | updated: 2026-09-29
     next: — (PR open; frontend told via FrontEnd #22) -->
# Plan: import the legacy member-acceptance email
## Tasks
- [x] docs/legacy/email-templates/member-acceptance-2019.html — original, with 2 names, 1 name+role and the phone redacted
- [x] README.md — provenance, what it seeds (#52, #82, P5 agenda), what must change before reuse
- [x] Verify: diff vs original = exactly 4 lines, all redactions; no personal details left; HTML parses with no unclosed tags
## Tests
- none — documentation import; verified by diff, grep and an HTML parse
## Files
- docs/legacy/email-templates/{member-acceptance-2019.html,README.md}
