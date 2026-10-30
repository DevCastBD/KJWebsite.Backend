# Email delivery via outbox, bn/en templates
- Source: issue #32 https://github.com/DevCastBD/KJWebsite.Backend/issues/32
- Docs: docs/MASTER_PLAN.md, ADR-007 (Postmark), legacy email.html
- Type: feature
## Request
- IEmailSender abstraction + Postmark impl + Development file/SMTP sink; bn/en templates (contact ack, registration received, admin notice); dispatched via outbox.
## Decisions
- Provider key from environment/user-secrets; SPF/DKIM/DMARC (#107) are manual and out of code scope
- Language defaults to Bengali; template render tested, client rendering (Gmail/Outlook) left manual
## Out of scope
- DNS setup, email client rendering checks, #107
## Follow-ups
- none yet
