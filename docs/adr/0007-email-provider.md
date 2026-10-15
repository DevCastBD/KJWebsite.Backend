# ADR-007: Postmark as the transactional email provider

- Status: Accepted
- Date: 2026-10-14
- Ratified by: Md Tahmidul Abedin (@tahmidsparrow)
- Source: [issue #12](https://github.com/DevCastBD/KJWebsite.Backend/issues/12)

## Context

No email sending exists yet. Upcoming work needs bilingual (bn/en) transactional email — CTA acknowledgements, member-acceptance notices, donation receipts — delivered with DKIM/SPF on the organisation's domain so it lands in the inbox rather than spam.

## Decision

Use Postmark as the primary transactional email provider, accessed through a provider-agnostic interface in `BuildingBlocks` so the implementation can be swapped for SendGrid or Amazon SES later without touching call sites. Templates are authored per-language (bn/en) and selected by the request's resolved language.

## Rejected alternatives

- SendGrid: comparable deliverability and API, kept as the documented fallback behind the same interface rather than the primary, to avoid maintaining two integrations up front.
- Amazon SES: cheaper at volume, but requires more manual reputation and domain warm-up work than this project needs at current scale; also kept as a documented fallback.
- Building directly against one provider's SDK with no abstraction: would make a later provider switch a rewrite instead of a swap.

## Consequences

- DKIM/SPF must be configured on the organisation's sending domain before any production email goes out.
- Every outgoing email needs both a bn and an en template, selected by the language resolution already established for content.
- Switching providers later is an implementation change behind the existing interface, not an API or call-site change.
