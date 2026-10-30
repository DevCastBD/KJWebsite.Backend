<!-- phase: DONE | branch: feature/issue-32-email-outbox | tasks: 5/5
     base: 4c56000 | updated: 2026-10-30
     next: none; committed locally -->
# Plan: Email delivery via outbox, bn/en templates
## Tasks
- [x] IEmailSender + Postmark + file-pickup dev sender; options
- [x] Template renderer + bn/en templates (3 kinds)
- [x] CTA submission enqueues ack + admin notification via outbox; Auth emails use sender
- [x] Tests: provider outage doesn't fail request, bilingual render, outbox dispatch
- [x] Docs, full suite + format
## Tests
- Standard + failure path
## Files
- src/BuildingBlocks, CTA + Auth services, tests, docs
