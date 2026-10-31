<!-- phase: PLAN | branch: feature/issue-32-email-outbox | tasks: 0/5
     base: — | updated: 2026-10-30
     next: await plan approval, then branch feature/issue-32-email-outbox (feature/issue-33-outbox-jobs) -->
# Plan: Email delivery via outbox, bn/en templates
## Tasks
- [ ] IEmailSender + Postmark + file-pickup dev sender; options
- [ ] Template renderer + bn/en templates (3 kinds)
- [ ] CTA submission enqueues ack + admin notification via outbox; Auth emails use sender
- [ ] Tests: provider outage doesn't fail request, bilingual render, outbox dispatch
- [ ] Docs, full suite + format
## Tests
- Standard + failure path
## Files
- src/BuildingBlocks, CTA + Auth services, tests, docs
